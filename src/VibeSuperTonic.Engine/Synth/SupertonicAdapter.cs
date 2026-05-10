using Microsoft.Win32;
using Supertonic;
using VibeSuperTonic.Engine.Settings;

namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// One adapter per voice id. The heavy <see cref="TextToSpeech"/> ONNX session set
/// is shared statically across all voice ids — only the small <see cref="Style"/>
/// (per-voice embedding JSON) is per-instance. Synthesis is serialized via a static
/// gate because ONNX <c>InferenceSession.Run</c> is not safe under concurrent calls.
/// </summary>
internal sealed class SupertonicAdapter
{
    // Supertonic's natural pace; range 0.9-1.5 per their docs.
    public const float DefaultSpeed = 1.05f;

    private static readonly object _ttsGate = new();
    private static TextToSpeech? _sharedTts;
    private static string? _ttsOnnxDir;

    private readonly object _styleGate = new();
    private readonly string _voiceId;
    private Style? _style;

    public SupertonicAdapter(string voiceId) => _voiceId = voiceId;

    private static string GetBaseDirOrThrow()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
        var baseDir = k?.GetValue("BaseDir") as string;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            throw new InvalidOperationException($"VibeSuperTonic BaseDir not configured or missing: '{baseDir}'");
        return baseDir!;
    }

    private static TextToSpeech GetSharedTts(out string baseDir)
    {
        baseDir = GetBaseDirOrThrow();
        if (_sharedTts is not null) return _sharedTts;
        lock (_ttsGate)
        {
            if (_sharedTts is not null) return _sharedTts;
            string onnxDir = Path.Combine(baseDir, "models", "onnx");
            if (!Directory.Exists(onnxDir))
                throw new FileNotFoundException($"Supertonic ONNX directory missing: {onnxDir}");
            // Pull the user's tweaks from the registry-backed settings (cached by
            // Settings\Version). The Control Panel surfaces all of these on the Advanced
            // tab so users can experiment with their CPU/GPU.
            int intraOp = 0, interOp = 1, dmlDevice = 0;
            bool useDml = false;
            try
            {
                var es = EngineSettingsCache.Resolve();
                intraOp = es.OnnxThreads;
                interOp = es.OnnxInterOpThreads;
                useDml = es.UseDirectML;
                dmlDevice = es.DirectMLDeviceId;
            }
            catch { /* defaults */ }
            _sharedTts = Helper.LoadTextToSpeech(onnxDir, useGpu: useDml,
                intraOpThreads: intraOp, interOpThreads: interOp, directMLDevice: dmlDevice);
            _ttsOnnxDir = onnxDir;
            return _sharedTts;
        }
    }

    private void EnsureStyleLoaded(string baseDir)
    {
        if (_style is not null) return;
        lock (_styleGate)
        {
            if (_style is not null) return;
            string voiceFile = Path.Combine(baseDir, "models", "voice_styles", $"{_voiceId}.json");
            if (!File.Exists(voiceFile))
                throw new FileNotFoundException($"Voice style file missing: {voiceFile}");
            _style = Helper.LoadVoiceStyle(new List<string> { voiceFile }, verbose: false);
        }
    }

    public short[] Synthesize(string text, int totalStep = 8, float speed = DefaultSpeed)
    {
        var tts = GetSharedTts(out string baseDir);
        EnsureStyleLoaded(baseDir);
        lock (_ttsGate)
        {
            var (wav, _) = tts.Call(text, "en", _style!, totalStep, speed);
            return FloatToInt16(wav);
        }
    }

    private const int MaxChunkChars = 200;
    private const int MinChunkChars = 100;
    private const int MergeCeiling = MaxChunkChars + 80; // allow merging slightly above max

    /// <summary>
    /// Three-pass chunker:
    /// 1. Helper.ChunkText splits at sentence boundaries (merges short sentences ≤MaxChunkChars).
    /// 2. SplitLongChunk breaks oversize sentences at internal punctuation/word boundaries.
    /// 3. Merge tiny chunks (&lt;MinChunkChars) with neighbors so all chunks have similar
    ///    synthesis cost — uniform sizes keep the pipeline flowing without gaps.
    /// </summary>
    public static List<string> ChunkText(string text)
    {
        var sentenceChunks = Helper.ChunkText(text, maxLen: MaxChunkChars);

        // Pass 2: split oversize chunks
        var split = new List<string>();
        foreach (var chunk in sentenceChunks)
        {
            if (chunk.Length <= MaxChunkChars + 30) split.Add(chunk);
            else split.AddRange(SplitLongChunk(chunk, MaxChunkChars));
        }

        // Pass 3: merge undersize chunks with neighbors
        var merged = new List<string>();
        foreach (var chunk in split)
        {
            if (merged.Count > 0)
            {
                int combinedLen = merged[^1].Length + 1 + chunk.Length;
                bool tinyExists = merged[^1].Length < MinChunkChars || chunk.Length < MinChunkChars;
                if (tinyExists && combinedLen <= MergeCeiling)
                {
                    merged[^1] = merged[^1] + " " + chunk;
                    continue;
                }
            }
            merged.Add(chunk);
        }
        return merged;
    }

    private static IEnumerable<string> SplitLongChunk(string text, int maxLen)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int remaining = text.Length - pos;
            if (remaining <= maxLen)
            {
                yield return text.Substring(pos).Trim();
                yield break;
            }
            int hardEnd = pos + maxLen;
            int boundary = FindSoftBoundary(text, pos, hardEnd);
            yield return text.Substring(pos, boundary - pos).Trim();
            pos = boundary;
            // Skip leading whitespace
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }
    }

    private static int FindSoftBoundary(string text, int start, int end)
    {
        // Prefer punctuation breaks in the latter half of the window
        int half = start + (end - start) / 2;
        for (int i = end - 1; i > half; i--)
        {
            char c = text[i];
            if (c == ',' || c == ';' || c == ':' || c == '—' /* em-dash */) return i + 1;
        }
        // Fall back to last whitespace anywhere in window (don't restrict to latter half —
        // that risks hard-cutting mid-word when latter half has no spaces).
        for (int i = end - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1;
        }
        // Pathological: no whitespace in window. Walk forward to next whitespace rather
        // than hard-cutting mid-word; chunk will be slightly oversize but words preserved.
        for (int i = end; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1;
        }
        return text.Length;
    }

    /// <summary>
    /// Kick off background ONNX model + style loading so the first Speak doesn't pay the cost.
    /// Safe to call multiple times — load is idempotent.
    /// </summary>
    public Task PreloadAsync() => Task.Run(() =>
    {
        try
        {
            GetSharedTts(out string baseDir);
            EnsureStyleLoaded(baseDir);
        }
        catch { /* ignore — Speak will surface real errors */ }
    });

    /// <summary>
    /// Voice-agnostic pre-warm of the shared ONNX models. Safe to call from static init —
    /// no voice id required because the heavy 380 MB load is shared. Per-voice Style files
    /// are tiny and load on first use.
    /// </summary>
    public static Task PreloadSharedAsync() => Task.Run(() =>
    {
        try { GetSharedTts(out _); }
        catch { /* swallow — first real Speak will report */ }
    });

    private static short[] FloatToInt16(float[] wav)
    {
        short[] pcm = new short[wav.Length];
        for (int i = 0; i < wav.Length; i++)
        {
            float s = wav[i];
            if (s > 1f) s = 1f;
            else if (s < -1f) s = -1f;
            pcm[i] = (short)(s * 32767f);
        }
        return pcm;
    }

    public int SampleRate
    {
        get
        {
            var tts = GetSharedTts(out _);
            return tts.SampleRate;
        }
    }
}

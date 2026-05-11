using Microsoft.Win32;
using Supertonic;
using VibeSuperTonic.Engine.Settings;

namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// One adapter per voice id. The heavy <see cref="TextToSpeech"/> ONNX session set
/// is shared statically across all voice ids — only the small <see cref="Style"/>
/// (per-voice embedding JSON) is per-instance. Synthesis is serialized via a static
/// gate because ONNX <c>InferenceSession.Run</c> is not safe under concurrent calls.
///
/// DirectML device-loss recovery: when the GPU is reset (TDR, sleep/resume, driver
/// crash) every <c>InferenceSession</c> in this process becomes permanently dead.
/// We catch the device-loss exception, dispose+null the shared session, rebuild
/// it, and retry once. Repeated losses within a short window latch DML off for
/// the rest of the process — the session reloads on CPU and the user keeps working.
/// </summary>
internal sealed class SupertonicAdapter
{
    // Supertonic's natural pace; range 0.9-1.5 per their docs.
    public const float DefaultSpeed = 1.05f;

    private static readonly object _ttsGate = new();
    private static TextToSpeech? _sharedTts;
    private static string? _ttsOnnxDir;

    // Process-local DML health latch. Set true after repeated device-loss; once set,
    // GetSharedTts ignores the registry's UseDirectML and loads on CPU. Never
    // persisted — a driver glitch shouldn't silently downgrade the user's settings.
    private static volatile bool _useDmlLatchedOff;
    // Sliding window of recent device-loss timestamps for the latch heuristic.
    private static readonly object _lossLogGate = new();
    private static readonly List<long> _lossTimestampsMs = new();
    private const int LossLatchThreshold = 3;
    private const int LossLatchWindowMs = 60_000;

    // External reset request — set by SessionRegistry when the launcher writes a
    // <pid>.reset marker. Consumed at the top of every Synthesize call.
    private static volatile bool _resetRequested;
    // Counter exposed to the launcher as part of session telemetry.
    private static int _deviceLossCount;
    public static int DeviceLossCount => _deviceLossCount;
    public static bool DmlLatchedOff => _useDmlLatchedOff;
    public static void RequestReset() => _resetRequested = true;

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
            // Honor the in-process latch: repeated GPU device-loss in this process
            // means the DML path is unhealthy here; force CPU until process exit.
            if (_useDmlLatchedOff) useDml = false;
            _sharedTts = Helper.LoadTextToSpeech(onnxDir, useGpu: useDml,
                intraOpThreads: intraOp, interOpThreads: interOp, directMLDevice: dmlDevice);
            _ttsOnnxDir = onnxDir;
            return _sharedTts;
        }
    }

    /// <summary>
    /// Disposes the shared <see cref="TextToSpeech"/> sessions and nulls the cache so
    /// the next <see cref="GetSharedTts"/> rebuilds. Caller must hold <c>_ttsGate</c>.
    /// </summary>
    private static void DisposeSharedTtsLocked()
    {
        var dead = _sharedTts;
        _sharedTts = null;
        _ttsOnnxDir = null;
        try { dead?.Dispose(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Returns true if the exception is a DirectML device-loss / device-removed
    /// failure. ORT wraps the DXGI HRESULT into an OnnxRuntimeException whose
    /// message contains the HRESULT and a description we can match on.
    /// </summary>
    private static bool IsDeviceLoss(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            string msg = e.Message ?? "";
            if (msg.Contains("887A0005", StringComparison.Ordinal)) return true;
            if (msg.Contains("887A0020", StringComparison.Ordinal)) return true; // DXGI_ERROR_DRIVER_INTERNAL_ERROR
            if (msg.Contains("device has been suspended", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("device removed", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("GetDeviceRemovedReason", StringComparison.Ordinal)) return true;
            if (msg.Contains("DXGI_ERROR", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    /// <summary>
    /// Records a device-loss event and returns true if the latch should engage.
    /// Window-based: <see cref="LossLatchThreshold"/> losses within
    /// <see cref="LossLatchWindowMs"/> trips the latch.
    /// </summary>
    private static bool RecordLossAndShouldLatch()
    {
        Interlocked.Increment(ref _deviceLossCount);
        long now = Environment.TickCount64;
        lock (_lossLogGate)
        {
            _lossTimestampsMs.Add(now);
            _lossTimestampsMs.RemoveAll(t => now - t > LossLatchWindowMs);
            return _lossTimestampsMs.Count >= LossLatchThreshold;
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
        // Honor an external reset request (launcher → SessionRegistry → here) before
        // we hand any new work to the (possibly-dead) shared session.
        if (_resetRequested)
        {
            _resetRequested = false;
            lock (_ttsGate) DisposeSharedTtsLocked();
        }

        var tts = GetSharedTts(out string baseDir);
        EnsureStyleLoaded(baseDir);
        try
        {
            lock (_ttsGate)
            {
                var (wav, _) = tts.Call(text, "en", _style!, totalStep, speed);
                return FloatToInt16(wav);
            }
        }
        catch (Exception ex) when (IsDeviceLoss(ex))
        {
            // GPU device-loss: every session in this process is now dead. Drop the
            // shared cache so GetSharedTts rebuilds, latch CPU if we've seen too many
            // losses already, and retry once. A second failure surfaces to the caller.
            //
            // Concurrency: the dispose + rebuild + retry must happen under a single
            // _ttsGate hold so a parallel thread (also seeing device-loss) doesn't
            // dispose the session we just built before we get to use it. Monitor is
            // re-entrant, so GetSharedTts reacquiring the gate inside this block is fine.
            bool latchNow = RecordLossAndShouldLatch();
            try
            {
                lock (_ttsGate)
                {
                    // If another thread already replaced the cached session while we were
                    // throwing, skip the dispose — `tts` is stale-and-orphaned, but the
                    // current `_sharedTts` belongs to that other thread and is alive.
                    if (ReferenceEquals(_sharedTts, tts))
                    {
                        DisposeSharedTtsLocked();
                        if (latchNow) _useDmlLatchedOff = true;
                    }
                    var rebuilt = GetSharedTts(out _);
                    var (wav, _) = rebuilt.Call(text, "en", _style!, totalStep, speed);
                    return FloatToInt16(wav);
                }
            }
            catch (Exception retryEx)
            {
                // Retry failed too. Surface both causes — the retry exception's
                // message often points at the rebuild path (eg. CPU fallback init
                // failing) while the original tells the operator which DXGI code
                // triggered the loss. Bundle them so engine.log captures both.
                throw new AggregateException(
                    "DirectML device-loss recovery failed after one retry.",
                    ex, retryEx);
            }
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

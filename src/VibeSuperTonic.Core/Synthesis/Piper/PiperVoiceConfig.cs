using System.Text.Json;

namespace VibeSuperTonic.Core.Synthesis.Piper;

/// <summary>
/// A Piper voice's <c>.onnx.json</c> — everything the graph needs that is not
/// weights, and the only place the voice's sample rate is written down.
///
/// <para>Parsed here rather than in the backend because none of it needs ONNX
/// Runtime and all of it is worth testing: an id map read wrong is audio that is
/// wrong rather than a render that fails, which is the failure class
/// <see href="../../../../docs/PIPER-PLAN.md#p1">P1</see> spent a day proving we
/// had avoided.</para>
///
/// <para><b>Only what is used is read.</b> <c>phoneme_map</c>, <c>num_symbols</c>,
/// <c>piper_version</c>, <c>language</c> and <c>dataset</c> are all present in
/// every voice file and none of them affects a render — P4 will want the
/// language block for the Voices tab, and can add it then. Unknown keys are
/// ignored rather than refused, because upstream adds them (this is the file
/// <c>alignments</c> would arrive in).</para>
/// </summary>
public sealed class PiperVoiceConfig
{
    private PiperVoiceConfig(
        int sampleRate, string espeakVoice, int numSpeakers,
        float noiseScale, float lengthScale, float noiseW,
        IReadOnlyDictionary<string, int[]> phonemeIdMap,
        string? quality)
    {
        SampleRate = sampleRate;
        EspeakVoice = espeakVoice;
        NumSpeakers = numSpeakers;
        NoiseScale = noiseScale;
        LengthScale = lengthScale;
        NoiseW = noiseW;
        PhonemeIdMap = phonemeIdMap;
        Quality = quality;
    }

    /// <summary>
    /// What the voice was trained at — 22050 for <c>medium</c> and <c>high</c>,
    /// 16000 for <c>low</c>. <b>Not resampled</b>: the sink re-tunes to follow
    /// it. See the native-rate decision in the plan.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>The espeak-ng voice to phonemise with, e.g. "en-us".</summary>
    public string EspeakVoice { get; }

    /// <summary>
    /// Speakers in the graph. <b>1 means the graph has three inputs, not four</b>
    /// — the <c>sid</c> tensor is absent, and sending one to a single-speaker
    /// voice is an error rather than a no-op.
    /// </summary>
    public int NumSpeakers { get; }

    /// <summary>The voice's own defaults, from its <c>inference</c> block.</summary>
    public float NoiseScale { get; }
    public float LengthScale { get; }
    public float NoiseW { get; }

    /// <summary>
    /// Phoneme to model ids. ~154 entries, and a phoneme it does not know is
    /// SKIPPED rather than substituted — see <see cref="PiperPhonemes.ToIds"/>.
    /// A value is an array because a few entries map to more than one id.
    /// </summary>
    public IReadOnlyDictionary<string, int[]> PhonemeIdMap { get; }

    /// <summary>The tier — "low", "medium", "high" — or null on a voice that omits it.</summary>
    public string? Quality { get; }

    /// <summary>The per-utterance defaults this voice ships, as the backend takes them.</summary>
    public PiperOptions DefaultOptions(string voiceId, float silenceSeconds = 0.3f) =>
        new(voiceId, LengthScale, NoiseScale, NoiseW, SpeakerId: 0, SilenceSeconds: silenceSeconds);

    public static PiperVoiceConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Piper voice config not found: {path}", path);
        return Parse(File.ReadAllText(path), path);
    }

    public static PiperVoiceConfig Parse(string json, string? source = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string where = source is null ? "" : $" in {source}";

        int sampleRate = root.TryGetProperty("audio", out var audio)
                         && audio.TryGetProperty("sample_rate", out var sr)
            ? sr.GetInt32()
            : throw new InvalidDataException($"Piper voice config has no audio.sample_rate{where}");

        // A rate of zero or a negative one would reach the sink, the playback
        // clock and the time-stretch, each of which divides by it.
        if (sampleRate <= 0)
            throw new InvalidDataException($"Piper voice config has audio.sample_rate {sampleRate}{where}");

        // WHAT THE SYMBOLS ARE, asked before anything is done with them.
        //
        // A Piper voice's ids are not necessarily phonemes. phoneme_type "text"
        // means the id map is the language's LETTERS — uk_UA's ukrainian_tts is
        // one, and its map is Cyrillic graphemes. Nothing else distinguishes it:
        // it carries an espeak.voice like every other voice, its phoneme_id_map
        // is well formed, and it holds the three required symbols. Feeding it IPA
        // looks up phonemes that are almost all absent, so the model receives a
        // handful of punctuation ids and renders something between silence and
        // noise — a voice that downloaded, verified, calibrated and speaks
        // rubbish.
        //
        // The value is not always a plain string either: es_MX-ald-medium says
        // "PhonemeType.ESPEAK", a Python enum repr that leaked into upstream's
        // config and is espeak. Compared on the value rather than the spelling,
        // so a formatting accident does not cost a working voice.
        //
        // Absent means espeak, confirmed in upstream's config.py.
        if (root.TryGetProperty("phoneme_type", out var pt)
            && pt.ValueKind == JsonValueKind.String
            && pt.GetString() is { Length: > 0 } phonemeType)
        {
            string kind = phonemeType.Trim();
            int dot = kind.LastIndexOf('.');
            if (dot >= 0) kind = kind[(dot + 1)..];
            if (!kind.Equals("espeak", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Piper voice config has phoneme_type '{phonemeType}'{where}: its symbols are " +
                    "not espeak phonemes, so this voice cannot be spoken by this engine. " +
                    "A voice like this renders noise rather than failing, which is why it is " +
                    "refused here. Remove it and install one the Voices tab offers.");
        }

        string espeak = root.TryGetProperty("espeak", out var esp)
                        && esp.TryGetProperty("voice", out var v)
                        && v.GetString() is { Length: > 0 } voice
            ? voice
            // Not defaulted to en-us: phonemising the wrong language produces
            // fluent-sounding nonsense, which is worse than a refusal.
            : throw new InvalidDataException($"Piper voice config has no espeak.voice{where}");

        // Defaulted, unlike the two above: upstream's own values, and a voice
        // that omits the block renders correctly with them.
        float noiseScale = 0.667f, lengthScale = 1.0f, noiseW = 0.8f;
        if (root.TryGetProperty("inference", out var inf))
        {
            if (inf.TryGetProperty("noise_scale", out var ns)) noiseScale = ns.GetSingle();
            if (inf.TryGetProperty("length_scale", out var ls)) lengthScale = ls.GetSingle();
            if (inf.TryGetProperty("noise_w", out var nw)) noiseW = nw.GetSingle();
        }

        int numSpeakers = root.TryGetProperty("num_speakers", out var nsp) ? nsp.GetInt32() : 1;

        if (!root.TryGetProperty("phoneme_id_map", out var map) || map.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Piper voice config has no phoneme_id_map{where}");

        var ids = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var entry in map.EnumerateObject())
        {
            var list = new List<int>();
            foreach (var id in entry.Value.EnumerateArray()) list.Add(id.GetInt32());
            ids[entry.Name] = list.ToArray();
        }

        // The three the id assembly indexes unconditionally. Checked here so a
        // truncated or hand-edited map fails at load with the key named, rather
        // than as a KeyNotFoundException on the first press.
        foreach (var required in PiperPhonemes.RequiredSymbols)
            if (!ids.ContainsKey(required))
                throw new InvalidDataException(
                    $"Piper voice config's phoneme_id_map has no '{required}' entry{where}");

        string? quality = audio.ValueKind == JsonValueKind.Object
                          && audio.TryGetProperty("quality", out var q) ? q.GetString() : null;

        return new PiperVoiceConfig(sampleRate, espeak, numSpeakers,
            noiseScale, lengthScale, noiseW, ids, quality);
    }
}

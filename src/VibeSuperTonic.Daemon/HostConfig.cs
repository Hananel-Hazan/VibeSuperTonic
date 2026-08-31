using System.Text.Json;
using System.Text.Json.Nodes;
using VibeSuperTonic.Core.Settings;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The subset of <c>settings.json</c> the Linux daemon can honestly apply.
///
/// <para><b>Why a subset and not the engine's type.</b> <c>EngineSettings</c> is
/// <c>internal</c> to a <c>net10.0-windows</c> project and cannot be referenced
/// from here. Moving it into Core is the right end state and is explicitly
/// <i>not</i> this phase's job: it is a behaviour-affecting change to the
/// shipping platform at the seam that produced R-14, and the plan puts it in
/// "the Windows convergence", on its own branch with a TestHarness run either
/// side. So this is a deliberate, temporary duplication of the field names —
/// and the file is shared, so the names must match the engine's exactly.</para>
///
/// <para><b>Deliberately absent.</b> <c>UseDirectML</c>, <c>DirectMLDeviceId</c>,
/// <c>OnnxThreads</c> and <c>OnnxInterOpThreads</c> are not read and are never
/// written — there is no DirectML on Linux, and Phase 0 measured any manual
/// <c>OnnxThreads</c> value at roughly 2x worse than letting ORT choose,
/// including the setting that was harmless on Windows. <c>MaxCpuPercent</c>
/// below overrides that measurement deliberately, for a cost Phase 0 was not
/// measuring — see its own documentation.</para>
///
/// <para><b>DSP.</b> <c>DspRate</c>, <c>VolumeTrimDb</c> and
/// <c>RateClampCeiling</c> are read and applied — <see cref="SpeechRate"/>
/// splits the requested rate between the model and a pitch-preserving
/// time-stretch exactly as the Windows engine does, so the same settings.json
/// speaks at the same speed on both platforms. This was a real gap rather than
/// a theoretical one: the portable install on the development machine carries
/// <c>DspRate: 1.35</c>, and Linux was ignoring it.</para>
///
/// <para><b><c>InterChunkSilenceMs</c> is read as of this change</b>, and was
/// the last Windows key the daemon ignored. The Windows engine writes explicit
/// silence between chunks through the SAPI site; the Linux session now writes
/// the same gap through its own sink. It was left out originally because it
/// changes chunk timing and therefore boundary scheduling — which is exactly why
/// it landed before Phase 6 rather than during it, so the highlight is verified
/// once against final timing instead of twice.</para>
///
/// <para><b>Unknown keys survive a read/write cycle</b> — see
/// <see cref="Extra"/>. The daemon still never writes this file, but Phase 6's
/// Tune tab does, and the discipline has to exist before the writer does.</para>
/// </summary>
public sealed class LinuxSettings
{
    public string DefaultVoice { get; set; } = "M1";

    /// <summary>
    /// The engine-qualified default voice — <c>piper:de_DE-thorsten-high</c> or
    /// <c>supertonic:M1</c>. Supersedes <see cref="DefaultVoice"/> when present.
    ///
    /// <para><b>Why a second key rather than widening the first.</b> The Windows
    /// engine reads this same <c>settings.json</c> and knows nothing about Piper.
    /// It carries unknown keys through its <c>[JsonExtensionData]</c> untouched —
    /// landed one release before there was a writer, for exactly this — so a
    /// Linux user who picks a Piper voice keeps a file the Windows engine can
    /// still read, and it goes on speaking <c>DefaultVoice</c>. Widening the
    /// existing key would have meant Windows reading <c>piper:de_DE-thorsten-high</c>
    /// as a Supertonic style name and failing to find it.</para>
    ///
    /// <para>Empty means "not set", which is why it is not null: the file is
    /// hand-editable and a key someone blanked should mean the same as a key
    /// they removed.</para>
    /// </summary>
    public string VoiceId { get; set; } = "";
    public string Language { get; set; } = SupertonicLanguages.Default;
    public int TotalStep { get; set; } = 8;
    public float EngineSpeed { get; set; } = 1.05f;
    public float DspRate { get; set; } = 1.0f;
    public float VolumeTrimDb { get; set; } = 0f;
    public float RateClampCeiling { get; set; } = 1.3f;
    public float SynthesisSilenceSec { get; set; } = 0.3f;
    public int MaxChunkChars { get; set; } = 200;
    public int MinChunkChars { get; set; } = 100;

    /// <summary>
    /// Silence between one chunk and the next, milliseconds. Default 200,
    /// matching the Windows engine, so the same file paces both platforms alike.
    ///
    /// <para>Real audio in the stream rather than a scheduling hint, so it moves
    /// every boundary after it — <see cref="SpeechSession"/> counts it into the
    /// stream position before planning the next chunk. Zero removes the gap
    /// entirely and runs sentences together.</para>
    /// </summary>
    public int InterChunkSilenceMs { get; set; } = 200;

    /// <summary>
    /// Read CLIPBOARD when the selection is provably stale. Off by default.
    ///
    /// <para>For applications that render selectable text and never claim
    /// PRIMARY — Gmail's in-frame attachment viewer in Brave is the one this was
    /// found against — the clipboard is the only place the text can be reached
    /// without synthesising keystrokes. With this on, selecting inside such a
    /// viewer, pressing Ctrl+C and then the hotkey works.</para>
    ///
    /// <para>Off by default because X11 cannot distinguish "the application did
    /// not publish my selection" from "the user pressed the key twice on the
    /// same selection", and in the second case reading the clipboard is the
    /// wrong answer. See <see cref="VibeSuperTonic.Core.Selection.SelectionFreshness"/>.
    /// The explanatory notice is emitted either way.</para>
    /// </summary>
    public bool ClipboardFallback { get; set; }

    /// <summary>
    /// Percentage of logical processors inference may use. Default 20.
    ///
    /// <para>ORT's own choice is every core, which is why the desktop stutters
    /// during a read — reported from daily use on a 20-thread machine, where
    /// speed was never the constraint. The request that prompted this was "no
    /// more than 80%"; the measurement said 20% is both lighter and slightly
    /// faster, so the default satisfies the request with room to spare rather
    /// than sitting on it. 100 restores ORT's pick. See
    /// <see cref="VibeSuperTonic.Core.Synthesis.CpuBudget"/> for the numbers,
    /// which are worth reading before raising this: 40% measured as the slowest
    /// setting of all.</para>
    ///
    /// <para>Read at startup, not per utterance: ORT sizes its thread pool when
    /// the session is created, so changing this needs a daemon restart. The
    /// <c>reload</c> verb will not move it.</para>
    ///
    /// <para><b>Only consulted when there is no usable measurement.</b>
    /// <c>vst-ctl benchmark</c> writes <c>data/benchmark.json</c>, and a profile
    /// that still describes this machine wins over this percentage — see
    /// <see cref="VibeSuperTonic.Core.Synthesis.ExecutionDecision"/>. This
    /// remains the answer for a machine that has never been swept, and the
    /// fallback for one whose profile no longer applies. <c>vst-ctl config</c>
    /// reports which of the two is in force and why.</para>
    /// </summary>
    public int MaxCpuPercent { get; set; } = 20;

    /// <summary>
    /// Which execution provider to render on: <c>auto</c>, <c>cpu</c> or
    /// <c>gpu</c>. Default <c>auto</c>.
    ///
    /// <para><c>auto</c> means "what the benchmark chose, subject to the battery
    /// rule" — so on a machine that has never been swept, or one with no GPU
    /// provider pack installed, it means CPU. The two explicit values are a
    /// person overriding a measurement, which is allowed: someone permanently
    /// plugged into a dock can set <c>gpu</c> and mean it.</para>
    ///
    /// <para><b>It cannot conjure a GPU.</b> With no provider pack, no driver or
    /// no device, <c>gpu</c> still renders on the CPU — and <c>config</c> says
    /// which of those it was rather than reporting a setting back as if it were
    /// an outcome.</para>
    ///
    /// <para>Read per utterance, unlike <see cref="MaxCpuPercent"/>: the provider
    /// is bound to the ONNX session, and Phase 8b builds a new session between
    /// utterances precisely so this does not need a restart.</para>
    ///
    /// <para><b>Linux-only, and absent from the Windows engine's settings type.</b>
    /// It survives a round trip on Windows through that type's own extension-data
    /// dictionary, the same mechanism <see cref="Extra"/> provides here.</para>
    /// </summary>
    public string Provider { get; set; } = ProviderPreference.Auto;

    /// <summary>
    /// Use the GPU while running on battery. Off by default.
    ///
    /// <para><b>Requested explicitly, and it is the right default.</b> A discrete
    /// GPU on a laptop is the difference between a machine that lasts an
    /// afternoon and one that does not, and nothing is lost by switching: the CPU
    /// path renders about five times faster than real time, so the reading does
    /// not stutter — it only starts a little later.</para>
    ///
    /// <para>Checked at the start of an utterance and never during one. Changing
    /// provider means disposing the ONNX session and building another, roughly a
    /// second, and doing that mid-sentence to chase a power event the user did
    /// not notice would be indefensible.</para>
    /// </summary>
    public bool GpuOnBattery { get; set; }

    /// <summary>
    /// Every key this type does not declare, carried through unchanged.
    ///
    /// <para><b>Landed before the writer, deliberately.</b> The daemon only
    /// reads, so today this changes nothing — but Phase 6's Tune tab makes the
    /// UI the second writer of a file that crosses platforms, on the one medium
    /// where that matters: a portable folder on a USB stick, a dual-boot mount,
    /// a synced directory. Without this, the first Linux save erases
    /// <c>UseDirectML</c>, <c>DirectMLDeviceId</c>, <c>OnnxThreads</c>,
    /// <c>PerVoice</c> and anything a later Windows release adds — silently, with
    /// every declared property saving perfectly.</para>
    ///
    /// <para>This is mechanic 4 of the port plan, which the Windows side already
    /// carries on <c>EngineSettings</c> in both the engine and the launcher. The
    /// mechanism it depends on can break invisibly under an SDK bump —
    /// <c>[JsonExtensionData]</c> is not supported by the source generator's
    /// fast-serialization path, so the generator must fall back to metadata mode
    /// for any type declaring it — which is why
    /// <c>LinuxSettingsRoundTripTests</c> pins it rather than a comment
    /// mentioning it.</para>
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(LinuxSettings))]
[JsonSerializable(typeof(PronunciationsConfig))]
internal partial class HostConfigJsonContext : JsonSerializerContext { }

/// <summary>
/// The daemon's configuration: <c>settings.json</c> and
/// <c>pronunciations.json</c> from the portable data directory, reloaded when
/// they change.
///
/// <para><b>This is what made Linux apply no pronunciation rules at all.</b>
/// <see cref="SpeechSession"/> has threaded rules through
/// <see cref="SynthTextPipeline"/> since Phase 3 and nothing ever filled them
/// in, so the two platforms spoke the same text differently — and harness step
/// 10's cross-platform boundary agreement held only while the Windows side had
/// no rules either. That is the drift Core exists to prevent, arriving through
/// the one door Core does not cover.</para>
///
/// <para><b>Reload is both automatic and explicit.</b> The Windows engine
/// re-reads on mtime, once per Speak, and costs one <c>stat</c>; matching that
/// means an edit in the UI takes effect on the next utterance on both platforms
/// with no verb involved. The explicit <c>reload</c> verb exists on top of it
/// for scripts, for a UI that wants to force the point, and because "did it
/// pick up my change" should have an answer that is not "speak something and
/// listen".</para>
///
/// <para><b>The daemon does not write these files.</b> The Windows Control
/// Panel writes <c>settings.json</c> and the engine only reads it; Phase 6's
/// app is the same shape, which keeps exactly one writer and leaves the daemon
/// with no concurrency story to get wrong. That is also why there is no
/// <c>config set</c> verb — see the plan's Phase 4b notes.</para>
/// </summary>
public sealed class HostConfig
{
    private readonly object _gate = new();
    private float _synthSpeed = SpeechRate.DefaultEngineSpeed;
    private double _supertonicStretch = 1.0;
    private double _requestedRate = SpeechRate.DefaultEngineSpeed;
    private long _settingsMtime = long.MinValue;
    private long _pronMtime = long.MinValue;
    private bool _everLoaded;

    public HostConfig(string dataDir, string modelsRoot)
    {
        DataDir = dataDir;
        ModelsRoot = modelsRoot;
        PiperVoices = new PiperVoiceStore(modelsRoot);
        Settings = new LinuxSettings();
        Pronunciations = new PronunciationsConfig();
        Compiled = Array.Empty<Regex?>();
        SessionOptions = new SpeechSessionOptions();
    }

    public string DataDir { get; }
    public string ModelsRoot { get; }

    /// <summary>
    /// The Piper voices installed beside the Supertonic models. Held here
    /// because this is what turns a voice id into an engine, and the per-utterance
    /// options are built here.
    /// </summary>
    public PiperVoiceStore PiperVoices { get; }
    public string SettingsPath => LinuxDataPaths.SettingsFile(DataDir);
    public string PronunciationsPath => LinuxDataPaths.PronunciationsFile(DataDir);

    public LinuxSettings Settings { get; private set; }
    public PronunciationsConfig Pronunciations { get; private set; }
    public IReadOnlyList<Regex?> Compiled { get; private set; }

    /// <summary>
    /// Built from the two files, ready to hand to
    /// <see cref="SpeechSession.Options"/>. Rebuilt only when something
    /// changed, so the regexes are compiled once per edit rather than once per
    /// utterance — a regex compile inside the press-to-speech budget is exactly
    /// what Phase 4b was told not to do.
    /// </summary>
    public SpeechSessionOptions SessionOptions { get; private set; }

    /// <summary>True when the data directory exists and can be written to.</summary>
    public bool Writable { get; private set; }

    /// <summary>Non-fatal problems from the last load, for <c>config</c> to report.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Re-read if the files have changed, or unconditionally when
    /// <paramref name="force"/> is set.
    /// </summary>
    /// <returns>True if anything was actually re-read.</returns>
    public bool Reload(bool force = false)
    {
        long s = MtimeTicks(SettingsPath);
        long p = MtimeTicks(PronunciationsPath);

        lock (_gate)
        {
            if (!force && _everLoaded && s == _settingsMtime && p == _pronMtime)
                return false;

            var notes = new List<string>();

            Writable = LinuxDataPaths.TryEnsureWritable(DataDir);
            if (!Writable)
                notes.Add($"{DataDir} is not writable — settings load but cannot be saved.");

            Settings = LoadSettings(SettingsPath, notes);
            Pronunciations = LoadPronunciations(PronunciationsPath, notes);

            var compiled = new Regex?[Pronunciations.Rules.Count];
            for (int i = 0; i < Pronunciations.Rules.Count; i++)
            {
                compiled[i] = PronunciationsConfig.Compile(Pronunciations.Rules[i]);
                if (compiled[i] is null && Pronunciations.Rules[i].Enabled)
                    notes.Add($"pronunciation rule {i + 1} (\"{Pronunciations.Rules[i].Match}\") did not compile and is skipped.");
            }
            Compiled = compiled;

            // Chunk sizes are clamped by SentenceChunker at the point of use —
            // the two sliders' ranges overlap, so min > max has always been
            // selectable, and the settings-file import path range-checks
            // nothing at all.
            // The model takes what it can and the stretch absorbs the rest, so
            // both halves come from one computation and must not drift apart.
            var (synthSpeed, stretch) = SpeechRate.Compute(
                Settings.EngineSpeed, Settings.DspRate, Settings.RateClampCeiling);
            _synthSpeed = synthSpeed;
            _supertonicStretch = stretch;

            // The SAME number both engines are asked for, and the reason one
            // settings.json speaks at one speed whichever voice is selected.
            // Supertonic splits it between the model and the stretch because it
            // degrades past ~1.3; Piper gives the whole of it to length_scale
            // until the model saturates. See PiperRateCalibration.
            float engine = Settings.EngineSpeed > 0 ? Settings.EngineSpeed : SpeechRate.DefaultEngineSpeed;
            float dsp = Settings.DspRate > 0 ? Settings.DspRate : 1.0f;
            _requestedRate = engine * dsp;

            SessionOptions = new SpeechSessionOptions(
                Pronunciations: Pronunciations.Enabled ? Pronunciations : null,
                CompiledPronunciations: Pronunciations.Enabled ? Compiled : null,
                MaxChunkChars: Settings.MaxChunkChars,
                MinChunkChars: Settings.MinChunkChars,
                StretchFactor: stretch,
                VolumeScale: SpeechRate.VolumeScale(Settings.VolumeTrimDb),
                InterChunkSilenceMs: Math.Max(0, Settings.InterChunkSilenceMs));

            Notes = notes;
            _settingsMtime = s;
            _pronMtime = p;
            _everLoaded = true;
            return true;
        }
    }

    /// <summary>
    /// Everything one utterance needs that depends on which engine speaks it.
    /// </summary>
    /// <param name="Synthesis">For the backend.</param>
    /// <param name="StretchFactor">
    /// For the DSP stage, at the engine's own sample rate. On the Supertonic path
    /// this is the half of the rate the model cannot supply; on the Piper path it
    /// is 1.0 until the model saturates just under 2x, and the remainder past it.
    /// </param>
    /// <param name="Engine">"supertonic" or "piper", for the log and for status.</param>
    /// <param name="Note">
    /// Something the user should know about this utterance, or null. Today: the
    /// voice has not been calibrated yet, so its rate is approximate.
    /// </param>
    public readonly record struct UtterancePlan(
        SynthesisOptions Synthesis, double StretchFactor, string Engine, string? Note);

    /// <summary>
    /// Build one utterance's plan. A value from the request wins over the file,
    /// which wins over the built-in default — an explicit ask is always more
    /// specific than a stored preference.
    ///
    /// <para><b>The voice chooses the engine.</b> There is no Engine setting:
    /// a voice belongs to exactly one engine, so a setting that could disagree
    /// with the voice is a setting that eventually will.</para>
    /// </summary>
    /// <summary>The raw settings object, for resolving scoped overrides.</summary>
    private JsonObject? _rawSettings;

    /// <summary>Merged settings per scope key, cleared whenever the file reloads.</summary>
    private readonly Dictionary<string, LinuxSettings> _scoped = new(StringComparer.Ordinal);

    /// <summary>
    /// The settings that apply to one voice: the file's own values, with this
    /// engine's overrides on top, with this voice's on top of those.
    ///
    /// <para>Returns the global settings unchanged when nothing is overridden,
    /// which is every install until somebody uses the scope selector — so the
    /// common path allocates nothing and behaves exactly as it did.</para>
    /// </summary>
    /// <summary>
    /// The diffusion steps THIS VOICE runs at, which is not always the file's
    /// top-level number.
    ///
    /// <para><b>Reported 2026-08-30, and it made the benchmark measure a
    /// configuration nothing uses.</b> Scoped settings arrived in 0.2.11, so
    /// <c>TotalStep</c> can be set per engine or per voice — and four places
    /// went on reading the global one: what <c>config</c> reports, what the
    /// benchmark records, what the staleness guard re-tests, and what the
    /// startup decision is made from. On an install with
    /// <c>PerEngine.supertonic.TotalStep 6</c> under a global 12, every
    /// utterance ran at 6 while all four of those said 12 — and the guard whose
    /// whole job is to notice "the measurement no longer describes this daemon"
    /// compared 12 against 12 and reported everything was fine.</para>
    ///
    /// <para>Synthesis has always used the scoped value
    /// (<see cref="Utterance"/>); this is the same answer for everything that
    /// only wanted the number.</para>
    /// </summary>
    public int TotalStepFor(string? voice) => SettingsFor(voice).TotalStep;

    public LinuxSettings SettingsFor(string? voice)
    {
        if (_rawSettings is null) return Settings;

        var id = VoiceId.Parse(voice ?? ConfiguredVoice);
        string engine = id.MayBePiper && PiperVoices.Config(id.Bare) is not null ? "piper"
            : id.Engine == VoiceEngine.Piper ? "piper" : "supertonic";
        string key = SettingsScope.KeyFor(id);

        if (!SettingsScope.Has(_rawSettings, SettingsScopeKind.Engine, engine, key)
            && !SettingsScope.Has(_rawSettings, SettingsScopeKind.Voice, engine, key))
        {
            return Settings;
        }

        string cacheKey = engine + "|" + key;
        lock (_scoped)
        {
            if (_scoped.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var merged = SettingsScope.Merge(_rawSettings, engine, key);

        LinuxSettings resolved;
        try
        {
            resolved = merged.Deserialize(HostConfigJsonContext.Default.LinuxSettings) ?? Settings;
        }
        catch (JsonException)
        {
            // A hand-edited override that does not parse must not take the voice
            // down with it: the global settings still speak.
            resolved = Settings;
        }

        lock (_scoped) _scoped[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// The session options for one voice — chunking, volume and gaps — resolved
    /// through the same scope rules, with that voice's own stretch factor.
    /// </summary>
    public SpeechSessionOptions SessionOptionsFor(string? voice)
    {
        var s = SettingsFor(voice);
        if (ReferenceEquals(s, Settings)) return SessionOptions;

        var (_, stretch) = SpeechRate.Compute(s.EngineSpeed, s.DspRate, s.RateClampCeiling);
        return SessionOptions with
        {
            MaxChunkChars = s.MaxChunkChars,
            MinChunkChars = s.MinChunkChars,
            StretchFactor = stretch,
            VolumeScale = SpeechRate.VolumeScale(s.VolumeTrimDb),
            InterChunkSilenceMs = Math.Max(0, s.InterChunkSilenceMs),
        };
    }

    public UtterancePlan Utterance(string? voice, string? language)
    {
        // The one place the qualified form is unwrapped. Everything downstream —
        // the options record, the store, the calibration file, the engine router
        // — works in bare ids, because a bare id is what names a directory.
        var requested = VoiceId.Parse(voice ?? ConfiguredVoice);
        string voiceId = requested.Bare;

        // THE SETTINGS THIS VOICE ACTUALLY RUNS ON. Identical to the global ones
        // unless somebody has scoped something to this engine or this voice, and
        // reference-equal in that case, so the rate below is the same arithmetic
        // it always was for every install that has no overrides.
        var scoped = SettingsFor(voice);
        double rate = ReferenceEquals(scoped, Settings)
            ? _requestedRate
            : (scoped.EngineSpeed > 0 ? scoped.EngineSpeed : SpeechRate.DefaultEngineSpeed)
              * (scoped.DspRate > 0 ? scoped.DspRate : 1.0f);

        if (requested.MayBePiper && PiperVoices.Config(voiceId) is { } piperVoice)
        {
            var calibration = PiperVoices.Calibration(voiceId);
            var plan = calibration?.Plan(rate)
                       ?? PiperRateCalibration.Reciprocal(rate, piperVoice.LengthScale);

            var options = new PiperOptions(
                voiceId,
                plan.LengthScale,
                piperVoice.NoiseScale,
                piperVoice.NoiseW,
                // Clamped by PiperSynthesizer against the voice's own count, so
                // an id carrying #12 for a voice that has since been replaced by
                // a single-speaker one renders speaker 0 rather than throwing.
                SpeakerId: requested.Speaker ?? 0,
                SilenceSeconds: scoped.SynthesisSilenceSec);

            return new UtterancePlan(options, plan.StretchFactor, "piper",
                calibration is null && Math.Abs(rate - 1.0) > 0.01
                    ? $"'{voiceId}' has not been measured yet, so {rate:F2}x is approximate " +
                      "— the daemon is measuring it now and the next reading will be exact"
                    : null);
        }

        if (ReferenceEquals(scoped, Settings))
            return new UtterancePlan(Synthesis(voiceId, language), _supertonicStretch, "supertonic", null);

        var (synthSpeed, stretch) = SpeechRate.Compute(
            scoped.EngineSpeed, scoped.DspRate, scoped.RateClampCeiling);

        return new UtterancePlan(
            new SupertonicOptions(
                voiceId,
                SupertonicLanguages.Normalize(language ?? scoped.Language),
                scoped.TotalStep,
                synthSpeed,
                scoped.SynthesisSilenceSec),
            stretch, "supertonic", null);
    }

    /// <summary>
    /// The configured default voice, as written — the engine-qualified
    /// <c>VoiceId</c> when the file sets one, otherwise <c>DefaultVoice</c>.
    ///
    /// <para>Two keys and one answer, so that everything reading "the default
    /// voice" reads the same thing. A blank <c>VoiceId</c> means unset rather
    /// than empty: the file is hand-edited, and a key someone cleared should
    /// behave like a key they deleted.</para>
    /// </summary>
    public string ConfiguredVoice =>
        string.IsNullOrWhiteSpace(Settings.VoiceId) ? Settings.DefaultVoice : Settings.VoiceId.Trim();

    /// <summary>
    /// The Supertonic half of <see cref="Utterance"/>, kept separate because the
    /// benchmark sweep wants exactly this and has no use for a stretch factor.
    /// </summary>
    public SynthesisOptions Synthesis(string? voice, string? language) =>
        new SupertonicOptions(
            // Bare, for the same reason Utterance unwraps: a Supertonic style
            // names models/voice_styles/<style>.json, and "supertonic:M1" is not
            // a filename.
            VoiceId.Parse(voice ?? ConfiguredVoice).Bare,
            SupertonicLanguages.Normalize(language ?? Settings.Language),
            Settings.TotalStep,
            // The CLAMPED speed, not EngineSpeed: the model is only well behaved
            // in roughly [0.9, ceiling], and whatever was asked for beyond that
            // is the stretch's job. Handing the raw value here would ask the
            // model for a rate it degrades at and then stretch it again.
            _synthSpeed,
            Settings.SynthesisSilenceSec);

    private static long MtimeTicks(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : long.MinValue; }
        catch { return long.MinValue; }
    }

    private LinuxSettings LoadSettings(string path, List<string> notes)
    {
        _rawSettings = null;
        _scoped.Clear();

        if (!File.Exists(path)) return new LinuxSettings();
        try
        {
            // FileShare.ReadWrite | Delete: the UI writes this file with a
            // tmp+rename, and a reader that locks it would make the UI's save
            // fail intermittently.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            // Read the file ONCE, as a node, and deserialize from that. The
            // typed view is what the daemon runs on; the node is what per-engine
            // and per-voice overrides are merged on, because "is this key set"
            // is a question only the raw JSON can answer. A typed override would
            // have to say it with sentinels — 0 means unset — which cannot
            // express VolumeTrimDb 0, and that is a real setting.
            _rawSettings = JsonNode.Parse(fs, nodeOptions: default,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;

            if (_rawSettings is null) return new LinuxSettings();
            return _rawSettings.Deserialize(HostConfigJsonContext.Default.LinuxSettings)
                   ?? new LinuxSettings();
        }
        catch (Exception ex)
        {
            // Defaults, and say so. Silently falling back would present as
            // "my settings stopped applying" with nothing anywhere to explain it.
            notes.Add($"could not read {path}: {ex.Message} — using defaults.");
            return new LinuxSettings();
        }
    }

    private static PronunciationsConfig LoadPronunciations(string path, List<string> notes)
    {
        if (!File.Exists(path)) return new PronunciationsConfig();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, HostConfigJsonContext.Default.PronunciationsConfig)
                   ?? new PronunciationsConfig();
        }
        catch (Exception ex)
        {
            notes.Add($"could not read {path}: {ex.Message} — no pronunciation rules applied.");
            return new PronunciationsConfig();
        }
    }
}

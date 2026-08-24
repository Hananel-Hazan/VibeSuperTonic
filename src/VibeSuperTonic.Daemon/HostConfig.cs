using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
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
    private long _settingsMtime = long.MinValue;
    private long _pronMtime = long.MinValue;
    private bool _everLoaded;

    public HostConfig(string dataDir, string modelsRoot)
    {
        DataDir = dataDir;
        ModelsRoot = modelsRoot;
        Settings = new LinuxSettings();
        Pronunciations = new PronunciationsConfig();
        Compiled = Array.Empty<Regex?>();
        SessionOptions = new SpeechSessionOptions();
    }

    public string DataDir { get; }
    public string ModelsRoot { get; }
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
    /// Per-utterance synthesis options. A value from the request wins over the
    /// file, which wins over the built-in default — an explicit ask is always
    /// more specific than a stored preference.
    /// </summary>
    public SynthesisOptions Synthesis(string? voice, string? language) =>
        new(voice ?? Settings.DefaultVoice,
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

    private static LinuxSettings LoadSettings(string path, List<string> notes)
    {
        if (!File.Exists(path)) return new LinuxSettings();
        try
        {
            // FileShare.ReadWrite | Delete: the UI writes this file with a
            // tmp+rename, and a reader that locks it would make the UI's save
            // fail intermittently.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, HostConfigJsonContext.Default.LinuxSettings)
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

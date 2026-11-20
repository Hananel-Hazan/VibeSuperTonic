using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// Engine-side snapshot of the per-process knobs. Persisted by the Control
/// Panel into <see cref="DataPaths.SettingsFilePath"/> (a JSON file under the
/// portable data root). The launcher writes; the engine reads. Cached by file
/// mtime — every <see cref="EngineSettingsCache.Resolve"/> call does one
/// <c>FileInfo.LastWriteTimeUtc</c> stat (microseconds), and reparses only
/// when the timestamp moves.
///
/// Pre-data-folder installs kept these values in
/// <c>HKCU\SOFTWARE\VibeSuperTonic\Settings</c> with a <c>Version</c> DWORD as
/// the cache key. The reload path falls back to that registry layout when
/// <c>settings.json</c> is missing — covers the window after an upgrade but
/// before the launcher has run its migration.
/// </summary>
internal sealed class EngineSettings
{
    public int    TotalStep           { get; set; } = 8;
    /// <summary>
    /// Supertonic language code used when the SAPI client doesn't tag the text
    /// with a language of its own. A fragment carrying an <c>xml:lang</c> (which
    /// reaches us as <c>SPVSTATE.LangID</c>) overrides this per fragment.
    /// </summary>
    public string Language            { get; set; } = SupertonicLanguages.Default;
    public float  EngineSpeed         { get; set; } = 1.05f;
    public float  DspRate             { get; set; } = 1.0f;
    public string DefaultVoice        { get; set; } = "M1";
    public float  VolumeTrimDb        { get; set; } = 0f;
    public int    MaxChunkChars       { get; set; } = 200;
    public int    MinChunkChars       { get; set; } = 100;
    public int    InterChunkSilenceMs { get; set; } = 200;
    public float  SynthesisSilenceSec { get; set; } = 0.3f;
    public float  RateClampCeiling    { get; set; } = 1.3f;
    public int    OnnxThreads         { get; set; } = 0;
    public int    OnnxInterOpThreads  { get; set; } = 1;
    public bool   UseDirectML         { get; set; } = true;
    /// <summary>
    /// Keep DirectML on battery. Off by default: the CPU path runs several times
    /// faster than real time, and a GPU on a laptop is the difference between an
    /// afternoon and a lunch break. Global only — it is not a per-voice knob, so
    /// <see cref="ResolveFor"/> does not carry it. Same key as the Linux daemon's.
    /// </summary>
    public bool   GpuOnBattery        { get; set; } = false;
    public int    DirectMLDeviceId    { get; set; } = 0;
    /// <summary>
    /// Silence appended after each utterance, in ms. Negative (the default)
    /// scales it to the utterance, 150–700 ms; see <c>SapiEngine.TailPadMs</c>.
    /// Global only. Review item 51.
    /// </summary>
    public int    TailSilenceMs       { get; set; } = -1;
    public Dictionary<string, VoiceOverride> PerVoice { get; set; } = new();

    /// <summary>
    /// Keys this build does not know about, carried through a read/write cycle
    /// unchanged.
    ///
    /// Without it, the deserializer drops unrecognised properties and whichever
    /// host saves last erases everything the other one added — the data folder is
    /// portable by design, so a USB stick, a dual-boot mount or a synced
    /// directory puts the Windows Control Panel and the Linux daemon on the same
    /// <c>settings.json</c>. It also protects a downgrade: an older build no
    /// longer silently strips a newer one's settings.
    ///
    /// <see cref="PerVoice"/> nests this same type, so per-voice overrides
    /// inherit the behaviour.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Per-voice resolution: <c>PerVoice[id].Knob</c> if set, else <c>this.Knob</c>.</summary>
    public EngineSettings ResolveFor(string voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return this;
        if (!PerVoice.TryGetValue(voiceId, out var pv)) return this;
        return new EngineSettings
        {
            TotalStep           = pv.TotalStep != 0 ? pv.TotalStep : TotalStep,
            Language            = !string.IsNullOrWhiteSpace(pv.Language) ? pv.Language : Language,
            EngineSpeed         = pv.EngineSpeed > 0 ? pv.EngineSpeed : EngineSpeed,
            DspRate             = pv.DspRate > 0 ? pv.DspRate : DspRate,
            DefaultVoice        = pv.DefaultVoice ?? DefaultVoice,
            VolumeTrimDb        = pv.VolumeTrimDb,
            // Tier B: the voice's value only when it names one. A voice entry
            // used to be a whole EngineSettings, whose initialisers supplied
            // MaxChunkChars = 200 for a key the file did not have — so every
            // voice with a Tune-tab preset pinned the Advanced tab's knobs to
            // their defaults (review item 32).
            MaxChunkChars       = pv.MaxChunkChars is > 0 and var mx ? mx : MaxChunkChars,
            MinChunkChars       = pv.MinChunkChars is > 0 and var mn ? mn : MinChunkChars,
            InterChunkSilenceMs = pv.InterChunkSilenceMs is >= 0 and var ic ? ic : InterChunkSilenceMs,
            SynthesisSilenceSec = pv.SynthesisSilenceSec is >= 0 and var ss ? ss : SynthesisSilenceSec,
            RateClampCeiling    = pv.RateClampCeiling is > 0 and var rc ? rc : RateClampCeiling,
            OnnxThreads         = pv.OnnxThreads ?? OnnxThreads,
            OnnxInterOpThreads  = pv.OnnxInterOpThreads is > 0 and var io ? io : OnnxInterOpThreads,
            TailSilenceMs       = TailSilenceMs,
        };
    }
}

/// <summary>
/// One voice's entry under <c>PerVoice</c>. The Tune-tab (Tier A) knobs keep
/// the shapes they always had; the Advanced-tab (Tier B) knobs are nullable,
/// because a voice that does not name one has not chosen one, and a value type
/// cannot say so. The launcher writes the same shape.
/// </summary>
internal sealed class VoiceOverride
{
    public int     TotalStep           { get; set; }
    public string? Language            { get; set; }
    public float   EngineSpeed         { get; set; }
    public float   DspRate             { get; set; }
    public string? DefaultVoice        { get; set; }
    public float   VolumeTrimDb        { get; set; }

    public int?    MaxChunkChars       { get; set; }
    public int?    MinChunkChars       { get; set; }
    public int?    InterChunkSilenceMs { get; set; }
    public float?  SynthesisSilenceSec { get; set; }
    public float?  RateClampCeiling    { get; set; }
    public int?    OnnxThreads         { get; set; }
    public int?    OnnxInterOpThreads  { get; set; }

    /// <summary>Carried through unchanged; see <see cref="EngineSettings.Extra"/>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// A pre-portable registry entry. Its Tier B values are dropped: the Tune
    /// tab that wrote them copied the globals into every voice, so they record
    /// nothing the user chose for that voice.
    /// </summary>
    public static VoiceOverride FromLegacy(EngineSettings s) => new()
    {
        TotalStep = s.TotalStep, Language = s.Language, EngineSpeed = s.EngineSpeed,
        DspRate = s.DspRate, DefaultVoice = s.DefaultVoice, VolumeTrimDb = s.VolumeTrimDb,
    };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EngineSettings))]
internal partial class EngineSettingsJsonContext : JsonSerializerContext { }

internal static class EngineSettingsCache
{
    private const string LegacyRegRoot = @"SOFTWARE\VibeSuperTonic\Settings";
    private const string DefaultSub    = "Default";
    private const string PerVoiceSub   = "PerVoice";

    private static readonly object _gate = new();
    // Stored as Ticks (long, 8 bytes) instead of DateTime to avoid torn reads
    // on x86 — DateTime is a struct of two 4-byte fields and isn't atomic on
    // 32-bit hosts. Engine x86 build runs inside 32-bit SAPI clients (Lingoes,
    // Balabolka 32-bit, …); a torn read would compare bogus halves and produce
    // either spurious reloads or stale cache. <c>Volatile.Read</c> + 64-bit
    // value gives us atomic semantics on both bitnesses.
    private static long _cachedMtimeTicks = DateTime.MinValue.Ticks;
    private static EngineSettings _cached = new();
    private static bool _everLoaded;

    /// <summary>
    /// Returns the latest settings, reloading only when <c>settings.json</c>'s
    /// mtime has moved (or on first call).
    /// </summary>
    public static EngineSettings Resolve()
    {
        string path = DataPaths.SettingsFilePath;
        long mtimeTicks;
        try { mtimeTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : DateTime.MinValue.Ticks; }
        catch { mtimeTicks = DateTime.MinValue.Ticks; }

        // Volatile.Read so the JIT can't hoist the comparison above a concurrent
        // write inside the lock. Once everLoaded flips to true under lock, every
        // subsequent reader sees a coherent (mtime, cached) pair.
        if (Volatile.Read(ref _everLoaded) && Volatile.Read(ref _cachedMtimeTicks) == mtimeTicks)
            return _cached;
        lock (_gate)
        {
            if (_everLoaded && _cachedMtimeTicks == mtimeTicks) return _cached;
            var loaded = TryLoadFile(path);
            if (loaded is null && _everLoaded && _loadedFromFile)
            {
                // A file this process read successfully is now missing or
                // unreadable: half-written, hand-edited into invalid JSON, or
                // briefly absent while a writer replaces it. Keep the settings in
                // force rather than dropping every voice to the built-in defaults
                // mid-session (review item 66). The mtime is remembered so the
                // broken file is not re-parsed on every Speak; the next write
                // moves it and is read normally.
                _cachedMtimeTicks = mtimeTicks;
                return _cached;
            }
            _cached = loaded ?? ReadFromLegacyRegistry();
            _loadedFromFile = loaded is not null;
            _cachedMtimeTicks = mtimeTicks;
            _everLoaded = true;
            return _cached;
        }
    }

    /// <summary>Whether <see cref="_cached"/> came from settings.json rather than the legacy registry or defaults.</summary>
    private static bool _loadedFromFile;

    /// <summary>The parsed file, or null when it is absent or does not parse.</summary>
    private static EngineSettings? TryLoadFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var parsed = JsonSerializer.Deserialize(fs, EngineSettingsJsonContext.Default.EngineSettings);
            if (parsed is not null) return parsed;
        }
        catch { /* treated as absent */ }
        return null;
    }

    /// <summary>
    /// Read the pre-portable layout from <c>HKCU\SOFTWARE\VibeSuperTonic\Settings</c>.
    /// Engaged only when the JSON file is missing — used during the upgrade
    /// window before the launcher has run its registry → file migration.
    /// </summary>
    private static EngineSettings ReadFromLegacyRegistry()
    {
        var s = new EngineSettings();
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(LegacyRegRoot);
            if (settings is null) return s;
            using (var def = settings.OpenSubKey(DefaultSub))
                if (def is not null) ReadInto(s, def);
            using var pv = settings.OpenSubKey(PerVoiceSub);
            if (pv is not null)
            {
                foreach (var name in pv.GetSubKeyNames())
                {
                    using var voiceKey = pv.OpenSubKey(name);
                    if (voiceKey is null) continue;
                    var perVoice = new EngineSettings();
                    ReadInto(perVoice, voiceKey);
                    s.PerVoice[name] = VoiceOverride.FromLegacy(perVoice);
                }
            }
        }
        catch { /* fall through with defaults */ }
        return s;
    }

    private static void ReadInto(EngineSettings s, RegistryKey k)
    {
        // No Language here on purpose: this reader only serves pre-portable
        // installs (settings.json missing), and the registry layout predates
        // multilingual support — the value can never be present.
        if (k.GetValue("TotalStep")           is int    ts)  s.TotalStep = ts;
        if (k.GetValue("EngineSpeed")         is string es)  s.EngineSpeed = ParseFloat(es, s.EngineSpeed);
        if (k.GetValue("DspRate")             is string dr)  s.DspRate = ParseFloat(dr, s.DspRate);
        if (k.GetValue("DefaultVoice")        is string dv)  s.DefaultVoice = dv;
        if (k.GetValue("VolumeTrimDb")        is string vt)  s.VolumeTrimDb = ParseFloat(vt, s.VolumeTrimDb);
        if (k.GetValue("MaxChunkChars")       is int    mxc) s.MaxChunkChars = mxc;
        if (k.GetValue("MinChunkChars")       is int    mnc) s.MinChunkChars = mnc;
        if (k.GetValue("InterChunkSilenceMs") is int    ics) s.InterChunkSilenceMs = ics;
        if (k.GetValue("SynthesisSilenceSec") is string sss) s.SynthesisSilenceSec = ParseFloat(sss, s.SynthesisSilenceSec);
        if (k.GetValue("RateClampCeiling")    is string rcc) s.RateClampCeiling = ParseFloat(rcc, s.RateClampCeiling);
        if (k.GetValue("OnnxThreads")         is int    ot)  s.OnnxThreads = ot;
        if (k.GetValue("OnnxInterOpThreads")  is int    iot) s.OnnxInterOpThreads = iot;
        if (k.GetValue("UseDirectML")         is int    udm) s.UseDirectML = udm != 0;
        if (k.GetValue("DirectMLDeviceId")    is int    did) s.DirectMLDeviceId = did;
    }

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

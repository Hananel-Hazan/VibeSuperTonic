using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

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
    public int    DirectMLDeviceId    { get; set; } = 0;
    public int    VocoderMode         { get; set; } = 3;
    public Dictionary<string, EngineSettings> PerVoice { get; set; } = new();

    /// <summary>Per-voice resolution: <c>PerVoice[id].Knob</c> if set, else <c>this.Knob</c>.</summary>
    public EngineSettings ResolveFor(string voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return this;
        if (!PerVoice.TryGetValue(voiceId, out var pv)) return this;
        return new EngineSettings
        {
            TotalStep           = pv.TotalStep != 0 ? pv.TotalStep : TotalStep,
            EngineSpeed         = pv.EngineSpeed > 0 ? pv.EngineSpeed : EngineSpeed,
            DspRate             = pv.DspRate > 0 ? pv.DspRate : DspRate,
            DefaultVoice        = pv.DefaultVoice ?? DefaultVoice,
            VolumeTrimDb        = pv.VolumeTrimDb,
            MaxChunkChars       = pv.MaxChunkChars > 0 ? pv.MaxChunkChars : MaxChunkChars,
            MinChunkChars       = pv.MinChunkChars > 0 ? pv.MinChunkChars : MinChunkChars,
            InterChunkSilenceMs = pv.InterChunkSilenceMs >= 0 ? pv.InterChunkSilenceMs : InterChunkSilenceMs,
            SynthesisSilenceSec = pv.SynthesisSilenceSec >= 0 ? pv.SynthesisSilenceSec : SynthesisSilenceSec,
            RateClampCeiling    = pv.RateClampCeiling > 0 ? pv.RateClampCeiling : RateClampCeiling,
            OnnxThreads         = pv.OnnxThreads,
            OnnxInterOpThreads  = pv.OnnxInterOpThreads > 0 ? pv.OnnxInterOpThreads : OnnxInterOpThreads,
        };
    }
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
            _cached = LoadFromFileOrLegacyRegistry(path);
            _cachedMtimeTicks = mtimeTicks;
            _everLoaded = true;
            return _cached;
        }
    }

    private static EngineSettings LoadFromFileOrLegacyRegistry(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var parsed = JsonSerializer.Deserialize(fs, EngineSettingsJsonContext.Default.EngineSettings);
                if (parsed is not null) return parsed;
            }
            catch { /* fall through to registry fallback */ }
        }
        return ReadFromLegacyRegistry();
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
                    s.PerVoice[name] = perVoice;
                }
            }
        }
        catch { /* fall through with defaults */ }
        return s;
    }

    private static void ReadInto(EngineSettings s, RegistryKey k)
    {
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
        if (k.GetValue("VocoderMode")         is int    vm)  s.VocoderMode = vm;
    }

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

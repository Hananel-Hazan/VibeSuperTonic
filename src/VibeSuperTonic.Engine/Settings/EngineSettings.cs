using System.Globalization;
using Microsoft.Win32;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// Engine-side snapshot of the per-process knobs the Control Panel writes to
/// HKCU\SOFTWARE\VibeSuperTonic\Settings. Cached by Settings\Version DWORD —
/// every call to <see cref="Resolve"/> reads only Version (microseconds);
/// the heavy reload runs only when the counter has moved.
/// </summary>
internal sealed class EngineSettings
{
    public int    TotalStep           { get; set; } = 8;        // legacy default until UI writes settings
    public float  EngineSpeed         { get; set; } = 1.05f;
    public float  DspRate             { get; set; } = 1.0f;
    public string DefaultVoice        { get; set; } = "M1";
    public float  VolumeTrimDb        { get; set; } = 0f;
    public int    MaxChunkChars       { get; set; } = 200;
    public int    MinChunkChars       { get; set; } = 100;
    public int    InterChunkSilenceMs { get; set; } = 200;
    public float  SynthesisSilenceSec { get; set; } = 0.3f;
    public float  RateClampCeiling    { get; set; } = 1.3f;
    public int    OnnxThreads         { get; set; } = 0; // intra-op threads (per ORT op)
    public int    OnnxInterOpThreads  { get; set; } = 1; // inter-op threads (across ORT ops)
    public bool   UseDirectML         { get; set; } = true;  // GPU default; engine falls back to CPU if DML init fails
    public int    DirectMLDeviceId    { get; set; } = 0;
    public int    VocoderMode         { get; set; } = 3;     // peak radius 3 — chosen after A/B testing
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

internal static class EngineSettingsCache
{
    private const string Root        = @"SOFTWARE\VibeSuperTonic\Settings";
    private const string DefaultSub  = "Default";
    private const string PerVoiceSub = "PerVoice";

    private static readonly object _gate = new();
    private static int _cachedVersion = -1;
    private static EngineSettings _cached = new();

    /// <summary>Get the latest settings, reloading only when Version DWORD has bumped.</summary>
    public static EngineSettings Resolve()
    {
        int version = ReadVersion();
        if (version == _cachedVersion) return _cached;
        lock (_gate)
        {
            if (version == _cachedVersion) return _cached;
            _cached = Reload();
            _cachedVersion = version;
            return _cached;
        }
    }

    public static int CurrentVersion() => ReadVersion();

    private static int ReadVersion()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Root);
            return k?.GetValue("Version") is int v ? v : 0;
        }
        catch { return 0; }
    }

    private static EngineSettings Reload()
    {
        var s = new EngineSettings();
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(Root);
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

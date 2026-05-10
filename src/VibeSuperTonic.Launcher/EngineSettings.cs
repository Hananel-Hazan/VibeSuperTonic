using System.Globalization;
using Microsoft.Win32;

namespace VibeSuperTonic.Launcher;

internal enum QualityPreset { Draft, Balanced, Quality, HiFi, Custom }

/// <summary>
/// Snapshot of all engine knobs. The Control Panel writes these to the registry
/// under HKCU\SOFTWARE\VibeSuperTonic\Settings\... and bumps Settings\Version.
/// The engine reads the same keys and caches by Version.
/// </summary>
internal sealed class EngineSettings
{
    public QualityPreset Preset { get; set; } = QualityPreset.Balanced;
    public int TotalStep { get; set; } = 6;
    public float EngineSpeed { get; set; } = 1.05f;
    public float DspRate { get; set; } = 1.0f;
    public string DefaultVoice { get; set; } = "M1";
    public float VolumeTrimDb { get; set; } = 0f;

    public int MaxChunkChars { get; set; } = 200;
    public int MinChunkChars { get; set; } = 100;
    public int InterChunkSilenceMs { get; set; } = 200;
    public float SynthesisSilenceSec { get; set; } = 0.3f;
    public float RateClampCeiling { get; set; } = 1.3f;
    public int OnnxThreads { get; set; } = 0;         // 0 = ORT auto (intra-op)
    public int OnnxInterOpThreads { get; set; } = 1;  // ORT inter-op (across ops)
    public bool UseDirectML { get; set; } = true;     // GPU on by default; engine falls back to CPU if init fails
    public int DirectMLDeviceId { get; set; } = 0;    // 0 = primary GPU
    public int VocoderMode { get; set; } = 3;         // peak radius 3 (chosen after A/B testing)

    public Dictionary<string, EngineSettings> PerVoice { get; set; } = new();

    /// <summary>
    /// Apply a preset's defaults. Presets only change <see cref="TotalStep"/>
    /// (the diffusion-iteration knob — the only thing that defines "quality"),
    /// not <see cref="EngineSpeed"/>. Speed is orthogonal: all presets render
    /// at 1.0x by default and the user can adjust speed independently with the
    /// Engine speed slider. Earlier builds nudged speed up at lower quality
    /// to mask audible artifacts; that was confusing — removed.
    /// </summary>
    public void ApplyPreset(QualityPreset p)
    {
        Preset = p;
        switch (p)
        {
            case QualityPreset.Draft:    TotalStep = 4;  EngineSpeed = 1.00f; DspRate = 1.0f; break;
            case QualityPreset.Balanced: TotalStep = 6;  EngineSpeed = 1.00f; DspRate = 1.0f; break;
            case QualityPreset.Quality:  TotalStep = 8;  EngineSpeed = 1.00f; DspRate = 1.0f; break;
            case QualityPreset.HiFi:     TotalStep = 12; EngineSpeed = 1.00f; DspRate = 1.0f; break;
            case QualityPreset.Custom:   /* leave existing values */ break;
        }
    }

    public EngineSettings Clone()
    {
        var c = (EngineSettings)MemberwiseClone();
        c.PerVoice = PerVoice.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        return c;
    }
}

internal static class EngineSettingsRegistry
{
    private const string Root = @"SOFTWARE\VibeSuperTonic\Settings";
    private const string DefaultSub = "Default";
    private const string PerVoiceSub = "PerVoice";

    public static EngineSettings Load()
    {
        var s = new EngineSettings();
        using var settings = Registry.CurrentUser.OpenSubKey(Root);
        if (settings is null) return s; // first run — built-in defaults

        using var def = settings.OpenSubKey(DefaultSub);
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
        return s;
    }

    public static void Save(EngineSettings s)
    {
        // Auto-clean: drop any per-voice entries that exactly match the global default.
        // Without this, a user who clicks Save while in "Per voice: X" mode (without
        // having changed any knob) creates a sticky snapshot — and a later global
        // change (e.g. DspRate 1.0 → 1.4) gets shadowed by that voice's stale 1.0
        // override, with no visible cause. Auto-cleaning keeps "no override" the
        // default state.
        var prunedPerVoice = new Dictionary<string, EngineSettings>();
        foreach (var kv in s.PerVoice)
        {
            if (!IsEffectivelyGlobal(kv.Value, s)) prunedPerVoice[kv.Key] = kv.Value;
        }
        s.PerVoice = prunedPerVoice;

        // Snapshot existing PerVoice in case the write fails partway and we need to
        // leave the registry in a recoverable state.
        var existingPerVoice = new EngineSettings();
        existingPerVoice.PerVoice = Load().PerVoice;

        using var settings = Registry.CurrentUser.CreateSubKey(Root, writable: true);

        try
        {
            using (var def = settings.CreateSubKey(DefaultSub, writable: true))
                WriteFrom(s, def);

            Registry.CurrentUser.DeleteSubKeyTree($@"{Root}\{PerVoiceSub}", throwOnMissingSubKey: false);
            if (s.PerVoice.Count > 0)
            {
                using var pv = settings.CreateSubKey(PerVoiceSub, writable: true);
                foreach (var kv in s.PerVoice)
                {
                    using var voiceKey = pv.CreateSubKey(kv.Key, writable: true);
                    WriteFrom(kv.Value, voiceKey);
                }
            }
            BumpVersion();
        }
        catch
        {
            // Best-effort restore of PerVoice if we crashed between the delete and re-write.
            try
            {
                using var pv = settings.CreateSubKey(PerVoiceSub, writable: true);
                foreach (var kv in existingPerVoice.PerVoice)
                {
                    using var voiceKey = pv.CreateSubKey(kv.Key, writable: true);
                    WriteFrom(kv.Value, voiceKey);
                }
            }
            catch { /* nothing else we can do */ }
            throw;
        }
    }

    /// <summary>
    /// True when the per-voice entry has the same effective values as the global
    /// snapshot — meaning the override is doing nothing useful and should be pruned.
    /// </summary>
    private static bool IsEffectivelyGlobal(EngineSettings pv, EngineSettings global) =>
        pv.TotalStep           == global.TotalStep
        && pv.EngineSpeed      == global.EngineSpeed
        && pv.DspRate          == global.DspRate
        && pv.DefaultVoice     == global.DefaultVoice
        && pv.VolumeTrimDb     == global.VolumeTrimDb
        && pv.MaxChunkChars    == global.MaxChunkChars
        && pv.MinChunkChars    == global.MinChunkChars
        && pv.InterChunkSilenceMs == global.InterChunkSilenceMs
        && pv.SynthesisSilenceSec == global.SynthesisSilenceSec
        && pv.RateClampCeiling == global.RateClampCeiling
        && pv.OnnxThreads      == global.OnnxThreads
        && pv.OnnxInterOpThreads == global.OnnxInterOpThreads
        && pv.UseDirectML      == global.UseDirectML
        && pv.DirectMLDeviceId == global.DirectMLDeviceId;

    public static int CurrentVersion()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(Root);
        return settings?.GetValue("Version") is int v ? v : 0;
    }

    private const int CurrentSchemaVersion = 3;

    /// <summary>
    /// One-shot per-build migration. Called on Launcher startup. Bumps the schema
    /// version and applies any pending behavior changes:
    ///   v0 → v1: prune per-voice entries that match global (Save() does this now too).
    ///   v1 → v2: default UseDirectML=true on installs that haven't set it; nuke all
    ///            existing per-voice overrides (legacy stale snapshots).
    ///   v2 → v3: force EngineSpeed=1.0 — the Supertonic model truncates the last
    ///            phoneme above 1.0; we always render at 1.0 and route speedup
    ///            through DSP. Slider is also disabled in the Tune tab.
    /// Idempotent — re-running is a no-op once SchemaVersion == CurrentSchemaVersion.
    /// </summary>
    public static void EnsureMigrated()
    {
        using var settings = Registry.CurrentUser.OpenSubKey(Root, writable: true);
        int schema = settings?.GetValue("SchemaVersion") is int sv ? sv : 0;
        if (schema >= CurrentSchemaVersion) return;

        var s = Load();
        if (schema < 2)
        {
            s.UseDirectML = true;
            s.PerVoice.Clear();
        }
        if (schema < 3)
        {
            s.EngineSpeed = 1.0f;
            // Also clear it from any per-voice overrides that survived v2.
            foreach (var pv in s.PerVoice.Values) pv.EngineSpeed = 1.0f;
        }
        Save(s);

        using var k = Registry.CurrentUser.CreateSubKey(Root, writable: true);
        k.SetValue("SchemaVersion", CurrentSchemaVersion, RegistryValueKind.DWord);
    }

    public static void BumpVersion()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(Root, writable: true);
        int current = settings.GetValue("Version") is int v ? v : 0;
        settings.SetValue("Version", current + 1, RegistryValueKind.DWord);
    }

    public static void ResetToDefaults()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        BumpVersion();
    }

    public static bool TrySetSingle(string key, string value, out string? error)
    {
        error = null;
        var s = Load();
        try
        {
            switch (key.ToLowerInvariant())
            {
                case "preset":
                    if (!Enum.TryParse<QualityPreset>(value, ignoreCase: true, out var p))
                    { error = $"Unknown preset: {value}"; return false; }
                    s.ApplyPreset(p);
                    break;
                case "totalstep":      s.TotalStep = int.Parse(value, CultureInfo.InvariantCulture); s.Preset = QualityPreset.Custom; break;
                case "enginespeed":    s.EngineSpeed = float.Parse(value, CultureInfo.InvariantCulture); s.Preset = QualityPreset.Custom; break;
                case "dsprate":        s.DspRate = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "defaultvoice":   s.DefaultVoice = value; break;
                case "volumetrimdb":   s.VolumeTrimDb = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "maxchunkchars":  s.MaxChunkChars = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "minchunkchars":  s.MinChunkChars = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "interchunksilencems": s.InterChunkSilenceMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "synthesissilencesec": s.SynthesisSilenceSec = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "rateclampceiling":    s.RateClampCeiling = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "onnxthreads":         s.OnnxThreads = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "onnxinteropthreads":  s.OnnxInterOpThreads = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "usedirectml":         s.UseDirectML = bool.Parse(value); break;
                case "directmldeviceid":    s.DirectMLDeviceId = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "vocodermode":         s.VocoderMode = int.Parse(value, CultureInfo.InvariantCulture); break;
                default:               error = $"Unknown key: {key}"; return false;
            }
            Save(s);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static void ReadInto(EngineSettings s, RegistryKey k)
    {
        if (k.GetValue("Preset") is string presetStr && Enum.TryParse<QualityPreset>(presetStr, true, out var p)) s.Preset = p;
        if (k.GetValue("TotalStep") is int ts) s.TotalStep = ts;
        if (k.GetValue("EngineSpeed") is string es) s.EngineSpeed = ParseFloat(es, s.EngineSpeed);
        if (k.GetValue("DspRate") is string dr) s.DspRate = ParseFloat(dr, s.DspRate);
        if (k.GetValue("DefaultVoice") is string dv) s.DefaultVoice = dv;
        if (k.GetValue("VolumeTrimDb") is string vt) s.VolumeTrimDb = ParseFloat(vt, s.VolumeTrimDb);
        if (k.GetValue("MaxChunkChars") is int mxc) s.MaxChunkChars = mxc;
        if (k.GetValue("MinChunkChars") is int mnc) s.MinChunkChars = mnc;
        if (k.GetValue("InterChunkSilenceMs") is int ics) s.InterChunkSilenceMs = ics;
        if (k.GetValue("SynthesisSilenceSec") is string sss) s.SynthesisSilenceSec = ParseFloat(sss, s.SynthesisSilenceSec);
        if (k.GetValue("RateClampCeiling") is string rcc) s.RateClampCeiling = ParseFloat(rcc, s.RateClampCeiling);
        if (k.GetValue("OnnxThreads") is int ot) s.OnnxThreads = ot;
        if (k.GetValue("OnnxInterOpThreads") is int iot) s.OnnxInterOpThreads = iot;
        if (k.GetValue("UseDirectML") is int udm) s.UseDirectML = udm != 0;
        if (k.GetValue("DirectMLDeviceId") is int did) s.DirectMLDeviceId = did;
        if (k.GetValue("VocoderMode") is int vm) s.VocoderMode = vm;
    }

    private static void WriteFrom(EngineSettings s, RegistryKey k)
    {
        k.SetValue("Preset", s.Preset.ToString(), RegistryValueKind.String);
        k.SetValue("TotalStep", s.TotalStep, RegistryValueKind.DWord);
        k.SetValue("EngineSpeed", s.EngineSpeed.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String);
        k.SetValue("DspRate", s.DspRate.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String);
        k.SetValue("DefaultVoice", s.DefaultVoice, RegistryValueKind.String);
        k.SetValue("VolumeTrimDb", s.VolumeTrimDb.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String);
        k.SetValue("MaxChunkChars", s.MaxChunkChars, RegistryValueKind.DWord);
        k.SetValue("MinChunkChars", s.MinChunkChars, RegistryValueKind.DWord);
        k.SetValue("InterChunkSilenceMs", s.InterChunkSilenceMs, RegistryValueKind.DWord);
        k.SetValue("SynthesisSilenceSec", s.SynthesisSilenceSec.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String);
        k.SetValue("RateClampCeiling", s.RateClampCeiling.ToString("R", CultureInfo.InvariantCulture), RegistryValueKind.String);
        k.SetValue("OnnxThreads", s.OnnxThreads, RegistryValueKind.DWord);
        k.SetValue("OnnxInterOpThreads", s.OnnxInterOpThreads, RegistryValueKind.DWord);
        k.SetValue("UseDirectML", s.UseDirectML ? 1 : 0, RegistryValueKind.DWord);
        k.SetValue("DirectMLDeviceId", s.DirectMLDeviceId, RegistryValueKind.DWord);
        k.SetValue("VocoderMode", s.VocoderMode, RegistryValueKind.DWord);
    }

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

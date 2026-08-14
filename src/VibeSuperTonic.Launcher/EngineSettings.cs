using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace VibeSuperTonic.Launcher;

internal enum QualityPreset { Draft, Balanced, Quality, HiFi, Custom }

/// <summary>
/// Snapshot of all engine knobs. Persisted as JSON at
/// <see cref="DataPaths.SettingsFilePath"/> (default <c>&lt;BaseDir&gt;\data\settings.json</c>).
/// The engine reads the same file via mtime-based caching.
/// </summary>
internal sealed class EngineSettings
{
    public QualityPreset Preset { get; set; } = QualityPreset.Balanced;
    public int TotalStep { get; set; } = 6;
    public float EngineSpeed { get; set; } = 1.05f;
    public float DspRate { get; set; } = 1.0f;
    public string DefaultVoice { get; set; } = "M1";
    /// <summary>
    /// Supertonic language code spoken when the SAPI client doesn't tag the
    /// text itself. Settable globally or per voice. A client that sends SSML
    /// <c>xml:lang</c> overrides it for the tagged fragment.
    /// </summary>
    public string Language { get; set; } = Shared.SupertonicLanguages.Default;
    public float VolumeTrimDb { get; set; } = 0f;

    public int MaxChunkChars { get; set; } = 200;
    public int MinChunkChars { get; set; } = 100;
    public int InterChunkSilenceMs { get; set; } = 200;
    public float SynthesisSilenceSec { get; set; } = 0.3f;
    public float RateClampCeiling { get; set; } = 1.3f;
    public int OnnxThreads { get; set; } = 0;
    public int OnnxInterOpThreads { get; set; } = 1;
    public bool UseDirectML { get; set; } = true;
    public int DirectMLDeviceId { get; set; } = 0;

    /// <summary>
    /// Schema version of the on-disk format. Bumped when fields are added or
    /// semantics shift; <see cref="EngineSettingsRegistry.EnsureMigrated"/>
    /// applies behavior changes once per upgrade.
    /// </summary>
    public int SchemaVersion { get; set; } = 0;

    public Dictionary<string, EngineSettings> PerVoice { get; set; } = new();

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

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(EngineSettings))]
internal partial class EngineSettingsJsonContext : JsonSerializerContext { }

/// <summary>
/// Read/write portal for the JSON settings file. Name kept for source compat —
/// historically this wrote to the registry; now it writes to
/// <see cref="DataPaths.SettingsFilePath"/>. Save() is atomic (tmp + rename).
/// </summary>
internal static class EngineSettingsRegistry
{
    private const string LegacyRegRoot = @"SOFTWARE\VibeSuperTonic\Settings";
    private const string LegacyDefaultSub = "Default";
    private const string LegacyPerVoiceSub = "PerVoice";
    private const int CurrentSchemaVersion = 6;
    private static readonly object _writeGate = new();

    public static EngineSettings Load()
    {
        string path = DataPaths.SettingsFilePath;
        if (File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var parsed = JsonSerializer.Deserialize(fs, EngineSettingsJsonContext.Default.EngineSettings);
                if (parsed is not null) return parsed;
            }
            catch { /* fall through to legacy / defaults */ }
        }
        // No file yet — try legacy registry, else built-in defaults.
        return ReadFromLegacyRegistry();
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

        if (s.SchemaVersion < CurrentSchemaVersion) s.SchemaVersion = CurrentSchemaVersion;

        DataPaths.EnsureExists();
        string path = DataPaths.SettingsFilePath;
        string tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s, EngineSettingsJsonContext.Default.EngineSettings);

        lock (_writeGate)
        {
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch
            {
                // File.Move with overwrite can fail under AV scans; fall back to delete+move.
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                File.Move(tmp, path);
            }
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
        && pv.Language         == global.Language
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

    /// <summary>
    /// One-shot per-build migration. Applies pending behavior changes:
    ///   v0 → v1: prune per-voice entries that match global (Save() does this now too).
    ///   v1 → v2: default UseDirectML=true on installs that haven't set it; nuke all
    ///            existing per-voice overrides (legacy stale snapshots).
    ///   v2 → v3: force EngineSpeed=1.0 — the Supertonic model truncates the last
    ///            phoneme above 1.0; we always render at 1.0 and route speedup
    ///            through DSP. Slider is also disabled in the Tune tab.
    ///   v3 → v4: settings now live in <see cref="DataPaths.SettingsFilePath"/>
    ///            (portable JSON file) instead of <c>HKCU\SOFTWARE\VibeSuperTonic\Settings</c>.
    ///            <see cref="Load"/> reads the legacy keys when the file is
    ///            missing; this migration writes them out as JSON, then deletes
    ///            the legacy subtree so the data root is the only source of truth.
    ///   v4 → v5: DSP path swapped from phase vocoder to Sonic (PSOLA). The
    ///            <c>VocoderMode</c> field was removed from the schema; existing
    ///            JSON entries deserialize harmlessly (ignored) and are dropped
    ///            on the next <see cref="Save"/>. Bumping the version triggers
    ///            one such Save here so the user's <c>settings.json</c> sheds
    ///            the stale field without waiting for a Tune-tab edit.
    ///   v5 → v6: <c>Language</c> added. Per-voice snapshots written before this
    ///            build deserialize with the field's default ("en"), which the
    ///            engine can't tell apart from a deliberate choice — so a user
    ///            who then sets the global language to German would find one
    ///            voice stubbornly speaking English with no visible cause.
    ///            Re-stamp every existing per-voice entry with the global value.
    /// Idempotent — re-running is a no-op once SchemaVersion == CurrentSchemaVersion.
    /// </summary>
    public static void EnsureMigrated()
    {
        DataPaths.EnsureExists();
        string path = DataPaths.SettingsFilePath;
        bool fileExists = File.Exists(path);

        var s = Load();
        int schema = fileExists ? s.SchemaVersion : 0;
        if (schema >= CurrentSchemaVersion && fileExists) return;

        if (schema < 2)
        {
            s.UseDirectML = true;
            s.PerVoice.Clear();
        }
        if (schema < 3)
        {
            s.EngineSpeed = 1.0f;
            foreach (var pv in s.PerVoice.Values) pv.EngineSpeed = 1.0f;
        }
        if (schema < 6)
        {
            foreach (var pv in s.PerVoice.Values) pv.Language = s.Language;
        }
        // schema < 4: just persist to disk so we own the data.
        // schema < 5: the VocoderMode field is gone — the Save() below drops it
        //             from settings.json since the property no longer exists in
        //             the schema. Same for any VocoderMode entries in PerVoice.

        s.SchemaVersion = CurrentSchemaVersion;
        Save(s);

        // Now that the JSON file is the source of truth, remove the legacy
        // registry subtree so the two never disagree. Voice tokens (HKLM) and
        // the CLSID InprocServer32 stay where they are — those are required by
        // the OS for COM activation.
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(LegacyRegRoot, throwOnMissingSubKey: false);
        }
        catch { /* legacy cleanup is best-effort */ }
    }

    public static void ResetToDefaults()
    {
        // "Default" preserves per-voice overrides — historically this wiped
        // them too. Keep parity with prior behavior: nuke everything.
        Save(new EngineSettings());
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
                case "language":
                {
                    // Reject rather than silently coerce: a CLI --set is an explicit
                    // instruction, and "typo'd de-DE quietly became English" is a
                    // worse outcome here than an error the caller can read.
                    string want = value.Trim().ToLowerInvariant();
                    if (want != "na" && !Shared.SupertonicLanguages.All.Any(l => l.Code == want))
                    {
                        error = $"Unknown language: {value}. Supported: " +
                                string.Join(", ", Shared.SupertonicLanguages.All.Select(l => l.Code));
                        return false;
                    }
                    s.Language = want;
                    break;
                }
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
                default:               error = $"Unknown key: {key}"; return false;
            }
            Save(s);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// One-time hydration from <c>HKCU\SOFTWARE\VibeSuperTonic\Settings</c>.
    /// Engaged only when <c>settings.json</c> doesn't exist yet — first run
    /// after upgrading from a pre-portable build.
    /// </summary>
    private static EngineSettings ReadFromLegacyRegistry()
    {
        var s = new EngineSettings();
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(LegacyRegRoot);
            if (settings is null) return s;
            using (var def = settings.OpenSubKey(LegacyDefaultSub))
                if (def is not null) ReadInto(s, def);
            using var pv = settings.OpenSubKey(LegacyPerVoiceSub);
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
            // Preserve old SchemaVersion so EnsureMigrated knows which steps to apply.
            if (settings.GetValue("SchemaVersion") is int sv) s.SchemaVersion = sv;
        }
        catch { /* fall through with defaults */ }
        return s;
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
    }

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

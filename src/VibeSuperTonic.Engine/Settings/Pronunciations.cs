using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// One user-defined text substitution applied to every SAPI text fragment before
/// it reaches the model. Designed for fixing mispronunciations that the model's
/// front-end can't help with — e.g. all-caps short tokens that get spelled as
/// letters ("NOT" → "N O T") or non-pronounceable glyphs ("Bᵠ" → "B phi").
/// </summary>
internal sealed class PronunciationRule
{
    public bool   Enabled       { get; set; } = true;
    public string Match         { get; set; } = "";
    public string Replace       { get; set; } = "";
    public bool   WholeWord     { get; set; } = true;
    public bool   CaseSensitive { get; set; } = true;
    public string Notes         { get; set; } = "";
}

/// <summary>
/// Persisted as JSON at <c>&lt;DataDir&gt;\pronunciations.json</c>. Read by the
/// engine on every Speak via <see cref="PronunciationsCache.Resolve"/> (mtime-
/// cached). Written by the launcher's Pronunciations tab.
/// </summary>
internal sealed class PronunciationsConfig
{
    public bool Enabled { get; set; } = true;
    public List<PronunciationRule> Rules { get; set; } = new();

    /// <summary>
    /// Apply every enabled rule to <paramref name="text"/> in order. Returns the
    /// original string if the master switch is off, the list is empty, or no rule
    /// fires. Compiled regex objects are cached by <see cref="PronunciationsCache"/>
    /// for the lifetime of the loaded config.
    /// </summary>
    public string Apply(string text, IReadOnlyList<Regex?> compiled)
    {
        if (!Enabled || string.IsNullOrEmpty(text) || Rules.Count == 0) return text;
        for (int i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Match)) continue;
            var re = compiled[i];
            if (re is null) continue; // rule failed to compile — skip silently
            text = re.Replace(text, rule.Replace ?? "");
        }
        return text;
    }

    internal static Regex? Compile(PronunciationRule r)
    {
        if (string.IsNullOrEmpty(r.Match)) return null;
        try
        {
            string pattern = Regex.Escape(r.Match);
            if (r.WholeWord) pattern = @"\b" + pattern + @"\b";
            var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (!r.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            return new Regex(pattern, opts);
        }
        catch { return null; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PronunciationsConfig))]
internal partial class PronunciationsJsonContext : JsonSerializerContext { }

/// <summary>
/// Mtime-cached reader for <c>pronunciations.json</c>. Mirrors
/// <see cref="EngineSettingsCache"/> — one stat per call, reparse only when the
/// timestamp moves. Compiled regexes are cached alongside the config so each
/// Speak pays the regex setup cost zero times.
/// </summary>
internal static class PronunciationsCache
{
    private static readonly object _gate = new();
    private static long _cachedMtimeTicks = DateTime.MinValue.Ticks;
    private static PronunciationsConfig _cached = new();
    private static Regex?[] _compiled = Array.Empty<Regex?>();
    private static bool _everLoaded;

    public static (PronunciationsConfig config, Regex?[] compiled) Resolve()
    {
        string path = PronunciationsPath;
        long mtimeTicks;
        try { mtimeTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : DateTime.MinValue.Ticks; }
        catch { mtimeTicks = DateTime.MinValue.Ticks; }

        if (Volatile.Read(ref _everLoaded) && Volatile.Read(ref _cachedMtimeTicks) == mtimeTicks)
            return (_cached, _compiled);
        lock (_gate)
        {
            if (_everLoaded && _cachedMtimeTicks == mtimeTicks) return (_cached, _compiled);
            _cached = LoadFromFile(path);
            _compiled = new Regex?[_cached.Rules.Count];
            for (int i = 0; i < _cached.Rules.Count; i++)
                _compiled[i] = PronunciationsConfig.Compile(_cached.Rules[i]);
            _cachedMtimeTicks = mtimeTicks;
            _everLoaded = true;
            return (_cached, _compiled);
        }
    }

    public static string PronunciationsPath =>
        Path.Combine(DataPaths.DataDir, "pronunciations.json");

    private static PronunciationsConfig LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new PronunciationsConfig();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, PronunciationsJsonContext.Default.PronunciationsConfig)
                   ?? new PronunciationsConfig();
        }
        catch { return new PronunciationsConfig(); }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// Mirror of <c>VibeSuperTonic.Engine.Settings.PronunciationRule</c> — duplicated
/// for the same reason as <see cref="DataPaths"/> and <see cref="EngineSettings"/>:
/// the engine loads as a COM in-proc DLL inside arbitrary host processes and
/// pulling the launcher in via project reference would balloon every host's
/// working set. Keep the field set in sync.
/// </summary>
internal sealed class PronunciationRule
{
    public bool   Enabled       { get; set; } = true;
    public string Match         { get; set; } = "";
    public string Replace       { get; set; } = "";
    public bool   WholeWord     { get; set; } = true;
    public bool   CaseSensitive { get; set; } = true;
    public string Notes         { get; set; } = "";

    public PronunciationRule Clone() => (PronunciationRule)MemberwiseClone();
}

internal sealed class PronunciationsConfig
{
    public bool Enabled { get; set; } = true;
    public List<PronunciationRule> Rules { get; set; } = new();

    /// <summary>
    /// Used by the Pronunciations tab's "Test" pane so the user sees exactly
    /// what the engine will see. Engine-side <c>Apply</c> uses pre-compiled
    /// regexes (cached); the tab compiles on demand because edits are rare.
    /// </summary>
    public string Apply(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text) || Rules.Count == 0) return text;
        foreach (var r in Rules)
        {
            if (!r.Enabled || string.IsNullOrEmpty(r.Match)) continue;
            try
            {
                string pattern = Regex.Escape(r.Match);
                if (r.WholeWord) pattern = @"\b" + pattern + @"\b";
                var opts = RegexOptions.CultureInvariant;
                if (!r.CaseSensitive) opts |= RegexOptions.IgnoreCase;
                text = Regex.Replace(text, pattern, r.Replace ?? "", opts);
            }
            catch { /* skip broken rule */ }
        }
        return text;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PronunciationsConfig))]
internal partial class PronunciationsJsonContext : JsonSerializerContext { }

/// <summary>
/// Load/Save portal for <c>pronunciations.json</c>. Atomic writes (tmp + rename)
/// so the engine's mtime-cached reader never sees a partial file.
/// </summary>
internal static class PronunciationsRegistry
{
    private static readonly object _writeGate = new();

    public static string Path => System.IO.Path.Combine(DataPaths.DataDir, "pronunciations.json");

    public static PronunciationsConfig Load()
    {
        string path = Path;
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

    public static void Save(PronunciationsConfig c)
    {
        DataPaths.EnsureExists();
        string path = Path;
        string tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(c, PronunciationsJsonContext.Default.PronunciationsConfig);
        lock (_writeGate)
        {
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                File.Move(tmp, path);
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Launcher;

// PronunciationRule and PronunciationsConfig used to be declared here as a
// hand-maintained mirror of the engine's copies. They now come from
// VibeSuperTonic.Core.Text, so the Test pane in the Pronunciations tab runs the
// exact code the engine runs — the previous arrangement had two Apply methods
// that were merely intended to agree, and one of them (this one) compiled its
// regexes with different options.
//
// The load/save portal below stays here: it resolves the path through the
// launcher's DataPaths, which is registry-backed and therefore Windows-only.

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

using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

[JsonSourceGenerationOptions(
    // A file a person is meant to open and argue with. The whole reason the table
    // is kept is so the winning number can be checked rather than trusted, and a
    // single-line JSON blob is not something anyone checks.
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BenchmarkProfile))]
internal partial class BenchmarkJsonContext : JsonSerializerContext { }

/// <summary>
/// <c>data/benchmark.json</c> — where a machine keeps what it measured about
/// itself.
///
/// <para><b>A separate file from <c>settings.json</c>, deliberately.</b> That one
/// has exactly one writer by design — the UI writes, the daemon reads — and it is
/// the file that crosses platforms in a portable folder. This one is the
/// daemon's own output: it is written by the only process that can measure, it
/// means nothing on another machine, and putting a measurement inside a
/// user-edited file would put two writers on it for no gain.</para>
///
/// <para><b>Failing to save is not failing.</b> A read-only install still gets a
/// correct answer out of the sweep; it just cannot keep it. Phase 4b measured a
/// legitimate <c>DataDirWritable: false</c> case, so this is a real path rather
/// than a defensive one, and the caller reports it rather than throwing.</para>
/// </summary>
internal static class BenchmarkStore
{
    /// <summary>The stored profile, or null when absent, unreadable or malformed.</summary>
    public static BenchmarkProfile? Load(string dataDir)
    {
        string path = LinuxDataPaths.BenchmarkFile(dataDir);
        try
        {
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, BenchmarkJsonContext.Default.BenchmarkProfile);
        }
        catch
        {
            // A corrupt profile is the same as no profile: fall back to the
            // percentage, which is a guess that has already shipped. Throwing
            // here would be a daemon that will not start because a cache file is
            // damaged.
            return null;
        }
    }

    /// <summary>
    /// Write it, atomically. Returns false when the folder is read-only.
    ///
    /// <para>Temp-and-rename rather than a plain write, for the same reason the
    /// UI uses it on <c>settings.json</c>: a daemon killed mid-write would
    /// otherwise leave a truncated file that parses as "no profile" on the next
    /// start, silently discarding a measurement the user waited a minute for.</para>
    /// </summary>
    public static bool TrySave(string dataDir, BenchmarkProfile profile, out string path, out string? error)
    {
        path = LinuxDataPaths.BenchmarkFile(dataDir);
        string temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(dataDir);

            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, profile, BenchmarkJsonContext.Default.BenchmarkProfile);

            File.Move(temp, path, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return false;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Core.Synthesis;

[JsonSourceGenerationOptions(
    // A file a person is meant to open and argue with. The whole reason the table
    // is kept is so the winning number can be checked rather than trusted, and a
    // single-line JSON blob is not something anyone checks.
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BenchmarkProfile))]
public partial class BenchmarkJsonContext : JsonSerializerContext { }

/// <summary>
/// <c>benchmark.json</c> — where a machine keeps what it measured about itself.
///
/// <para><b>Shared by both platforms as of 2026-08-19.</b> This was the Linux
/// daemon's, and the Windows port needed the identical file in the identical
/// format under the identical name. Two implementations of one file format is the
/// drift Core exists to prevent, and this one is pure JSON over a stream with no
/// platform in it — the only thing that was ever Linux-specific was the
/// <em>path</em>, which is why every method here takes a full one and none of them
/// knows what a data directory is (R-12).</para>
///
/// <para><b>A separate file from <c>settings.json</c>, deliberately.</b> That one
/// has exactly one writer by design — the UI or Control Panel writes, the engine
/// reads — and it is the file that crosses machines in a portable folder. This one
/// is the measuring process's own output: it is written by the only thing that can
/// measure, it means nothing on another machine, and putting a measurement inside
/// a user-edited file would put two writers on it for no gain.</para>
///
/// <para><b>Failing to save is not failing.</b> A read-only install still gets a
/// correct answer out of the sweep; it just cannot keep it. Phase 4b measured a
/// legitimate read-only data directory, so this is a real path rather than a
/// defensive one, and the caller reports it rather than throwing.</para>
/// </summary>
public static class BenchmarkStore
{
    /// <summary>The conventional filename, so the two platforms cannot disagree about it.</summary>
    public const string FileName = "benchmark.json";

    /// <summary>The stored profile, or null when absent, unreadable or malformed.</summary>
    public static BenchmarkProfile? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, BenchmarkJsonContext.Default.BenchmarkProfile);
        }
        catch
        {
            // A corrupt profile is the same as no profile: fall back to whatever
            // the platform's guess is, which is a guess that has already shipped.
            // Throwing here would be a daemon that will not start — or a SAPI host
            // that cannot speak — because a cache file is damaged.
            return null;
        }
    }

    /// <summary>
    /// Write it, atomically. Returns false when the folder is read-only.
    ///
    /// <para>Temp-and-rename rather than a plain write, for the same reason both
    /// platforms use it on <c>settings.json</c>: a process killed mid-write would
    /// otherwise leave a truncated file that parses as "no profile" on the next
    /// start, silently discarding a measurement the user waited a minute for.</para>
    /// </summary>
    public static bool TrySave(string path, BenchmarkProfile profile, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);

        string temp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Launcher.Telemetry;

/// <summary>
/// Engine-side snapshot for one SAPI client process. Mirrors
/// <c>VibeSuperTonic.Engine.Telemetry.SessionSnapshotDto</c> — keep field names
/// in sync. JSON files are at
/// <c>%LOCALAPPDATA%\VibeSuperTonic\sessions\&lt;pid&gt;.json</c>.
/// </summary>
internal sealed class TelemetrySnapshot
{
    public int    SchemaVersion { get; set; }
    public int    Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public bool   IsActive { get; set; }
    public string VoiceId { get; set; } = "";
    public string CurrentText { get; set; } = "";
    public string LastError { get; set; } = "";
    public int    TotalStep { get; set; }
    public float  EngineSpeed { get; set; }
    public float  DspRate { get; set; }
    public double FirstByteLatencyMs { get; set; }
    public double RollingRtf { get; set; }
    public int    PipelineDepth { get; set; }
    public double InterChunkGapMs { get; set; }
    public double EngineCpuPct { get; set; }
    public double EngineRssMb { get; set; }
    public int    OnnxThreads { get; set; }
    public int    UnderrunCount { get; set; }
    public int    DeviceLossCount { get; set; }
    public bool   DmlLatchedOff { get; set; }
    public DateTime SampleTimeUtc { get; set; }

    /// <summary>
    /// File mtime of the snapshot — used as a heartbeat. A snapshot whose mtime
    /// is older than <see cref="TelemetryReader.StaleAfter"/> is treated as a
    /// dead engine instance and filtered out.
    /// </summary>
    public DateTime FileMtimeUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(TelemetrySnapshot))]
internal partial class SnapshotJsonContext : JsonSerializerContext { }

/// <summary>
/// Enumerates the per-PID JSON files written by every engine instance. Designed
/// for the Monitor tab's polling loop: cheap to call at ~5 Hz.
/// </summary>
internal static class TelemetryReader
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

    public static string SessionsDir => DataPaths.SessionsDir;

    public static bool IsAvailable()
    {
        try { return ListLiveSessions().Count > 0; }
        catch { return false; }
    }

    /// <summary>
    /// All sessions whose snapshot file mtime is within
    /// <see cref="StaleAfter"/>. Sorted: active first, then most-recent sample.
    /// </summary>
    public static List<TelemetrySnapshot> ListLiveSessions()
    {
        var live = new List<TelemetrySnapshot>();
        string dir = SessionsDir;
        if (!Directory.Exists(dir)) return live;

        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        foreach (var path in EnumerateSessionFiles(dir))
        {
            TelemetrySnapshot? snap;
            DateTime mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(path);
                if (mtime < cutoff) continue;
                snap = ReadSnapshot(path);
            }
            catch { continue; }
            if (snap is null) continue;
            snap.FileMtimeUtc = mtime;
            live.Add(snap);
        }

        live.Sort((a, b) =>
        {
            int byActive = (b.IsActive ? 1 : 0) - (a.IsActive ? 1 : 0);
            if (byActive != 0) return byActive;
            return b.SampleTimeUtc.CompareTo(a.SampleTimeUtc);
        });
        return live;
    }

    /// <summary>
    /// Asks the engine running in <paramref name="pid"/> to drop its shared ONNX
    /// session and rebuild on the next request. Mechanism: we touch a sentinel
    /// file in the sessions dir; the engine polls for it at 1 Hz (and at every
    /// telemetry tick).
    /// </summary>
    public static void RequestReset(int pid)
    {
        try
        {
            string dir = SessionsDir;
            Directory.CreateDirectory(dir);
            string marker = Path.Combine(dir, $"{pid}.reset");
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        }
        catch { /* best-effort; if it fails the user can retry */ }
    }

    private static IEnumerable<string> EnumerateSessionFiles(string dir)
    {
        // Filter to "{pid}.json" — skip the .tmp atomic-write swap files and
        // .reset markers. PIDs are integers so the regex is trivial.
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json"); }
        catch { yield break; }
        foreach (var path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, out _)) yield return path;
        }
    }

    private static TelemetrySnapshot? ReadSnapshot(string path)
    {
        // The engine writes atomically (tmp + rename); a torn read is unlikely but
        // possible during AV scans. On JsonException treat as not-yet-readable.
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, SnapshotJsonContext.Default.TelemetrySnapshot);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

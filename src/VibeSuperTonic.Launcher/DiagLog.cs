using System.Text;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// Dead-simple, flush-on-every-write diagnostic log for the Launcher process.
/// Writes to <c>&lt;LogsDir&gt;\launcher.log</c> (next to the engine's
/// <c>engine.log</c>), falling back to <c>%TEMP%\VibeSuperTonic-launcher.log</c>
/// if the data dir isn't writable.
///
/// Why flush every line: the crashes we're chasing on the Export tab are native
/// COM access violations inside SAPI — those terminate the process WITHOUT
/// unwinding managed catch blocks, so anything buffered in memory is lost. By
/// appending + closing the handle per line, the last line on disk after a hard
/// crash pinpoints the exact step that died. This is intentionally not a
/// performance-oriented logger; correctness-under-crash beats throughput here.
/// </summary>
internal static class DiagLog
{
    private static readonly object Gate = new();

    public static string LogPath
    {
        get
        {
            try
            {
                string dir = DataPaths.LogsDir;
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "launcher.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "VibeSuperTonic-launcher.log");
            }
        }
    }

    public static void Write(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                // Rolls at 4 MB, keeping one previous generation. Checked per
                // write rather than on a timer: this logger already opens and
                // closes the file every line, so one extra stat is noise, and a
                // crash-oriented logger can't rely on ever reaching a shutdown.
                string path = LogPath;
                Shared.LogRotation.RollIfNeeded(path);
                // AppendAllText opens, writes, flushes, and closes — so the line
                // is on disk before this call returns, surviving a later AV.
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Last-ditch: try TEMP directly. If even that fails, swallow —
            // a logging failure must never become the visible crash.
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "VibeSuperTonic-launcher.log");
                File.AppendAllText(fallback, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public static void WriteException(string where, Exception ex)
    {
        Write($"EXCEPTION in {where}: {ex.GetType().FullName}: {ex.Message}");
        Write($"  StackTrace: {ex.StackTrace}");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Write($"  Inner: {inner.GetType().FullName}: {inner.Message}");
    }
}

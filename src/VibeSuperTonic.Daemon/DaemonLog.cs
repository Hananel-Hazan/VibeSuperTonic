using System.Text;
using VibeSuperTonic.Core.Diagnostics;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Everything the daemon says, to <c>&lt;LogsDir&gt;/daemon.log</c> as well as to
/// stderr.
///
/// <para><b>Why this exists.</b> The daemon used to write only to stderr, and
/// nothing a user does starts it from a terminal: <c>vst-ctl</c>'s auto-start
/// (R-5) inherits whatever the hotkey's environment was, and a double-click from
/// a file manager discards stderr outright. So every diagnostic the daemon
/// produces — "another daemon is listening", "no models under…", a pronunciation
/// rule that did not compile, the inference thread budget — was invisible in
/// exactly the runs a field report would be written about. Found in about four
/// seconds by a user double-clicking the binary.</para>
///
/// <para><b>Stderr as well, not instead.</b> A daemon started from a terminal
/// during development must keep behaving as it did, and a log that is only on
/// disk is one nobody reads while working.</para>
///
/// <para>The counterpart of the Windows launcher's <c>DiagLog</c> and the
/// engine's trace, deliberately down to the file layout: <c>data/logs/</c> is the
/// same directory on both platforms, so a portable folder that has been used on
/// both has one place to look.</para>
/// </summary>
internal static class DaemonLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _path;

    /// <summary>
    /// Point the log at this install's data directory. Until this is called —
    /// argument parsing, and the failure to resolve a data directory at all —
    /// lines go to stderr only, which is the best that can be done before the
    /// destination is known.
    /// </summary>
    /// <remarks>
    /// Failure is not fatal and not reported as an error. A portable folder can
    /// sit on a read-only mount; <see cref="HostConfig"/> already measures that
    /// and reports it through <c>config</c>, and a daemon that refused to start
    /// because it could not open a log would turn a diagnostic into the user's
    /// actual problem.
    /// </remarks>
    public static void Initialize(string dataDir)
    {
        try
        {
            string dir = LinuxDataPaths.LogsDir(dataDir);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "daemon.log");

            // Probe by appending nothing: CreateDirectory succeeding says the
            // directory exists, not that we may write in it, and discovering
            // otherwise on the first real line would lose that line.
            File.AppendAllText(path, "");
            lock (Gate) _path = path;
        }
        catch
        {
            // Read-only install, or a directory owned by somebody else. Try a
            // temp file so a field report is still possible — it is a
            // diagnostic, not state, so it does not have to travel with the
            // folder the way settings.json does.
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "vibesupertonicd.log");
                File.AppendAllText(fallback, "");
                lock (Gate) _path = fallback;
                Write($"data directory is not writable; logging to {fallback}");
            }
            catch { /* stderr only, then */ }
        }
    }

    /// <summary>The file being written, or null when only stderr is available.</summary>
    public static string? LogPath { get { lock (Gate) return _path; } }

    public static void Write(string message)
    {
        Console.Error.WriteLine($"[vibesupertonicd] {message}");

        string? path;
        lock (Gate) path = _path;
        if (path is null) return;

        try
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                // Rolls at 4 MB keeping one generation, the same bound the
                // Windows logs have. Checked per write: this opens and closes
                // the file every line anyway, so one extra stat is noise, and
                // the daemon has no shutdown it can rely on reaching — SIGKILL
                // and a hard logout both skip it.
                LogRotation.RollIfNeeded(path);
                // UTF8Encoding(false), not Encoding.UTF8: the latter emits a byte
                // order mark when it creates the file, and a log that starts with
                // an invisible three bytes is one that greps oddly on its first
                // line and looks corrupt when pasted into a report.
                File.AppendAllText(path, line, Utf8NoBom);
            }
        }
        catch
        {
            // A logging failure must never become the visible failure. The line
            // has already reached stderr, which is the half a developer sees.
        }
    }
}

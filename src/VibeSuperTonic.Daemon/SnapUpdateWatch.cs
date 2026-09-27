namespace VibeSuperTonic.Daemon;

/// <summary>
/// Inside a snap: notice that snapd is holding an update back because we are
/// running, so the user can choose when it happens.
///
/// <para><b>Why.</b> snapd will not refresh a snap while any of its apps runs.
/// The daemon runs from the first hotkey press until logout and the Speech
/// Dispatcher module for as long as speech-dispatcher does, so for anyone who
/// uses either, every update waits. snapd downloads it in the background,
/// shows its own "close the app" notification, and applies it when the apps
/// stop, or after 14 days regardless. What it cannot do is offer a button,
/// because closing our apps means closing three processes, one of which the
/// user never started by hand.</para>
///
/// <para><b>Never on its own.</b> The user decides (2026-09-27): no restart
/// they did not ask for, and no slow first press they were not warned about.
/// This type only notices. The tray asks, and <c>DaemonServer.ApplyUpdate</c>
/// acts on the answer.</para>
///
/// <para><b>How it notices without privileges.</b> A snap cannot ask snapd
/// about its own refreshes: <c>snapctl refresh --pending</c> is refused to a
/// non-root app ("non-root users can only use --tracking", snapd 2.76), and the
/// notices API needs <c>snap-refresh-observe</c>, which the Store grants only
/// by review. But every snap's AppArmor profile lets it read its own
/// <c>/var/lib/snapd/snaps/&lt;name&gt;_*.snap</c>, and snapd pre-downloads a
/// held revision to exactly that name. The files are root-only (0600); only
/// their existence is asked.</para>
///
/// <para>The revision can also already be current: snapd's 14-day limit, or a
/// <c>snap revert</c>. Then this daemon is the stale one and the same question
/// applies.</para>
/// </summary>
internal static class SnapUpdateWatch
{
    /// <summary>How often it looks. A stat per probe, so cheap.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>How far past our own revision to look. Revisions are per snap, not per channel.</summary>
    internal const int Probe = 50;

    internal const string SnapsDir = "/var/lib/snapd/snaps";

    /// <summary>
    /// The revision waiting to replace this one, or null. Newest wins when snapd
    /// has several on disk.
    /// </summary>
    /// <param name="instance">The snap's instance name, <c>$SNAP_INSTANCE_NAME</c>.</param>
    /// <param name="own">This process's revision.</param>
    /// <param name="current">What <c>/snap/&lt;name&gt;/current</c> names, or null if unreadable.</param>
    /// <param name="exists">File.Exists, or a test's fake.</param>
    internal static int? Pending(string? instance, string? own, string? current, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(instance) || !int.TryParse(own, out int mine)) return null;

        // Already current and it is not us: an update or a revert happened while
        // this daemon ran.
        if (int.TryParse(current, out int cur) && cur != mine) return cur;

        for (int r = mine + Probe; r > mine; r--)
            if (exists(Path.Combine(SnapsDir, $"{instance}_{r}.snap"))) return r;
        return null;
    }

    /// <summary>This process's revision, or null outside a snap.</summary>
    internal static string? OwnRevision(string? snap) =>
        string.IsNullOrWhiteSpace(snap) || !snap.StartsWith('/')
            ? null
            : Path.GetFileName(snap.TrimEnd('/'));

    /// <summary>
    /// The revision <c>current</c> names, read from the link beside
    /// <paramref name="snap"/>. Null if it cannot be read.
    /// </summary>
    internal static string? CurrentRevision(string? snap)
    {
        if (string.IsNullOrWhiteSpace(snap)) return null;
        try
        {
            string? parent = Path.GetDirectoryName(snap.TrimEnd('/'));
            if (parent is null) return null;
            string? target = new FileInfo(Path.Combine(parent, "current")).LinkTarget;
            return string.IsNullOrEmpty(target) ? null : Path.GetFileName(target.TrimEnd('/'));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

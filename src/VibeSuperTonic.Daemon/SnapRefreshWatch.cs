namespace VibeSuperTonic.Daemon;

/// <summary>
/// Inside a snap: step aside, when idle, for a revision that snapd has made
/// current while this daemon was running.
///
/// <para><b>Inert since 2026-09-27.</b> It was written for
/// <c>refresh-mode: ignore-running</c>, which the Store refuses on an app that
/// is not a service, so snapd still will not refresh while the daemon runs and
/// <c>current</c> cannot move under it. Kept because it is correct if that ever
/// changes (a service daemon, or a forced refresh); see STORE-SUBMISSION's
/// refresh item. The paragraphs below describe the design as intended.</para>
///
/// <para><b>Why the daemon has to do this itself.</b> snapd refuses a manual
/// <c>snap refresh</c>, and postpones an automatic one for up to 14 days,
/// while any of a snap's apps runs. This daemon is long-lived by design (a warm
/// model is what makes a hotkey press start speaking at once), and the Speech
/// Dispatcher module lives for the whole login session, so every user who
/// pressed the hotkey once, or registered the module, held back every update.
/// Found on Kubuntu, 2026-09-25: "has running apps (speechd)". So both apps are
/// declared <c>refresh-mode: ignore-running</c> in snapcraft.yaml.in, and snapd
/// updates underneath them.</para>
///
/// <para>That leaves the old daemon serving every hotkey press from the old
/// revision, the same hazard <c>install.sh</c> guards against for the tarball.
/// It keeps working, because the snap's AppArmor profile lets an app read and
/// run every revision of its own snap (<c>/snap/&lt;name&gt;/** mrkix</c>,
/// checked on revision 5). It is only out of date. So once a minute this compares
/// its own revision with <c>/snap/&lt;name&gt;/current</c>, and once a different
/// one is current and nothing has used the daemon for <see cref="Grace"/>, it
/// shuts down. The next hotkey press runs <c>/snap/bin/…ctl</c>, which is the
/// current revision, and starts the current daemon.</para>
///
/// <para><b>"Different", not "newer".</b> <c>snap revert</c> makes an older
/// revision current, and the daemon should follow that too.</para>
///
/// <para><b>The module cannot do the same.</b> Measured on speech-dispatcher
/// 0.12.1, 2026-09-26: a module that exits is not restarted; speechd routes
/// every later request to its fallback and keeps listing the dead module. So
/// the module stays on the old revision until speech-dispatcher restarts,
/// normally at the next login, and keeps working meanwhile.</para>
/// </summary>
internal static class SnapRefreshWatch
{
    /// <summary>How long nothing may have used the daemon before it steps aside.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>How often it looks.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The rule, pure. Null when the daemon should keep running; otherwise the
    /// sentence it logs on the way out.
    /// </summary>
    /// <param name="own">This process's revision: the last element of <c>$SNAP</c>.</param>
    /// <param name="current">What <c>/snap/&lt;name&gt;/current</c> points at, or null if unreadable.</param>
    /// <param name="openConnections">Clients connected now. An open window is one.</param>
    /// <param name="speaking">Anything other than idle: speaking, paused, stopping, or a benchmark.</param>
    /// <param name="idleFor">Time since the last connection opened or closed.</param>
    internal static string? StepAside(string? own, string? current, int openConnections,
        bool speaking, TimeSpan idleFor)
    {
        if (string.IsNullOrEmpty(own) || string.IsNullOrEmpty(current)) return null;
        if (current == own) return null;
        if (speaking || openConnections > 0) return null;
        if (idleFor < Grace) return null;
        return $"revision {current} is now current and this daemon is revision {own}; " +
               $"idle for {(int)idleFor.TotalMinutes} min, so it is exiting, and the next hotkey press starts revision {current}";
    }

    /// <summary>This process's revision, or null outside a snap.</summary>
    internal static string? OwnRevision(string? snap) =>
        string.IsNullOrWhiteSpace(snap) || !snap.StartsWith('/')
            ? null
            : Path.GetFileName(snap.TrimEnd('/'));

    /// <summary>
    /// The revision <c>current</c> names, read from the link beside
    /// <paramref name="snap"/>. Null if it cannot be read, which keeps the
    /// daemon running: not knowing is never a reason to stop speaking.
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

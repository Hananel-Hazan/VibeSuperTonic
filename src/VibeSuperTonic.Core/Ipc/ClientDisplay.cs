namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// The graphical session the daemon's clients last reported, for the one thing
/// the daemon starts on screen: the window, from the tray.
///
/// <para><b>Why the daemon cannot use its own.</b> It is long-lived by design,
/// and a process's environment is fixed when it starts. Started by the Speech
/// Dispatcher module, it inherits a systemd user service's environment, and
/// that survives a logout on Ubuntu. After the user logged out and back in
/// (2026-09-25, revision 4 of the snap) the daemon still held
/// <c>XAUTHORITY=/run/user/1000/xauth_RxnJOD</c> while the session's was
/// <c>xauth_jHeGTu</c>, and every click on the tray icon aborted the window
/// with <c>XOpenDisplay failed</c>. The hotkey client, on the other hand, is
/// started by the desktop for every press, so it always has the current
/// session's values. <see cref="Request.Display"/> already travels for the same
/// reason; <see cref="Request.XAuthority"/> travels beside it.</para>
///
/// <para>Only requests that arrive over the socket are observed. The tray's own
/// requests are the daemon's, and carry the stale values this exists to
/// replace.</para>
/// </summary>
public sealed class ClientDisplay
{
    private sealed record Reported(string Display, string? XAuthority);

    private volatile Reported? _latest;

    /// <summary>Record what a client said, if it said anything.</summary>
    public void Observe(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Display)) return;
        _latest = new Reported(request.Display.Trim(),
            string.IsNullOrWhiteSpace(request.XAuthority) ? null : request.XAuthority.Trim());
    }

    /// <summary>
    /// Variables to set on a window the daemon starts. A null value means
    /// remove it. Empty when no client has reported a session, so a daemon
    /// nobody has pressed a key at keeps its own environment, as before.
    ///
    /// <para>The client's <c>XAUTHORITY</c> is used only while its file
    /// exists: a stale report is no better than a stale environment. A client
    /// that had none means "the default", <c>~/.Xauthority</c>, which a stale
    /// value of the daemon's own would hide, so that one is removed.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string?> ForWindow(Func<string, bool> fileExists)
    {
        var latest = _latest;
        var vars = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (latest is null) return vars;

        vars["DISPLAY"] = latest.Display;
        if (latest.XAuthority is null) vars["XAUTHORITY"] = null;
        else if (fileExists(latest.XAuthority)) vars["XAUTHORITY"] = latest.XAuthority;
        return vars;
    }
}

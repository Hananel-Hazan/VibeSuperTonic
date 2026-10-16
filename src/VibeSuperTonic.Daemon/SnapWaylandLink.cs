namespace VibeSuperTonic.Daemon;

/// <summary>
/// Inside a snap: make the compositor's socket reachable from the snap's private
/// runtime directory, before the daemon decides which selection source to build.
///
/// <para><b>Why.</b> Inside a snap <c>$XDG_RUNTIME_DIR</c> is
/// <c>/run/user/&lt;uid&gt;/snap.&lt;name&gt;</c>, and <c>wl_display_connect</c>
/// looks for <c>$WAYLAND_DISPLAY</c> there. The compositor's socket is one level
/// up. The GNOME extension's <c>desktop-launch</c> links it in, but only the
/// window app runs that chain; the daemon and <c>ctl</c> are bare on purpose
/// (every hotkey press would otherwise pay for a launcher script). So the link
/// exists only once a window has opened since the runtime directory was created.
/// After a reboot, on Kubuntu 2026-09-27, the hotkey started the daemon first:
/// the probe failed, the daemon built the X11 source for the rest of its life,
/// and every press in a Wayland application said "nothing is selected" while
/// the window, opened later, made the link that would have fixed it.</para>
///
/// <para>The same link <c>desktop-launch</c> makes, at the same path, so the two
/// never disagree about it. The <c>wayland</c> plug the daemon carries (it has
/// every plug of the window app) is what lets it through.</para>
/// </summary>
internal static class SnapWaylandLink
{
    /// <summary>
    /// Link the socket in if it is missing. Null when nothing was done or needed;
    /// otherwise the sentence to log. Never throws: a daemon that cannot make the
    /// link still starts, on X11, and says why.
    /// </summary>
    /// <param name="runtimeDir">The snap's <c>$XDG_RUNTIME_DIR</c>.</param>
    /// <param name="waylandDisplay"><c>$WAYLAND_DISPLAY</c>; libwayland's default when unset.</param>
    internal static string? Ensure(string? runtimeDir, string? waylandDisplay)
    {
        if (string.IsNullOrWhiteSpace(runtimeDir)) return null;
        string name = string.IsNullOrEmpty(waylandDisplay) ? "wayland-0" : waylandDisplay;
        // An absolute or nested name is not resolved against the runtime dir.
        if (name.Contains('/')) return null;

        string inside = Path.Combine(runtimeDir, name);
        string outside = Path.Combine(runtimeDir, "..", name);
        try
        {
            if (File.Exists(inside) || new FileInfo(inside).LinkTarget is not null) return null;
            if (!File.Exists(outside)) return null;   // no compositor: X11, honestly
            File.CreateSymbolicLink(inside, outside);
            return $"linked {inside} to the compositor's socket (no window had opened yet to do it)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not link {inside} to the compositor's socket, so Wayland " +
                   $"applications' selections will not be seen: {ex.Message}";
        }
    }
}

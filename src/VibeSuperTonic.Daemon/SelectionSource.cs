namespace VibeSuperTonic.Daemon;

/// <summary>
/// Where a bare <c>toggle</c> gets its text.
///
/// A seam rather than a call because it is Phase 4's whole job — reading the X11
/// PRIMARY selection without stealing it back from the window that owns it
/// [R-6], and refusing to read a whole book [R-9]. Phase 3 needs the daemon to
/// work end to end before that exists, so it ships with
/// <see cref="NullSelectionSource"/> and the verb reports honestly that it has
/// nothing to read.
///
/// Keeping the capture daemon-side rather than in the client is what makes the
/// hotkey, the tray menu and D-Bus behave identically — there is one place that
/// decides what "speak what I selected" means.
/// </summary>
public interface ISelectionSource
{
    /// <summary>
    /// Capture the current selection.
    ///
    /// <para><b>Synchronous on purpose.</b> The implementation blocks on
    /// <c>SelectionNotify</c> with a 300 ms cap, which is inside the
    /// acknowledgement budget (28 ms of it is spent so far) and far below the
    /// ~600 ms floor before the first sound. An async seam here would buy
    /// nothing and would put the capture on a different thread from the gate
    /// decision that depends on it.</para>
    /// </summary>
    /// <param name="display">
    /// The X display to read from — <c>$DISPLAY</c> as the *client* saw it, not
    /// as this process did. Null means "use whatever this process has".
    ///
    /// <para>This parameter exists because the obvious design does not work. The
    /// daemon can legitimately be started without a session: from a bare shell,
    /// from systemd --user, or by <c>vst-ctl</c>'s autostart (R-5), which
    /// inherits whatever environment its caller had. The plan said to "re-check
    /// per request rather than caching a failure", which reads well and does
    /// nothing — a process's own environment is fixed for its lifetime, so
    /// re-reading <c>$DISPLAY</c> returns the same answer until the daemon is
    /// restarted. The session identity has to arrive with the request, from a
    /// client that by construction runs inside the session.</para>
    /// </param>
    SelectionResult Capture(string? display = null);
}

/// <summary>
/// What a capture attempt produced.
///
/// <para>A record rather than <c>bool TryGet(out text, out reason)</c> because
/// there are three outcomes, not two: got it, could not, and <b>got it but
/// changed it</b>. R-9 caps a selection at 100 KB and truncates at a sentence
/// boundary, and the plan says to report what was dropped — a two-outcome seam
/// has nowhere to say so, and the tray tooltip Phase 6 hangs off R-9 would have
/// had to reach around this interface to find out.</para>
/// </summary>
public sealed record SelectionResult
{
    public bool Ok { get; init; }

    /// <summary>The captured text. Empty when <see cref="Ok"/> is false.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// Why not, when <see cref="Ok"/> is false. Surfaced to the client, so it
    /// must be a sentence a user can act on rather than a status code.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// What was changed about a successful capture — truncation, and nothing
    /// else so far. Null when the text is exactly what was selected.
    /// </summary>
    public string? Notice { get; init; }

    public static SelectionResult Captured(string text, string? notice = null) =>
        new() { Ok = true, Text = text, Notice = notice };

    public static SelectionResult None(string reason) =>
        new() { Ok = false, Reason = reason };
}

/// <summary>
/// Stands in until Phase 4. Says so plainly rather than returning empty text,
/// which would be indistinguishable from "you selected nothing".
/// </summary>
public sealed class NullSelectionSource : ISelectionSource
{
    public SelectionResult Capture(string? display = null) =>
        SelectionResult.None(
            "selection capture is not implemented yet (Phase 4). " +
            "Send text explicitly: vst-ctl speak \"...\"");
}

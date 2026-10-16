namespace VibeSuperTonic.Daemon;

/// <summary>
/// How long to wait on the application that owns the selection — <b>one
/// statement of it, read by both the Wayland and the X11 source</b>.
///
/// <para><b>Reported 2026-09-06:</b> "I hit the hotkey on text in Firefox or
/// VS Code and it is not read... then I copy it to Kate, select all, hit the
/// hotkey, and it reads it." Handing over a selection is a round trip through
/// the OWNING application's event loop. Kate is a light native app and answers
/// in about 15 ms every time; a browser or an Electron app stalls its loop for
/// hundreds of milliseconds under load — rendering, GC, an extension host, a
/// large file.</para>
///
/// <para>Both sources allowed 300 ms and both lost the press. Measured by
/// freezing a selection owner's event loop: a <b>0.5 s stall was already
/// enough</b>. The X11 source's own comment claimed 300 ms was "long enough that
/// a loaded Firefox still makes it", which is the belief this measurement
/// refutes.</para>
///
/// <para><b>Shared so the two cannot drift.</b> They are the same question asked
/// of two protocols, and a user on X11 has exactly the bug a user on Wayland
/// has.</para>
/// </summary>
public static class SelectionWait
{
    /// <summary>
    /// Wait for the owner to answer at all. Two seconds: it covers the stalls
    /// that were losing presses, still fails in a time a person will sit
    /// through, and costs nothing in the ordinary case — a responsive owner
    /// answers in tens of milliseconds.
    /// </summary>
    public const int OwnerReplyMs = 2000;

    /// <summary>
    /// Once it is talking, how long a gap between chunks is still "working".
    /// Shorter than the first reply, because an owner that has started writing
    /// has its data ready.
    /// </summary>
    public const int BetweenChunksMs = 1000;

    /// <summary>
    /// The ceiling on one transfer, so an owner that trickles forever cannot
    /// hold the press open. R-9's 100 KB cap bounds the size; this bounds time.
    /// </summary>
    public const int TotalMs = 5000;
}

/// <summary>
/// Where a bare <c>toggle</c> gets its text.
///
/// A seam rather than a call because it is Phase 4's whole job — reading the
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
    /// <para><b>Synchronous on purpose.</b> An async seam here would buy nothing
    /// and would put the capture on a different thread from the gate decision
    /// that depends on it.</para>
    ///
    /// <para><b>It can now block for as long as <see cref="SelectionWait"/>
    /// allows, and that is a deliberate reversal.</b> This used to say the cap
    /// was 300 ms and "inside the acknowledgement budget". It was — and it was
    /// also short enough that a busy Firefox or VS Code lost the press
    /// entirely, silently, which is the failure R-5 exists to prevent. Waiting
    /// out a busy application beats answering instantly with nothing, so the
    /// press may now take a moment when the owner is slow. It is unchanged in
    /// the ordinary case: a responsive owner answers in tens of
    /// milliseconds.</para>
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

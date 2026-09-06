using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Daemon.Interop;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Reads the selection on a Wayland session, through ext-data-control-v1.
///
/// <para><b>Why this exists.</b> <see cref="X11SelectionSource"/> reads X11
/// PRIMARY, and on a Wayland session a native Wayland client's selection never
/// reaches X11 at all. Measured on Plasma 6.6.6 / Ubuntu 26.04, 2026-08-22: the
/// only X11 client running on the user's desktop was one Electron app, and
/// <c>vst-ctl read</c> answered "nothing is selected" for every other
/// application — which is the whole product, silently doing nothing.</para>
///
/// <para><b>Why it replaces rather than joins the X11 source.</b> KWin bridges
/// XWayland clients' selections into Wayland, so this source sees legacy X11
/// applications too — verified by having <c>xclip</c> take PRIMARY and reading
/// it back through this path. On a Wayland session it is therefore a strict
/// superset of the X11 source, and running both would only add a way for them to
/// disagree.</para>
///
/// <para>The <c>display</c> argument of <see cref="Capture"/> is ignored. It
/// carries <c>$DISPLAY</c> as the client saw it, which is an X11 concept; the
/// Wayland equivalent would be <c>$WAYLAND_DISPLAY</c>, and a daemon started
/// outside the session cannot use it anyway — see the note on the failure
/// message below.</para>
/// </summary>
public sealed class WaylandSelectionSource : ISelectionSource
{
    private readonly bool _clipboardFallback;
    private readonly Func<bool, WaylandNative.WaylandCapture> _capture;

    public WaylandSelectionSource(bool clipboardFallback = false)
        : this(clipboardFallback, primary => WaylandNative.Capture(primary, Budget)) { }

    /// <summary>
    /// The seam the tests use. The real capture needs a compositor, a seat and an
    /// application holding a selection, so without it every sentence this class
    /// chooses between is reachable only by hand — which is how the timeout
    /// message came to say the owner had CLOSED when it was merely busy.
    /// </summary>
    internal WaylandSelectionSource(
        bool clipboardFallback, Func<bool, WaylandNative.WaylandCapture> capture)
    {
        _clipboardFallback = clipboardFallback;
        _capture = capture;
    }

    /// <summary>R-9, and the same number the X11 source caps at.</summary>
    public const int MaxChars = 100 * 1024;

    /// <summary>
    /// How long to wait on the application that owns the selection. See
    /// <see cref="WaylandNative.ReceiveBudget"/> for why it is three numbers and
    /// not one, and for the report that made it so.
    /// </summary>
    internal static WaylandNative.ReceiveBudget Budget => WaylandNative.ReceiveBudget.Default;

    public SelectionResult Capture(string? display = null)
    {
        var got = _capture(true);

        if (!got.Connected)
            return SelectionResult.None(
                "no Wayland display. The daemon was started outside a graphical " +
                "session, so it cannot see what you have selected — speak text " +
                "explicitly, or start it from the desktop session.");

        if (!got.ProtocolPresent)
            return SelectionResult.None(
                "this compositor does not offer ext-data-control-v1, so a " +
                "background process cannot read the selection. KDE and wlroots " +
                "compositors support it; GNOME deliberately does not. Copy the " +
                "text and use `vst-ctl speak`, or run an X11 session.");

        // Nothing owns PRIMARY. On Wayland this is a real state, unlike X11 where
        // the last owner keeps it after the user clicks away — so unlike the X11
        // path there is no "you selected the same thing again" ambiguity here.
        if (!got.HadOffer && got.Error is null)
        {
            if (!_clipboardFallback)
                return SelectionResult.None("nothing is selected.");

            var clip = _capture(false);
            if (!clip.HadOffer || string.IsNullOrEmpty(clip.Text))
                return SelectionResult.None("nothing is selected, and the clipboard is empty.");

            return Cap(clip.Text!, "read from the clipboard, because nothing was selected");
        }

        if (got.Error is { } why)
            return SelectionResult.None(why);

        // THE APPLICATION IS BUSY, WHICH IS NOT THE SAME AS GONE.
        //
        // Reported 2026-09-06: the hotkey does nothing on text selected in
        // Firefox or VS Code, repeatedly, for ten seconds or more — and the same
        // text pasted into Kate and selected there reads immediately. Handing
        // over a selection is a round trip through the owning application's event
        // loop, and a browser or an Electron app stalls that loop for hundreds of
        // milliseconds under load while Kate answers in about fifteen.
        //
        // The old message for this said the owner "may have closed", which is a
        // diagnosis of the wrong failure and sends a person looking in the wrong
        // place. It is also the only thing they would have had to go on, since it
        // reaches a stderr that a desktop shortcut discards.
        if (got.TimedOut && string.IsNullOrEmpty(got.Text))
            return SelectionResult.None(
                $"the application holding the selection did not hand it over within " +
                $"{Budget.FirstByteMs / 1000.0:0.#} s. It is most likely busy — browsers and " +
                "Electron apps stall their event loop for hundreds of milliseconds at a " +
                "time. Press again in a moment, or copy the text elsewhere and select it there.");

        if (string.IsNullOrEmpty(got.Text))
            return SelectionResult.None(
                "the selection's owner offered text and then sent none. It may " +
                "have closed between the selection being made and being read.");

        // A PARTIAL TRANSFER IS SPOKEN, AND SAID SO. Refusing would throw away
        // text the user can hear; returning it silently — which is what the old
        // loop did, having no way to tell a timeout from an end of file — reads
        // half a passage and stops with nothing anywhere reporting a fault.
        if (got.TimedOut)
            return Cap(got.Text!,
                $"the application stopped sending part way through, so only the first " +
                $"{got.Text!.Length} characters were read");

        return Cap(got.Text!, null);
    }

    /// <summary>
    /// R-9. Truncates at a sentence boundary and says what was dropped — the
    /// notice reaches the event stream and the tray tooltip, so a user who asked
    /// for a book hears the first part and is told why it stopped.
    /// </summary>
    private static SelectionResult Cap(string text, string? extraNotice)
    {
        string capped = TextCap.Apply(text, MaxChars, out string? notice);
        string? combined = (notice, extraNotice) switch
        {
            (null, null) => null,
            ({ } n, null) => n,
            (null, { } e) => e,
            ({ } n, { } e) => e + "; " + n,
        };
        return SelectionResult.Captured(capped, combined);
    }
}

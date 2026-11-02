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
    private readonly ICopyKey? _copyKey;

    public WaylandSelectionSource(bool clipboardFallback = false)
        : this(clipboardFallback, primary => WaylandNative.Capture(primary, Budget)) { }

    internal WaylandSelectionSource(bool clipboardFallback, ICopyKey? copyKey)
        : this(clipboardFallback, primary => WaylandNative.Capture(primary, Budget), copyKey) { }

    /// <summary>
    /// The seam the tests use. The real capture needs a compositor, a seat and an
    /// application holding a selection, so without it every sentence this class
    /// chooses between is reachable only by hand — which is how the timeout
    /// message came to say the owner had CLOSED when it was merely busy.
    /// </summary>
    internal WaylandSelectionSource(
        bool clipboardFallback, Func<bool, WaylandNative.WaylandCapture> capture,
        ICopyKey? copyKey = null, int copyWaitMs = CopyWaitMs)
    {
        _clipboardFallback = clipboardFallback;
        _capture = capture;
        _copyKey = copyKey;
        _copyWaitMs = copyWaitMs;
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
            if (TryAutoCopy(null) is { } autoCopied) return autoCopied;

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

        if (TryAutoCopy(got.Text!) is { } autoCopy) return autoCopy;

        if (_clipboardFallback && CopiedSinceSelected(got.Text!) is { } copied)
            return Cap(copied,
                "read from the clipboard, because the selection has not changed since " +
                "the last read and something was copied since");

        return Cap(got.Text!, null);
    }

    /// <summary>
    /// How long to wait for the clipboard to change after Ctrl+C. A copy lands in
    /// tens of milliseconds (measured 2026-10-30); a busy application takes longer.
    /// </summary>
    internal const int CopyWaitMs = 1000;
    private const int CopyPollMs = 40;
    private readonly int _copyWaitMs;

    private readonly object _autoLock = new();
    private bool _autoSeen;
    private string? _autoLastPrimary;
    private string? _autoLastCopied;

    /// <summary>
    /// <b>AutoCopy.</b> When the selection is the same as at the last press — which
    /// is what an application that never publishes its highlight looks like, and
    /// also a second press on the same text — press Ctrl+C and read what arrives.
    ///
    /// <para>Applications that do publish are never touched: a new highlight
    /// changes the selection, so the press reads it and no key is sent. The first
    /// press after the daemon starts has nothing to compare with and counts as
    /// unchanged, which costs one clipboard-history entry in an ordinary
    /// application and reads the right text in PdfCraft.</para>
    ///
    /// <para><b>Whether the copy happened is judged by the clipboard changing.</b>
    /// If it does not change, the previous AutoCopy's text is returned when the
    /// clipboard still holds it (the second press on the same highlight, whose
    /// copy is identical), and otherwise this returns null so the ordinary rules
    /// run — Ctrl+C with nothing highlighted must not make the press fail.</para>
    /// </summary>
    private SelectionResult? TryAutoCopy(string? primary)
    {
        if (_copyKey is null) return null;

        lock (_autoLock)
        {
            bool unchanged = !_autoSeen || string.Equals(primary, _autoLastPrimary, StringComparison.Ordinal);
            _autoSeen = true;
            _autoLastPrimary = primary;
            if (!unchanged) { _autoLastCopied = null; return null; }
        }

        string? before = ClipboardText();
        var sent = _copyKey.Send();
        if (!sent.Sent)
        {
            // Waiting on a dialog: say so and stop. Any other failure falls through
            // to the ordinary read, with the reason in the daemon log.
            return sent.Pending ? SelectionResult.None(sent.Why!) : null;
        }

        string? text = null;
        long deadline = Environment.TickCount64 + _copyWaitMs;
        while (true)
        {
            string? now = ClipboardText();
            if (now is not null && !string.Equals(now, before, StringComparison.Ordinal)) { text = now; break; }
            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(CopyPollMs);
        }

        lock (_autoLock)
        {
            if (text is null && before is not null
                && string.Equals(before, _autoLastCopied, StringComparison.Ordinal))
                text = before;                       // the same highlight copied again
            if (text is null) return null;
            _autoLastCopied = text;
        }

        return Cap(text, "read by pressing Ctrl+C for you, because the selection had not changed");
    }

    private string? ClipboardText()
    {
        var clip = _capture(false);
        return clip.HadOffer && clip.Error is null && !clip.TimedOut && !string.IsNullOrEmpty(clip.Text)
            ? clip.Text : null;
    }

    private readonly object _seenLock = new();
    private bool _seenAny;
    private string? _lastPrimary;
    private string? _lastClipboard;
    private bool _clipboardNewer;

    /// <summary>
    /// The clipboard's text when it, and not PRIMARY, is what the user touched
    /// last; otherwise null.
    ///
    /// <para><b>Reported 2026-10-23 from PdfCraft</b>, an egui application:
    /// highlighting text there never reaches PRIMARY — measured by polling both
    /// selections while the user highlighted, where only Ctrl+C changed
    /// anything, and only CLIPBOARD. So the press read whatever another window
    /// had last selected, which was usually the passage already playing. The X11
    /// source answers this with <see cref="VibeSuperTonic.Core.Selection.SelectionFreshness"/>,
    /// but data-control has no TIMESTAMP, so the order is reconstructed from what
    /// each selection held at the previous press: PRIMARY unchanged and
    /// CLIPBOARD changed means a copy was the latest thing the user did.</para>
    ///
    /// <para>The verdict sticks until PRIMARY changes, as the X11 rule's does
    /// (a copy stays newer than a selection made before it), so a second press
    /// in the same window reads the copy again rather than the stale selection.
    /// Both changing since the last press cannot be ordered, and reads PRIMARY.
    /// The first press after the daemon starts has nothing to compare against
    /// and reads PRIMARY too.</para>
    /// </summary>
    private string? CopiedSinceSelected(string primary)
    {
        var clip = _capture(false);
        bool clipFailed = clip.Error is not null || clip.TimedOut;
        string? clipText = clip.HadOffer && !string.IsNullOrEmpty(clip.Text) ? clip.Text : null;

        lock (_seenLock)
        {
            bool primaryChanged = !_seenAny || !string.Equals(primary, _lastPrimary, StringComparison.Ordinal);
            bool clipChanged = _seenAny && !clipFailed && clipText is not null
                            && !string.Equals(clipText, _lastClipboard, StringComparison.Ordinal);

            if (primaryChanged) _clipboardNewer = false;
            else if (clipChanged) _clipboardNewer = true;

            _seenAny = true;
            _lastPrimary = primary;
            // A clipboard that could not be read says nothing about whether it
            // changed, so the next press compares against the last good read.
            if (!clipFailed) _lastClipboard = clipText;

            return _clipboardNewer && !clipFailed && clipText is not null
                && !string.Equals(clipText, primary, StringComparison.Ordinal)
                ? clipText : null;
        }
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

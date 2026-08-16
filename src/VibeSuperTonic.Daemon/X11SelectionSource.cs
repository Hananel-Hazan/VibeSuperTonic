using System.Runtime.InteropServices;
using System.Text;
using VibeSuperTonic.Core.Selection;
using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Daemon.Interop;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Reads the X11 PRIMARY selection — what the user has highlighted — without
/// ever touching CLIPBOARD.
///
/// <para>PRIMARY is the right selection and CLIPBOARD is the wrong one: PRIMARY
/// <em>is</em> the highlight, updated as the user drags, and reading it costs
/// the user nothing. Taking CLIPBOARD would mean either stealing what they last
/// copied or making them press Ctrl+C first, and a read-aloud tool that
/// clobbers the clipboard gets uninstalled.</para>
///
/// <para><b>Everything is per request and nothing is cached</b> — the display
/// connection included. A press is at most a few hundred microseconds of
/// connection setup against a ~600 ms floor before any sound, so there is
/// nothing to win by holding one, and plenty to lose: the daemon outlives
/// individual X sessions, and a cached connection to a dead session is a
/// permanently broken hotkey. The one thing carried between requests is the
/// last selection's <em>identity</em> — two numbers, no handles — which is what
/// makes a stale selection detectable at all.</para>
///
/// <para><b>CLIPBOARD is read only under opt-in, and only when PRIMARY is
/// provably stale.</b> The paragraph above is still the rule: a tool that
/// clobbers or helps itself to the clipboard gets uninstalled. But some windows
/// render selectable text and never claim PRIMARY — Gmail's in-frame attachment
/// viewer in Brave, found on 2026-08-16 — and for those the clipboard is the
/// only place the text can be got at without synthesising keystrokes. See
/// <see cref="SelectionFreshness"/> for why staleness is decidable and what the
/// opt-in is protecting against.</para>
/// </summary>
public sealed class X11SelectionSource : ISelectionSource
{
    private readonly bool _clipboardFallback;

    /// <summary>
    /// PRIMARY's owner and ownership time as of the last capture that read it.
    ///
    /// <para>Not a cache: nothing is served from it and it holds no X resources.
    /// It is the only way to answer "has the selection been re-established since
    /// I last looked", which is the question that distinguishes a fresh
    /// selection from an application that never published one.</para>
    /// </summary>
    private SelectionStamp? _lastPrimary;

    public X11SelectionSource(bool clipboardFallback = false) =>
        _clipboardFallback = clipboardFallback;
    /// <summary>R-9. Roughly a long article; Ctrl+A in a book is the case this exists for.</summary>
    public const int MaxChars = 100 * 1024;

    /// <summary>
    /// How long to wait for the owning window to answer.
    ///
    /// <para>Selection transfer is a round trip through another application's
    /// event loop, so a busy or wedged owner can simply never reply. 300 ms is
    /// short enough to stay inside the acknowledgement budget and long enough
    /// that a loaded Firefox still makes it.</para>
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Bytes to ask for in one go — 32-bit units, so this is 4 MB.
    ///
    /// <para>Deliberately far above <see cref="MaxChars"/>: the cap is in
    /// characters and this is in bytes, UTF-8 runs up to four bytes per
    /// character, and asking for too little would truncate mid-sequence before
    /// <see cref="TextCap"/> ever sees the text.</para>
    /// </summary>
    private const int RequestUnits = 1024 * 1024;

    public SelectionResult Capture(string? display = null)
    {
        EnsureErrorHandlerInstalled();

        string? name = string.IsNullOrWhiteSpace(display)
            ? Environment.GetEnvironmentVariable("DISPLAY")
            : display;

        if (string.IsNullOrWhiteSpace(name))
            return SelectionResult.None(
                "no X display. The daemon was started outside a graphical session, " +
                "so it cannot see what you have selected — speak text explicitly, " +
                "or start it from the desktop session.");

        IntPtr dpy = X11Native.XOpenDisplay(name);
        if (dpy == IntPtr.Zero)
            return SelectionResult.None(
                $"could not open X display {name}. If this is a remote or su'd " +
                "session, the daemon may not be authorised to connect to it.");

        try
        {
            return CaptureFrom(dpy);
        }
        finally
        {
            X11Native.XCloseDisplay(dpy);
        }
    }

    private SelectionResult CaptureFrom(IntPtr dpy)
    {
        IntPtr primary = X11Native.XInternAtom(dpy, "PRIMARY", false);
        IntPtr owner = X11Native.XGetSelectionOwner(dpy, primary);

        // Nothing is highlighted anywhere. A no-op with a tray blip — never a
        // fallback to re-reading the last thing, which is surprising and reads
        // as a bug.
        if (owner == X11Native.None)
            return SelectionResult.None("nothing is selected.");

        // R-6: our own Reader tab's text is selectable, so selecting any of it
        // makes us the PRIMARY owner and the next press would re-read our own
        // window instead of the document the user was on.
        if (OwnerIsThisProcess(dpy, owner))
            return SelectionResult.None(
                "the current selection is inside VibeSuperTonic's own window; " +
                "select text in another application first.");

        IntPtr root = X11Native.XDefaultRootWindow(dpy);

        // An unmapped 1x1 window purely as a delivery address. Never shown, and
        // destroyed before this method returns.
        IntPtr window = X11Native.XCreateSimpleWindow(dpy, root, 0, 0, 1, 1, 0, 0, 0);

        try
        {
            return Decide(dpy, window, primary, owner);
        }
        finally
        {
            X11Native.XDestroyWindow(dpy, window);
        }
    }

    /// <summary>
    /// Work out which selection holds what the user meant, then read it.
    /// </summary>
    private SelectionResult Decide(IntPtr dpy, IntPtr window, IntPtr primary, IntPtr owner)
    {
        uint? primaryTime = SelectionTime(dpy, window, primary);

        // An owner that will not answer TIMESTAMP tells us nothing about
        // freshness, and guessing would put a misleading notice on a perfectly
        // good read. Fall through to the behaviour that shipped before this
        // check existed, and forget any previous claim so the next press does
        // not compare against a stamp this one could not refresh.
        if (primaryTime is not uint stampTime)
        {
            _lastPrimary = null;
            return ReadSelection(dpy, window, primary);
        }

        var stamp = new SelectionStamp((ulong)owner.ToInt64(), stampTime);
        SelectionStamp? clipboardStamp = null;
        IntPtr clipboard = X11Native.None;

        if (_clipboardFallback)
        {
            clipboard = X11Native.XInternAtom(dpy, "CLIPBOARD", false);
            IntPtr clipOwner = X11Native.XGetSelectionOwner(dpy, clipboard);

            // Our own clipboard would be no more use than our own selection.
            if (clipOwner != X11Native.None && !OwnerIsThisProcess(dpy, clipOwner)
                && SelectionTime(dpy, window, clipboard) is uint clipTime)
            {
                clipboardStamp = new SelectionStamp((ulong)clipOwner.ToInt64(), clipTime);
            }
        }

        SelectionUse use = SelectionFreshness.Decide(
            stamp, _lastPrimary, clipboardStamp, _clipboardFallback);

        SelectionResult result = ReadSelection(
            dpy, window, use == SelectionUse.Clipboard ? clipboard : primary);

        // Recorded whichever selection was read, and only on a read that got
        // that far: the comparison is about PRIMARY's claim, not about what we
        // did with it. Updating it on a failed read would make the next press
        // call a genuinely new selection stale.
        if (result.Ok || use != SelectionUse.Clipboard)
            _lastPrimary = stamp;

        if (!result.Ok) return result;

        string? extra = use switch
        {
            SelectionUse.PrimaryUnchanged =>
                "the selection has not changed since the last read. If you just " +
                "selected something, that application may not publish selections " +
                "to X11 — copy it with Ctrl+C and press again.",

            SelectionUse.Clipboard =>
                "read from the clipboard: the selection had not changed, and you " +
                "copied something more recently.",

            _ => null,
        };

        if (extra is null) return result;

        // R-9's truncation notice can arrive with either of the above, and
        // dropping one to keep the other would hide that the text was cut.
        return result with
        {
            Notice = result.Notice is { } had ? had + " " + extra : extra,
        };
    }

    /// <summary>
    /// Ask <paramref name="selection"/> when its owner acquired it.
    ///
    /// <para>ICCCM requires every owner to answer TIMESTAMP, but "requires" is
    /// not "does", so a null return has to mean "unknown" rather than "old".</para>
    /// </summary>
    private static uint? SelectionTime(IntPtr dpy, IntPtr window, IntPtr selection)
    {
        IntPtr timestamp = X11Native.XInternAtom(dpy, "TIMESTAMP", false);
        IntPtr property = X11Native.XInternAtom(dpy, "VST_SELECTION_TIME", false);

        X11Native.XConvertSelection(dpy, selection, timestamp, property, window, X11Native.CurrentTime);
        X11Native.XFlush(dpy);

        if (!WaitForSelectionNotify(dpy, window, timestamp, out XEvent reply)
            || reply.Property == X11Native.None)
            return null;

        int status = X11Native.XGetWindowProperty(
            dpy, window, reply.Property,
            longOffset: 0, longLength: 1,
            delete: true,
            requestedType: X11Native.None,
            out _, out int format,
            out nuint itemCount, out _,
            out IntPtr data);

        if (status != 0 || data == IntPtr.Zero) return null;

        try
        {
            // 32-bit property, widened to long by Xlib exactly as _NET_WM_PID is
            // — the same trap, and wrong in the same silent way if read as four
            // bytes.
            if (format != 32 || itemCount < 1) return null;
            return unchecked((uint)Marshal.ReadInt64(data));
        }
        finally
        {
            X11Native.XFree(data);
        }
    }

    /// <summary>
    /// Convert one selection to UTF-8 text and read the result.
    /// </summary>
    private static SelectionResult ReadSelection(IntPtr dpy, IntPtr window, IntPtr selection)
    {
        IntPtr utf8 = X11Native.XInternAtom(dpy, "UTF8_STRING", false);
        IntPtr target = X11Native.XInternAtom(dpy, "VST_SELECTION", false);

        X11Native.XConvertSelection(dpy, selection, utf8, target, window, X11Native.CurrentTime);
        X11Native.XFlush(dpy);

        if (!WaitForSelectionNotify(dpy, window, utf8, out XEvent reply))
            return SelectionResult.None(
                "the window holding the selection did not answer within " +
                $"{Timeout.TotalMilliseconds:F0} ms.");

        // The owner exists but cannot express the selection as UTF-8 text.
        // Some Java/Swing and a few Electron windows behave this way; the
        // plan records it as a documented limit rather than a bug to chase.
        if (reply.Property == X11Native.None)
            return SelectionResult.None(
                "the application holding the selection could not provide it as text.");

        return ReadProperty(dpy, window, reply.Property);
    }

    /// <summary>
    /// Pump events until the SelectionNotify for <paramref name="expectedTarget"/>
    /// arrives or the deadline passes.
    /// </summary>
    /// <remarks>
    /// The target has to be matched, not just the requestor. A capture now makes
    /// two conversions on this one window — TIMESTAMP, then UTF8_STRING — and
    /// the first reply is still queued when the second is issued. Matching on
    /// the requestor alone hands the text read the timestamp's reply, whose
    /// property is four bytes of server time that decode as plausible garbage
    /// rather than as an error. Found while writing the equivalent probe in
    /// Python, where it produced exactly that: ten characters of noise where the
    /// selection should have been.
    /// </remarks>
    private static bool WaitForSelectionNotify(
        IntPtr dpy, IntPtr window, IntPtr expectedTarget, out XEvent reply)
    {
        long deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;
        reply = default;

        while (Environment.TickCount64 < deadline)
        {
            // XNextEvent blocks with no timeout, which on an unanswered request
            // would hang the daemon rather than the press. Only ever called
            // once XPending has confirmed something is queued.
            if (X11Native.XPending(dpy) == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            X11Native.XNextEvent(dpy, out XEvent e);

            // Ours specifically: this connection is private to this call, but a
            // stray event on it should not be mistaken for the answer.
            if (e.Type == X11Native.SelectionNotify
                && e.Requestor == window
                && e.Target == expectedTarget)
            {
                reply = e;
                return true;
            }
        }

        return false;
    }

    private static SelectionResult ReadProperty(IntPtr dpy, IntPtr window, IntPtr property)
    {
        int status = X11Native.XGetWindowProperty(
            dpy, window, property,
            longOffset: 0, longLength: RequestUnits,
            delete: true,                       // consume it; the window dies next anyway
            requestedType: X11Native.None,      // AnyPropertyType
            out IntPtr actualType, out int format,
            out nuint itemCount, out nuint bytesAfter,
            out IntPtr data);

        if (status != 0 || data == IntPtr.Zero)
            return SelectionResult.None("the selection could not be read.");

        try
        {
            // INCR: the owner wants to transfer this in chunks. Implementing the
            // protocol properly is a fair amount of code for selections far
            // above the R-9 cap, so this reports honestly rather than
            // half-implementing it and silently returning the length header.
            IntPtr incr = X11Native.XInternAtom(dpy, "INCR", true);
            if (incr != X11Native.None && actualType == incr)
                return SelectionResult.None(
                    "the selection is too large to transfer in one piece " +
                    "(the application offered it incrementally). Select less of it.");

            if (itemCount == 0)
                return SelectionResult.None("the selection is empty.");

            // format is bits per item: 8 for a byte string, which is what
            // UTF8_STRING is. Anything else is not text we asked for.
            if (format != 8)
                return SelectionResult.None("the selection is not text.");

            byte[] bytes = new byte[(int)itemCount];
            Marshal.Copy(data, bytes, 0, bytes.Length);

            string text = Encoding.UTF8.GetString(bytes);

            // Whitespace-only counts as nothing selected. A press that speaks a
            // space is indistinguishable from one that did nothing, except it
            // also consumed the toggle.
            if (string.IsNullOrWhiteSpace(text))
                return SelectionResult.None("the selection is empty.");

            string capped = TextCap.Apply(text, MaxChars, out string? notice);

            // Only reachable if a single selection exceeded the 4 MB read above,
            // which the cap makes academic — but silence here would look exactly
            // like a clean read.
            if (bytesAfter > 0 && notice is null)
                notice = "the selection was longer than could be read in one piece; " +
                         "reading what arrived.";

            return SelectionResult.Captured(capped, notice);
        }
        finally
        {
            X11Native.XFree(data);
        }
    }

    /// <summary>
    /// R-6. Compares the owning window's <c>_NET_WM_PID</c> against our own.
    ///
    /// <para>Comparing window ids would be more direct and does not work: by
    /// Phase 6 the owner will be an Avalonia window created on a different
    /// display connection, and this one has no register of it. <c>_NET_WM_PID</c>
    /// is the EWMH property every mainstream toolkit sets, so it survives that.
    /// A window that does not set it is treated as foreign — the failure this
    /// direction is re-reading our own text once, which is visible and
    /// harmless; the other direction would refuse to read a legitimate
    /// selection and look broken.</para>
    /// </summary>
    private static bool OwnerIsThisProcess(IntPtr dpy, IntPtr owner)
    {
        IntPtr pidAtom = X11Native.XInternAtom(dpy, "_NET_WM_PID", true);
        if (pidAtom == X11Native.None) return false;

        int status = X11Native.XGetWindowProperty(
            dpy, owner, pidAtom,
            longOffset: 0, longLength: 1,
            delete: false,
            requestedType: X11Native.XA_CARDINAL,
            out _, out int format,
            out nuint itemCount, out _,
            out IntPtr data);

        if (status != 0 || data == IntPtr.Zero) return false;

        try
        {
            if (format != 32 || itemCount < 1) return false;

            // 32-bit property, but Xlib returns CARDINAL properties widened to
            // long — 64 bits here. Reading four bytes would work on little
            // endian by luck and is wrong; ReadInt64 is what Xlib actually
            // wrote.
            long pid = Marshal.ReadInt64(data);
            return pid == Environment.ProcessId;
        }
        finally
        {
            X11Native.XFree(data);
        }
    }

    // ------------------------------------------------------------------ errors

    private static readonly Lock ErrorHandlerGate = new();
    private static X11Native.XErrorHandler? _errorHandler;

    /// <summary>
    /// Replace Xlib's default error handler, which calls <c>exit()</c>.
    ///
    /// <para>This is not defensive tidiness. <c>XGetSelectionOwner</c> followed
    /// by a property read on that window is a race the user wins routinely:
    /// select some text, close the window, press the key. The owner is gone by
    /// the second call, Xlib raises <c>BadWindow</c>, and the default handler
    /// prints to stderr and terminates the process — so the daemon would
    /// vanish, mid-utterance, with no exception to catch and nothing in its own
    /// log. Same class of failure as calling <c>pa_simple_flush</c> from another
    /// thread, and just as invisible in testing, because nothing about a
    /// deliberate test closes a window at exactly the wrong moment.</para>
    ///
    /// <para>The handler is process-global and the delegate must outlive it, so
    /// it is installed once and held in a static field. Phase 6 note: Avalonia
    /// installs its own, and whichever runs last wins — if the tray or window
    /// ever starts swallowing X errors, this is why.</para>
    /// </summary>
    private static void EnsureErrorHandlerInstalled()
    {
        lock (ErrorHandlerGate)
        {
            if (_errorHandler is not null) return;

            _errorHandler = static (_, _) => 0;   // report nothing, and above all do not exit
            X11Native.XSetErrorHandler(_errorHandler);
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;
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
/// permanently broken hotkey.</para>
/// </summary>
public sealed class X11SelectionSource : ISelectionSource
{
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

    private static SelectionResult CaptureFrom(IntPtr dpy)
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

        IntPtr utf8 = X11Native.XInternAtom(dpy, "UTF8_STRING", false);
        IntPtr target = X11Native.XInternAtom(dpy, "VST_SELECTION", false);
        IntPtr root = X11Native.XDefaultRootWindow(dpy);

        // An unmapped 1x1 window purely as a delivery address. Never shown, and
        // destroyed before this method returns.
        IntPtr window = X11Native.XCreateSimpleWindow(dpy, root, 0, 0, 1, 1, 0, 0, 0);

        try
        {
            X11Native.XConvertSelection(dpy, primary, utf8, target, window, X11Native.CurrentTime);
            X11Native.XFlush(dpy);

            if (!WaitForSelectionNotify(dpy, window, out XEvent reply))
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
        finally
        {
            X11Native.XDestroyWindow(dpy, window);
        }
    }

    /// <summary>
    /// Pump events until our SelectionNotify arrives or the deadline passes.
    /// </summary>
    private static bool WaitForSelectionNotify(IntPtr dpy, IntPtr window, out XEvent reply)
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
            if (e.Type == X11Native.SelectionNotify && e.Requestor == window)
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

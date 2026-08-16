using System.Runtime.InteropServices;

namespace VibeSuperTonic.Daemon.Interop;

/// <summary>
/// The X11 surface needed to read a selection, and nothing else.
///
/// <para><b>A folder here rather than a VibeSuperTonic.Linux.X11 project</b>,
/// which is worth explaining because <c>Linux.Audio</c> made the opposite
/// choice. That project's own comment gives its reason — "Phase 6's app is a
/// second consumer" — and it does not transfer: [R-1] requires the Phase 6 UI
/// to drive the daemon over the socket like any external client, so nothing but
/// this daemon will ever capture a selection. Every other native surface in
/// this repository is already a folder inside the project that owns it
/// (<c>Engine/Interop</c> for SAPI, <c>Launcher/Export</c> for Media
/// Foundation, <c>Launcher/Integrity</c> for DXGI and Restart Manager), which
/// is also the .NET convention; <c>Linux.Audio</c> is the exception.</para>
///
/// <para>The isolation that matters is <see cref="ISelectionSource"/>, and that
/// already exists: it is what makes Wayland an implementation swap and what
/// lets the daemon run headless. A project boundary would add a compile-time
/// barrier on top, at the cost of moving that seam out of the daemon that is
/// its only consumer.</para>
///
/// <para><b>No <c>unsafe</c> anywhere, which was the condition on that
/// decision.</b> The one call that looks like it needs it —
/// <c>XGetWindowProperty</c>, whose last parameter is <c>unsigned char**</c> —
/// takes an <c>out IntPtr</c> and is read back with <see cref="Marshal.Copy"/>.
/// <c>XEvent</c> is a 192-byte union, declared here with explicit field offsets
/// instead of a <c>fixed</c> buffer for the same reason. Had either needed
/// pointers, <c>AllowUnsafeBlocks</c> would have had to go on the whole daemon,
/// and the separate project would have been the better trade.</para>
/// </summary>
internal static class X11Native
{
    private const string Lib = "libX11.so.6";

    // ---------------------------------------------------------------- display

    [DllImport(Lib)]
    internal static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? display);

    [DllImport(Lib)]
    internal static extern int XCloseDisplay(IntPtr display);

    [DllImport(Lib)]
    internal static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(Lib)]
    internal static extern int XFlush(IntPtr display);

    // ----------------------------------------------------------------- atoms

    [DllImport(Lib)]
    internal static extern IntPtr XInternAtom(
        IntPtr display,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.I1)] bool onlyIfExists);

    // ---------------------------------------------------------------- windows

    [DllImport(Lib)]
    internal static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y,
        uint width, uint height, uint borderWidth, nuint border, nuint background);

    [DllImport(Lib)]
    internal static extern int XDestroyWindow(IntPtr display, IntPtr window);

    // -------------------------------------------------------------- selection

    [DllImport(Lib)]
    internal static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);

    [DllImport(Lib)]
    internal static extern int XConvertSelection(
        IntPtr display, IntPtr selection, IntPtr target,
        IntPtr property, IntPtr requestor, IntPtr time);

    // ------------------------------------------------------------- properties

    /// <summary>
    /// <paramref name="longLength"/> and <paramref name="longOffset"/> are in
    /// 32-bit units, not bytes — a units mix-up here reads a quarter of the
    /// selection and looks like a truncation bug.
    /// </summary>
    [DllImport(Lib)]
    internal static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property,
        nint longOffset, nint longLength,
        [MarshalAs(UnmanagedType.I1)] bool delete,
        IntPtr requestedType,
        out IntPtr actualType, out int actualFormat,
        out nuint itemCount, out nuint bytesAfter,
        out IntPtr data);

    [DllImport(Lib)]
    internal static extern int XFree(IntPtr data);

    // ------------------------------------------------------------------ events

    [DllImport(Lib)]
    internal static extern int XPending(IntPtr display);

    [DllImport(Lib)]
    internal static extern int XNextEvent(IntPtr display, out XEvent e);

    // ------------------------------------------------------------------ errors

    internal delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(Lib)]
    internal static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    // --------------------------------------------------------------- constants

    internal const int SelectionNotify = 31;

    /// <summary>Predefined atom <c>XA_CARDINAL</c>, used to read <c>_NET_WM_PID</c>.</summary>
    internal static readonly IntPtr XA_CARDINAL = 6;

    /// <summary><c>None</c> — the null atom and the null window.</summary>
    internal static readonly IntPtr None = IntPtr.Zero;

    /// <summary><c>CurrentTime</c>.</summary>
    internal static readonly IntPtr CurrentTime = IntPtr.Zero;
}

/// <summary>
/// The XEvent union, 192 bytes on 64-bit, overlaid with just the
/// <c>XSelectionEvent</c> fields this code reads.
///
/// <para>Explicit offsets rather than a <c>fixed</c> byte buffer so the file
/// needs no <c>unsafe</c>. The offsets follow the LP64 layout of
/// <c>XSelectionEvent</c>: <c>int type</c> padded to 8, then <c>serial</c>,
/// <c>send_event</c> padded to 8, <c>display</c>, <c>requestor</c>,
/// <c>selection</c>, <c>target</c>, <c>property</c>, <c>time</c>.</para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] internal int Type;

    [FieldOffset(32)] internal IntPtr Requestor;
    [FieldOffset(40)] internal IntPtr Selection;
    [FieldOffset(48)] internal IntPtr Target;

    /// <summary>
    /// The property the owner wrote the data into, or <c>None</c> when it
    /// declined to supply the target at all.
    /// </summary>
    [FieldOffset(56)] internal IntPtr Property;
}

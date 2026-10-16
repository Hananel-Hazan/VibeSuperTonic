using System.Runtime.InteropServices;
using System.Text;

// x11-own — take ownership of the X11 PRIMARY selection and serve a fixed
// string as UTF8_STRING until killed.
//
//   x11-own "The sea is everything."      # owns PRIMARY, serves that text
//   x11-own --file <path>                 # serves a file's contents
//   x11-own --size 200000                 # serves generated text of N chars
//
// Exists so Phase 4's capture path is testable: stock Mint has no xclip or
// xsel, so nothing on the machine can put text on PRIMARY on demand.
//
// The --size mode is the R-9 case. Selecting 100 KB by hand is not something
// anyone will do twice, and the truncation path is the one most likely to be
// wrong and least likely to be noticed.

const string Lib = "libX11.so.6";

// Refuse every target, the way some Java/Swing and Electron windows do. The
// plan records that as a documented limit rather than a bug to chase, and this
// is what lets the message the user gets be checked rather than assumed.
bool refuse = args.Contains("--refuse");

// Accept ownership and then never answer a request, the way a wedged or very
// busy application does. Exercises the requestor's timeout rather than its
// error handling — a window that is present, owns the selection, and simply
// does not reply.
bool hang = args.Contains("--hang");

// Flags are filtered out before positional parsing: "--hang <text>" and
// "<text> --hang" must both work, and an earlier version silently fell through
// to the usage message for both, which made a test look like it had passed a
// timeout when the owner had in fact exited.
string[] positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

string text;
if (refuse || hang)
    text = positional.Length > 0 ? positional[0] : "placeholder";
else if (args.Contains("--file") && positional.Length > 0)
    text = File.ReadAllText(positional[0]);
else if (args.Contains("--size") && positional.Length > 0)
    text = GenerateProse(int.Parse(positional[0]));
else if (positional.Length > 0)
    text = positional[0];
else
{
    Console.Error.WriteLine("x11-own <text> | --file <path> | --size <chars> | --refuse | --hang");
    return 2;
}

byte[] payload = Encoding.UTF8.GetBytes(text);

IntPtr dpy = XOpenDisplay(null);
if (dpy == IntPtr.Zero) { Console.Error.WriteLine("cannot open display"); return 1; }

IntPtr root = XDefaultRootWindow(dpy);
IntPtr win = XCreateSimpleWindow(dpy, root, 0, 0, 1, 1, 0, 0, 0);

IntPtr primary = XInternAtom(dpy, "PRIMARY", false);
IntPtr utf8 = XInternAtom(dpy, "UTF8_STRING", false);
IntPtr targets = XInternAtom(dpy, "TARGETS", false);
IntPtr atomAtom = XInternAtom(dpy, "ATOM", false);

XSetSelectionOwner(dpy, primary, win, IntPtr.Zero);
if (XGetSelectionOwner(dpy, primary) != win)
{
    Console.Error.WriteLine("failed to take PRIMARY ownership");
    return 1;
}

Console.WriteLine($"owning PRIMARY, serving {payload.Length} bytes ({text.Length} chars). Ctrl-C to release.");

while (true)
{
    XNextEvent(dpy, out XEvent e);

    // SelectionRequest
    if (e.Type != 30) continue;

    IntPtr requestor = e.Requestor;
    IntPtr target = e.Target;
    IntPtr property = e.Property;

    // A requestor using the obsolete convention passes None and expects the
    // target atom to be used as the property.
    if (property == IntPtr.Zero) property = target;

    if (hang) continue;

    IntPtr replyProperty = property;

    if (refuse)
    {
        replyProperty = IntPtr.Zero;
    }
    else if (target == targets)
    {
        // Advertise what we can supply. Real owners do this and a well-behaved
        // requestor may ask first.
        IntPtr[] supported = [targets, utf8];
        byte[] buffer = new byte[supported.Length * IntPtr.Size];
        for (int i = 0; i < supported.Length; i++)
            BitConverter.GetBytes((long)supported[i]).CopyTo(buffer, i * IntPtr.Size);

        XChangeProperty(dpy, requestor, property, atomAtom, 32, 0, buffer, supported.Length);
    }
    else if (target == utf8)
    {
        XChangeProperty(dpy, requestor, property, utf8, 8, 0, payload, payload.Length);
    }
    else
    {
        // Refuse: None tells the requestor we could not supply that target,
        // which is the path X11SelectionSource reports as "could not provide it
        // as text".
        replyProperty = IntPtr.Zero;
    }

    var notify = new XEvent
    {
        Type = 31,                    // SelectionNotify
        SendEvent = 1,
        Display = dpy,
        Requestor = requestor,
        Selection = e.Selection,
        Target = target,
        Property = replyProperty,
        Time = e.Time,
    };

    XSendEvent(dpy, requestor, false, 0, ref notify);
    XFlush(dpy);
}

static string GenerateProse(int chars)
{
    // Real sentences, so the truncation path has boundaries to find. A wall of
    // 'x' would exercise only the hard-cut branch.
    var sb = new StringBuilder(chars + 128);
    int n = 0;
    while (sb.Length < chars)
        sb.Append($"This is sentence number {++n} of a deliberately long selection. ");
    return sb.ToString(0, chars);
}

[DllImport(Lib)] static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? d);
[DllImport(Lib)] static extern IntPtr XDefaultRootWindow(IntPtr d);
[DllImport(Lib)] static extern IntPtr XCreateSimpleWindow(IntPtr d, IntPtr parent, int x, int y, uint w, uint h, uint bw, nuint border, nuint background);
[DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.I1)] bool onlyIfExists);
[DllImport(Lib)] static extern int XSetSelectionOwner(IntPtr d, IntPtr selection, IntPtr owner, IntPtr time);
[DllImport(Lib)] static extern IntPtr XGetSelectionOwner(IntPtr d, IntPtr selection);
[DllImport(Lib)] static extern int XNextEvent(IntPtr d, out XEvent e);
[DllImport(Lib)] static extern int XChangeProperty(IntPtr d, IntPtr w, IntPtr property, IntPtr type, int format, int mode, byte[] data, int elements);
[DllImport(Lib)] static extern int XSendEvent(IntPtr d, IntPtr w, [MarshalAs(UnmanagedType.I1)] bool propagate, nint mask, ref XEvent e);
[DllImport(Lib)] static extern int XFlush(IntPtr d);

[StructLayout(LayoutKind.Explicit, Size = 192)]
struct XEvent
{
    [FieldOffset(0)] public int Type;
    [FieldOffset(16)] public int SendEvent;
    [FieldOffset(24)] public IntPtr Display;

    // XSelectionRequestEvent: owner(32) requestor(40) selection(48) target(56)
    // property(64) time(72). XSelectionEvent shifts up by one field because it
    // has no owner: requestor(32) selection(40) target(48) property(56)
    // time(64). These overlap, so the two are read and written through separate
    // accessors rather than shared ones.
    [FieldOffset(40)] public IntPtr RequestRequestor;
    [FieldOffset(48)] public IntPtr RequestSelection;
    [FieldOffset(56)] public IntPtr RequestTarget;
    [FieldOffset(64)] public IntPtr RequestProperty;
    [FieldOffset(72)] public IntPtr RequestTime;

    [FieldOffset(32)] public IntPtr NotifyRequestor;
    [FieldOffset(40)] public IntPtr NotifySelection;
    [FieldOffset(48)] public IntPtr NotifyTarget;
    [FieldOffset(56)] public IntPtr NotifyProperty;
    [FieldOffset(64)] public IntPtr NotifyTime;

    // Reading a SelectionRequest, writing a SelectionNotify.
    public IntPtr Requestor { get => RequestRequestor; set => NotifyRequestor = value; }
    public IntPtr Selection { get => RequestSelection; set => NotifySelection = value; }
    public IntPtr Target { get => RequestTarget; set => NotifyTarget = value; }
    public IntPtr Property { get => RequestProperty; set => NotifyProperty = value; }
    public IntPtr Time { get => RequestTime; set => NotifyTime = value; }
}

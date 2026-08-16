using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// avaprimary — does an Avalonia window claim the X11 PRIMARY selection?
//
//   avaprimary [--seconds 20]
//
// Opens a window holding a SelectableTextBlock, selects text in it several ways,
// and after each one asks the X server who owns PRIMARY. Prints the owner window,
// its _NET_WM_PID and its WM_CLASS — the same three facts the daemon's R-6 check
// looks at — and says whether that pid is this process.
//
// Read the result as follows:
//
//   owner never changes from the baseline
//       -> Avalonia does not claim PRIMARY. R-6 cannot fire, the daemon's
//          OwnerIsThisProcess is dead code on this toolkit, and Phase 6 owes it
//          nothing. R-6 becomes a note in the plan.
//
//   owner becomes a window whose _NET_WM_PID is ours
//       -> R-6 is real. Cheapest fix is to stop the Reader claiming PRIMARY at
//          all; the daemon's pid check is the fallback, and it works.
//
//   owner becomes one of our windows but carries NO _NET_WM_PID
//       -> the worst case, and not hypothetical: Phase 4 recorded that
//          Chromium's PRIMARY owner carries neither WM_CLASS nor _NET_WM_PID. The
//          pid check would then silently fail open and read our own text back,
//          and detection is not the answer — not claiming is.

int seconds = 20;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);

Console.WriteLine($"avaprimary: pid {Environment.ProcessId}, DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}");
Console.WriteLine();

using var x = new X11Probe();
Console.WriteLine("BASELINE (before any window exists)");
x.Report("  ");
Console.WriteLine();

AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(
    new[] { seconds.ToString() });

return 0;

internal sealed class App : Application
{
    private const string Sample = "The sea is everything. It covers seven tenths of the globe.";

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        int seconds = desktop.Args is { Length: > 0 } a ? int.Parse(a[0]) : 20;

        // No theme package is referenced, so nothing here may need a control
        // template. SelectableTextBlock draws its own text and is the control
        // Phase 6's Reader tab is actually specified to use, which is what makes
        // a themeless window the right measurement rather than a compromise.
        var block = new SelectableTextBlock
        {
            Text = Sample,
            FontSize = 18,
            Foreground = Brushes.Black,
            Margin = new Thickness(16),
        };

        var window = new Window
        {
            Title = "avaprimary",
            Width = 520,
            Height = 180,
            Background = Brushes.White,
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Children =
                {
                    new TextBlock
                    {
                        Text = "measuring PRIMARY ownership — closes by itself",
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(16, 16, 16, 0),
                    },
                    block,
                },
            },
        };

        desktop.MainWindow = window;
        window.Opened += (_, _) => _ = MeasureAsync(block, window, seconds);
    }

    private static async Task MeasureAsync(SelectableTextBlock block, Window window, int seconds)
    {
        using var x = new X11Probe();

        // The window exists and is mapped, but nothing has been selected. If
        // ownership has already moved here, the claim is not selection-driven at
        // all and everything below is beside the point.
        await Task.Delay(700);
        Console.WriteLine("WINDOW OPEN, nothing selected");
        x.Report("  ");
        Console.WriteLine();

        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            block.Focus();
            block.SelectAll();
        });
        await Task.Delay(500);
        Console.WriteLine($"AFTER SelectAll() — SelectedText is {block.SelectedText.Length} chars");
        x.Report("  ");
        Console.WriteLine();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A partial selection, in case the toolkit only publishes on a
            // range change rather than on a select-all.
            block.SelectionStart = 4;
            block.SelectionEnd = 7;
        });
        await Task.Delay(500);
        Console.WriteLine($"AFTER a range selection — SelectedText = \"{block.SelectedText}\"");
        x.Report("  ");
        Console.WriteLine();

        // Whatever is left is for a human with a mouse. A programmatic selection
        // and a dragged one go through different code in some toolkits, so the
        // measurement is not complete without the chance to try it — and if
        // nobody is watching, the poll simply reports no change and exits.
        Console.WriteLine($"POLLING for {seconds}s — drag-select inside the window to test a real one.");
        IntPtr last = x.Owner();
        for (int i = 0; i < seconds * 2; i++)
        {
            await Task.Delay(500);
            IntPtr now = x.Owner();
            if (now == last) continue;
            last = now;
            Console.WriteLine($"  [t+{i / 2.0:F1}s] owner changed:");
            x.Report("    ");
        }

        Console.WriteLine();
        Console.WriteLine("FINAL");
        x.Report("  ");
        await Dispatcher.UIThread.InvokeAsync(window.Close);
    }
}

/// <summary>
/// A second X connection, used only to ask questions. Self-contained P/Invokes
/// rather than the daemon's <c>X11Native</c>, which is internal to that assembly
/// — a spike is not a reason to widen it.
/// </summary>
internal sealed class X11Probe : IDisposable
{
    private const string Lib = "libX11.so.6";
    private static readonly IntPtr XaCardinal = 6;
    private static readonly IntPtr XaString = 31;

    private readonly IntPtr _dpy;
    private readonly IntPtr _primary;

    public X11Probe()
    {
        // Xlib's default error handler calls exit(). A window that goes away
        // between XGetSelectionOwner and the property read is a routine race —
        // the daemon installs a handler for exactly this and so must anything
        // that repeats its query.
        XSetErrorHandler(Ignore);
        _dpy = XOpenDisplay(null);
        if (_dpy == IntPtr.Zero) throw new InvalidOperationException("cannot open DISPLAY");
        _primary = XInternAtom(_dpy, "PRIMARY", false);
    }

    private static readonly XErrorHandler Ignore = (_, _) => 0;

    public IntPtr Owner() => XGetSelectionOwner(_dpy, _primary);

    public void Report(string indent)
    {
        IntPtr owner = Owner();
        if (owner == IntPtr.Zero)
        {
            Console.WriteLine($"{indent}PRIMARY has no owner.");
            return;
        }

        long? pid = Cardinal(owner, "_NET_WM_PID");
        string? cls = Text(owner, "WM_CLASS");

        // Walked because a toolkit may own the selection from an unmapped helper
        // window while the EWMH properties sit on the toplevel above it. The
        // daemon reads the owner window ONLY, so the two lines can disagree —
        // and if they do, that disagreement is the finding.
        long? viaParent = null;
        for (IntPtr w = Parent(owner); w != IntPtr.Zero && viaParent is null; w = Parent(w))
            viaParent = Cardinal(w, "_NET_WM_PID");

        Console.WriteLine($"{indent}owner window : 0x{owner.ToInt64():x}");
        Console.WriteLine($"{indent}_NET_WM_PID  : {(pid is null ? "ABSENT" : pid.ToString())}" +
                          (pid is null && viaParent is not null ? $"  (found {viaParent} on an ancestor)" : ""));
        Console.WriteLine($"{indent}WM_CLASS     : {cls ?? "ABSENT"}");
        Console.WriteLine($"{indent}is us?       : {(pid == Environment.ProcessId ? "YES" : pid is null ? "UNKNOWABLE from the owner window" : "no")}");
    }

    private IntPtr Parent(IntPtr w)
    {
        if (XQueryTree(_dpy, w, out _, out IntPtr parent, out IntPtr children, out _) == 0)
            return IntPtr.Zero;
        if (children != IntPtr.Zero) XFree(children);
        return parent;
    }

    private long? Cardinal(IntPtr window, string name)
    {
        IntPtr atom = XInternAtom(_dpy, name, true);
        if (atom == IntPtr.Zero) return null;

        if (XGetWindowProperty(_dpy, window, atom, 0, 1, false, XaCardinal,
                out _, out int format, out nuint count, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
            return null;

        try
        {
            // Xlib widens 32-bit CARDINALs to long. Reading four bytes works on
            // little endian by luck; ReadInt64 is what was written.
            return format == 32 && count >= 1 ? Marshal.ReadInt64(data) : null;
        }
        finally { XFree(data); }
    }

    private string? Text(IntPtr window, string name)
    {
        IntPtr atom = XInternAtom(_dpy, name, true);
        if (atom == IntPtr.Zero) return null;

        if (XGetWindowProperty(_dpy, window, atom, 0, 64, false, XaString,
                out _, out _, out nuint count, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
            return null;

        try
        {
            var bytes = new byte[(int)count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            // WM_CLASS is two NUL-separated strings.
            return Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
        }
        finally { XFree(data); }
    }

    public void Dispose() { if (_dpy != IntPtr.Zero) XCloseDisplay(_dpy); }

    private delegate int XErrorHandler(IntPtr display, IntPtr error);

    [DllImport(Lib)] private static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? d);
    [DllImport(Lib)] private static extern int XCloseDisplay(IntPtr d);
    [DllImport(Lib)] private static extern IntPtr XInternAtom(IntPtr d, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, bool onlyIfExists);
    [DllImport(Lib)] private static extern IntPtr XGetSelectionOwner(IntPtr d, IntPtr selection);
    [DllImport(Lib)] private static extern int XFree(IntPtr data);
    [DllImport(Lib)] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
    [DllImport(Lib)] private static extern int XQueryTree(IntPtr d, IntPtr w, out IntPtr root, out IntPtr parent, out IntPtr children, out uint nchildren);

    [DllImport(Lib)]
    private static extern int XGetWindowProperty(
        IntPtr d, IntPtr w, IntPtr property, long offset, long length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int actualFormat,
        out nuint itemCount, out nuint bytesAfter, out IntPtr data);
}

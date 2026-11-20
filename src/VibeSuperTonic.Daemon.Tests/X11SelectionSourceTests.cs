using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VibeSuperTonic.Daemon.Interop;
using Xunit;
using Xunit.Abstractions;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// <see cref="X11SelectionSource"/> against a real, private X server. Written
/// 2026-11-20 for review items 61 and 87.
///
/// <para><b>Every test here needs an X server of its own</b>, never the user's:
/// taking PRIMARY on a live desktop would steal the user's selection. Each test
/// starts one on a free display number — <c>Xvfb</c> when it is on PATH, or
/// whatever <c>VST_TEST_X11_SERVER</c> names (<c>Xwayland</c> on a Wayland
/// desktop with no Xvfb; it opens a small rootful window there, and was checked
/// not to bridge its selections to the desktop). With neither, the tests return
/// without asserting, because xunit 2 has no runtime skip — so a green run on a
/// machine with no X server says nothing about this file.</para>
///
/// <para>The selection owners are in-process <see cref="ScriptedOwner"/>s rather
/// than <c>xclip</c>, which answers <em>every</em> target with its text —
/// TIMESTAMP included, as three bytes of "abc" — so it can never reach the
/// freshness or clipboard paths.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class X11SelectionSourceTests(ITestOutputHelper output)
{
    /// <summary>
    /// The owner holds PRIMARY and never answers. Before 2026-11-20 the capture
    /// waited 2 s for TIMESTAMP and then a fresh 2 s for UTF8_STRING, so a press
    /// against a busy window took 4 s where Wayland takes 2. Measured then:
    /// 4001 ms without the clipboard fallback, 4006 ms with it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stalled_owner_costs_one_deadline_not_one_per_conversion(bool clipboardFallback)
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var primary = ScriptedOwner.Stalled(server.Display, "PRIMARY");
        using var clipboard = clipboardFallback ? ScriptedOwner.Stalled(server.Display, "CLIPBOARD") : null;

        AssertOneDeadline(new X11SelectionSource(clipboardFallback), server.Display,
            $"stalled owner, clipboardFallback={clipboardFallback}");
    }

    /// <summary>
    /// The other way to spend two deadlines: PRIMARY's owner answers TIMESTAMP
    /// and then stalls on the text, while CLIPBOARD's owner stalls outright.
    /// Sequentially that was 2 s for CLIPBOARD's stamp and 2 s more for
    /// PRIMARY's text.
    /// </summary>
    [Fact]
    public void A_stalled_clipboard_owner_does_not_add_a_second_deadline()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var primary = ScriptedOwner.Start(server.Display, "PRIMARY", text: null, time: 1000);
        using var clipboard = ScriptedOwner.Stalled(server.Display, "CLIPBOARD");

        AssertOneDeadline(new X11SelectionSource(clipboardFallback: true), server.Display,
            "primary answers TIMESTAMP only, clipboard stalled");
    }

    private void AssertOneDeadline(X11SelectionSource source, string display, string label)
    {
        var clock = Stopwatch.StartNew();
        var result = source.Capture(display);
        clock.Stop();

        Report($"{label}: {clock.ElapsedMilliseconds} ms, ok={result.Ok}, reason={result.Reason}");

        Assert.False(result.Ok);
        Assert.Contains("did not answer", result.Reason);
        // One 2 s deadline plus generous slack for a loaded CI machine, and well
        // under the 4 s the sequential waits cost.
        Assert.InRange(clock.ElapsedMilliseconds, SelectionWait.OwnerReplyMs - 50, SelectionWait.OwnerReplyMs + 1000);
    }

    /// <summary>
    /// The conversions now share a window and are in flight together, so the
    /// reply has to be matched on selection and target. A mismatch reads the
    /// TIMESTAMP's bytes as text — plausible garbage, not an error.
    /// </summary>
    [Fact]
    public void A_responsive_owner_is_read_correctly_and_fast()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        const string text = "the quick brown fox, read once";
        using var owner = ScriptedOwner.Start(server.Display, "PRIMARY", text, time: 1000);

        var source = new X11SelectionSource();
        var clock = Stopwatch.StartNew();
        var result = source.Capture(server.Display);
        clock.Stop();
        Report($"responsive owner: {clock.ElapsedMilliseconds} ms");

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(text, result.Text);
        Assert.Null(result.Notice);
        Assert.True(clock.ElapsedMilliseconds < 1000);

        // The second press on the same claim is the freshness path.
        var again = source.Capture(server.Display);
        Assert.True(again.Ok, again.Reason);
        Assert.Equal(text, again.Text);
        Assert.Contains("has not changed", again.Notice);
    }

    /// <summary>
    /// The clipboard's text is the one conversion asked late — only once the
    /// stamps say to read it — so it is the path that depends on the shared
    /// deadline still having time left.
    /// </summary>
    [Fact]
    public void The_clipboard_fallback_reads_a_copy_newer_than_the_selection()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var primary = ScriptedOwner.Start(server.Display, "PRIMARY", "what was selected", time: 1000);
        using var clipboard = ScriptedOwner.Start(server.Display, "CLIPBOARD", "what was copied after", time: 2000);
        var source = new X11SelectionSource(clipboardFallback: true);

        var first = source.Capture(server.Display);
        Assert.True(first.Ok, first.Reason);
        Assert.Equal("what was selected", first.Text);

        var clock = Stopwatch.StartNew();
        var second = source.Capture(server.Display);
        clock.Stop();
        Report($"clipboard fallback read: {clock.ElapsedMilliseconds} ms, notice={second.Notice}");

        Assert.True(second.Ok, second.Reason);
        Assert.Equal("what was copied after", second.Text);
        Assert.Contains("read from the clipboard", second.Notice);
        Assert.True(clock.ElapsedMilliseconds < 1000);
    }

    /// <summary>
    /// Item 95: <c>read --clipboard</c> reads CLIPBOARD, never PRIMARY, even with
    /// the fallback off and PRIMARY holding different text; a stalled clipboard
    /// owner costs one deadline, as a selection read does.
    /// </summary>
    [Fact]
    public void Read_clipboard_reads_CLIPBOARD_not_PRIMARY()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var primary = ScriptedOwner.Start(server.Display, "PRIMARY", "what was selected", time: 2000);
        using var clipboard = ScriptedOwner.Start(server.Display, "CLIPBOARD", "what was copied", time: 1000);
        var source = new X11SelectionSource(clipboardFallback: false);

        var result = source.CaptureClipboard(server.Display);
        Assert.True(result.Ok, result.Reason);
        Assert.Equal("what was copied", result.Text);

        // And the ordinary read is untouched by it.
        Assert.Equal("what was selected", source.Capture(server.Display).Text);
    }

    [Fact]
    public void A_stalled_clipboard_owner_costs_one_deadline_for_read_clipboard()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var clipboard = ScriptedOwner.Stalled(server.Display, "CLIPBOARD");
        var clock = Stopwatch.StartNew();
        var result = new X11SelectionSource().CaptureClipboard(server.Display);
        clock.Stop();
        Report($"read --clipboard against a stalled owner: {clock.ElapsedMilliseconds} ms, reason={result.Reason}");

        Assert.False(result.Ok);
        Assert.InRange(clock.ElapsedMilliseconds, SelectionWait.OwnerReplyMs - 100, SelectionWait.OwnerReplyMs + 1000);
    }

    /// <summary>
    /// The X server goes away while a capture waits on it. Xlib's default IO
    /// error handling calls <c>exit(1)</c>, so before 2026-11-20 this ended the
    /// whole process — the daemon, here the test host — with nothing to catch
    /// and nothing in any log. Observed then: "Test host process crashed : X
    /// connection to :177 broken (explicit kill or server shutdown)."
    /// </summary>
    [Fact]
    public async Task An_X_server_that_dies_mid_capture_does_not_end_the_process()
    {
        using var server = PrivateXServer.TryStart(output);
        if (server is null) return;

        using var owner = ScriptedOwner.Stalled(server.Display, "PRIMARY");

        var source = new X11SelectionSource();
        var clock = Stopwatch.StartNew();
        var capture = Task.Run(() => source.Capture(server.Display));

        await Task.Delay(300);
        owner.Abandon();
        server.Kill();

        // Throws TimeoutException if the capture hangs on the dead connection.
        var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Report($"server killed at 300 ms: returned after {clock.ElapsedMilliseconds} ms, ok={result.Ok}, reason={result.Reason}");
        Assert.False(result.Ok);
        Assert.Contains("connection to the X display was lost", result.Reason);

        // And the next press, with no server at all, is an ordinary refusal.
        Assert.False(source.Capture(server.Display).Ok);
    }

    // ------------------------------------------------------------ harness

    /// <summary>
    /// xunit shows a passing test's output nowhere, and the wall time is the
    /// measurement these tests exist for; <c>VST_TEST_TIMING_LOG</c> names a file
    /// to append it to.
    /// </summary>
    private void Report(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("VST_TEST_TIMING_LOG") is { Length: > 0 } log)
            File.AppendAllText(log, line + Environment.NewLine);
    }

    /// <summary>An X server on a display number nothing else is using.</summary>
    private sealed class PrivateXServer : IDisposable
    {
        private readonly Process _process;
        public string Display { get; }

        private PrivateXServer(Process process, string display) => (_process, Display) = (process, display);

        // DIAGNOSTICS (2026-11-20). CI's linux job began failing here with the
        // owner's XOpenDisplay returning null right after this server's own
        // probe connected, and the server's stderr was thrown away, so nothing
        // said why. It is kept now, and a failed open reports it.
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stderr = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrivateXServer> Running = new();

        /// <summary>What a failed connection to <paramref name="display"/> should say.</summary>
        public static string Diagnose(string display)
        {
            string auth = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Posix.getenv("XAUTHORITY")) ?? "(unset)";
            string n = display.TrimStart(':');
            string files = $"lock={File.Exists($"/tmp/.X{n}-lock")} socket={File.Exists($"/tmp/.X11-unix/X{n}")}";
            if (!Running.TryGetValue(display, out var server))
                return $"no server of ours on {display}; native XAUTHORITY={auth}; {files}";
            string state = server._process.HasExited ? $"exited with {server._process.ExitCode}" : "running";
            return $"server on {display} {state}; native XAUTHORITY={auth}; {files}; stderr: "
                + string.Join(" | ", server._stderr.TakeLast(15));
        }

        private static bool OnPath(string exe) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(d => File.Exists(Path.Combine(d, exe)));

        public static PrivateXServer? TryStart(ITestOutputHelper output)
        {
            string? exe = Environment.GetEnvironmentVariable("VST_TEST_X11_SERVER");
            if (string.IsNullOrWhiteSpace(exe)) exe = OnPath("Xvfb") ? "Xvfb" : null;
            if (exe is null)
            {
                output.WriteLine("no Xvfb and no VST_TEST_X11_SERVER: not asserted");
                return null;
            }

            bool xwayland = Path.GetFileName(exe) == "Xwayland";

            for (int n = 140 + Environment.ProcessId % 40; n < 400; n++)
            {
                if (File.Exists($"/tmp/.X{n}-lock") || File.Exists($"/tmp/.X11-unix/X{n}")) continue;

                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                // -noreset (2026-11-20): an X server resets when its last client
                // leaves, and the probe below is that client. A connection that
                // arrives during the reset fails with the server still running
                // and nothing on its stderr: measured with Xvfb 21.1.22, 22 of
                // 2,000 reconnects after a close failed without it, 0 of 2,000
                // with it. That is what turned CI's linux job red that day.
                foreach (string a in xwayland
                             ? new[] { $":{n}", "-geometry", "64x64", "-nolisten", "tcp", "-noreset" }
                             : new[] { $":{n}", "-screen", "0", "64x64x24", "-nolisten", "tcp", "-noreset" })
                    psi.ArgumentList.Add(a);

                var p = Process.Start(psi)!;
                p.OutputDataReceived += (_, _) => { };
                var server = new PrivateXServer(p, $":{n}");
                p.ErrorDataReceived += (_, e) => { if (e.Data is { } line) server._stderr.Enqueue(line); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 5000 && !p.HasExited)
                {
                    IntPtr probe = X11Native.XOpenDisplay(server.Display);
                    if (probe != IntPtr.Zero)
                    {
                        X11Native.XCloseDisplay(probe);
                        output.WriteLine($"{exe} on {server.Display}");
                        Running[server.Display] = server;
                        return server;
                    }
                    Thread.Sleep(50);
                }

                server.Dispose();   // lost a race for the number, or failed to start
            }

            return null;
        }

        /// <summary>
        /// SIGTERM, so the server removes its own lock file and socket; SIGKILL
        /// left both behind in /tmp. Whatever is still there afterwards is
        /// removed: the number was free when this server took it.
        /// </summary>
        public void Kill()
        {
            if (!_process.HasExited)
            {
                Posix.kill(_process.Id, Posix.SIGTERM);
                if (!_process.WaitForExit(5000)) _process.Kill();
                _process.WaitForExit(5000);
            }

            string n = Display.TrimStart(':');
            foreach (string leftover in new[] { $"/tmp/.X{n}-lock", $"/tmp/.X11-unix/X{n}" })
                try { File.Delete(leftover); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            Running.TryRemove(new KeyValuePair<string, PrivateXServer>(Display, this));
            Kill();
            _process.Dispose();
        }
    }

    /// <summary>
    /// A client that owns one selection on its own connection and answers it
    /// as told: TIMESTAMP with <c>time</c>, UTF8_STRING with <c>text</c>, and a
    /// null for either means that request is never answered — an owner whose
    /// event loop is busy. <see cref="Stalled"/> answers nothing and never reads
    /// its connection at all.
    /// </summary>
    private sealed class ScriptedOwner : IDisposable
    {
        private readonly IntPtr _dpy;
        private readonly Thread? _pump;
        private volatile bool _stop;
        private bool _abandoned;

        private ScriptedOwner(IntPtr dpy, Thread? pump) => (_dpy, _pump) = (dpy, pump);

        public static ScriptedOwner Stalled(string display, string selection) =>
            Create(display, selection, text: null, time: null, pump: false);

        public static ScriptedOwner Start(string display, string selection, string? text, uint? time) =>
            Create(display, selection, text, time, pump: true);

        private static ScriptedOwner Create(string display, string selectionName, string? text, uint? time, bool pump)
        {
            IntPtr dpy = X11Native.XOpenDisplay(display);
            Assert.True(dpy != IntPtr.Zero, $"XOpenDisplay({display}) failed: {PrivateXServer.Diagnose(display)}");

            IntPtr window = X11Native.XCreateSimpleWindow(dpy, X11Native.XDefaultRootWindow(dpy), 0, 0, 1, 1, 0, 0, 0);
            IntPtr selection = X11Native.XInternAtom(dpy, selectionName, false);
            IntPtr timestamp = X11Native.XInternAtom(dpy, "TIMESTAMP", false);
            IntPtr utf8 = X11Native.XInternAtom(dpy, "UTF8_STRING", false);

            OwnerIo.XSetSelectionOwner(dpy, selection, window, X11Native.CurrentTime);
            Assert.Equal(window, X11Native.XGetSelectionOwner(dpy, selection));
            X11Native.XFlush(dpy);

            ScriptedOwner? owner = null;
            Thread? thread = pump
                ? new Thread(() => owner!.Serve(timestamp, utf8, text, time)) { IsBackground = true }
                : null;
            owner = new ScriptedOwner(dpy, thread);
            thread?.Start();
            return owner;
        }

        private void Serve(IntPtr timestamp, IntPtr utf8, string? text, uint? time)
        {
            while (!_stop)
            {
                if (X11Native.XPending(_dpy) == 0) { Thread.Sleep(2); continue; }

                OwnerIo.XNextEvent(_dpy, out SelectionRequest req);
                if (req.Type != OwnerIo.SelectionRequestType) continue;

                if (req.Target == timestamp)
                {
                    if (time is not uint t) continue;   // never answered
                    nint[] value = [(nint)t];           // format 32 is a C long per item
                    OwnerIo.XChangeProperty(_dpy, req.Requestor, req.Property, OwnerIo.XA_INTEGER, 32, 0, value, 1);
                    Notify(req, req.Property);
                }
                else if (req.Target == utf8)
                {
                    if (text is null) continue;         // never answered
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    OwnerIo.XChangeProperty(_dpy, req.Requestor, req.Property, utf8, 8, 0, bytes, bytes.Length);
                    Notify(req, req.Property);
                }
                else
                {
                    Notify(req, X11Native.None);        // refused, as ICCCM asks
                }
            }
        }

        private void Notify(SelectionRequest req, IntPtr property)
        {
            var e = new XEvent
            {
                Type = X11Native.SelectionNotify,
                Requestor = req.Requestor,
                Selection = req.Selection,
                Target = req.Target,
                Property = property,
            };
            OwnerIo.XSendEvent(_dpy, req.Requestor, propagate: 0, eventMask: 0, ref e);
            X11Native.XFlush(_dpy);
        }

        /// <summary>
        /// For a connection whose server is about to die: closing it would raise
        /// the IO error on a display <see cref="X11SelectionSource"/> did not
        /// open, and that one keeps Xlib's default, which exits. One leaked
        /// connection in a test host is the cheaper failure.
        /// </summary>
        public void Abandon()
        {
            _abandoned = true;
            Stop();
        }

        private void Stop()
        {
            _stop = true;
            _pump?.Join();
        }

        public void Dispose()
        {
            Stop();
            if (!_abandoned) X11Native.XCloseDisplay(_dpy);
        }
    }

    /// <summary>
    /// <c>XSelectionRequestEvent</c>, LP64: as <see cref="XEvent"/>'s
    /// <c>XSelectionEvent</c> but with <c>owner</c> at 32, so every later field
    /// sits 8 bytes further on.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct SelectionRequest
    {
        [FieldOffset(0)] internal int Type;
        [FieldOffset(40)] internal IntPtr Requestor;
        [FieldOffset(48)] internal IntPtr Selection;
        [FieldOffset(56)] internal IntPtr Target;
        [FieldOffset(64)] internal IntPtr Property;
    }

    private static class OwnerIo
    {
        private const string Lib = "libX11.so.6";

        internal const int SelectionRequestType = 30;
        internal static readonly IntPtr XA_INTEGER = 19;

        [DllImport(Lib)]
        internal static extern int XSetSelectionOwner(IntPtr display, IntPtr selection, IntPtr owner, IntPtr time);

        [DllImport(Lib)]
        internal static extern int XNextEvent(IntPtr display, out SelectionRequest e);

        [DllImport(Lib)]
        internal static extern int XChangeProperty(
            IntPtr display, IntPtr window, IntPtr property, IntPtr type,
            int format, int mode, byte[] data, int elements);

        [DllImport(Lib)]
        internal static extern int XChangeProperty(
            IntPtr display, IntPtr window, IntPtr property, IntPtr type,
            int format, int mode, nint[] data, int elements);

        [DllImport(Lib)]
        internal static extern int XSendEvent(
            IntPtr display, IntPtr window, int propagate, nint eventMask, ref XEvent e);
    }

    private static class Posix
    {
        internal const int SIGTERM = 15;

        [DllImport("libc", SetLastError = true)]
        internal static extern int kill(int pid, int sig);

        [DllImport("libc")]
        internal static extern IntPtr getenv(string name);
    }
}

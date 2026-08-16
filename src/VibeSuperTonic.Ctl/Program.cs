using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VibeSuperTonic.Core.Ipc;

// vst-ctl — the stateless client. Connect, write one line, exit.
//
//   vst-ctl read                read the selection, interrupting anything playing
//   vst-ctl toggle              press: speak, or stop if already speaking
//   vst-ctl speak "some text"   speak this specific text
//   vst-ctl seek 42             re-read from character 42 of what is on screen
//   vst-ctl stop | pause | resume
//   vst-ctl status              one JSON line
//   vst-ctl subscribe           stream events until interrupted
//   vst-ctl reload              re-read settings.json and pronunciations.json
//   vst-ctl config              where config was read from, and what it made of it
//
// It holds no state. The daemon decides what a toggle means, which is what
// makes the hotkey, the tray menu and D-Bus behave identically — and what lets
// this be a process whose whole job is one write.

const int AutoStartBudgetMs = 5000;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        vst-ctl <verb> [text]

          read         read the selection, interrupting anything playing
          toggle       speak the selection, or stop if already speaking
          speak TEXT   speak TEXT
          seek N       start again from character N of the current text
          stop         stop speaking
          pause        stop feeding the device; buffered audio plays out
          resume       continue
          status       print the daemon's state as one JSON line
          subscribe    print the event stream until interrupted
          reload       re-read settings.json and pronunciations.json
          config       print the effective configuration and its paths

        Options:
          --no-start   fail instead of starting a daemon that is not running
        """);
    return args.Length == 0 ? 2 : 0;
}

bool noStart = args.Contains("--no-start");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

if (!Enum.TryParse<RequestVerb>(positional[0], ignoreCase: true, out var verb))
{
    Console.Error.WriteLine($"unknown verb: {positional[0]}");
    return 2;
}

// seek's argument is a number, not text. Without this it would be joined into
// Text and the daemon would speak the digits.
int? seekOffset = null;
if (verb == RequestVerb.Seek)
{
    if (positional.Length < 2 || !int.TryParse(positional[1], out int parsed))
    {
        Console.Error.WriteLine("seek needs a character offset, e.g. `vst-ctl seek 42`");
        return 2;
    }
    seekOffset = parsed;
}

var request = new Request
{
    Verb = verb,
    Text = verb == RequestVerb.Seek
        ? null
        : positional.Length > 1 ? string.Join(' ', positional.Skip(1)) : null,

    Offset = seekOffset,

    // The session travels with the request, because the daemon may not have one.
    // This process was started by the keybinding, the tray or a shell — all
    // inside the X session — while the daemon could have been started by
    // systemd --user or by a shell that had no $DISPLAY, and a process's
    // environment cannot change afterwards. Phase 4 reads PRIMARY from this.
    Display = verb is RequestVerb.Toggle or RequestVerb.Read
        ? Environment.GetEnvironmentVariable("DISPLAY")
        : null,
};

if (verb == RequestVerb.Speak && string.IsNullOrWhiteSpace(request.Text))
{
    Console.Error.WriteLine("speak needs text");
    return 2;
}

string path = Protocol.SocketPath();

Socket? socket = Connect(path);
if (socket is null)
{
    // R-5: a hotkey with a dead daemon is silence. Autostart may not have fired,
    // or the daemon may have crashed; either way the user pressed a key and is
    // owed something. Starting it here costs a cold load — slow, but audibly
    // *something* — against a key that does nothing at all and teaches the user
    // the feature is broken.
    if (noStart)
    {
        Console.Error.WriteLine($"no daemon listening on {path}");
        return 1;
    }

    if (!TryStartDaemon(out string startError))
    {
        Console.Error.WriteLine($"no daemon listening on {path} and could not start one: {startError}");
        return 1;
    }

    socket = WaitForDaemon(path, AutoStartBudgetMs);
    if (socket is null)
    {
        Console.Error.WriteLine(
            $"started a daemon but it did not accept a connection within {AutoStartBudgetMs} ms");
        return 1;
    }
}

using (socket)
using (var stream = new NetworkStream(socket, ownsSocket: false))
using (var reader = new StreamReader(stream, Encoding.UTF8))
{
    var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    writer.WriteLine(Protocol.Encode(request));

    string? line = reader.ReadLine();
    if (line is null)
    {
        Console.Error.WriteLine("daemon closed the connection without replying");
        return 1;
    }

    var response = Protocol.TryDecode<Response>(line);
    if (response is null)
    {
        Console.Error.WriteLine($"unparseable reply: {line}");
        return 1;
    }

    if (!response.Ok)
    {
        Console.Error.WriteLine(response.Error ?? "failed");
        return 1;
    }

    // Succeeded, with something worth saying — a selection truncated at the
    // R-9 cap is the only case so far. stderr, not stdout, so `vst-ctl status`
    // stays a clean JSON line for jq.
    if (response.Notice is { } notice) Console.Error.WriteLine(notice);

    if (verb == RequestVerb.Status)
    {
        Console.WriteLine(Protocol.Encode(response.Status));
        return 0;
    }

    // Same shape as status: one JSON line on stdout for jq. This is the verb
    // someone runs when a portable folder is not behaving -- "which data
    // directory is this instance actually using" -- so it has to be the easy
    // thing to read.
    if (verb == RequestVerb.Config)
    {
        Console.WriteLine(Protocol.Encode(response.Config));
        return 0;
    }

    if (verb == RequestVerb.Reload)
    {
        Console.Error.WriteLine("reloaded");
        return 0;
    }

    // A debounced press is a success that does nothing. Saying so on stderr
    // keeps stdout clean for scripts while making "the key sometimes does
    // nothing" diagnosable instead of mysterious.
    // Labelled with the verb the user actually sent: a `read` reply saying
    // "toggle:" is a small lie that costs someone ten minutes one day.
    if (response.Action is { } taken)
        Console.Error.WriteLine(
            $"{verb.ToString().ToLowerInvariant()}: {taken.ToString().ToLowerInvariant()}");

    if (verb != RequestVerb.Subscribe) return 0;

    // The snapshot is the first line of the stream, not a separate thing to ask
    // for. A subscriber that arrives mid-read needs the text and position before
    // the events mean anything — a WordBoundary at offset 46 is unusable without
    // knowing what it indexes into — and fetching it with a second round trip
    // would miss every event in between.
    if (response.Status is { } snapshot) Console.WriteLine(Protocol.Encode(snapshot));

    // The connection now belongs to the event stream. One JSON object per line,
    // flushed as it arrives, so `vst-ctl subscribe | jq` works the way anyone
    // would expect it to.
    using var interrupted = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupted.Cancel(); };

    try
    {
        while (!interrupted.IsCancellationRequested)
        {
            string? evt = reader.ReadLine();
            if (evt is null) break;              // daemon went away
            Console.WriteLine(evt);
        }
    }
    catch (IOException) { /* daemon went away mid-line */ }

    return 0;
}

// ---------------------------------------------------------------------- helpers

/// <summary>
/// Connect, or return null to mean "there is genuinely no daemon there".
///
/// <para>The distinction matters more than it looks. Returning null is what
/// makes the caller start a daemon (R-5), so treating every failure as "nothing
/// is listening" turns a busy daemon into a second daemon — and the second one
/// would go on to unlink the first one's socket. A unix socket with a full
/// accept queue fails connect() with EAGAIN immediately rather than waiting,
/// and under stress that was 158 failures in 400 calls, so this is a transient
/// worth riding out rather than an absence worth acting on.</para>
/// </summary>
static Socket? Connect(string path)
{
    if (!File.Exists(path)) return null;

    // Short and bounded: this sits in front of every hotkey press, so the
    // budget here is tens of milliseconds, not seconds. Only reached at all
    // when the daemon is too busy to accept.
    const int TransientRetries = 5;

    for (int attempt = 0; ; attempt++)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return socket;
        }
        catch (SocketException ex)
        {
            socket.Dispose();

            // Nobody is listening: the file outlived the daemon — a crash, or a
            // kill -9. The daemon clears it on its next start. This is the one
            // error that means "start one".
            if (ex.SocketErrorCode == SocketError.ConnectionRefused) return null;

            // Busy, not absent. Back off briefly and ask again.
            if (attempt >= TransientRetries) return null;
            Thread.Sleep(10 * (attempt + 1));
        }
    }
}

static Socket? WaitForDaemon(string path, int budgetMs)
{
    var deadline = Environment.TickCount64 + budgetMs;
    while (Environment.TickCount64 < deadline)
    {
        if (Connect(path) is { } socket) return socket;
        Thread.Sleep(50);
    }
    return null;
}

static bool TryStartDaemon(out string error)
{
    // Next to this executable is the only location worth guessing: the tarball
    // ships both binaries in one directory, so anything else would be looking
    // for a layout this product does not produce.
    string exe = Environment.GetEnvironmentVariable("VST_DAEMON")
        ?? Path.Combine(AppContext.BaseDirectory, "vibesupertonicd");

    if (!File.Exists(exe))
    {
        error = $"{exe} not found (set VST_DAEMON to override)";
        return false;
    }

    try
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            // The daemon outlives this process, so its output cannot go to a
            // terminal that is about to disappear. Discarded rather than
            // captured: a client that held the pipe would keep the daemon's
            // stderr blocked once the buffer filled, which is a hang hours
            // later with no apparent cause.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var proc = Process.Start(psi);
        if (proc is null) { error = "Process.Start returned null"; return false; }

        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        error = "";
        return true;
    }
    catch (Exception ex)
    {
        error = $"{ex.GetType().Name}: {ex.Message}";
        return false;
    }
}

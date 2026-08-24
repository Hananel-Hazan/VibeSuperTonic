using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Synthesis;

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
//   vst-ctl benchmark           measure this machine and record its thread count
//   vst-ctl shutdown            stop the daemon; the next press starts it again
//
// It holds no state. The daemon decides what a toggle means, which is what
// makes the hotkey, the tray menu and D-Bus behave identically — and what lets
// this be a process whose whole job is one write.
//
// `benchmark` is the one verb that reads more than one reply: the daemon reports
// a row at a time over tens of seconds. It is here rather than in the app because
// it has to work over ssh, on a server, and before any window exists — and
// because the Tune tab calls this same verb rather than having a path of its own.

const int AutoStartBudgetMs = 5000;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return args.Length == 0 ? 2 : 0;
}

// Answered here, before the verb check, because it is a question about this
// binary rather than a request to the daemon — asking a daemon its version to
// learn this one's would defeat the purpose. Note which flag this is: until
// 2026-08-18 `vst-ctl --version` was the exact input that reached the empty
// positional array below and turned into SIGABRT. It now has a meaning.
//
// build/pack-tar.sh runs this on the SHIPPED ELF and refuses to build an archive
// whose three binaries disagree, which is what makes this more than cosmetic.
if (args.Contains("--version"))
{
    Console.WriteLine(
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]
            ?? "unknown");
    return 0;
}

bool noStart = args.Contains("--no-start");
bool force = args.Contains("--force");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

// Every argument was an option, so there is no verb to run. This is checked
// rather than left to positional[0]: an IndexOutOfRangeException out of a
// NativeAOT binary is SIGABRT, a core file and exit 134 — which is what
// `vst-ctl --version`, or one mistyped flag, produced before this line existed.
// Same shape as the daemon's over-long socket path, and the same conclusion:
// a refusal a person can read beats a crash a person has to interpret.
if (positional.Length == 0)
{
    Console.Error.WriteLine($"no verb in: {string.Join(' ', args)}");
    PrintUsage();
    return 2;
}

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

    // Only benchmark reads it, and only to override its load guard. Sent as null
    // otherwise so the flag cannot quietly acquire a second meaning later.
    Force = verb == RequestVerb.Benchmark && force ? true : null,
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
    // Starting a daemon in order to stop it is absurd, and "there is no daemon"
    // is the state the caller asked for — so this is a success, not an error.
    // Reporting it as a failure would make `benchmark && shutdown` fail on the
    // machine where it had the least to do.
    if (verb == RequestVerb.Shutdown)
    {
        Console.Error.WriteLine("no daemon running");
        return 0;
    }

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

    // The one verb whose reply is a stream of replies. Handled before the
    // single-line path below rather than inside it, because "read exactly one
    // line" is the contract every other verb depends on and is worth leaving
    // undisturbed.
    if (verb == RequestVerb.Benchmark) return RunBenchmark(reader);

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

    if (verb == RequestVerb.Shutdown)
    {
        // Says what happens next, because "stopped" on its own invites the
        // conclusion that the hotkey is now dead. It is not: the next press
        // starts a fresh daemon (R-5), and the only cost is the model load.
        Console.Error.WriteLine("daemon stopped — the next hotkey press will start it again");
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
/// What this program accepts. Printed on <c>--help</c>, on no arguments at all,
/// and on options with no verb behind them — the three ways of arriving here
/// without having said what to do.
/// </summary>
static void PrintUsage() =>
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
          benchmark    measure this machine's best thread count and record it
          shutdown     stop the daemon; the next hotkey press starts it again

        Options:
          --no-start   fail instead of starting a daemon that is not running
          --force      benchmark even on a busy machine (the result is worth less)
        """);

/// <summary>
/// Read the sweep: a row at a time on stderr, then the table and the verdict,
/// with the profile itself as one JSON line on stdout.
///
/// <para>Same split as <c>status</c> and <c>config</c> — stdout stays clean for
/// <c>jq</c>, everything a person reads goes to stderr. Here that split earns
/// more than consistency: the sweep takes the better part of a minute, and a
/// terminal printing nothing for that long is indistinguishable from one that
/// has hung.</para>
/// </summary>
static int RunBenchmark(StreamReader reader)
{
    while (true)
    {
        string? line = reader.ReadLine();
        if (line is null)
        {
            Console.Error.WriteLine("the daemon closed the connection mid-benchmark");
            return 1;
        }

        var reply = Protocol.TryDecode<Response>(line);
        if (reply is null)
        {
            Console.Error.WriteLine($"unparseable reply: {line}");
            return 1;
        }

        if (!reply.Ok)
        {
            Console.Error.WriteLine(reply.Error ?? "benchmark failed");
            return 1;
        }

        if (reply.Progress is { } progress)
        {
            var row = progress.Row;
            Console.Error.WriteLine(row.Failed
                ? $"  [{progress.Index}/{progress.Total}] {row.Label,-4} failed: {row.Error}"
                : $"  [{progress.Index}/{progress.Total}] {row.Label,-4} " +
                  $"{row.MedianWallMs / 1000.0,6:F2} s   RTF {row.Rtf,5:F3}   {row.AvgCores,4:F1} cores");
            continue;
        }

        if (reply.Benchmark is not { } result)
        {
            Console.Error.WriteLine("the daemon ended the benchmark without a result");
            return 1;
        }

        PrintTable(result);
        Console.WriteLine(Protocol.Encode(result.Profile));
        return 0;
    }
}

static void PrintTable(BenchmarkPayload result)
{
    var profile = result.Profile;

    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {profile.Machine.Cpu} · {profile.Machine.LogicalProcessors} logical processors · " +
                            $"TotalStep {profile.Machine.TotalStep} · {profile.Machine.PowerState}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  threads   median      RTF   cores   core-s");

    foreach (var row in profile.Table)
    {
        if (row.Failed)
        {
            Console.Error.WriteLine($"  {row.Label,-7}   (failed)");
            continue;
        }

        // The winner is marked in the table rather than only announced below it:
        // the reason the whole table is printed is so the pick can be argued
        // with, and that needs the pick visible in its own row.
        bool won = row.Threads == profile.Threads && row.Provider == profile.Provider;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {row.Label,-7} {row.MedianWallMs / 1000.0,7:F2} s  {row.Rtf,5:F3}  {row.AvgCores,6:F1}  {row.CoreSeconds,7:F1}{(won ? "   <- picked" : "")}"));
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine(
        $"  picked {(profile.Threads == CpuBudget.Auto ? "auto" : profile.Threads + " threads")} " +
        $"({profile.Provider}), from {profile.SampleSeconds:F1} s of audio per run");

    if (result.Saved) Console.Error.WriteLine($"  saved to {result.Path}");
    if (result.AppliesNow) Console.Error.WriteLine("  in force now");

    foreach (string note in result.Notes) Console.Error.WriteLine($"  note: {note}");
    Console.Error.WriteLine();
}

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

/// <summary>
/// One absolute path, written by <c>&lt;AppImage&gt; bind</c> beside this binary.
/// Absent for a tarball install, which is the common case and needs nothing.
/// </summary>
static string SidecarPath() => Path.Combine(AppContext.BaseDirectory, "vst-ctl.appimage");

static string? ReadAppImageSidecar()
{
    try
    {
        string path = SidecarPath();
        if (!File.Exists(path)) return null;
        string value = File.ReadAllText(path).Trim();
        return value.Length > 0 ? value : null;
    }
    catch { return null; }
}

static bool TryStartDaemon(out string error)
{
    // Three places, in order of how specific they are.
    //
    //   1. $VST_DAEMON, which is a person overriding everything.
    //   2. An AppImage recorded beside this binary. This client was copied out
    //      of an image to ~/.local/bin so the hotkey would not pay a squashfs
    //      mount per press, which means there is no vibesupertonicd next to it —
    //      and starting one on the first press is R-5, the rule that keeps a
    //      hotkey from silently doing nothing.
    //   3. Next to this executable, which is what the tarball ships.
    string exe = Environment.GetEnvironmentVariable("VST_DAEMON") ?? "";
    string? appImage = exe.Length == 0 ? ReadAppImageSidecar() : null;
    var arguments = new List<string>();

    if (exe.Length == 0 && appImage is not null)
    {
        exe = appImage;
        arguments.Add("daemon");
    }
    else if (exe.Length == 0)
    {
        exe = Path.Combine(AppContext.BaseDirectory, "vibesupertonicd");
    }

    if (!File.Exists(exe))
    {
        // The sidecar case gets its own sentence: the file it names has been
        // moved or deleted since the hotkeys were bound, and re-binding is the
        // fix. "vibesupertonicd not found" would send the user looking for a
        // binary that was never supposed to be there.
        error = appImage is not null
            ? $"{exe} is recorded in {SidecarPath()} but is not there any more — " +
              "the AppImage was moved or deleted. Run `<AppImage> bind` again from where it is now."
            : $"{exe} not found (set VST_DAEMON to override)";
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

        foreach (string argument in arguments) psi.ArgumentList.Add(argument);

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

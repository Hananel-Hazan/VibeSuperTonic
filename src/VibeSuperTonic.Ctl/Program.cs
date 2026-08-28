using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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
//   vst-ctl render "text"       synthesise to a WAV on stdout, playing nothing
//   vst-ctl voices              what is installed, and what the catalog offers
//   vst-ctl voice install ID    download a catalog voice and calibrate it
//   vst-ctl voice remove ID     delete an installed Piper voice
//   vst-ctl shutdown            stop the daemon; the next press starts it again
//
// It holds no state. The daemon decides what a toggle means, which is what
// makes the hotkey, the tray menu and D-Bus behave identically — and what lets
// this be a process whose whole job is one write.
//
// `benchmark` and `voice install` are the two verbs that read more than one
// reply: the daemon reports a row, or a byte count, at a time over tens of
// seconds. Both are here rather than in the app because they have to work over
// ssh, on a server, and before any window exists — and because the Tune and
// Voices tabs call these same verbs rather than having paths of their own.

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

// Trap 7: the voices are a SECOND licence axis, separate from the engine's, and
// each voice differs. The daemon refuses an install without this, and names the
// terms in the refusal so the accepting is deliberate rather than discovered.
bool acceptLicence = args.Contains("--accept-licence") || args.Contains("--accept-license")
                     || args.Contains("--yes") || args.Contains("-y");

// --voice takes a value, so its argument has to come out of the positional list
// as well as the flag itself — otherwise the voice id is spoken as text, which
// is exactly what happened while this was being written.
string? voice = null;
int voiceAt = Array.IndexOf(args, "--voice");
if (voiceAt >= 0)
{
    if (voiceAt + 1 >= args.Length || args[voiceAt + 1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("--voice needs a voice id, e.g. `--voice en_US-lessac-medium`");
        return 2;
    }
    voice = args[voiceAt + 1];
}

// render's destination. "-" is stdout, which is what a Speech Dispatcher module
// asks for: the module's whole job is `... | $PLAY_COMMAND`, and a temp file in
// the middle of that is latency a screen reader pays on every utterance.
string outPath = "-";
int outAt = Array.IndexOf(args, "--out");
if (outAt >= 0)
{
    if (outAt + 1 >= args.Length || args[outAt + 1].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("--out needs a path, or - for stdout");
        return 2;
    }
    outPath = args[outAt + 1];
}

// -1 for "no --voice", and the guard matters: without it the excluded index is
// 0, which is the VERB, and every command without --voice fails as "unknown
// verb: <the text>".
int voiceValueAt = voiceAt >= 0 ? voiceAt + 1 : -1;
int outValueAt = outAt >= 0 ? outAt + 1 : -1;
var positional = args
    .Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal)
                     && i != voiceValueAt && i != outValueAt)
    .ToArray();

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

// `voice install <id>` and `voice remove <id>` are two words, and the wire verb
// is one. Folded here rather than adding VoiceInstall/VoiceRemove as things a
// user types: "voice" is the noun the Voices tab uses and the pair reads as a
// pair, which `voiceinstall` never would.
string? voiceSubject = null;
if (string.Equals(positional[0], "voice", StringComparison.OrdinalIgnoreCase))
{
    if (positional.Length < 2)
    {
        Console.Error.WriteLine("voice needs an action: `voice install <id>` or `voice remove <id>`");
        return 2;
    }

    string action = positional[1].ToLowerInvariant();
    if (action is not ("install" or "remove"))
    {
        Console.Error.WriteLine($"unknown voice action: {positional[1]} (install, remove)");
        return 2;
    }

    // The id may arrive as the third positional or as --voice; both read
    // naturally and neither should be the one that fails.
    voiceSubject = positional.Length > 2 ? positional[2] : voice;
    if (string.IsNullOrWhiteSpace(voiceSubject))
    {
        Console.Error.WriteLine($"voice {action} needs a voice id — `vst-ctl voices` lists them");
        return 2;
    }

    positional = new[] { action == "install" ? "voiceinstall" : "voiceremove" };
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

    // Which voice, and therefore which ENGINE: the daemon routes to Piper
    // exactly when the id names an installed Piper voice, so this one flag is
    // how a person asks for the other engine. Null means the daemon's default.
    // For `voice install|remove` it is the SUBJECT rather than the voice to
    // speak with, which is the same field carrying the same kind of value.
    Voice = voiceSubject ?? voice,

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
    Force = verb is RequestVerb.Benchmark or RequestVerb.VoiceRemove && force ? true : null,

    // Only the install reads it, and sending it on anything else would let the
    // flag quietly acquire a second meaning later.
    AcceptLicence = verb == RequestVerb.VoiceInstall && acceptLicence ? true : null,
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

    if (!TryStartDaemon(out string startError, out string? startedFrom))
    {
        Console.Error.WriteLine($"no daemon listening on {path} and could not start one: {startError}");
        return 1;
    }

    socket = WaitForDaemon(path, AutoStartBudgetMs);
    if (socket is null)
    {
        Console.Error.WriteLine(
            $"started a daemon but it did not accept a connection within {AutoStartBudgetMs} ms");

        // The AppImage case has one failure this sentence cannot name, and it is
        // the one a user cannot diagnose: no FUSE means the image never mounted,
        // so nothing inside it ran at all. See AppImageStartHint.
        if (startedFrom is { } image) Console.Error.WriteLine(AppImageStartHint(image));
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

    // The second streaming verb, and it reads by the same rule: lines until one
    // arrives without a progress field.
    if (verb == RequestVerb.VoiceInstall) return RunVoiceInstall(reader);

    // The third, and the only one whose payload is bytes rather than text.
    if (verb == RequestVerb.Render) return RunRender(reader, socket, outPath);

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

    if (verb == RequestVerb.Voices)
    {
        if (response.Voices is { } list) PrintVoices(list);
        return 0;
    }

    if (verb == RequestVerb.VoiceRemove)
    {
        Console.Error.WriteLine(response.Voice?.Message ?? "removed");
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
          voices       list installed voices and what the catalog offers
          voice install ID   download a catalog voice and calibrate it
          voice remove ID    delete an installed Piper voice
          shutdown     stop the daemon; the next hotkey press starts it again

        Options:
          --voice ID   speak with this voice — a Supertonic style (M1) or an
                       installed Piper voice (en_US-lessac-medium), which also
                       chooses the engine
          --no-start   fail instead of starting a daemon that is not running
          --force      benchmark even on a busy machine (the result is worth less);
                       also removes a voice that is the configured default
          --accept-licence
                       accept the voice's own licence terms, which `voice install`
                       requires. Each voice carries its own — some are
                       NON-COMMERCIAL — and the refusal names them. (-y, --yes)
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

/// <summary>
/// Why an AppImage-hosted daemon might have gone nowhere, in the one case the
/// generic message cannot name.
///
/// <para>Without <c>/dev/fuse</c> the image never mounts, so <c>AppRun</c> never
/// runs and neither does anything inside it — <c>Process.Start</c> still
/// succeeds, the child exits immediately, and all this process can honestly say
/// is that nothing connected. A hotkey press has no terminal to print any of
/// this to, which is why the sentence has to be worth reading when someone
/// finally runs the verb by hand.</para>
/// </summary>
static string AppImageStartHint(string image)
{
    if (File.Exists("/dev/fuse"))
        return $"The daemon is started from {image}. Run `{image} daemon` in a terminal to see why it stopped.";

    return $"/dev/fuse is not present, so {image} cannot be mounted and nothing inside it can run.\n" +
           "Install FUSE — libfuse2 on Debian and Ubuntu — or start the daemon once with:\n" +
           $"    {image} --appimage-extract-and-run daemon &\n" +
           "which unpacks to /tmp instead. That is four times slower to start and leaves\n" +
           "about 125 MB there, so it is a way through rather than a way to live.";
}

/// <param name="startedFrom">
/// The AppImage the daemon was launched from, or null for an ordinary install.
/// Carried out so the caller can say something useful when nothing connects.
/// </param>
static bool TryStartDaemon(out string error, out string? startedFrom)
{
    startedFrom = null;
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
        startedFrom = appImage;
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

/// <summary>
/// Print the voice list for a person: what is installed, then what the catalog
/// offers, grouped by language.
///
/// <para>Not JSON on stdout, unlike <c>status</c> and <c>config</c>. Those two
/// exist to be piped into <c>jq</c> when something is misbehaving; this one is a
/// menu, and 43 catalog entries as one JSON line is not a menu. Anyone scripting
/// against it can send the verb themselves — the wire format is the same
/// one-object-per-line it has always been.</para>
/// </summary>
static void PrintVoices(VoicesPayload list)
{
    Console.WriteLine("INSTALLED");
    if (list.Installed.Count == 0)
    {
        Console.WriteLine("  (none — no models found)");
    }
    else
    {
        foreach (var v in list.Installed)
        {
            var bits = new List<string> { v.Engine };
            if (v.Quality is { } q) bits.Add(q);
            if (v.Language is { } lang) bits.Add(lang);
            if (v.SampleRate is { } rate) bits.Add($"{rate / 1000} kHz");
            if (v.Bytes is > 0) bits.Add($"{v.Bytes / (1024 * 1024)} MB");
            if (v.Licence is { } lic) bits.Add(lic);
            // "styles" for Supertonic and "speakers" for Piper, because they are
            // not the same thing: a style is a separate voice file, a speaker is
            // a sid inside one graph.
            if (v.Speakers is > 1)
                bits.Add($"{v.Speakers} {(v.Engine == "supertonic" ? "styles" : "speakers")}");

            // Uncalibrated is worth a mark rather than a footnote: until the
            // curve is measured a requested rate is served by the reciprocal and
            // can be 20% out, which is audible and otherwise unexplained.
            if (v.Calibrated == false) bits.Add("not yet calibrated");

            Console.WriteLine($"  {(v.IsDefault ? "*" : " ")} {v.Id,-38} {string.Join(" · ", bits)}");
        }
    }

    if (list.Available.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"AVAILABLE ({list.Available.Count}" +
                          (list.CatalogRevision is { } rev ? $", pinned to {rev}" : "") + ")");

        foreach (var group in list.Available
                     .GroupBy(v => v.Language ?? "")
                     .OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            Console.WriteLine($"  {group.Key}");
            foreach (var v in group.OrderBy(v => v.Quality switch
                     {
                         "high" => 0, "medium" => 1, "low" => 2, _ => 3,
                     }))
            {
                string nc = v.LicenceClass == "nc" ? "  NON-COMMERCIAL" : "";
                Console.WriteLine(
                    $"      {v.Id,-38} {v.Quality,-6} {v.SampleRate / 1000,2} kHz " +
                    $"{v.Bytes / (1024 * 1024),4} MB  {v.Licence}{nc}");
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"store: {list.StoreRoot}");
    foreach (string note in list.Notes ?? Array.Empty<string>())
        Console.WriteLine($"note:  {note}");
}

/// <summary>
/// Read a render's replies and write a WAV.
///
/// <para>The loop itself is <see cref="RenderWav.Read"/>, in Core, because every
/// rule it enforces fails silently and none of them could be tested from here —
/// see that type. What is left in this file is the part that is genuinely about
/// a process: a socket, a destination, and a signal.</para>
///
/// <para><b>SIGTERM is a stop, not a failure.</b> Under route B the Speech
/// Dispatcher module runs this as a child process and terminates it when speechd
/// sends <c>STOP</c> — which Orca does on very nearly every keystroke. Left to
/// the default disposition the process is killed mid-write and speechd logs a
/// dead child every time; handled, the render stops, whatever was already
/// produced is flushed, and the exit is 0. docs/SPEECHD-PLAN.md, S1.</para>
/// </summary>
static int RunRender(StreamReader reader, Socket socket, string outPath)
{
    Stream output;
    try
    {
        output = outPath == "-"
            ? Console.OpenStandardOutput()
            : File.Create(outPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"cannot write to {outPath}: {ex.Message}");
        return 1;
    }

    using var _ = output;

    bool stopping = false;

    // Shut the socket down rather than only setting a flag: the read below
    // blocks, and a flag nothing wakes would go unnoticed until the daemon sent
    // the next chunk — which, on a stop, is the one thing that will not happen.
    void Stop()
    {
        stopping = true;
        try { socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { /* already gone */ }
        catch (ObjectDisposedException) { /* already gone */ }
    }

    using var sigterm = PosixSignalRegistration.Create(
        PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Stop(); });
    using var sigint = PosixSignalRegistration.Create(
        PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Stop(); });

    string? ReadLine()
    {
        try { return reader.ReadLine(); }
        catch (IOException) { return null; }            // shut down under us
        catch (ObjectDisposedException) { return null; }
    }

    return RenderWav.Read(ReadLine, output, Console.Error, () => stopping);
}


/// <summary>
/// Read an install: a progress line as the bytes arrive, then the verdict.
///
/// <para>Same contract as the sweep — lines until one arrives without a progress
/// field — and the same stdout/stderr split, so the progress that would otherwise
/// fill a pipe stays on stderr. The bar is rewritten in place with a carriage
/// return when stderr is a terminal, and printed as ordinary lines when it is
/// not: a log file full of \r is worse than no bar at all.</para>
/// </summary>
static int RunVoiceInstall(StreamReader reader)
{
    bool tty = !Console.IsErrorRedirected;
    bool wroteBar = false;

    while (true)
    {
        string? line = reader.ReadLine();
        if (line is null)
        {
            Console.Error.WriteLine("the daemon closed the connection mid-install");
            return 1;
        }

        var reply = Protocol.TryDecode<Response>(line);
        if (reply is null)
        {
            Console.Error.WriteLine($"unparseable reply: {line}");
            return 1;
        }

        if (reply.VoiceProgress is { } p)
        {
            if (p.Message is { } message)
            {
                if (wroteBar) { Console.Error.WriteLine(); wroteBar = false; }
                Console.Error.WriteLine(message);
            }
            else if (p.BytesTotal > 0)
            {
                double pct = 100.0 * p.BytesReceived / p.BytesTotal;
                string bar = $"  {p.File} {p.BytesReceived / (1024 * 1024),4}/" +
                             $"{p.BytesTotal / (1024 * 1024)} MB  {pct,5:F1}%";
                if (tty) { Console.Error.Write($"\r{bar}   "); wroteBar = true; }
                else Console.Error.WriteLine(bar);
            }
            continue;
        }

        if (wroteBar) Console.Error.WriteLine();

        if (!reply.Ok)
        {
            Console.Error.WriteLine(reply.Error ?? "install failed");
            return 1;
        }

        Console.Error.WriteLine(reply.Voice?.Message ?? "installed");
        if (reply.Notice is { } notice) Console.Error.WriteLine(notice);
        return 0;
    }
}

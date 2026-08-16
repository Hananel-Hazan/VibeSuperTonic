using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using VibeSuperTonic.Linux.Audio;
using VibeSuperTonic.Onnx.Cpu;

// vibesupertonicd — holds the warm model for the life of the login session.
//
//   vibesupertonicd [--data <dir>] [--models <dir>] [--voice M1] [--lang en] [--preload]
//
// PORTABLE BY DEFAULT. Everything is resolved relative to the directory holding
// this executable -- models/ beside it, data/ beside it -- so the folder can be
// copied to a USB stick or another machine and resume with every setting it was
// last used with. Nothing is read from or written to $HOME. See LinuxDataPaths.
//
// The model is loaded lazily by default. It is ~830 MB resident once warm,
// which on a desktop is a permanent cost rather than the transient one it is on
// Windows (where the engine lives only as long as its SAPI host), so paying it
// at login for a user who may never press the key is the wrong default. The
// tray icon makes the first-press wait legible instead.

[assembly: SupportedOSPlatform("linux")]

// The environment variables are escape hatches, not the default. The default is
// the portable folder: a previous version defaulted models to
// ~/.local/share/vibesupertonic/models, so a portable install carrying 383 MB of
// models beside the binary would have ignored them and looked in the home
// directory instead.
string? modelsArg = Environment.GetEnvironmentVariable("VST_MODELS");
string? dataArg = Environment.GetEnvironmentVariable("VST_DATA_DIR");

string? voice = null;
string? language = null;
bool preload = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length: dataArg = args[++i]; break;
        case "--models" when i + 1 < args.Length: modelsArg = args[++i]; break;
        case "--voice" when i + 1 < args.Length: voice = args[++i]; break;
        case "--lang" when i + 1 < args.Length: language = args[++i]; break;
        case "--preload": preload = true; break;
        case "--help" or "-h":
            Console.WriteLine(
                "vibesupertonicd [--data <dir>] [--models <dir>] [--voice M1] [--lang en] [--preload]");
            Console.WriteLine();
            Console.WriteLine("Portable: with no arguments, reads models/ and data/ from the");
            Console.WriteLine($"directory holding this executable ({LinuxDataPaths.BaseDir}).");
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 2;
    }
}

// Missing models are NOT a reason to refuse to start, and making them one broke
// the one path that exists to stop a hotkey being silent. On a fresh install
// vst-ctl finds no socket, starts a daemon (R-5), and waits up to 5 s for it to
// answer — so a daemon that exits 2 turns "press the key" into five seconds of
// nothing followed by "started a daemon but it did not accept a connection",
// which names neither the cause nor the fix, in a terminal the user pressing a
// hotkey is not looking at.
//
// Start anyway, answer status, and fail the speak with something actionable.
// The models are downloaded on first run of the app, never by install.sh,
// because the OpenRAIL-M acceptance has to be something a human agrees to.
string dataDir = LinuxDataPaths.ResolveDataDir(dataArg, LinuxDataPaths.BaseDir);
string modelsRoot = string.IsNullOrWhiteSpace(modelsArg)
    ? LinuxDataPaths.DefaultModelsDir
    : Path.GetFullPath(modelsArg);

// Before anything worth logging happens, and after the data directory is known
// — which is the earliest either can be true. Everything below goes to
// data/logs/daemon.log as well as stderr, because a daemon started by a hotkey
// or a double-click has no terminal to print to and those are precisely the runs
// a user files a report about.
DaemonLog.Initialize(dataDir);

// Load whatever the last usage left in the folder, before anything can speak.
// This is the "first run picks up where it left off" half of being portable:
// with no config read, a portable install spoke with built-in defaults and
// applied no pronunciation rules at all, whatever its settings.json said.
var config = new HostConfig(dataDir, modelsRoot);
config.Reload(force: true);

DaemonLog.Write($"data {dataDir}{(config.Writable ? "" : " (read-only)")}");
foreach (string note in config.Notes)
    DaemonLog.Write(note);

if (!Directory.Exists(Path.Combine(modelsRoot, "onnx")))
{
    DaemonLog.Write(
        $"no onnx/ under {modelsRoot} — starting anyway; " +
        "speech will fail until the models are downloaded. " +
        "Pass --models <dir> or set VST_MODELS if they are elsewhere.");
}

// From <VstVersion> via the csproj, not a literal. `status` reports it so a
// stale client is diagnosable, and a version that has to be edited by hand is
// one that will eventually lie — which is worse than reporting nothing.
// InformationalVersion carries "+<sha>" when a build adds source metadata;
// the part before it is the number the packers and the user talk about.
string version = Assembly.GetEntryAssembly()
    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion
    ?.Split('+')[0]
    ?? "unknown";

var options = new DaemonOptions(voice, language, Version: version);

// Capped rather than left to ORT, which sizes its pool to every core and makes
// the desktop stutter for the length of each read. Startup-only: ORT builds the
// thread pool with the session, so `reload` cannot move it.
int intraOp = CpuBudget.IntraOpThreads(config.Settings.MaxCpuPercent, Environment.ProcessorCount);
DaemonLog.Write(
    $"version {version}, inference threads {(intraOp == CpuBudget.Auto ? "auto" : intraOp.ToString())} " +
    $"of {Environment.ProcessorCount} ({config.Settings.MaxCpuPercent}% budget)");

using ISynthesizer synth = new CpuSynthesizer(modelsRoot, intraOpThreads: intraOp);

// Opened on the first request that needs to play, not here. Opening here meant a
// machine whose audio server was unreachable got an unhandled exception and a
// core dump before the control socket existed -- so every verb that could have
// explained it was gone too, and with vst-ctl's autostart (R-5) that is a hotkey
// that silently does nothing. Same failure shape as the missing model set, and
// the same fix: start anyway, and let the speak path say what is wrong.
using var sink = new LazyAudioSink(44100, () => new PulseAudioSink(44100, "VibeSuperTonic"));
var session = new SpeechSession(synth, sink, config.SessionOptions);
// ClipboardFallback is read once here rather than per capture: a reload can
// change it, but the selection source is built with the daemon, and a hotkey
// that changes behaviour halfway through a session is worse than one that needs
// a restart to pick the setting up. Noted in the setting's own documentation.
using var server = new DaemonServer(
    options, config, synth, session,
    new X11SelectionSource(config.Settings.ClipboardFallback), sink);

using var lifetime = new CancellationTokenSource();

// Every way this process is asked to stop has to stop the *speech* too: a
// daemon that exits mid-utterance leaves libpulse holding a buffer that keeps
// playing after the process is gone.
//
// SIGHUP is in the list because the daemon is often started from a shell that
// then exits, and the default action for SIGHUP is to terminate — which killed
// it mid-utterance with no cleanup and no log line during development.
//
// Deliberately NO ProcessExit handler. It runs after Main has returned and
// disposed everything, so cancelling from it throws ObjectDisposedException on
// a thread with no handler — the process then core-dumps *after* a clean
// shutdown. Exit code 134 and a core file is what systemd and anyone reading
// journald would call a crash, and a restart policy would act on it.
var signals = new List<PosixSignalRegistration>();
foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
{
    signals.Add(PosixSignalRegistration.Create(signal, ctx =>
    {
        ctx.Cancel = true;      // we are handling it; do not take the default action
        lifetime.Cancel();
    }));
}

if (preload) await server.PreloadAsync(lifetime.Token);

try
{
    await server.RunAsync(lifetime.Token);
}
catch (IOException ex)
{
    DaemonLog.Write(ex.Message);
    return 1;
}

session.Stop();
try { await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* going away */ }

foreach (var registration in signals) registration.Dispose();

DaemonLog.Write("stopped");
return 0;

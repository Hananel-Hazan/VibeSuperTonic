using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VibeSuperTonic.Daemon.Interop;
using VibeSuperTonic.Daemon.Tray;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using VibeSuperTonic.Linux.Audio;
using VibeSuperTonic.Onnx.Ort;
using VibeSuperTonic.Piper;

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

// From <VstVersion> via the csproj, not a literal. `status` reports it so a
// stale client is diagnosable, and a version that has to be edited by hand is
// one that will eventually lie — which is worse than reporting nothing.
// InformationalVersion carries "+<sha>" when a build adds source metadata;
// the part before it is the number the packers and the user talk about.
//
// Computed HERE, above the argument loop, because `--version` is answered inside
// it. build/pack-tar.sh runs all three binaries and refuses to build an archive
// whose parts disagree, so this is not merely informational — it is the guard
// against a stale binary surviving in an output folder and shipping a UI that
// disagrees with its daemon.
string version = Assembly.GetEntryAssembly()
    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion
    ?.Split('+')[0]
    ?? "unknown";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data" when i + 1 < args.Length: dataArg = args[++i]; break;
        case "--models" when i + 1 < args.Length: modelsArg = args[++i]; break;
        case "--voice" when i + 1 < args.Length: voice = args[++i]; break;
        case "--lang" when i + 1 < args.Length: language = args[++i]; break;
        case "--preload": preload = true; break;
        case "--version":
            Console.WriteLine(version);
            return 0;
        // For AppRun and install-gpu.sh, which need the store and must not
        // re-implement the rule that picks it. One owner, asked rather than
        // copied — the same reason there is no `config set` verb.
        case "--print-store":
            Console.WriteLine(LinuxDataPaths.StoreRoot);
            return 0;
        // Called by the window at startup. The daemon does the same thing on its
        // own start; this exists so that opening the window — which is what a
        // person does when the hotkey stopped working — repairs it too.
        case "--ensure-client":
            if (LinuxDataPaths.AppImageFile is { } img)
            {
                string? said = AppImageClient.EnsureInstalled(
                    img, AppContext.BaseDirectory.TrimEnd('/'), version);
                if (said is not null) Console.WriteLine(said);
            }
            return 0;
        case "--help" or "-h":
            Console.WriteLine(
                "vibesupertonicd [--data <dir>] [--models <dir>] [--voice M1] [--lang en] [--preload]");
            Console.WriteLine("                [--version] [--print-store]");
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
// StoreRoot, not BaseDir. They are the same directory for every install that is
// not an AppImage — the tarball, a portable folder on a stick — and different
// exactly once: an AppImage's BaseDir is a read-only squashfs mount at a path
// that changes on every start, so nothing writable can be anchored to it.
string dataDir = LinuxDataPaths.ResolveDataDir(dataArg, LinuxDataPaths.StoreRoot);
string modelsRoot = string.IsNullOrWhiteSpace(modelsArg)
    ? LinuxDataPaths.DefaultModelsDir
    : Path.GetFullPath(modelsArg);

// Before anything worth logging happens, and after the data directory is known
// — which is the earliest either can be true. Everything below goes to
// data/logs/daemon.log as well as stderr, because a daemon started by a hotkey
// or a double-click has no terminal to print to and those are precisely the runs
// a user files a report about.
DaemonLog.Initialize(dataDir);

// BEFORE ANY ONNX RUNTIME CALL, and before the socket exists. If a GPU provider
// pack is installed this replaces the process with itself, with the pack's CUDA
// libraries on the loader's path — the only arrangement measured not to corrupt
// the heap at exit. Returns immediately when there is no pack, which is every
// ordinary install.
GpuProviderPack.ReexecIfNeeded(LinuxDataPaths.StoreRoot, DaemonLog.Write);

// Load whatever the last usage left in the folder, before anything can speak.
// This is the "first run picks up where it left off" half of being portable:
// with no config read, a portable install spoke with built-in defaults and
// applied no pronunciation rules at all, whatever its settings.json said.
var config = new HostConfig(dataDir, modelsRoot);
config.Reload(force: true);

if (LinuxDataPaths.AppImageFile is { } appImage)
{
    DaemonLog.Write($"appimage {appImage}, store {LinuxDataPaths.StoreRoot} " +
                    $"({LinuxDataPaths.Store.Reason})");

    // A portable home is worth naming in the log — $HOME is not where the reader
    // thinks it is, and that explains a lot of otherwise baffling paths below.
    // It is NOT a reason to remove the directory: `bind` handles this itself by
    // writing the desktop's registration to the real home, and removing a
    // portable home that holds the store is how a person loses one.
    if (LinuxDataPaths.PortableHome is { } portable)
        DaemonLog.Write(
            $"portable home in use ({portable}), so $HOME points inside it. " +
            "Hotkey binding writes the desktop's shortcut configuration to the real " +
            "home instead; everything else stays here.");

    // The hotkeys point at a copy of vst-ctl in ~/.local/bin, and upgrading is
    // replacing one file that has nothing to do with that copy. Re-checked here
    // so that every way of waking this product up also repairs the client.
    string? installed = AppImageClient.EnsureInstalled(
        appImage, AppContext.BaseDirectory.TrimEnd('/'), version);
    if (installed is not null) DaemonLog.Write(installed);
}

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

var options = new DaemonOptions(voice, language, Version: version);

// HOW MANY THREADS, AND ON WHAT. Capped rather than left to ORT, which sizes its
// pool to every core and makes the desktop stutter for the length of each read.
//
// A stored measurement beats the percentage whenever it still describes this
// machine, because the percentage is a guess and this is not: the cost curve is
// not monotonic, so a share of the machine cannot be turned into the right
// thread count by arithmetic. `vst-ctl benchmark` is what writes the profile and
// `vst-ctl config` reports which of the two is in force, with its reason.
//
// This is the decision the FIRST session is built from. It is not the last word
// any more: ProviderSwitchingSynthesizer asks again before each utterance, which
// is what lets the battery rule, an edited Provider setting and a freshly
// written benchmark take effect without a restart. Until Phase 8b that was
// impossible and this comment said so — ORT does size its pool at session
// construction, and the answer turned out to be building another session rather
// than living with the first.
//
// Assigned once the session exists, which is after the synthesizer it speaks
// through — the switch asks "is anything in flight" and the answer before there
// is a session to ask is idle, which is exactly right for the startup window.
Func<SpeechStateProbe>? sessionState = null;

// Asked once, here, and remembered. The probe loads the CUDA provider library
// and reports one sentence when it cannot — the default install has no provider
// pack, so "cannot" is the ordinary answer rather than an error, and the daemon
// logs which it got because "why is my GPU idle" needs somewhere to look.
string? gpuUnavailable = OrtSynthesizer.ProbeCuda();
DaemonLog.Write(gpuUnavailable is null
    ? "gpu: CUDA provider available"
    : $"gpu: CUDA unavailable — {gpuUnavailable}");

var storedProfile = BenchmarkStore.Load(LinuxDataPaths.BenchmarkFile(dataDir));
var machineNow = MachineFacts.Current(modelsRoot, config.Settings.TotalStep,
    voice ?? config.Settings.DefaultVoice, language ?? config.Settings.Language);
var startupDecision = ExecutionDecision.Decide(
    storedProfile,
    machineNow,
    config.Settings.MaxCpuPercent,
    Environment.ProcessorCount,
    config.Settings.Provider,
    gpuUnavailable,
    machineNow.PowerState,
    config.Settings.GpuOnBattery);

DaemonLog.Write($"version {version}, inference {startupDecision.Describe()} " +
                $"of {Environment.ProcessorCount} logical processors, power {machineNow.PowerState}");

// The factory the benchmark verb sweeps with, and the one the provider switch
// rebuilds through — the same constructor the daemon's own session uses, so a
// row measures what the daemon would actually get.
Func<int, string, ISynthesizer> synthesizerFor =
    (threads, provider) => new OrtSynthesizer(modelsRoot, provider, intraOpThreads: threads);

// The session speaks through the switch, not through a fixed backend: the
// battery rule, an edited Provider setting and a freshly written benchmark all
// take effect on the next utterance rather than the next daemon start. It owns
// the inner synthesizer's lifetime, which is why only this one is `using`.
ProviderSwitchingSynthesizer? switcher = null;
using ISynthesizer synth = switcher = new ProviderSwitchingSynthesizer(
    config,
    startupDecision,
    synthesizerFor(startupDecision.Threads, startupDecision.Provider),
    synthesizerFor,
    gpuUnavailable,
    () => sessionState?.Invoke() ?? SpeechStateProbe.Idle,
    DaemonLog.Write,
    voice,
    language);

// ------------------------------------------------------------------ Piper
//
// The second engine. Which voice speaks decides which engine renders — a voice
// belongs to exactly one of them, so an Engine setting that could disagree with
// the voice is a setting that eventually would.
//
// PROBED AT STARTUP AND LOGGED, because the difference between our espeak-ng and
// a distro one is inaudible right up until it is a report about prosody:
// espeak_TextToPhonemesWithTerminator does not exist at the 1.52.0 TAG, and
// without it every input collapses to one sentence and the trailing punctuation
// the models were trained on is missing. Whether a distro package has it is a
// separate question the version string does not answer — Ubuntu 26.04's 1.52.0
// package DOES export it, Debian 12's 1.51 does not — so the phonemizer asks the
// library it bound rather than reasoning from the version. See EspeakLibrary.
var espeak = EspeakLibrary.Probe();
DaemonLog.Write(config.PiperVoices.Voices.Count > 0 || espeak.Path is not null
    ? espeak.Describe()
    : $"piper: no voices in {config.PiperVoices.Root}");

// Built on first use rather than at startup: it loads dictionaries and
// initialises process-global state in a native library, and an install with no
// Piper voices should pay none of that. One per process — espeak_Initialize is
// global and a second one with a different data directory silently rebinds the
// first.
var phonemizer = new Lazy<EspeakPhonemizer>(
    () => new EspeakPhonemizer(espeak), LazyThreadSafetyMode.ExecutionAndPublication);

// CPU, and the same thread count the Supertonic decision arrived at. P2 measured
// CUDA buying the `high` tier 399 -> 116 ms and the `medium` tier only
// 72 -> 61 ms for 350 MB of resident set, so the provider is worth choosing per
// tier — but choosing it is a decision with its own machinery (8b's) and
// belongs beside the benchmark rather than hard-coded here.
using var engines = new EngineRoutingSynthesizer(
    synth,
    config.PiperVoices,
    modelPath => new PiperSynthesizer(
        modelPath, phonemizer.Value,
        ExecutionProviders.Cpu,
        intraOpThreads: switcher.Decision.Threads),
    () => sessionState?.Invoke() ?? SpeechStateProbe.Idle,
    DaemonLog.Write);

// Pointed at the default voice's engine now rather than at the first press. Two
// things follow from doing it here: the log says which engine a daemon is
// holding before anyone asks it to speak, and a Piper voice that has never been
// calibrated starts being measured at startup instead of during the first
// utterance the user is waiting on.
if (engines.Select(voice ?? config.Settings.DefaultVoice) is { Error: { } engineError })
    DaemonLog.Write($"engine: {engineError}");

// Opened on the first request that needs to play, not here. Opening here meant a
// machine whose audio server was unreachable got an unhandled exception and a
// core dump before the control socket existed -- so every verb that could have
// explained it was gone too, and with vst-ctl's autostart (R-5) that is a hotkey
// that silently does nothing. Same failure shape as the missing model set, and
// the same fix: start anyway, and let the speak path say what is wrong.
// 44100 is Supertonic's rate and therefore the daemon's starting one; a Piper
// voice re-tunes it between utterances, because nothing here resamples.
using var sink = new LazyAudioSink(
    TimeStretch.SupertonicSampleRate,
    rate => new PulseAudioSink(rate, "VibeSuperTonic"));
// The session speaks through the ENGINE ROUTER, which speaks through the
// provider switch when the voice is Supertonic's. Three layers, each of which
// may only change between utterances, and each of which the session is unaware
// of by design: it holds one ISynthesizer for the life of the daemon.
var session = new SpeechSession(engines, sink, config.SessionOptions);
sessionState = () => session.State == SpeechState.Idle ? SpeechStateProbe.Idle : SpeechStateProbe.Busy;
// ClipboardFallback is read once here rather than per capture: a reload can
// change it, but the selection source is built with the daemon, and a hotkey
// that changes behaviour halfway through a session is worse than one that needs
// a restart to pick the setting up. Noted in the setting's own documentation.
//
// WHICH SOURCE. On a Wayland session the X11 source is nearly blind: a native
// Wayland client's selection never reaches X11 PRIMARY, and on the machine this
// was written against exactly one running application was an X11 client — so
// `read` answered "nothing is selected" for everything the user actually uses.
// The Wayland source is a strict superset there, because KWin bridges XWayland
// clients' selections into Wayland and this reads both.
//
// Decided by asking the compositor rather than by trusting $XDG_SESSION_TYPE:
// the variable is set by the session manager and a daemon started from cron, a
// service or an ssh login may not have inherited it, while a connection either
// succeeds or does not. VST_SELECTION forces the answer for diagnosis.
//
// Unlike the X11 source there is no per-request display to thread through here.
// $DISPLAY had to arrive with the request because a daemon can legitimately be
// started with the wrong one; the Wayland equivalent is $XDG_RUNTIME_DIR, and
// this daemon's control socket already lives there — so a daemon vst-ctl can
// reach at all is one whose runtime dir is right by construction.
string? forced = Environment.GetEnvironmentVariable("VST_SELECTION");
bool useWayland = forced switch
{
    "wayland" => true,
    "x11" => false,
    _ => WaylandNative.CanConnect(),
};
ISelectionSource selectionSource = useWayland
    ? new WaylandSelectionSource(config.Settings.ClipboardFallback)
    : new X11SelectionSource(config.Settings.ClipboardFallback);
DaemonLog.Write($"selection source: {(useWayland ? "wayland (ext-data-control-v1)" : "x11 (PRIMARY)")}"
    + (forced is null ? "" : $", forced by VST_SELECTION={forced}"));

using var server = new DaemonServer(
    options, config, engines, session,
    selectionSource, sink,
    synthesizerFor, switcher, engines);

// BEFORE the tray and before --preload. A daemon that has lost the race for the
// socket must not register a tray icon on its way out, and a preloading daemon
// must be answering while it loads: vst-ctl waits five seconds for an autostarted
// daemon to accept a connection, and a model set takes longer than that to read.
try
{
    server.Bind();
}
catch (IOException ex)
{
    DaemonLog.Write(ex.Message);
    return 1;
}

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

// The tray. Built after the server because it is a client of it — its menu rows
// call the same verbs vst-ctl does — and started before the accept loop so the
// icon is there by the time anything can ask about it.
//
// It is handed a subscription and an invoker, never the session: R-1's hazard is
// tray code reading pipeline state directly because it happens to be in the same
// process, and the cheapest defence is not giving it the reference.
//
// StartAsync never throws. A tray that cannot be built is a cosmetic loss; a
// daemon that refuses to start is a hotkey that silently does nothing, and that
// trade is the whole reason `status` carries a Tray field.
using var tray = new TrayIcon(server.Invoke, () => server.UiAttached, DaemonLog.Write);
server.TrayStatus = () => tray.Status;
session.Emitted += tray.OnSessionEvent;
server.UiAttachedChanged += tray.OnUiAttachedChanged;
await tray.StartAsync();

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

// The renders measured since the last flush. Batched during the run so a page of
// text is not a dozen file writes inside the press-to-speech budget, which leaves
// a handful owed at exit — and this is the only place they can be paid. SIGKILL
// and a hard logout skip it and lose them, which is the right trade for a
// measurement that reproduces by using the program again.
switcher?.FlushUsage();

DaemonLog.Write("stopped");
return 0;

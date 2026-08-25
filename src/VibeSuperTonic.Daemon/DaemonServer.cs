using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The control socket, the state machine, and the one place a request from the
/// hotkey, the tray or D-Bus is turned into an action.
///
/// <para><b>The state machine lives here, not in the client.</b> The client
/// connects, writes one line and exits — it has nothing to remember, which is
/// what lets it be a process that starts in a millisecond, and what makes the
/// behaviour identical no matter which surface the request came from.</para>
///
/// <para><b>Events go out over <c>subscribe</c> and nowhere else</b> [R-1]. The
/// tray and window will live in this process in Phase 6, which makes it trivial
/// for them to read session state directly — and then splitting them out, or
/// adding any external subscriber, becomes a rewrite. Building the stream first,
/// with a client that only prints it, is the enforcement.</para>
/// </summary>
public sealed class DaemonServer : IDisposable
{
    private readonly DaemonOptions _options;
    private readonly HostConfig _config;
    private readonly ISelectionSource _selection;
    private readonly ISynthesizer _synth;
    private readonly SpeechSession _session;
    private readonly LazyAudioSink? _sink;
    private readonly ToggleGate _gate = new();
    private readonly object _gateLock = new();

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private volatile bool _modelLoaded;

    private readonly Func<int, string, ISynthesizer>? _synthesizerFor;
    private readonly ProviderSwitchingSynthesizer? _switch;

    /// <summary>
    /// What is in force right now — asked of the switch rather than remembered,
    /// because it changes between utterances as of Phase 8b. A field holding the
    /// startup answer would report "CUDA" for the rest of the session to a user
    /// who unplugged their laptop an hour ago.
    /// </summary>
    private ExecutionDecision Execution =>
        _switch?.Decision ?? new ExecutionDecision(CpuBudget.Auto, "cpu", "not recorded", FromProfile: false);

    // 0 idle, 1 sweeping. A second benchmark would measure the first one.
    private int _benchmarking;

    /// <summary>
    /// Linked to the token <see cref="RunAsync"/> was given, so the daemon can
    /// stop itself without <c>Program</c> having to hand its lifetime source into
    /// the server. Cancelling it ends the accept loop exactly as a signal does,
    /// which means <c>shutdown</c> and SIGTERM leave by the same door — and that
    /// door is the one already known to stop speech and release the audio device
    /// cleanly.
    /// </summary>
    private CancellationTokenSource? _stopping;

    /// <param name="sink">
    /// The deferred audio device, so the speak path can open it and report a
    /// failure to the caller. Null in tests that drive the session directly.
    /// </param>
    /// <param name="synthesizerFor">
    /// Builds a synthesizer for a given intra-op thread count and provider, for
    /// <see cref="RequestVerb.Benchmark"/>. Null disables the verb — which is the
    /// right answer for a test that has no models, and the reason the failure is
    /// a sentence rather than a NullReferenceException.
    /// </param>
    /// <param name="providerSwitch">
    /// The synthesizer the session speaks through, which owns the provider and
    /// thread count in force and can change them between utterances. Null in
    /// tests that drive a fixed synthesizer directly, where "not recorded" is the
    /// honest answer to <c>config</c>.
    /// </param>
    public DaemonServer(DaemonOptions options, HostConfig config, ISynthesizer synth,
        SpeechSession session, ISelectionSource? selection = null, LazyAudioSink? sink = null,
        Func<int, string, ISynthesizer>? synthesizerFor = null,
        ProviderSwitchingSynthesizer? providerSwitch = null)
    {
        _options = options;
        _config = config;
        _synth = synth;
        _session = session;
        _selection = selection ?? new NullSelectionSource();
        _sink = sink;
        _synthesizerFor = synthesizerFor;
        _switch = providerSwitch;

        _session.Emitted += OnSessionEvent;
    }

    private sealed class Subscriber
    {
        public required StreamWriter Writer { get; init; }

        /// <summary>What connected, from the subscribe request's hello. Null for
        /// an anonymous subscriber, which is what `vst-ctl subscribe` is.</summary>
        public string? Kind { get; init; }
        public int? Pid { get; init; }

        public object Lock { get; } = new();
    }

    /// <summary>
    /// Is a window attached? The tray icon's answer — it shows a different state
    /// when the Reader is open, and the daemon owns the icon while the UI comes
    /// and goes.
    /// </summary>
    public bool UiAttached =>
        _subscribers.Values.Any(s => s.Kind == Request.ClientKindUi);

    /// <summary>Raised when that answer changes, so the tray icon can redraw.</summary>
    public event Action? UiAttachedChanged;

    /// <summary>
    /// What the tray is doing, for <c>status</c>. A function rather than a value
    /// because the tray is built after this server — it needs a way to invoke
    /// verbs — and because the honest answer changes while the daemon runs.
    ///
    /// <para>It is reported at all because the alternative is the failure mode
    /// the plan names: a daemon with no session bus presents as a tray that
    /// silently never appears, with nothing anywhere saying why.</para>
    /// </summary>
    public Func<string>? TrayStatus { get; set; }

    /// <summary>
    /// Run a verb as though it had arrived on the socket. The tray's only way in
    /// — it is a client that happens to share a process, and parity says its
    /// menu rows call the same verbs <c>vst-ctl</c> does rather than reaching
    /// past them into the session.
    /// </summary>
    public Response Invoke(Request request) => Handle(request);

    // --------------------------------------------------------------- listening

    /// <summary>
    /// Bind and serve until <paramref name="token"/> fires.
    /// </summary>
    /// <exception cref="IOException">Another daemon holds the socket.</exception>
    public async Task RunAsync(CancellationToken token)
    {
        string path = Protocol.SocketPath();
        string dir = Path.GetDirectoryName(path)!;

        Directory.CreateDirectory(dir);
        // Explicit rather than inherited. Under $XDG_RUNTIME_DIR the parent is
        // already 0700, but the /tmp fallback is world-writable and the socket
        // is a remote-control interface for the user's speakers.
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        ClearStaleSocket(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException ex)
        {
            // Two daemons started at once and the other won the race between
            // ClearStaleSocket and Bind.
            throw new IOException($"could not bind {path}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            // A unix socket path is capped at 108 bytes by the kernel, and
            // $XDG_RUNTIME_DIR is somebody else's variable — a container, a
            // deeply nested scratch directory, or an su'd session can all make
            // one long enough to break the cap. Constructing the endpoint throws
            // ArgumentOutOfRangeException, which is NOT a SocketException, so it
            // escaped as an unhandled exception: no log line, exit 134 and a core
            // file, which is what journald and a systemd restart policy would
            // read as a crash. Same class of failure as the audio device and the
            // missing models, and the same fix — say what is wrong, in the place
            // that is read.
            throw new IOException(
                $"cannot use {path} as a control socket: {ex.Message} " +
                "Set $XDG_RUNTIME_DIR to a shorter directory.", ex);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // A unix socket whose accept queue is full fails connect() with EAGAIN
        // immediately — it does not queue and it does not wait. At a backlog of
        // 16, twenty threads issuing status calls lost 158 connections out of
        // 400 under stress. Worse than the failure itself is what the client
        // concluded from it: a failed connect used to read as "no daemon is
        // listening", which is the trigger for auto-starting one (R-5).
        listener.Listen(512);

        Log($"listening on {path}");

        // Everything below runs on the linked token rather than the caller's, so
        // that the `shutdown` verb can end the loop from inside a connection.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        _stopping = stopping;
        var serving = stopping.Token;

        try
        {
            while (!serving.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(serving); }
                catch (OperationCanceledException) { break; }

                // Fire and forget: one misbehaving client must not stall accept.
                _ = Task.Run(() => ServeAsync(client, serving), CancellationToken.None);
            }
        }
        finally
        {
            _stopping = null;
            listener.Close();
            try { File.Delete(path); } catch { /* going away anyway */ }
        }
    }

    /// <summary>
    /// End the accept loop, as if a signal had arrived. Safe to call more than
    /// once and safe to call before <see cref="RunAsync"/> has started, which is
    /// what the null check is for — a client cannot reach this before the socket
    /// exists, but a test can.
    /// </summary>
    private void RequestShutdown()
    {
        try { _stopping?.Cancel(); }
        catch (ObjectDisposedException) { /* already on the way out */ }
    }

    /// <summary>
    /// Remove a socket file left behind by a daemon that died without cleaning
    /// up — a crash, or a kill -9. Probing by connecting is the only reliable
    /// test: the file's existence says nothing about whether anyone is
    /// listening, and deleting a live daemon's socket would leave it running but
    /// unreachable, which is far worse than refusing to start.
    /// </summary>
    private static void ClearStaleSocket(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            throw new IOException($"another vibesupertonicd is already listening on {path}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Nobody is listening: the file outlived its daemon. This is the
            // only error that proves that, and therefore the only one that
            // justifies deleting the file.
            File.Delete(path);
        }
        catch (SocketException ex)
        {
            // Anything else — EAGAIN from a saturated accept queue being the
            // realistic one — means the daemon may well be alive and merely
            // busy. The old code deleted the socket for any SocketException at
            // all, so a burst of load could make a second daemon unlink a live
            // daemon's socket and bind its own: two processes, one holding the
            // audio device and now unreachable by any client. This method's own
            // comment already said that is far worse than refusing to start,
            // and it is exactly what the code did.
            throw new IOException(
                $"could not determine whether a daemon is listening on {path} " +
                $"({ex.SocketErrorCode}). Refusing to start rather than risk " +
                "unlinking a live daemon's socket.", ex);
        }
    }

    // ------------------------------------------------------------- connections

    private async Task ServeAsync(Socket client, CancellationToken token)
    {
        Guid id = Guid.NewGuid();
        try
        {
            using var _ = client;
            await using var stream = new NetworkStream(client, ownsSocket: false);
            // detectEncodingFromByteOrderMarks: false is load-bearing, not
            // tidiness. It defaults to true, and the protocol is bytes from an
            // arbitrary local client — so a request beginning 0xFF 0xFE is read
            // as a UTF-16LE byte-order mark, silently switches this connection's
            // decoder, and every subsequent byte is decoded as UTF-16. The
            // newline that frames the protocol then never appears and the
            // connection hangs until the client times out. Found by fuzzing:
            // "raw binary" was the one malformed input that produced no reply
            // at all rather than "unparseable request".
            using var reader = new StreamReader(
                stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token);
                if (line is null) break;              // client hung up

                var request = Protocol.TryDecode<Request>(line);
                if (request is null)
                {
                    await WriteAsync(writer, Response.Fail("unparseable request"));
                    continue;
                }

                if (request.Verb == RequestVerb.Subscribe)
                {
                    // The snapshot rides on the subscribe reply rather than being
                    // fetched separately, and that is the whole point: a client
                    // that called status and then subscribed would miss every
                    // event in between, which at 3-4 word boundaries a second is
                    // a highlight that starts out wrong.
                    //
                    // Registering and snapshotting under the subscriber's own
                    // lock is what closes the gap. Writing the reply first and
                    // registering after — which is what this did — guaranteed
                    // the snapshot was never NEWER than the first streamed
                    // event, but left a window where an event fired between the
                    // two reached nobody at all: the subscriber did not exist
                    // yet, and the snapshot it had just been sent predated the
                    // event. Phase 6's highlight would sit one word stale until
                    // the next boundary arrived. Holding the lock makes any
                    // concurrent event queue behind the snapshot instead, so the
                    // stream is both ordered and gapless.
                    var subscriber = new Subscriber
                    {
                        Writer = writer,
                        Kind = request.ClientKind,
                        Pid = request.ClientPid,
                    };

                    // Only identified clients are logged. An anonymous subscriber
                    // is someone running `vst-ctl subscribe | jq`, and a line per
                    // debugging session is noise in the file that exists to make
                    // a silent press explainable.
                    if (subscriber.Kind is { } kind)
                    {
                        Log($"{kind} attached (pid {subscriber.Pid?.ToString() ?? "unknown"})");
                        if (kind == Request.ClientKindUi) UiAttachedChanged?.Invoke();
                    }

                    lock (subscriber.Lock)
                    {
                        _subscribers[id] = subscriber;
                        writer.WriteLine(Protocol.Encode(
                            new Response { Ok = true, Status = Snapshot() }));
                    }

                    // The connection now belongs to the event stream. Park here
                    // until the client goes away; anything it sends afterwards
                    // is ignored, because a subscription is not a session.
                    await reader.ReadToEndAsync(token);
                    break;
                }

                if (request.Verb == RequestVerb.Benchmark)
                {
                    // The only verb that answers more than once. It writes a row
                    // per configuration and then a terminal reply, so it owns the
                    // writer for the duration rather than returning a Response.
                    await BenchmarkAsync(writer, request, token);
                    continue;
                }

                if (request.Verb == RequestVerb.Shutdown)
                {
                    // Answer BEFORE tearing anything down. Cancelling first would
                    // race the listener's close against this write, and the client
                    // would see "the daemon closed the connection without
                    // replying" — a success that reads as a failure, on the one
                    // verb whose whole job is to end the process.
                    await WriteAsync(writer, Response.Success());
                    Log("shutdown requested");
                    RequestShutdown();
                    break;
                }

                await WriteAsync(writer, Handle(request));
            }
        }
        catch (IOException) { /* client vanished mid-write */ }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Log($"connection error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            if (_subscribers.TryRemove(id, out var gone) && gone.Kind is { } kind)
            {
                Log($"{kind} detached (pid {gone.Pid?.ToString() ?? "unknown"})");
                if (kind == Request.ClientKindUi) UiAttachedChanged?.Invoke();
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, Response response) =>
        await writer.WriteLineAsync(Protocol.Encode(response));

    // ---------------------------------------------------------------- dispatch

    private Response Handle(Request request)
    {
        switch (request.Verb)
        {
            case RequestVerb.Toggle:
                return Toggle(request);

            case RequestVerb.Speak:
                if (string.IsNullOrWhiteSpace(request.Text))
                    return Response.Fail("speak needs text");
                return StartSpeaking(request.Text, request);

            case RequestVerb.Read:
                return ReadSelection(request);

            case RequestVerb.Seek:
                return Seek(request);

            case RequestVerb.Stop:
                lock (_gateLock) _gate.NoteState(SpeechState.Stopping);
                _session.Stop();
                return Response.Success();

            case RequestVerb.Pause:
                _session.Pause();
                return Response.Success();

            case RequestVerb.Resume:
                _session.Resume();
                return Response.Success();

            case RequestVerb.Status:
                return new Response { Ok = true, Status = Snapshot() };

            case RequestVerb.Reload:
                RefreshConfig(force: true);
                return new Response
                {
                    Ok = true,
                    Notice = _config.Notes.Count > 0 ? string.Join(" ", _config.Notes) : null,
                };

            case RequestVerb.Config:
                RefreshConfig(force: false);
                return new Response { Ok = true, Config = ConfigSnapshot() };

            case RequestVerb.Shutdown:
                // Handled in the connection loop, which owns the ordering between
                // the reply and the teardown. Reaching here means someone called
                // Handle directly, and answering honestly beats falling through
                // to "unsupported verb" — which would be a lie about a verb that
                // exists.
                RequestShutdown();
                return Response.Success();

            default:
                return Response.Fail($"unsupported verb {request.Verb}");
        }
    }

    /// <summary>
    /// Everything a client needs to render the current moment without having
    /// watched the whole utterance.
    /// </summary>
    /// <summary>
    /// argv beats the file, and a per-request value beats both. An explicit ask
    /// is always more specific than a stored preference — while the file
    /// winning by default is what makes a portable folder resume with the voice
    /// it was last used with.
    /// </summary>
    private string EffectiveVoice => _options.VoiceOverride ?? _config.Settings.DefaultVoice;
    private string EffectiveLanguage => _options.LanguageOverride ?? _config.Settings.Language;

    /// <summary>
    /// Pick up an edited settings.json / pronunciations.json and hand the result
    /// to the session. Cheap: two stats, and a rebuild only when a timestamp
    /// actually moved.
    /// </summary>
    private void RefreshConfig(bool force)
    {
        if (_config.Reload(force))
            _session.Options = _config.SessionOptions;
    }

    private ConfigPayload ConfigSnapshot()
    {
        // The store's reason belongs with the other non-fatal explanations
        // rather than in the field itself: FirstRunView writes 383 MB to this
        // path, and a client that has to strip a parenthesis off it before
        // calling Path.Combine is a client that will one day forget.
        var notes = LinuxDataPaths.IsAppImage
            ? _config.Notes
                .Append($"running from {LinuxDataPaths.AppImageFile} — models and data are in " +
                        $"{LinuxDataPaths.StoreRoot} ({LinuxDataPaths.Store.Reason}), " +
                        "not beside the program, because an AppImage is read-only.")
                .ToList()
            : _config.Notes;

        if (LinuxDataPaths.PortableHome is { } portable)
            notes = notes.Append(
                $"a portable home is in use ({portable}): hotkey binding writes into it " +
                "instead of your desktop's configuration, so keys bound from here do nothing.")
                .ToList();

        return new ConfigPayload(
        LinuxDataPaths.BaseDir,
        _config.DataDir,
        _config.ModelsRoot,
        _config.Writable,
        File.Exists(_config.SettingsPath),
        File.Exists(_config.PronunciationsPath),
        _config.Pronunciations.Rules.Count,
        _config.Pronunciations.Enabled,
        EffectiveVoice,
        EffectiveLanguage,
        _config.Settings.TotalStep,
        _config.Settings.MaxChunkChars,
        _config.Settings.MinChunkChars,
        _config.Settings.InterChunkSilenceMs,
        notes,
        Execution.Provider,
        Execution.Threads,
        Execution.Reason,
        BenchmarkSnapshot(),
        LinuxDataPaths.StoreRoot);
    }

    /// <summary>
    /// The stored profile as reported, re-tested against the machine as it is
    /// now rather than as it was at startup.
    ///
    /// <para>Re-testing matters: <c>TotalStep</c> is a setting, so editing
    /// <c>settings.json</c> can invalidate a profile without anything else
    /// happening — and the moment a user asks <c>config</c> why their benchmark
    /// stopped applying is exactly when the answer needs to be current.</para>
    /// </summary>
    private BenchmarkSummary? BenchmarkSnapshot()
    {
        var stored = BenchmarkStore.Load(LinuxDataPaths.BenchmarkFile(_config.DataDir));
        if (stored is null) return null;

        var now = MachineFacts.Current(
            _config.ModelsRoot, _config.Settings.TotalStep, EffectiveVoice, EffectiveLanguage);

        var staleness = stored.StalenessAgainst(now);

        // "Applied" is about the session that is running, not only about
        // staleness: a profile measured five minutes ago is perfectly valid and
        // still not in force until the daemon restarts.
        bool inForce = staleness.Count == 0
                       && stored.Threads == Execution.Threads
                       && string.Equals(stored.Provider, Execution.Provider, StringComparison.Ordinal);

        var reasons = staleness.Count > 0
            ? staleness
            : inForce
                ? Array.Empty<string>()
                : new[] { "valid, but the daemon started before it was measured — restart to apply" };

        return new BenchmarkSummary(
            stored.MeasuredUtc, stored.Threads, stored.Provider, inForce, reasons);
    }

    // --------------------------------------------------------------- benchmark

    /// <summary>
    /// Sweep this machine and record the answer, reporting a row at a time.
    ///
    /// <para><b>Why the daemon runs it and not the client.</b> Sweeping means
    /// building ONNX sessions, and <c>vst-ctl</c> is NativeAOT precisely so that
    /// it carries nothing — putting ORT in it would trade 6 ms of process start
    /// for ~100 ms on every hotkey press. The daemon is the process that already
    /// has the model, so it is the one that can measure.</para>
    ///
    /// <para><b>What it costs while it runs.</b> One extra session at a time,
    /// built and disposed per row, alongside whatever the daemon already holds.
    /// Peak is therefore two sessions, not eight.</para>
    /// </summary>
    private async Task BenchmarkAsync(StreamWriter writer, Request request, CancellationToken token)
    {
        if (_synthesizerFor is null)
        {
            await WriteAsync(writer, Response.Fail("this daemon was built without a benchmark backend"));
            return;
        }

        // Interlocked rather than a lock: the refusal has to be immediate and
        // must never queue behind the sweep it is refusing to duplicate.
        if (Interlocked.CompareExchange(ref _benchmarking, 1, 0) != 0)
        {
            await WriteAsync(writer, Response.Fail("a benchmark is already running"));
            return;
        }

        try
        {
            RefreshConfig(force: false);

            if (!Directory.Exists(Path.Combine(_config.ModelsRoot, "onnx")))
            {
                await WriteAsync(writer, Response.Fail(
                    $"no onnx/ under {_config.ModelsRoot} — nothing to measure. " +
                    "Download the models first."));
                return;
            }

            // Speaking is the most obvious form of "this machine is busy", and it
            // is also the one case where the load is ours: the sweep would both
            // measure the utterance and stutter it.
            if (_session.State != SpeechState.Idle)
            {
                await WriteAsync(writer, Response.Fail(
                    "the daemon is speaking — stop it first, or the sweep measures the utterance"));
                return;
            }

            var notes = new List<string>();
            bool force = request.Force ?? false;

            double load = await MachineFacts.IdleCpuPercentAsync(cancellationToken: token);
            if (load >= LoadGuardPercent && !force)
            {
                await WriteAsync(writer, Response.Fail(
                    $"this machine is {load:F0}% busy — a sweep run now measures whatever else is " +
                    "running, and would be recorded with a timestamp as if it were sound. " +
                    "Wait, or pass --force."));
                return;
            }

            if (load >= LoadGuardPercent)
                notes.Add($"measured with the machine {load:F0}% busy (--force): treat the table as indicative.");
            else if (load < 0)
                notes.Add("could not read /proc/stat, so the machine's load before the sweep is unknown.");

            var machine = MachineFacts.Current(
                _config.ModelsRoot, _config.Settings.TotalStep,
                EffectiveVoice, EffectiveLanguage, Math.Max(load, 0));

            var options = _config.Synthesis(EffectiveVoice, EffectiveLanguage);
            var writeLock = new object();

            Log($"benchmark: sweeping {BenchmarkSweep.Candidates(machine.LogicalProcessors).Count} " +
                $"configurations on {machine.LogicalProcessors} logical processors");

            // Off the connection's thread: the sweep is tens of seconds of
            // blocking native work, and holding an async continuation on it would
            // occupy a thread-pool thread for the duration.
            var profile = await Task.Run(() => BenchmarkSweep.Run(
                machine,
                _synthesizerFor,
                options,
                candidates: null,
                gpuProviders: _switch?.SweepableGpuProviders,
                onProgress: p =>
                {
                    // Nothing else writes to this connection — it is not a
                    // subscriber — so the lock is here to make that fact local
                    // rather than something a reader has to go and verify.
                    lock (writeLock)
                    {
                        writer.WriteLine(Protocol.Encode(new Response { Ok = true, Progress = p }));
                    }
                },
                cancellationToken: token), token);

            foreach (var failed in profile.Table.Where(r => r.Failed))
                notes.Add($"{failed.Label} threads could not be measured: {failed.Error}");

            string path = LinuxDataPaths.BenchmarkFile(_config.DataDir);
            bool saved = BenchmarkStore.TrySave(path, profile, out string? saveError);
            if (!saved)
                notes.Add($"could not save the profile to {path}: {saveError}. " +
                          "The measurement above is still correct; it just will not survive a restart.");

            // Applied here rather than at the next start. The session has been
            // idle since the guard above, and the switch rebuilds when the answer
            // changes — so a sweep the user waited a minute for is in force by the
            // time it prints, which is what everybody assumed it already did.
            //
            // It can still legitimately NOT be in force: the profile may have
            // picked the GPU on a machine that has since been unplugged. Reporting
            // the decision rather than the profile is what tells those apart.
            bool rebuilt = _switch?.ReevaluateWhenIdle() ?? false;

            bool appliesNow =
                profile.Threads == Execution.Threads &&
                string.Equals(profile.Provider, Execution.Provider, StringComparison.Ordinal);

            if (appliesNow && rebuilt)
                notes.Add("applied now: the daemon rebuilt its session on this profile, " +
                          "so the next press uses it. No restart needed.");
            else if (!appliesNow)
                notes.Add(
                    $"the daemon is running {Execution.Describe()} rather than what this sweep picked. " +
                    "That is the settings and the power lead having their say — see `vst-ctl config`.");

            Log($"benchmark: picked {profile.Provider} {Describe(profile.Threads)}" +
                $"{(saved ? $", saved to {path}" : ", not saved")}");

            await WriteAsync(writer, new Response
            {
                Ok = true,
                Benchmark = new BenchmarkPayload(
                    profile, saved, saved ? path : null, appliesNow, notes),
            });
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-sweep. The client's connection is going away with
            // us, so there is nobody left to tell.
        }
        catch (Exception ex)
        {
            Log($"benchmark failed: {ex.GetType().Name}: {ex.Message}");
            await WriteAsync(writer, Response.Fail($"benchmark failed: {ex.Message}"));
        }
        finally
        {
            Interlocked.Exchange(ref _benchmarking, 0);
        }
    }

    /// <summary>
    /// Busy-percentage at or above which a sweep refuses to run unaided.
    ///
    /// <para>15 rather than something tighter: a desktop with a browser open is
    /// never at zero, and a guard that fires on an idle machine would be turned
    /// off with <c>--force</c> permanently, which is worse than not having it.</para>
    /// </summary>
    private const double LoadGuardPercent = 15;

    private static string Describe(int threads) =>
        threads == CpuBudget.Auto ? "auto threads" : $"{threads} threads";

    private StatusPayload Snapshot()
    {
        var at = _session.LastBoundary;
        return new StatusPayload(
            _session.State, _session.IsPaused, _modelLoaded,
            EffectiveVoice, EffectiveLanguage, _options.Version,
            _session.CurrentText, at?.SourceOffset, at?.SourceLength,
            TrayStatus?.Invoke(),
            // Asked of the switch every time rather than cached: it is the one
            // status field that changes without anything being spoken, which is
            // the whole point of the battery rule.
            _switch is null ? null : Execution.Describe());
    }

    /// <summary>
    /// The primary key: read what is selected now, interrupting whatever is
    /// playing. Never means stop — <c>stop</c> has its own binding.
    /// </summary>
    private Response ReadSelection(Request request)
    {
        lock (_gateLock)
        {
            if (!_gate.PressToRead(Environment.TickCount64))
                return Response.Success(ToggleAction.Ignored);
        }

        string? text = request.Text;
        string? notice = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            // BEFORE the stop, not after. Capturing first means a press with
            // nothing selected leaves the current reading alone; capturing
            // after would silence it and then fail, so a mis-press would cost
            // the user their place with nothing to show for it.
            var selection = _selection.Capture(request.Display);
            if (!selection.Ok) return Refuse(selection.Reason!);
            text = selection.Text;
            notice = selection.Notice;

            // Already reading exactly this? Then the press means nothing and
            // must do nothing.
            //
            // X11 has no "nothing is selected". PRIMARY keeps its owner after
            // the user clicks away, so a press with nothing newly highlighted
            // still captures the LAST selection — and restarting the passage
            // the user is in the middle of hearing, from the top, is never what
            // that press meant. Reported from real use, and it is the one case
            // the capture-before-stop ordering could not cover on its own:
            // the capture succeeds, it just succeeds with the same text.
            //
            // Only for a captured selection. An explicit `read <text>` is a
            // caller asking for something by name, and re-reading it is a
            // reasonable thing to have asked for.
            if (_session.State != SpeechState.Idle &&
                string.Equals(text, _session.CurrentText, StringComparison.Ordinal))
            {
                lock (_gateLock) _gate.NoteState(_session.State);
                return Response.Success(ToggleAction.Ignored);
            }
        }

        if (NotReadyToSpeak() is { } refusal) return refusal;

        var options = _config.Synthesis(
            request.Voice ?? _options.VoiceOverride,
            request.Language ?? _options.LanguageOverride);

        // The notice goes out on BOTH paths, and they are not redundant. The
        // response answers this caller — vst-ctl, which prints to a stderr that
        // in the hotkey path nobody is looking at. The Preparing event reaches
        // every subscriber, which is where the tray and the Reader tab are, and
        // is therefore the only one of the two a user actually sees.
        if (!_session.Restart(text!, options, notice: notice))
            return Refuse($"busy ({_session.State})");

        _modelLoaded = true;
        return new Response { Ok = true, Action = ToggleAction.Speak, Notice = notice };
    }

    /// <summary>
    /// Jump to a character offset and read from there.
    ///
    /// <para>Deliberately <b>not</b> debounced, which is the one place this
    /// departs from the other speaking verbs. The 150 ms window exists because a
    /// finger on a key double-taps and the second tap means nothing; a click is
    /// aimed at a specific word, so dropping it silently would leave the user
    /// having clicked and heard nothing. Two clicks in 150 ms are two requests,
    /// and the later one wins because it stops the earlier.</para>
    ///
    /// <para>The gate is still told the resulting state, or the next press of the
    /// hotkey would be decided from a stale one.</para>
    /// </summary>
    private Response Seek(Request request)
    {
        if (request.Offset is not int offset)
            return Response.Fail("seek needs an offset");
        if (offset < 0)
            return Response.Fail("offset cannot be negative");

        // CurrentText while a reading is in progress, LastText after it ended.
        // The second is the ordinary case rather than the exotic one: the reading
        // finishes, the text is still on screen, and the user clicks a word in it.
        string? text = request.Text ?? _session.CurrentText ?? _session.LastText;
        if (string.IsNullOrEmpty(text))
            return Response.Fail("nothing has been read yet, so there is nothing to seek in");

        if (offset >= text.Length)
            return Response.Fail($"offset {offset} is past the end of {text.Length} characters");

        // Snapped in the daemon, not the client, so every client agrees about
        // where a word starts -- and agrees with the boundary events that drew
        // the highlight the user clicked on.
        int start = BoundaryPlanner.SnapToWordStart(text, offset);

        if (NotReadyToSpeak() is { } refusal) return refusal;

        var options = _config.Synthesis(
            request.Voice ?? _options.VoiceOverride,
            request.Language ?? _options.LanguageOverride);

        if (!_session.Restart(text, options, startOffset: start))
            return Refuse($"busy ({_session.State})");

        lock (_gateLock) _gate.NoteState(_session.State);
        _modelLoaded = true;
        return Response.Success(ToggleAction.Speak);
    }

    /// <summary>
    /// One key does everything. The gate decides which thing, and it is held
    /// under a lock so that two clients pressing at once cannot both be told
    /// "you started it".
    ///
    /// <para>Kept alongside <see cref="ReadSelection"/> rather than replaced by
    /// it: the tray menu's single Speak/Stop item is a toggle, and so is
    /// anything scripted against the original contract.</para>
    /// </summary>
    private Response Toggle(Request request)
    {
        ToggleAction action;
        lock (_gateLock)
        {
            _gate.NoteState(_session.State);
            action = _gate.Press(Environment.TickCount64);
        }

        switch (action)
        {
            case ToggleAction.Speak:
                if (!string.IsNullOrWhiteSpace(request.Text))
                    return StartSpeaking(request.Text, request) with { Action = action };

                var selection = _selection.Capture(request.Display);
                if (!selection.Ok)
                {
                    // Not a state change. An empty selection is a no-op with a
                    // tray blip, so the gate must go back to Idle or the next
                    // press would be read as a stop.
                    lock (_gateLock) _gate.NoteState(_session.State);
                    return Response.Fail(selection.Reason!);
                }
                // Notice rides along rather than replacing the outcome: a
                // truncated selection (R-9) is speech that started, and the fact
                // that it was cut is something to say afterwards, not instead.
                // It is dropped if the speak itself failed, where it would only
                // dilute the reason.
                var started = StartSpeaking(selection.Text, request, selection.Notice)
                    with { Action = action };
                return started.Ok ? started with { Notice = selection.Notice } : started;

            case ToggleAction.Stop:
                _session.Stop();
                return Response.Success(ToggleAction.Stop);

            default:
                // Debounced, or a press that arrived mid-teardown. Success, not
                // an error: the request was understood and deliberately dropped,
                // and a client that logged an error for every double-tap would
                // be noise. Action says which, so the ambiguity is only in how
                // loudly it is reported, never in whether it can be known.
                return Response.Success(ToggleAction.Ignored);
        }
    }

    /// <summary>
    /// Everything that has to be true before speech can start, checked in one
    /// place because three verbs need it and three copies is how they drift.
    ///
    /// <para><b>Per request, never once at startup.</b> Both conditions can
    /// change under a running daemon — the app downloads the models into it, or
    /// the audio server comes back — and the next press should then work without
    /// a restart. Refusing at startup instead would take the control socket with
    /// it, which is the failure <see cref="LazyAudioSink"/> exists to prevent:
    /// the verbs that could explain the problem die with the daemon.</para>
    ///
    /// <para>Config is refreshed first, so an edit made since the last utterance
    /// is in force for this one. Same behaviour as the Windows engine: one stat
    /// per Speak, reparse only when the timestamp moved.</para>
    /// </summary>
    /// <returns>The refusal to send back, or null when speech may proceed.</returns>
    private Response? NotReadyToSpeak()
    {
        RefreshConfig(force: false);

        if (!Directory.Exists(Path.Combine(_config.ModelsRoot, "onnx")))
            return Refuse(
                $"no models under {_config.ModelsRoot}. Open VibeSuperTonic and " +
                "accept the model licence to download them.");

        // The message carries libpulse's own reason rather than a generic one:
        // "Connection refused" and "No such entity" send the reader somewhere
        // different, and this is the first thing a field report needs.
        if (_sink is not null)
        {
            int before = _sink.Reconnects;
            if (!_sink.TryOpen(out string? audioError))
                return Refuse($"no audio device available: {audioError}");

            // TryOpen verifies a cached device rather than trusting it, so this
            // is where an audio-server restart is noticed — before the utterance
            // that would otherwise have died on it. Logged because a daemon that
            // silently healed is indistinguishable from one that never broke, and
            // the difference is the whole content of a field report.
            if (_sink.Reconnects != before)
                Log($"audio device was lost ({_sink.LastLoss}) — reopened, " +
                    $"{_sink.Reconnects} time(s) this run");
        }

        return null;
    }

    /// <summary>
    /// Decline, and put the gate back where the session actually is — a refusal
    /// is not a state change, and leaving the optimistic Preparing behind would
    /// make the next press read as a stop.
    /// </summary>
    private Response Refuse(string message)
    {
        lock (_gateLock) _gate.NoteState(_session.State);
        return Response.Fail(message);
    }

    /// <param name="notice">
    /// Carried out on the Preparing event so subscribers see it — see
    /// <see cref="SessionEvent.Notice"/>. Null for a plain <c>speak</c>, where
    /// the caller named the text and there is nothing to report about it.
    /// </param>
    private Response StartSpeaking(string text, Request request, string? notice = null)
    {
        if (NotReadyToSpeak() is { } refusal) return refusal;

        // THE START OF AN UTTERANCE, which is the only moment a provider may
        // change. The power lead may have moved, settings.json may have been
        // edited, a benchmark may have been written — this is where all three are
        // noticed, and it is a no-op that costs one file read when none of them
        // has. It does nothing at all unless the session is idle, so the answer
        // cannot change underneath a render already in flight.
        //
        // Before Speak rather than after: rebuilding takes about a second, and
        // paying it after the session has begun scheduling boundaries would put
        // the highlight a second behind the audio for the first chunk.
        _switch?.ReevaluateWhenIdle();

        var options = _config.Synthesis(
            request.Voice ?? _options.VoiceOverride,
            request.Language ?? _options.LanguageOverride);

        if (!_session.Speak(text, options, notice: notice))
        {
            lock (_gateLock) _gate.NoteState(_session.State);
            return Response.Fail($"busy ({_session.State})");
        }

        _modelLoaded = true;   // the first Speak forces the load
        return Response.Success();
    }

    // ------------------------------------------------------------------ events

    private void OnSessionEvent(SessionEvent e)
    {
        lock (_gateLock)
        {
            if (e.Kind == SessionEventKind.StateChanged && e.State is { } state)
                _gate.NoteState(state);
        }

        // An utterance that failed is the single most useful line this daemon can
        // write, and until now it was written nowhere. It went to the event
        // stream and stopped there — and nothing subscribes to that yet, so the
        // one report of "the audio device died five hours ago" was delivered to
        // an empty room. The dead-stream defect survived exactly that long
        // because of this.
        if (e.Kind == SessionEventKind.Error)
            Log($"utterance failed: {e.Message}");

        if (_subscribers.IsEmpty) return;

        string line = Protocol.Encode(e);
        foreach (var (id, sub) in _subscribers)
        {
            try
            {
                // Per-subscriber lock: this is called from the audio thread and
                // two events must not interleave halfway through a line.
                lock (sub.Lock) sub.Writer.WriteLine(line);
            }
            catch
            {
                // A client that closed its end mid-utterance is routine, not an
                // error. Dropping it here rather than letting the exception
                // reach the session is what keeps one dead subscriber from
                // stopping the speech everyone else is listening to.
                _subscribers.TryRemove(id, out _);
            }
        }
    }

    /// <summary>Load the model now rather than on the first press.</summary>
    public async Task PreloadAsync(CancellationToken token)
    {
        try
        {
            await _synth.PreloadAsync(token);
            _modelLoaded = true;
            Log("model preloaded");
        }
        catch (Exception ex)
        {
            // Not fatal. A daemon that refuses to start because a preload failed
            // is worse than one that tries again on the first press and reports
            // the error where the user is looking.
            Log($"preload failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Stderr AND data/logs/daemon.log. A daemon started by a hotkey or a
    // double-click has no terminal, and those are the runs a field report is
    // written about — see DaemonLog.
    private static void Log(string message) => DaemonLog.Write(message);

    public void Dispose()
    {
        _session.Emitted -= OnSessionEvent;
        _session.Dispose();
    }
}

/// <param name="VoiceOverride">
/// Voice from <c>--voice</c>, or null to take whatever <c>settings.json</c>
/// says. A flag is an explicit instruction for this run and outranks the file.
/// </param>
/// <param name="LanguageOverride">Language from <c>--lang</c>, same precedence.</param>
/// <param name="Version">
/// Reported by <c>status</c> so a stale client is diagnosable. Supplied by
/// <c>Program</c> from the assembly, which takes it from <c>&lt;VstVersion&gt;</c>
/// in Directory.Build.props — the same element both packers read. The default
/// here is deliberately not a version number: a stale literal that looks
/// plausible is worse than one that says it does not know.
/// </param>
public sealed record DaemonOptions(
    string? VoiceOverride = null,
    string? LanguageOverride = null,
    string Version = "unknown");

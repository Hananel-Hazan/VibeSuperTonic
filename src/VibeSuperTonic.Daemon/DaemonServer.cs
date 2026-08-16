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

    /// <param name="sink">
    /// The deferred audio device, so the speak path can open it and report a
    /// failure to the caller. Null in tests that drive the session directly.
    /// </param>
    public DaemonServer(DaemonOptions options, HostConfig config, ISynthesizer synth,
        SpeechSession session, ISelectionSource? selection = null, LazyAudioSink? sink = null)
    {
        _options = options;
        _config = config;
        _synth = synth;
        _session = session;
        _selection = selection ?? new NullSelectionSource();
        _sink = sink;

        _session.Emitted += OnSessionEvent;
    }

    private sealed class Subscriber
    {
        public required StreamWriter Writer { get; init; }
        public object Lock { get; } = new();
    }

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

        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(token); }
                catch (OperationCanceledException) { break; }

                // Fire and forget: one misbehaving client must not stall accept.
                _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
            }
        }
        finally
        {
            listener.Close();
            try { File.Delete(path); } catch { /* going away anyway */ }
        }
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
                    var subscriber = new Subscriber { Writer = writer };
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

                await WriteAsync(writer, Handle(request));
            }
        }
        catch (IOException) { /* client vanished mid-write */ }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Log($"connection error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            _subscribers.TryRemove(id, out _);
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

    private ConfigPayload ConfigSnapshot() => new(
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
        _config.Notes);

    private StatusPayload Snapshot()
    {
        var at = _session.LastBoundary;
        return new StatusPayload(
            _session.State, _session.IsPaused, _modelLoaded,
            EffectiveVoice, EffectiveLanguage, _options.Version,
            _session.CurrentText, at?.SourceOffset, at?.SourceLength);
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
        if (_sink is not null && !_sink.TryOpen(out string? audioError))
            return Refuse($"no audio device available: {audioError}");

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

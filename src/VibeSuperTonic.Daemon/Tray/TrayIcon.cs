using System.Diagnostics;
using Tmds.DBus.Protocol;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Daemon.Tray;

/// <summary>
/// The tray icon, owned by the daemon because it has to exist while the window
/// is closed.
///
/// <para><b>R-1 holds by construction here, not by discipline.</b> This class is
/// given a way to <em>subscribe</em> and a way to <em>invoke a verb</em>, and no
/// reference to <c>SpeechSession</c> at all — so the hazard the plan names, tray
/// code reading pipeline state directly because it happens to be in the same
/// process, is not available to it. Every menu row below calls a verb
/// <c>vst-ctl</c> also has.</para>
///
/// <para><b><c>DBUS_SESSION_BUS_ADDRESS</c> is the new <c>$DISPLAY</c>.</b> A
/// process's environment is fixed for its life, so a daemon started outside a
/// session — <c>systemd --user</c>, a bare shell, a script — will never find the
/// session bus, and re-checking returns the same answer forever. In the ordinary
/// path <c>vst-ctl</c> starts the daemon from inside the session and it inherits
/// the variable, so the common case works by construction. Two rules regardless,
/// both of them here: the daemon <b>must not refuse to start</b> without a
/// session bus, and the absence must be visible in <c>status</c> rather than
/// presenting as a tray that silently never appears.</para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string WellKnownKde = "org.kde.StatusNotifierWatcher";
    private const string WellKnownX = "org.x.StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string ItemInterface = "org.kde.StatusNotifierItem";

    private readonly Func<Request, Response> _invoke;
    private readonly Func<bool> _uiAttached;
    private readonly Action<string> _log;

    private readonly CancellationTokenSource _stopping = new();

    private Connection? _connection;
    private TrayMenu? _menu;
    private string? _watcher;
    private string? _busName;
    private string? _watcherOwner;

    private TrayState _state = TrayState.Idle;
    private string _notice = "";
    private int _sentences;

    /// <param name="raiseWindow">
    /// Ask the open window to come to the front. A separate action rather than
    /// another <see cref="Request"/> because there is no window on the other end
    /// of a socket to answer one: this reaches the UI through the event stream
    /// it is already subscribed to.
    /// </param>
    public TrayIcon(
        Func<Request, Response> invoke,
        Func<bool> uiAttached,
        Action<string> log,
        Action? raiseWindow = null)
    {
        _invoke = invoke;
        _uiAttached = uiAttached;
        _log = log;
        _raiseWindow = raiseWindow ?? (() => { });
    }

    private readonly Action _raiseWindow;

    /// <summary>
    /// What <c>status</c> reports. Never "fine" by default: until
    /// <see cref="StartAsync"/> has actually registered with a watcher, this says
    /// so, because "there is no tray icon and nothing said why" is precisely the
    /// failure this field exists to prevent.
    /// </summary>
    public string Status { get; private set; } = "not started";

    public async Task StartAsync()
    {
        string? address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrEmpty(address))
        {
            Status = "no tray icon: DBUS_SESSION_BUS_ADDRESS is not set, so this daemon "
                   + "was started outside the desktop session";
            _log(Status);
            return;
        }

        try
        {
            var connection = new Connection(address);
            await connection.ConnectAsync();
            _connection = connection;

            // A well-known name of the shape the specification suggests. Hosts
            // accept the unique name too, but some log a complaint about it, and
            // the pid in the name is the thing you want when two daemons are
            // somehow running.
            string busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
            await RequestNameAsync(connection, busName);

            _menu = new TrayMenu(Rows(), _log);
            connection.AddMethodHandler(new StatusNotifierItem(
                view: () => new TrayView(_state, _uiAttached(), ToolTip()),
                activate: OpenWindow,
                secondaryActivate: () => Invoke(RequestVerb.Toggle),
                log: _log));
            connection.AddMethodHandler(_menu);

            string? watcher = await RegisterAsync(connection, busName);
            if (watcher is null)
            {
                Status = "no tray icon: nothing on the session bus implements "
                       + $"{WellKnownKde} or {WellKnownX}";
                _log(Status);
                return;
            }

            Status = $"registered with {watcher} as {busName}";
            _log($"tray icon {Status}");

            _watcher = watcher;
            _busName = busName;
            _watcherOwner = await OwnerOfAsync(connection, watcher);
            _ = Task.Run(() => FollowWatcherAsync(connection, _stopping.Token));

            // A tray whose bus connection dies looks exactly like a tray that was
            // never built — the icon disappears and nothing anywhere says why.
            // This is the same lesson the audio-device loss taught in a louder
            // register: the failure has to reach somewhere a person will look.
            _ = connection.DisconnectedAsync().ContinueWith(t =>
            {
                Status = $"tray icon lost the session bus: {t.Result?.Message ?? "disconnected"}";
                _log(Status);
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            // A tray that cannot be built is a cosmetic loss; a daemon that
            // cannot start is a hotkey that silently does nothing. This is the
            // whole reason the failure is caught rather than thrown.
            Status = $"no tray icon: {ex.GetType().Name}: {ex.Message}";
            _log(Status);
        }
    }

    // ------------------------------------------------------------- the stream

    /// <summary>
    /// The same <see cref="SessionEvent"/> channel every external client gets.
    /// Attached by the daemon so this class never sees the session itself.
    /// </summary>
    public void OnSessionEvent(SessionEvent evt)
    {
        var before = (_state, _notice, _sentences);

        switch (evt.Kind)
        {
            case SessionEventKind.StateChanged when evt.State == SpeechState.Preparing:
                _state = TrayState.Preparing;

                // Text and Notice ride on the Preparing transition and only
                // there — which is exactly why the tooltip can say how much
                // there is to read 28 ms after the press, rather than when the
                // first audio arrives ~750 ms later.
                _notice = evt.Notice ?? "";
                _sentences = evt.Text is { } text ? CountSentences(text) : 0;
                break;

            case SessionEventKind.StateChanged when evt.State is { } state:
                _state = state switch
                {
                    SpeechState.Speaking => TrayState.Speaking,
                    SpeechState.Preparing => TrayState.Preparing,
                    _ => TrayState.Idle,
                };
                if (_state == TrayState.Idle) { _notice = ""; _sentences = 0; }
                break;

            case SessionEventKind.Error:
                _notice = evt.Message ?? "";
                break;
        }

        if (before != (_state, _notice, _sentences)) Refresh();
    }

    /// <summary>Called when a window attaches or detaches: the icon carries that too.</summary>
    public void OnUiAttachedChanged() => Refresh();

    private void Refresh()
    {
        if (_connection is null || _menu is null) return;

        EmitSignal(StatusNotifierItem.ItemPath, ItemInterface, "NewIcon");
        EmitSignal(StatusNotifierItem.ItemPath, ItemInterface, "NewToolTip");

        // Two of the four rows change with state, and a host that has already
        // read the layout will not read it again unless told.
        EmitLayoutUpdated(_menu.NextRevision());
    }

    private string ToolTip()
    {
        string head = _state switch
        {
            TrayState.Speaking or TrayState.Preparing when _sentences > 1 => $"speaking {_sentences} sentences",
            TrayState.Speaking => "speaking",
            TrayState.Preparing => "preparing…",
            _ => "idle — press the hotkey to read the selection",
        };

        // The notice is the reason this tooltip was worth building: an R-9
        // truncation and a selection that never changed both reach the user
        // here or nowhere. Before SessionEvent.Notice existed they went only to
        // vst-ctl's stderr, which in the hotkey path is nobody.
        return _notice.Length == 0 ? head : $"{head}\n{_notice}";
    }

    /// <summary>
    /// How much there is to read, for the tooltip. A scan of the text rather
    /// than the chunker's plan: the tooltip answers "how long is this", not
    /// "where will the boundaries fall", and running the real chunker over
    /// 100 KB to phrase a tooltip would put the planner on the press path for a
    /// number nobody acts on.
    /// </summary>
    private static int CountSentences(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…')) continue;
            if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])) count++;
        }
        return Math.Max(count, 1);
    }

    // --------------------------------------------------------------- the menu

    private IReadOnlyList<MenuRow> Rows() =>
    [
        // The toggle verb, labelled for what the next click does. The daemon
        // decides what a toggle means — the tray is as stateless about it as
        // vst-ctl is, which is what keeps the hotkey and this menu item
        // behaving identically.
        new MenuRow(1,
            () => _state == TrayState.Idle ? "Read the selection" : "Stop reading",
            () => Invoke(RequestVerb.Toggle)),

        new MenuRow(2, () => "Open the window", OpenWindow),

        new MenuRow(3, () => "", () => { }, IsSeparator: true),

        // NOT Quit, and deliberately not labelled as it. R-5 brings the daemon
        // straight back on the next press, so what this does is stop holding
        // ~830 MB until it is wanted again. The product has no off switch and
        // the menu should not pretend otherwise.
        new MenuRow(4,
            () => "Stop the engine (the next press starts it again)",
            () => Invoke(RequestVerb.Shutdown)),
    ];

    private void Invoke(RequestVerb verb)
    {
        // $DISPLAY from this process's own environment, which is the same
        // fallback the daemon already applies to a request that arrives without
        // one. A daemon started outside the session has neither, and the
        // selection capture will say so.
        var response = _invoke(new Request
        {
            Verb = verb,
            Display = verb == RequestVerb.Toggle ? Environment.GetEnvironmentVariable("DISPLAY") : null,
        });

        if (!response.Ok) _log($"tray {verb}: {response.Error}");
    }

    /// <summary>
    /// The one menu row that is not a verb, because opening a window is not
    /// something a daemon can be asked to do over a socket — it is a process
    /// launch, and the binary sits beside this one for the same reason
    /// <c>vst-ctl</c>'s auto-start assumes it does.
    /// </summary>
    private readonly WindowLaunchGate _launchGate = new();

    private void OpenWindow()
    {
        // Raising an already-open window would need the UI to be listening for
        // it. Launching a second one instead would be worse than doing nothing,
        // so this says what happened rather than guessing — and it also refuses
        // while a launch is still in flight, because "is a window open" cannot
        // answer yes until the UI has started and subscribed. See
        // WindowLaunchGate: two activations inside that gap opened two windows.
        if (_launchGate.Refuse(_uiAttached(), DateTime.UtcNow) is { } why)
        {
            // A window that is already open is RAISED, not ignored. The user
            // clicked the tray because they could not see it — under three other
            // windows, or on another desktop — and an icon that answers a click
            // with nothing is one they stop trusting.
            if (_uiAttached()) _raiseWindow();
            _log($"tray: {why}");
            return;
        }

        string exe = Environment.GetEnvironmentVariable("VST_UI")
            ?? System.IO.Path.Combine(AppContext.BaseDirectory, "vibesupertonic-ui");

        if (!File.Exists(exe))
        {
            _launchGate.Failed();
            _log($"tray: {exe} not found (set VST_UI to override)");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex)
        {
            // Let the next click try again rather than sitting behind a grace
            // period for a launch that never happened.
            _launchGate.Failed();
            _log($"tray: could not open the window: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Register again when the watcher comes back.
    ///
    /// <para>Not speculative: a panel restart takes the watcher's bus name with
    /// it, and every registration made to the old one is gone. Without this the
    /// icon disappears for the life of the daemon — which, since the daemon is
    /// designed to run for a week, means "until the user reboots". Watched by
    /// polling the name's owner rather than by a match rule because the question
    /// is one string every fifteen seconds and the answer needs no
    /// subscription.</para>
    /// </summary>
    private async Task FollowWatcherAsync(Connection connection, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);

                string? owner = await OwnerOfAsync(connection, _watcher!);
                if (owner == _watcherOwner) continue;

                _watcherOwner = owner;
                if (owner is null) continue;                  // gone; wait for it to come back

                await RegisterAsync(connection, _busName!);
                _log("tray icon re-registered: the status watcher restarted");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Never fatal. The daemon's job is speech; the icon is how it
                // says what it is doing.
                _log($"tray: could not follow the watcher: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- the bus

    /// <summary>Who owns a bus name, or null when nobody does.</summary>
    private static async Task<string?> OwnerOfAsync(Connection connection, string name)
    {
        MessageBuffer Build()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "GetNameOwner",
                signature: "s");
            writer.WriteString(name);
            return writer.CreateMessage();
        }

        try
        {
            return await connection.CallMethodAsync(Build(), (Message m, object? _) => m.GetBodyReader().ReadString());
        }
        catch (DBusException)
        {
            return null;                                       // NameHasNoOwner
        }
    }

    // MessageWriter is a ref struct, so composing a message and awaiting the call
    // cannot happen in the same scope. Each of these builds in a local function
    // and awaits outside it — the reason for the shape, so it does not get
    // "simplified" back into a compiler error.
    private static Task RequestNameAsync(Connection connection, string name)
    {
        return connection.CallMethodAsync(Build());

        MessageBuffer Build()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "RequestName",
                signature: "su");
            writer.WriteString(name);
            writer.WriteUInt32(0);
            return writer.CreateMessage();
        }
    }

    /// <summary>
    /// Register with whichever watcher is there. Both names are live on Mint —
    /// <c>xapp-sn-watcher</c> owns them together — but the KDE name is the one
    /// every other desktop publishes, so it is tried first and the XApp name is
    /// the fallback rather than the assumption.
    /// </summary>
    private async Task<string?> RegisterAsync(Connection connection, string busName)
    {
        foreach (string watcher in new[] { WellKnownKde, WellKnownX })
        {
            MessageBuffer Build()
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(
                    destination: watcher,
                    path: WatcherPath,
                    @interface: WatcherInterface,
                    member: "RegisterStatusNotifierItem",
                    signature: "s");
                writer.WriteString(busName);
                return writer.CreateMessage();
            }

            try
            {
                await connection.CallMethodAsync(Build());
                return watcher;
            }
            catch (DBusException)
            {
                // Not there, or refused. Try the next one; if neither answers
                // the caller reports it rather than retrying forever.
            }
        }

        return null;
    }

    /// <summary>A signal with no arguments — NewIcon, NewToolTip.</summary>
    private void EmitSignal(string path, string @interface, string member)
    {
        if (_connection is not { } connection) return;

        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(
            destination: null,
            path: path,
            @interface: @interface,
            member: member,
            signature: "");

        connection.TrySendMessage(writer.CreateMessage());
    }

    /// <summary>
    /// <c>LayoutUpdated(u revision, i parent)</c>, written inline rather than
    /// through a body callback.
    ///
    /// <para><b>Deliberate, and the second time this bit.</b> A
    /// <c>MessageWriter</c> handed to an <c>Action&lt;MessageWriter&gt;</c> is
    /// <em>copied</em>, so the arguments land in the copy and the signal goes out
    /// declaring <c>ui</c> over an empty body. The bus answers a malformed
    /// message by disconnecting the sender, so the symptom is not "the menu
    /// failed to update" — it is the icon disappearing the first time the daemon
    /// starts speaking. See <c>StatusNotifierItem.WriteProperties</c> for the
    /// same mistake caught the same way.</para>
    /// </summary>
    private void EmitLayoutUpdated(uint revision)
    {
        if (_connection is not { } connection) return;

        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(
            destination: null,
            path: TrayMenu.MenuPath,
            @interface: "com.canonical.dbusmenu",
            member: "LayoutUpdated",
            signature: "ui");

        writer.WriteUInt32(revision);
        writer.WriteInt32(0);

        connection.TrySendMessage(writer.CreateMessage());
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _connection?.Dispose();
        _stopping.Dispose();
    }
}

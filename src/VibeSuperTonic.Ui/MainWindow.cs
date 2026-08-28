using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The window. Reader, Voices, Tune, Pronunciations, Status, About — and Monitor
/// is gone, collapsed into Status, because with one daemon instead of N SAPI
/// hosts per-host monitoring has lost the thing it monitored.
///
/// <para>Voices arrived in P4 and sits second, next to Reader rather than beside
/// the knobs: choosing a voice is a thing a person does deliberately and often,
/// and it is where a second engine becomes visible at all — the first-run screen
/// deliberately says nothing about Piper.</para>
///
/// <para><b>Parity.</b> Every control here sends a verb <c>vst-ctl</c> also has.
/// The one stated exception is configuration, which the UI writes as a file and
/// the daemon re-reads on mtime. Walk the toolbar and name the verb behind each
/// button: read, stop, pause, resume, reload. A control that cannot be named
/// that way does not belong in this window. Voices keeps the rule literally —
/// voices, voice install, voice remove, speak — with the same one exception: its
/// Use button writes settings.json.</para>
///
/// <para><b>The events arrive on a socket thread</b> and are marshalled here,
/// once, at the boundary. Nothing below this class touches a dispatcher.</para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly DaemonClient _client;
    private readonly ReaderTab _reader;
    private readonly StatusTab _status;
    private readonly TuneTab _tune;
    private readonly InstallBanner _banner = new();
    private readonly TabControl _tabs;
    private readonly TextBlock _connection = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

    /// <summary>The window's normal content, set aside while the first-run screen is up.</summary>
    private readonly Control _shell;

    /// <summary>
    /// What the first-run screen swaps in and out of. The window's own content is
    /// the banner plus this, so a screen change cannot take the banner with it —
    /// see the comment where it is built.
    /// </summary>
    private readonly ContentControl _screen = new();

    private bool _firstRunDecided;

    /// <summary>
    /// Once per launch. The banner reports a comparison the daemon made at ITS
    /// startup and then wrote down, so re-showing it on every reconnect would
    /// repeat a finding the user has already read and turn the strip into
    /// wallpaper.
    /// </summary>
    private bool _installShown;

    public MainWindow(DaemonClient client)
    {
        _client = client;
        _reader = new ReaderTab(client);
        _status = new StatusTab(client, _reader);
        _tune = new TuneTab(client);

        Title = "VibeSuperTonic";
        Width = 900;
        Height = 700;

        // OPENING IS NOT ONE EVENT, AND ON A SCALED SCREEN IT LOOKS LIKE IT.
        // Measured 2026-08-28 on KDE/Wayland at 170%, XWayland, by polling the
        // X server every 40 ms while the window opened:
        //
        //     427 ms   300x200+10+10       Avalonia's placeholder, at the origin
        //     469 ms   900x700+10+10       our size, in PHYSICAL pixels
        //     595 ms   900x700+1469+1429   moved to where it belongs
        //     637 ms   1528x1188+1469+1429 rescaled once the 1.7 factor is known
        //
        // Four different windows in a fifth of a second, and the user watching
        // reported it as opening huge and then shrinking. Nothing here is wrong
        // at the end — 1528x1188 IS 900x700 at this screen's scale — but every
        // intermediate step is on screen.
        //
        // The two halves are fixed differently. The jump across the desktop is
        // ours: without a startup location the window is mapped at +10+10 and
        // moved afterwards.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The resize is not ours to prevent — the scale factor arrives from the
        // X server after the window exists — so the window stays invisible until
        // it has stopped changing shape. Opacity rather than Hide/Show: the
        // window is already mapped by the lifetime before any of our code runs,
        // and hiding a mapped window is what makes it flash in the taskbar.
        Opacity = 0;
        bool shown = false;
        LayoutUpdated += (_, _) =>
        {
            if (shown) return;
            shown = true;

            // One frame after layout settles. Posting at Background rather than
            // setting it here is what lets the rescale land first; without the
            // post the fade begins mid-dance and shows exactly what it is meant
            // to hide.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => Opacity = 1, Avalonia.Threading.DispatcherPriority.Background);
        };

        // AND UNCONDITIONALLY, SHORTLY AFTER OPENING. The line above is the only
        // thing that makes this window visible, so a layout pass that never
        // arrives is not a cosmetic bug — it is an application that starts and
        // shows nothing, which is far worse than the flicker it was hiding.
        Opened += (_, _) =>
        {
            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            timer.Tick += (_, _) => { timer.Stop(); Opacity = 1; };
            timer.Start();
        };

        // Reader first, and it is what opens: this is a reader that has
        // settings, not a control panel that shows text.
        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Reader", Content = _reader },
                new TabItem { Header = "Voices", Content = new VoicesTab(client) },
                new TabItem { Header = "Tune", Content = _tune },
                new TabItem { Header = "Pronunciations", Content = new PronunciationsTab(client) },
                new TabItem { Header = "Status", Content = _status },
                new TabItem { Header = "About", Content = new AboutTab() },
            },
        };

        // Take the user to the sweep rather than running it invisibly: it costs
        // about a minute and the daemon cannot speak while it runs, so it must
        // happen where its progress is showing.
        _banner.RemeasureRequested += async () =>
        {
            _tabs.SelectedIndex = 2;
            await _tune.RunBenchmarkAsync();
        };

        _shell = new DockPanel
        {
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Top,
                    Padding = new Thickness(12, 8),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            Verb("Read selection", RequestVerb.Read),
                            Verb("Stop", RequestVerb.Stop),
                            Verb("Pause", RequestVerb.Pause),
                            Verb("Resume", RequestVerb.Resume),
                            _connection,
                        },
                    },
                },
                _tabs,
            },
        };

        _screen.Content = _shell;

        // THE BANNER LIVES ABOVE THE SCREEN SWAP, NOT INSIDE IT — and the case
        // that forced this is the one where it matters most. An AppImage that was
        // moved orphans its store, which puts the FIRST-RUN screen up asking for
        // a 762 MB download the user has already done; the banner is the only
        // thing that explains why, and while it lived inside the shell the swap
        // took it away just as it appeared.
        //
        // It is a child of THIS panel and of nothing else. Avalonia throws on a
        // control with two visual parents, so leaving it in the shell's children
        // as well is not a layout quirk — it is a hard crash at startup, every
        // time, which is how this was found.
        Content = new DockPanel { Children = { _banner, _screen } };

        // Deliberately after the window exists: a fresh install has no models,
        // and the screen that fixes that is the first thing it should show.
        AttachedToVisualTree += async (_, _) => await ShowFirstRunIfNeededAsync();

        client.Snapshotted += status => Dispatcher.UIThread.Post(async () =>
        {
            _connection.Text = $"connected — daemon {status.Version}";
            _reader.ApplySnapshot(status);

            // The check also runs on connect, because the window opens before
            // the daemon it just started is answering — and a fresh install is
            // exactly the case where that race is guaranteed: no models means a
            // daemon that has nothing to preload and a window with nothing to
            // ask until the socket exists.
            await ShowFirstRunIfNeededAsync();
        });

        client.Received += evt => Dispatcher.UIThread.Post(() =>
        {
            // The tray was clicked while this window was already open — under
            // other windows, or minimised, or on another desktop. Restore before
            // activating: a minimised window that is only Activate()d comes back
            // focused and still minimised on some compositors, which looks
            // exactly like the click doing nothing.
            if (evt.Kind == SessionEventKind.WindowRequested)
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                return;
            }

            _reader.Apply(evt);
        });

        client.ConnectedChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            if (connected) return;
            _connection.Text = "not connected";
            _reader.ShowDisconnected();
        });
    }

    /// <summary>
    /// A fresh install opens on the licence, the download and the two keys —
    /// see <see cref="FirstRunView"/>. Decided by asking whether the models are
    /// there, which is the same check the daemon makes and needs no marker file
    /// and no write.
    /// </summary>
    private async Task ShowFirstRunIfNeededAsync()
    {
        // Both readers are done from ONE config call. They run at the same two
        // moments — the window appearing, and the daemon answering — and asking
        // twice would put two round trips on the path a fresh install is already
        // waiting on.
        if (_firstRunDecided && _installShown) return;

        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is not { } c) return;              // no daemon yet; the toolbar says so

        if (!_installShown)
        {
            _installShown = true;

            // Null from a daemon older than this field, which is not a finding —
            // an absent answer and "nothing moved" must not look the same to
            // anything but this line, and the banner reads null as silence.
            _banner.Show(c.Install);
        }

        // Once. The screen swaps itself away when the download finishes, and a
        // reconnect afterwards must not bring it back over a working window.
        if (_firstRunDecided) return;
        _firstRunDecided = true;
        if (Directory.Exists(Path.Combine(c.ModelsRoot, "onnx"))) return;

        // BaseDir for the manifest, StoreRoot for the bytes. Identical on every
        // install that is not an AppImage; an older daemon sends no StoreRoot at
        // all, and BaseDir is then the right answer by construction.
        var first = new FirstRunView(
            c.BaseDir,
            string.IsNullOrWhiteSpace(c.StoreRoot) ? c.BaseDir : c.StoreRoot,
            c.ModelsRoot);
        first.Completed += () => _screen.Content = _shell;
        _screen.Content = first;
    }

    /// <summary>
    /// A button is a verb with a label on it. Written this way so that adding a
    /// control that is <em>not</em> a verb takes visible effort.
    /// </summary>
    private Button Verb(string label, RequestVerb verb)
    {
        var button = new Button { Content = label };

        // Read carries the client's $DISPLAY for the same reason vst-ctl does:
        // the daemon may have been started without one, and its environment is
        // fixed for life.
        button.Click += async (_, _) => await _client.SendAsync(new Request
        {
            Verb = verb,
            Display = verb == RequestVerb.Read ? Environment.GetEnvironmentVariable("DISPLAY") : null,
        });

        return button;
    }
}

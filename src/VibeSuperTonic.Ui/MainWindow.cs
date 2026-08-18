using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The window. Reader, Tune, Pronunciations, Status, About — and Monitor is gone,
/// collapsed into Status, because with one daemon instead of N SAPI hosts
/// per-host monitoring has lost the thing it monitored.
///
/// <para><b>Parity.</b> Every control here sends a verb <c>vst-ctl</c> also has.
/// The one stated exception is configuration, which the UI writes as a file and
/// the daemon re-reads on mtime. Walk the toolbar and name the verb behind each
/// button: read, stop, pause, resume, reload. A control that cannot be named
/// that way does not belong in this window.</para>
///
/// <para><b>The events arrive on a socket thread</b> and are marshalled here,
/// once, at the boundary. Nothing below this class touches a dispatcher.</para>
/// </summary>
public sealed class MainWindow : Window
{
    private readonly DaemonClient _client;
    private readonly ReaderTab _reader;
    private readonly StatusTab _status;
    private readonly TextBlock _connection = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };

    public MainWindow(DaemonClient client)
    {
        _client = client;
        _reader = new ReaderTab(client);
        _status = new StatusTab(client, _reader);

        Title = "VibeSuperTonic";
        Width = 900;
        Height = 700;

        // Reader first, and it is what opens: this is a reader that has
        // settings, not a control panel that shows text.
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Reader", Content = _reader },
                new TabItem { Header = "Tune", Content = new TuneTab(client) },
                new TabItem { Header = "Pronunciations", Content = new PronunciationsTab(client) },
                new TabItem { Header = "Status", Content = _status },
                new TabItem { Header = "About", Content = new AboutTab() },
            },
        };

        Content = new DockPanel
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
                tabs,
            },
        };

        client.Snapshotted += status => Dispatcher.UIThread.Post(() =>
        {
            _connection.Text = $"connected — daemon {status.Version}";
            _reader.ApplySnapshot(status);
        });

        client.Received += evt => Dispatcher.UIThread.Post(() => _reader.Apply(evt));

        client.ConnectedChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            if (connected) return;
            _connection.Text = "not connected";
            _reader.ShowDisconnected();
        });
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

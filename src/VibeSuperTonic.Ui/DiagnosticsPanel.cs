using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Telemetry;

namespace VibeSuperTonic.Ui;

/// <summary>
/// Live numbers from the daemon — the Windows Monitor tab's content, folded into
/// the Status tab the way the rest of that tab was.
///
/// <para><b>It asks the daemon; it does not watch a file.</b> The Windows Monitor
/// reads a snapshot the engine writes to disk every tick. Here there is one
/// daemon with one socket, and <see cref="RequestVerb.Diagnostics"/> answers on
/// demand, so there is no second copy to go stale and no file to leave behind.</para>
///
/// <para><b>Polling is modest and stops when nobody is looking.</b> Every
/// <see cref="PollSeconds"/> seconds while this panel is in the visual tree — the
/// TabControl removes a tab's content when another is selected — and not at all
/// otherwise, and never two requests at once. A diagnostics panel that costs the
/// daemon something while the window sits on another tab would be the thing it
/// exists to find.</para>
///
/// <para><b>Privacy.</b> The words being read are shown only after the box is
/// ticked, and then only their first 80 characters. The default is the length.
/// Diagnostics is what ends up in screenshots and bug reports.</para>
/// </summary>
public sealed class DiagnosticsPanel : UserControl
{
    /// <summary>Seconds between asks while visible. The daemon's rolling numbers do not change faster than an utterance.</summary>
    private const int PollSeconds = 2;

    private readonly DaemonClient _client;
    private readonly TextBlock _body = Ui.Body("");
    private readonly CheckBox _showText = new()
    {
        Content = "Show the start of the text being read (otherwise only its length)",
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(PollSeconds) };
    private bool _inFlight;

    public DiagnosticsPanel(DaemonClient client)
    {
        _client = client;

        _timer.Tick += async (_, _) => await PollAsync();

        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Diagnostics",
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                },
                Ui.Label("Live from the daemon, refreshed every "
                       + $"{PollSeconds} seconds while this tab is showing. RTF is synthesis time over "
                       + "audio time: under 1.0 keeps ahead of playback, over it cannot."),
                _showText,
                _body,
            },
        };

        // The tab's content leaves the visual tree when another tab is selected,
        // so these two ARE "visible" and "hidden" — the same signal the Status
        // tab already uses to refresh on a visit.
        AttachedToVisualTree += async (_, _) =>
        {
            _timer.Start();
            await PollAsync();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private async Task PollAsync()
    {
        // A slow daemon (it is busy loading a model) must not get a queue of asks.
        // Also skipped while the window is hidden or minimised, which a tab-level
        // check cannot see.
        if (_inFlight || !IsEffectivelyVisible) return;
        _inFlight = true;

        try
        {
            var reply = await _client.SendAsync(new Request
            {
                Verb = RequestVerb.Diagnostics,
                Text = _showText.IsChecked == true ? Protocol.DiagnosticsSnippet : null,
            });

            if (reply is null)
            {
                _body.Text = "no daemon is running.";
                return;
            }

            if (reply.Diagnostics is not { } snapshot)
            {
                // An older daemon answers "unsupported verb" — worth saying, since
                // the rest of this tab still works against it.
                _body.Text = reply.Error is { Length: > 0 } error
                    ? $"this daemon cannot report diagnostics ({error}). It is probably an older build than this window."
                    : "the daemon answered without diagnostics.";
                return;
            }

            _body.Text = DiagnosticsFormat.Text(snapshot, DateTime.UtcNow).TrimEnd('\n');
        }
        catch (Exception ex)
        {
            _body.Text = $"could not read diagnostics: {ex.Message}";
        }
        finally
        {
            _inFlight = false;
        }
    }
}

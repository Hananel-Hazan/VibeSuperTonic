using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VibeSuperTonic.Ui;

/// <summary>
/// Across the top of the window, on every tab, while the engine is not running.
///
/// <para><b>Reported 2026-09-27.</b> Stopped from the tray or with
/// <c>vst-ctl shutdown</c>, the daemon goes and the window stays. All that said
/// so was the header's grey "not connected" and a line on the Reader tab, and
/// on any other tab the window looked as if it worked. This says it where it
/// cannot be missed, and offers the one thing to do about it.</para>
/// </summary>
internal sealed class EngineOfflineBar : Border
{
    private readonly TextBlock _message = new()
    {
        Text = "The engine is not running. The next hotkey press starts it again.",
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _start = new() { Content = "Start it now" };

    internal EngineOfflineBar(Func<string?> start)
    {
        // The install banner's amber, for the same reason: it reads in both themes.
        Background = new SolidColorBrush(Color.FromArgb(38, 220, 150, 0));
        BorderBrush = new SolidColorBrush(Color.FromArgb(110, 220, 150, 0));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(12, 9);
        IsVisible = false;
        SetValue(DockPanel.DockProperty, Dock.Top);

        _start.Click += (_, _) =>
        {
            // The window reconnects by itself once the socket answers, and then
            // hides this. Only a failure to start is worth saying here.
            if (start() is { } error) _message.Text = $"The engine is not running, and starting it failed: {error}";
            else { _message.Text = "Starting the engine…"; _start.IsEnabled = false; }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _start } };
        buttons.SetValue(DockPanel.DockProperty, Dock.Right);
        Child = new DockPanel { Children = { buttons, _message } };
    }

    internal void Show(bool offline)
    {
        IsVisible = offline;
        if (!offline) return;
        _message.Text = "The engine is not running. The next hotkey press starts it again.";
        _start.IsEnabled = true;
    }
}

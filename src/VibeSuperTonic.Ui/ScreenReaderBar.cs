using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// After a snap update the user chose, until the screen reader speaks through
/// VibeSuperTonic again: say that it is on its fallback voice, and how to bring
/// ours back without logging out. See <see cref="SnapUpdateMarker"/>.
///
/// <para>The command is selectable, because the person reading this may be about
/// to paste it into a terminal, and retyping it is where a mistake gets in.</para>
/// </summary>
internal sealed class ScreenReaderBar : Border
{
    internal ScreenReaderBar()
    {
        Background = new SolidColorBrush(Color.FromArgb(38, 220, 150, 0));
        BorderBrush = new SolidColorBrush(Color.FromArgb(110, 220, 150, 0));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(12, 9);
        IsVisible = false;
        SetValue(DockPanel.DockProperty, Dock.Top);

        var dismiss = new Button { Content = "Dismiss", VerticalAlignment = VerticalAlignment.Top };
        dismiss.Click += (_, _) => IsVisible = false;
        dismiss.SetValue(DockPanel.DockProperty, Dock.Right);

        Child = new DockPanel
        {
            Children =
            {
                dismiss,
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = SnapUpdateMarker.SteppedAsideNotice, TextWrapping = TextWrapping.Wrap },
                        new SelectableTextBlock { Text = SnapUpdateMarker.RestoreCommand, FontFamily = new FontFamily("monospace") },
                    },
                },
            },
        };
    }

    internal void Show(bool steppedAside) => IsVisible = steppedAside;
}

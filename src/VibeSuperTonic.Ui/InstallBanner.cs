using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Install;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The strip across the top of the window that says this install is not where it
/// was, and what that invalidated.
///
/// <para><b>Above the tabs rather than inside one</b>, because a move invalidates
/// things owned by three of them at once — the hotkeys are nobody's tab, the
/// benchmark is Tune's, the calibrations are Voices'. A warning filed under one
/// of them would be a warning the user has to already suspect in order to
/// find.</para>
///
/// <para><b>It offers no "repair" button, and that is the honest shape of the
/// problem.</b> What a move breaks lives outside this folder: the desktop entry
/// and the desktop's own hotkey configuration. Writing those from in here does
/// not work — a running <c>kglobalaccel</c> does not re-read the file — and doing
/// it the other way, over D-Bus, has already crashed a session on this project.
/// So the banner says which script to run. The one thing it can offer is the
/// re-measure, because that is ours.</para>
///
/// <para><b>Hidden is the normal state.</b> It collapses to nothing when there is
/// nothing to say, which is every launch but a handful.</para>
/// </summary>
internal sealed class InstallBanner : Border
{
    private readonly StackPanel _lines = new() { Spacing = 3 };
    private readonly Button _remeasure;
    private readonly Button _dismiss = new() { Content = "Dismiss" };

    /// <summary>Raised when the user asks for the sweep. The window owns running it.</summary>
    internal event Action? RemeasureRequested;

    internal InstallBanner()
    {
        _remeasure = new Button { Content = "Re-measure this machine" };
        _remeasure.Click += (_, _) => RemeasureRequested?.Invoke();
        _dismiss.Click += (_, _) => IsVisible = false;

        // A warning colour that survives both themes rather than a hard-coded
        // one: the window follows the desktop's, and an amber that reads on a
        // light background can be invisible on a dark one.
        Background = new SolidColorBrush(Color.FromArgb(38, 220, 150, 0));
        BorderBrush = new SolidColorBrush(Color.FromArgb(110, 220, 150, 0));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(12, 9);
        IsVisible = false;

        // Set here rather than at the use site, so the banner cannot be added to
        // the window's DockPanel and silently fill it instead of topping it.
        SetValue(DockPanel.DockProperty, Dock.Top);

        Child = new DockPanel
        {
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Right,
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Top,
                    Children = { _remeasure, _dismiss },
                },
                _lines,
            },
        };
    }

    /// <summary>
    /// Show what the daemon found, or hide entirely.
    ///
    /// <para>Takes the whole <see cref="InstallCheck"/> rather than a formatted
    /// string so that the daemon stays the one place that decides what changed —
    /// a UI that re-derived any of this would be a second opinion, and the two
    /// would disagree the day one of them was edited.</para>
    /// </summary>
    internal void Show(InstallCheck? check)
    {
        if (check is null || !check.NeedsAttention)
        {
            IsVisible = false;
            return;
        }

        _lines.Children.Clear();

        _lines.Children.Add(new TextBlock
        {
            Text = check.Headline(),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        // The rest of the changes, when a move produced more than one. The first
        // is already the headline and must not be repeated.
        foreach (var change in check.Changes.Skip(1))
            _lines.Children.Add(Detail(change.What));

        // MEASURED, SO IT IS STATED AS A FACT. Everything else here is inferred
        // from a fingerprint; this is a launcher whose program is not on disk.
        foreach (string program in check.BrokenLaunchers)
            _lines.Children.Add(Detail($"a shortcut runs {program}, which is not there"));

        foreach (string reason in check.BenchmarkStale)
            _lines.Children.Add(Detail($"the benchmark was {reason}"));

        foreach (string advice in check.Advice())
            _lines.Children.Add(Detail(advice));

        // Nothing of ours to re-measure: a broken shortcut and a moved store are
        // both fixed elsewhere, and a button that ran a sweep nobody needed would
        // be a minute of the machine's time spent to no end.
        _remeasure.IsVisible =
            check.Impact.HasFlag(InstallImpact.Benchmark)
            || check.Impact.HasFlag(InstallImpact.Calibration);

        IsVisible = true;
    }

    private static TextBlock Detail(string text) => new()
    {
        Text = "• " + text,
        Opacity = 0.85,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 12, 0),
    };
}

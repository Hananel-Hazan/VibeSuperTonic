using VibeSuperTonic.Daemon.Tray;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The gate that stops one tray icon opening two windows.
///
/// <para>Written 2026-08-28 after a real install did exactly that: two UI
/// processes 100 ms apart from one activation, because "is a window open" is
/// answered by whether a UI has subscribed and neither had yet.</para>
/// </summary>
public sealed class WindowLaunchGateTests
{
    private static readonly DateTime T0 = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_activation_opens_a_window()
    {
        Assert.Null(new WindowLaunchGate().Refuse(uiAttached: false, T0));
    }

    /// <summary>The bug, in one assertion.</summary>
    [Fact]
    public void A_second_activation_while_the_first_is_still_starting_does_not()
    {
        var gate = new WindowLaunchGate();

        Assert.Null(gate.Refuse(uiAttached: false, T0));
        Assert.Equal("a window is already opening",
            gate.Refuse(uiAttached: false, T0.AddMilliseconds(100)));
    }

    [Fact]
    public void An_activation_with_a_window_already_open_says_so()
    {
        Assert.Equal("a window is already open",
            new WindowLaunchGate().Refuse(uiAttached: true, T0));
    }

    /// <summary>
    /// The window opened, the user closed it, and the icon must work again —
    /// without waiting out a grace period measured from a launch that finished.
    /// </summary>
    [Fact]
    public void Closing_the_window_lets_the_next_click_open_one()
    {
        var gate = new WindowLaunchGate();

        Assert.Null(gate.Refuse(uiAttached: false, T0));
        Assert.Equal("a window is already open", gate.Refuse(uiAttached: true, T0.AddSeconds(2)));
        Assert.Null(gate.Refuse(uiAttached: false, T0.AddSeconds(3)));
    }

    /// <summary>
    /// A UI that never starts must not leave a tray icon that ignores clicks
    /// forever. After the grace period the gate opens again on its own.
    /// </summary>
    [Fact]
    public void A_launch_that_never_arrives_is_retried_after_the_grace_period()
    {
        var gate = new WindowLaunchGate(TimeSpan.FromSeconds(5));

        Assert.Null(gate.Refuse(uiAttached: false, T0));
        Assert.NotNull(gate.Refuse(uiAttached: false, T0.AddSeconds(4)));
        Assert.Null(gate.Refuse(uiAttached: false, T0.AddSeconds(6)));
    }

    /// <summary>
    /// And a launch that failed outright is retried on the very next click: the
    /// binary was missing or the process could not start, so there is nothing
    /// in flight to wait for.
    /// </summary>
    [Fact]
    public void A_failed_launch_is_retried_immediately()
    {
        var gate = new WindowLaunchGate();

        Assert.Null(gate.Refuse(uiAttached: false, T0));
        gate.Failed();
        Assert.Null(gate.Refuse(uiAttached: false, T0.AddMilliseconds(50)));
    }
}

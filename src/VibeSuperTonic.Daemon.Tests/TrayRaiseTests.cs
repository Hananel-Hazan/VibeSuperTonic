using VibeSuperTonic.Daemon.Tray;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What a tray click does when a window is already open.
///
/// <para>Doing nothing was the old answer, and it reads as a broken icon: the
/// window IS open, buried under three others, and the user clicks the tray
/// precisely because they cannot see it. The gate still refuses to LAUNCH a
/// second one — that was this morning's two-windows bug — so the two answers
/// have to be told apart, and "refused" is not the same as "ignore".</para>
/// </summary>
public sealed class TrayRaiseTests
{
    private static readonly DateTime T0 = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_open_window_is_refused_a_launch_and_named_as_open()
    {
        // The distinction the tray acts on: this refusal means "raise it", the
        // other means "one is already on its way".
        Assert.Equal("a window is already open",
            new WindowLaunchGate().Refuse(uiAttached: true, T0));
    }

    [Fact]
    public void A_launch_in_flight_is_refused_differently_because_there_is_nothing_to_raise()
    {
        var gate = new WindowLaunchGate();
        Assert.Null(gate.Refuse(uiAttached: false, T0));

        // No window exists yet, so raising is not the answer — waiting is.
        Assert.Equal("a window is already opening",
            gate.Refuse(uiAttached: false, T0.AddMilliseconds(100)));
    }
}

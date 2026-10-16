using VibeSuperTonic.Core.Export;
using VibeSuperTonic.Core.Session;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Export while reading aloud is refused, and the refusal stays on screen until
/// the reading ends, then says so (<see cref="ExportBusyGate"/>).
/// </summary>
public sealed class ExportBusyGateTests
{
    [Theory]
    [InlineData(SpeechState.Preparing)]
    [InlineData(SpeechState.Speaking)]
    [InlineData(SpeechState.Stopping)]
    public void Refuses_while_anything_is_reading(SpeechState state)
    {
        var gate = new ExportBusyGate();
        Assert.Equal(ExportBusyGate.BusyMessage, gate.Check(state));
        Assert.True(gate.Waiting);
    }

    [Fact]
    public void Idle_lets_the_export_through_and_says_nothing()
    {
        var gate = new ExportBusyGate();
        Assert.Null(gate.Check(SpeechState.Idle));
        Assert.False(gate.Waiting);
        Assert.Null(gate.OnState(SpeechState.Idle));
    }

    [Fact]
    public void The_message_holds_until_idle_and_is_then_replaced_once()
    {
        var gate = new ExportBusyGate();
        gate.Check(SpeechState.Speaking);

        // Moving between reading states is not the end of the reading.
        Assert.Null(gate.OnState(SpeechState.Preparing));
        Assert.Null(gate.OnState(SpeechState.Speaking));
        Assert.Null(gate.OnState(SpeechState.Stopping));
        Assert.True(gate.Waiting);

        Assert.Equal(ExportBusyGate.ReadyMessage, gate.OnState(SpeechState.Idle));
        Assert.False(gate.Waiting);

        // A later reading and its end, with no refusal between, say nothing.
        Assert.Null(gate.OnState(SpeechState.Speaking));
        Assert.Null(gate.OnState(SpeechState.Idle));
    }

    [Fact]
    public void The_daemons_busy_refusal_is_taken_over_and_other_errors_are_not()
    {
        var gate = new ExportBusyGate();
        Assert.Null(gate.FromRefusal("no voice is installed"));
        Assert.Null(gate.FromRefusal(null));
        Assert.False(gate.Waiting);

        Assert.Equal(ExportBusyGate.BusyMessage,
            gate.FromRefusal("busy (Speaking) — this daemon is speaking, and a render would queue behind it"));
        Assert.True(gate.Waiting);
        Assert.Equal(ExportBusyGate.ReadyMessage, gate.OnState(SpeechState.Idle));
    }
}

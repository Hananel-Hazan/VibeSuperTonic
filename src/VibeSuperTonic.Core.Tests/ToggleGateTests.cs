using VibeSuperTonic.Core.Session;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The hotkey contract, tested as a table rather than as prose.
///
/// This is the whole interaction model of the product — one key, press to speak,
/// press again to stop — and it is the thing a user judges within their first
/// three presses. Everything here is deterministic because the gate takes the
/// current millisecond as a parameter instead of reading a clock.
/// </summary>
public class ToggleGateTests
{
    [Fact]
    public void Press_from_idle_speaks()
    {
        var gate = new ToggleGate();
        Assert.Equal(ToggleAction.Speak, gate.Press(1000));
        Assert.Equal(SpeechState.Preparing, gate.State);
    }

    [Fact]
    public void Press_while_preparing_cancels()
    {
        // The non-obvious row. A cold load is 2–5 s, which is exactly when a
        // user who has heard nothing presses again; if this returned Ignored the
        // key would feel dead in the one window it most needs to answer.
        var gate = new ToggleGate();
        gate.Press(1000);
        Assert.Equal(SpeechState.Preparing, gate.State);

        Assert.Equal(ToggleAction.Stop, gate.Press(2000));
        Assert.Equal(SpeechState.Stopping, gate.State);
    }

    [Fact]
    public void Press_while_speaking_stops()
    {
        var gate = new ToggleGate();
        gate.Press(1000);
        gate.NoteState(SpeechState.Speaking);

        Assert.Equal(ToggleAction.Stop, gate.Press(2000));
    }

    [Fact]
    public void Press_while_stopping_is_dropped_not_queued()
    {
        var gate = new ToggleGate();
        gate.Press(1000);
        gate.Press(2000);                       // -> Stopping
        Assert.Equal(SpeechState.Stopping, gate.State);

        // Queueing it would start a new utterance the instant the old one let go
        // of the device, which reads as the key having a mind of its own.
        Assert.Equal(ToggleAction.Ignored, gate.Press(3000));
        Assert.Equal(SpeechState.Stopping, gate.State);
    }

    [Fact]
    public void Speech_that_ends_on_its_own_returns_the_gate_to_speaking_from_idle()
    {
        var gate = new ToggleGate();
        gate.Press(1000);
        gate.NoteState(SpeechState.Speaking);
        gate.NoteState(SpeechState.Idle);       // finished by itself

        Assert.Equal(ToggleAction.Speak, gate.Press(2000));
    }

    // ----------------------------------------------------------------- debounce

    [Fact]
    public void A_double_tap_resolves_as_one_action()
    {
        // Without this, an accidental double-tap is speak-then-immediately-stop,
        // which is indistinguishable from "the key is broken" — and the user's
        // only diagnostic is to press it again.
        var gate = new ToggleGate();

        Assert.Equal(ToggleAction.Speak, gate.Press(1000));
        Assert.Equal(ToggleAction.Ignored, gate.Press(1040));
        Assert.Equal(ToggleAction.Ignored, gate.Press(1100));
        Assert.Equal(SpeechState.Preparing, gate.State);
    }

    [Fact]
    public void The_key_is_responsive_again_one_debounce_later()
    {
        var gate = new ToggleGate();
        gate.Press(1000);

        Assert.Equal(ToggleAction.Ignored, gate.Press(1149));
        Assert.Equal(ToggleAction.Stop, gate.Press(1150));
    }

    [Fact]
    public void Dropped_presses_do_not_extend_the_dead_window()
    {
        // Debounce runs from the last ACCEPTED press. Measuring from the last
        // press of any kind would let a nervous user hold the key unresponsive
        // for as long as they kept tapping it.
        var gate = new ToggleGate();
        gate.Press(1000);               // accepted
        gate.Press(1050);               // dropped
        gate.Press(1100);               // dropped

        Assert.Equal(ToggleAction.Stop, gate.Press(1150));
    }

    [Fact]
    public void A_held_key_repeating_does_not_thrash_the_pipeline()
    {
        // X11 auto-repeat fires at ~30 Hz. Every repeat past the first must be
        // dropped, or holding the key would start and stop speech thirty times a
        // second.
        var gate = new ToggleGate();
        var actions = new List<ToggleAction>();
        for (long t = 1000; t < 1500; t += 33) actions.Add(gate.Press(t));

        Assert.Equal(ToggleAction.Speak, actions[0]);
        Assert.Equal(2, actions.Count(a => a != ToggleAction.Ignored));   // speak, then one stop
    }

    [Fact]
    public void Zero_debounce_is_allowed_for_scripted_callers()
    {
        // D-Bus and vst-ctl are not fingers. A caller that wants every request
        // honoured should be able to say so.
        var gate = new ToggleGate(debounceMs: 0);
        Assert.Equal(ToggleAction.Speak, gate.Press(1000));
        Assert.Equal(ToggleAction.Stop, gate.Press(1000));
    }

    [Fact]
    public void Reset_clears_both_state_and_the_debounce_window()
    {
        var gate = new ToggleGate();
        gate.Press(1000);
        gate.Reset();

        Assert.Equal(SpeechState.Idle, gate.State);
        Assert.Equal(ToggleAction.Speak, gate.Press(1001));
    }
}

namespace VibeSuperTonic.Core.Session;

/// <summary>What a press turned out to mean.</summary>
public enum ToggleAction
{
    /// <summary>Debounced, or arrived while already stopping.</summary>
    Ignored,

    Speak,
    Stop,
}

/// <summary>
/// The hotkey contract, on its own, with no speech pipeline attached.
///
/// One key does everything: press to speak, press again to stop. Which of those
/// a press means depends only on <see cref="State"/>, so the decision is worth
/// exactly one small class that can be tested by pressing it a thousand times in
/// a microsecond.
///
/// <para><b>It lives in the daemon, not the client.</b> The client is stateless
/// — it connects, writes <c>toggle</c>, and exits. That is what makes the
/// behaviour identical whether the request came from the hotkey, the tray menu
/// or D-Bus, and it is why a NativeAOT client that starts in a millisecond is
/// possible at all: it has nothing to remember.</para>
///
/// <para><b>Time is a parameter.</b> Debounce is the one part with a clock in
/// it, and a class that read the clock itself could only be tested by sleeping.
/// The caller passes the current millisecond.</para>
/// </summary>
public sealed class ToggleGate
{
    /// <summary>
    /// Presses this close to the last accepted one are dropped. Without it an
    /// accidental double-tap reads as speak-then-immediately-stop, which is
    /// indistinguishable from "the key is broken" — the failure this product can
    /// least afford, because the user's only diagnostic is pressing it again.
    /// </summary>
    public const int DefaultDebounceMs = 150;

    private readonly int _debounceMs;

    /// <summary>
    /// Null until the first accepted press. Deliberately not a sentinel: the
    /// obvious <c>long.MinValue</c> makes <c>nowMs - _lastAcceptedMs</c>
    /// overflow to a negative number, which reads as "well inside the debounce
    /// window" and silently drops the first press of every session. The symptom
    /// is a freshly started daemon whose key does nothing until pressed twice.
    /// </summary>
    private long? _lastAcceptedMs;

    public ToggleGate(int debounceMs = DefaultDebounceMs)
    {
        if (debounceMs < 0) throw new ArgumentOutOfRangeException(nameof(debounceMs));
        _debounceMs = debounceMs;
    }

    /// <summary>
    /// Where the pipeline is. Set optimistically by <see cref="Press"/> and
    /// corrected by <see cref="NoteState"/> as the session reports progress —
    /// the press must decide the next press's meaning immediately, without
    /// waiting for a round trip through the audio thread.
    /// </summary>
    public SpeechState State { get; private set; } = SpeechState.Idle;

    /// <summary>
    /// A press arrived at <paramref name="nowMs"/>.
    /// </summary>
    /// <remarks>
    /// Debounce is measured from the last <em>accepted</em> press, not the last
    /// press of any kind. A run of accidental taps therefore resolves as one
    /// action and then becomes responsive again 150 ms later, rather than each
    /// dropped tap extending the dead window — which would turn a nervous
    /// double-tap into a key that stays unresponsive for as long as it is
    /// pressed.
    /// </remarks>
    public ToggleAction Press(long nowMs)
    {
        if (_lastAcceptedMs is long last && nowMs - last < _debounceMs) return ToggleAction.Ignored;

        switch (State)
        {
            case SpeechState.Idle:
                _lastAcceptedMs = nowMs;
                State = SpeechState.Preparing;
                return ToggleAction.Speak;

            // Preparing counts as active. A cold load is 2–5 s, which is exactly
            // when a user who has heard nothing presses again.
            case SpeechState.Preparing:
            case SpeechState.Speaking:
                _lastAcceptedMs = nowMs;
                State = SpeechState.Stopping;
                return ToggleAction.Stop;

            // Stop is three operations across two threads. A press arriving
            // mid-teardown is dropped, never queued: queueing it would start a
            // new utterance the moment the old one released the device, which
            // reads as the key having a mind of its own.
            case SpeechState.Stopping:
            default:
                return ToggleAction.Ignored;
        }
    }

    /// <summary>
    /// A press of a key that <em>always</em> speaks — it never means stop.
    ///
    /// <para>The primary key does this: interrupt whatever is playing and read
    /// the current selection. It shares this gate's debounce window with
    /// <see cref="Press"/> rather than keeping its own, so alternating the two
    /// keys cannot produce a burst that neither one alone would allow.</para>
    ///
    /// <para>Unlike <see cref="Press"/> there is no state to consult: the answer
    /// is always "speak", and the only question is whether the press is real.</para>
    /// </summary>
    /// <returns>False if debounced.</returns>
    public bool PressToRead(long nowMs)
    {
        if (_lastAcceptedMs is long last && nowMs - last < _debounceMs) return false;
        _lastAcceptedMs = nowMs;
        State = SpeechState.Preparing;
        return true;
    }

    /// <summary>
    /// The pipeline reported a state. Authoritative — it overrides the optimistic
    /// value <see cref="Press"/> set, which matters when an utterance ends on its
    /// own, when a load fails, or when speech was started by something other than
    /// a press.
    /// </summary>
    public void NoteState(SpeechState state) => State = state;

    /// <summary>
    /// Forget the debounce window. For tests and for a daemon that has just
    /// restarted its pipeline; not part of normal operation.
    /// </summary>
    public void Reset()
    {
        State = SpeechState.Idle;
        _lastAcceptedMs = null;
    }
}

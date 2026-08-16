namespace VibeSuperTonic.Core.Session;

/// <summary>What a <see cref="SessionEvent"/> reports.</summary>
public enum SessionEventKind
{
    /// <summary>The state machine moved. <c>State</c> carries the new value.</summary>
    StateChanged,

    /// <summary>First audio of an utterance has been written to the sink.</summary>
    Started,

    /// <summary>A word is being heard now. <c>SourceOffset</c>/<c>SourceLength</c> locate it.</summary>
    WordBoundary,

    /// <summary>A chunk — roughly a sentence — is being heard now.</summary>
    SentenceBoundary,

    /// <summary>The utterance finished on its own.</summary>
    Finished,

    /// <summary>The utterance was stopped before it finished.</summary>
    Stopped,

    /// <summary>Something failed. <c>Message</c> says what.</summary>
    Error,
}

/// <summary>
/// The single channel through which anything learns what the speech pipeline is
/// doing.
///
/// <para>That "single" is load-bearing and is [R-1]. The tray and the window
/// live in the daemon process, which makes it trivially easy for them to read
/// pipeline state directly — and then splitting them out later, or adding an
/// external subscriber, or a speech-dispatcher front end, becomes a rewrite. The
/// order of work is the enforcement: this stream and a <c>vst-ctl subscribe</c>
/// that merely prints it are built <em>before</em> anything consumes them, so it
/// has to be a real interface rather than a convenience wrapper over fields
/// someone already has a reference to.</para>
///
/// <para>One flat record with nullable fields rather than a subtype per kind:
/// this goes over a socket as one JSON object per line, and a shape that
/// serializes without a discriminator convention is a shape a shell script can
/// read with <c>jq</c>. Unset fields are omitted on the wire.</para>
///
/// <para>Offsets are in <em>source</em> coordinates — the text the user
/// selected, before any rewriting. See <c>BoundaryEvent</c> for why that is the
/// only coordinate space worth publishing.</para>
/// </summary>
public sealed record SessionEvent
{
    public required SessionEventKind Kind { get; init; }

    /// <summary>Set on <see cref="SessionEventKind.StateChanged"/>.</summary>
    public SpeechState? State { get; init; }

    /// <summary>
    /// The utterance about to be spoken. Set on exactly one event per utterance:
    /// the <see cref="SpeechState.Preparing"/> transition.
    ///
    /// <para><b>Without this the stream is unreadable to anyone who stays
    /// connected.</b> Every boundary reports an offset into the text being
    /// spoken, and a subscriber that was already attached when a new utterance
    /// began would otherwise be told positions in a string it has never seen. The
    /// <c>subscribe</c> snapshot only answers the arriving-mid-read case; a window
    /// left open across utterances — which is the normal case for Phase 6's
    /// Reader tab — is the one it does not cover. The alternative is a
    /// <c>status</c> round trip on every Preparing, which races the next
    /// utterance.</para>
    ///
    /// <para>On Preparing and nowhere else, deliberately. A selection may be
    /// 100 KB (R-9) and repeating it on the Speaking and Idle transitions would
    /// triple that on the wire for nothing — Preparing is the one moment the text
    /// actually changes. It is also the earliest: it arrives with the ~28 ms
    /// acknowledgement rather than the ~750 ms first audio, so the Reader tab can
    /// paint the text during the gap the user would otherwise spend wondering
    /// whether the key registered.</para>
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Set on word and sentence boundaries, and on the
    /// <see cref="SpeechState.Preparing"/> transition — where it is where in
    /// <see cref="Text"/> the reading begins, which is 0 for an ordinary read and
    /// the seek position for a click-to-jump.
    /// </summary>
    public int? SourceOffset { get; init; }

    /// <summary>Set on word and sentence boundaries.</summary>
    public int? SourceLength { get; init; }

    /// <summary>
    /// Position in the utterance's audio, seconds. Present on boundaries and on
    /// <see cref="SessionEventKind.Finished"/>, where it is the total spoken.
    /// </summary>
    public double? AudioSeconds { get; init; }

    /// <summary>Set on <see cref="SessionEventKind.Error"/>.</summary>
    public string? Message { get; init; }

    public static SessionEvent Of(SessionEventKind kind) => new() { Kind = kind };

    public static SessionEvent Of(SpeechState state) =>
        new() { Kind = SessionEventKind.StateChanged, State = state };

    /// <summary>
    /// The one state change that carries what is about to be spoken. See
    /// <see cref="Text"/> for why it is this transition and only this one.
    /// </summary>
    public static SessionEvent Preparing(string text, int startOffset) => new()
    {
        Kind = SessionEventKind.StateChanged,
        State = SpeechState.Preparing,
        Text = text,
        SourceOffset = startOffset,
    };

    public static SessionEvent Error(string message) =>
        new() { Kind = SessionEventKind.Error, Message = message };
}

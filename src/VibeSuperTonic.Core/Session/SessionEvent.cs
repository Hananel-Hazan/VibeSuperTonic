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
/// <para>That "single" is load-bearing and is [R-1]. The order of work was the
/// first enforcement: this stream and a <c>vst-ctl subscribe</c> that merely
/// prints it were built <em>before</em> anything consumed them, so it had to be
/// a real interface rather than a convenience wrapper over fields someone
/// already has a reference to.</para>
///
/// <para><b>Corrected 2026-08-16.</b> This used to say "the tray and the window
/// live in the daemon process". Half of that is now wrong and half is still the
/// hazard:</para>
///
/// <para>The <em>window</em> is its own process, <c>vibesupertonic-ui</c>, so
/// for it R-1 is enforced structurally — it cannot reach a field it has no
/// reference to. The <em>tray</em> is in the daemon, because it has to exist
/// while the window is closed, and there the original hazard is undiminished:
/// tray code sits next to <c>SpeechSession</c> and reading its state directly
/// would cost nothing today and a rewrite the first time anything else wants
/// the same information. <b>The tray subscribes here like any external client
/// and touches no session field.</b></para>
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

    /// <summary>
    /// Something the user should know about this utterance, which nonetheless
    /// started normally. Rides on the <see cref="SpeechState.Preparing"/>
    /// transition beside <see cref="Text"/>, and is null the rest of the time.
    ///
    /// <para><b>Why it is here and not only on <c>Response.Notice</c>.</b> A
    /// response goes to the client that sent the request, and in the hotkey path
    /// that client is <c>vst-ctl</c> — which prints to a stderr nobody is
    /// looking at and exits. So the two things a notice says about
    /// <em>speaking</em> — an R-9 truncation, and a selection that never changed
    /// — reached nobody who could show them to the user. The tray is not a
    /// caller, it is a subscriber, and a subscriber had no way to see them at
    /// all.</para>
    ///
    /// <para>Preparing for the same reason <see cref="Text"/> is: both notices
    /// are properties of the utterance being started, it is the one transition
    /// where they change, and it arrives with the ~28 ms acknowledgement rather
    /// than the ~750 ms first audio — so a tooltip can carry it before the first
    /// word is heard.</para>
    ///
    /// <para><c>Response.Notice</c> stays as well rather than being replaced. A
    /// scripted caller wants the answer to its own request; the tray wants
    /// whatever is true of the utterance now. They are genuinely different
    /// readers, and the config notices — a malformed <c>settings.json</c>, an
    /// unwritable data directory — ride on the <c>config</c> response only,
    /// because they are not about an utterance at all.</para>
    /// </summary>
    public string? Notice { get; init; }

    public static SessionEvent Of(SessionEventKind kind) => new() { Kind = kind };

    public static SessionEvent Of(SpeechState state) =>
        new() { Kind = SessionEventKind.StateChanged, State = state };

    /// <summary>
    /// The one state change that carries what is about to be spoken. See
    /// <see cref="Text"/> for why it is this transition and only this one, and
    /// <see cref="Notice"/> for why the notice rides here too.
    /// </summary>
    public static SessionEvent Preparing(string text, int startOffset, string? notice = null) => new()
    {
        Kind = SessionEventKind.StateChanged,
        State = SpeechState.Preparing,
        Text = text,
        SourceOffset = startOffset,
        Notice = notice,
    };

    public static SessionEvent Error(string message) =>
        new() { Kind = SessionEventKind.Error, Message = message };
}

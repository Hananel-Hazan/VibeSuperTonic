namespace VibeSuperTonic.Core.Audio;

/// <summary>What a <see cref="BoundaryEvent"/> marks the start of.</summary>
public enum BoundaryKind
{
    /// <summary>One synthesis chunk — roughly a sentence. Emitted at the chunk's first frame.</summary>
    Sentence,

    /// <summary>One word inside a chunk.</summary>
    Word,
}

/// <summary>
/// "At this point in the audio stream, the user is hearing these characters of
/// the text they selected."
///
/// The two coordinate systems in that sentence are the whole problem, and every
/// offset defect in this repo has been a confusion between them:
///
/// <list type="bullet">
/// <item><see cref="Frame"/> is in <em>stream</em> space — frames since the
/// start of this utterance, across all chunks.</item>
/// <item><see cref="SourceOffset"/> and <see cref="SourceLength"/> are in
/// <em>source</em> space — the text the user actually selected, before
/// sanitizing, pronunciation rules or the chunker's whitespace normalisation
/// rewrote any of it.</item>
/// </list>
///
/// Source space is the only one a highlight can use, and reaching it is not a
/// subtraction: it goes through <c>TextOffsetMap</c>. Both R-2 and R-14 were
/// this crossing done by arithmetic instead. By the time a value is in this
/// record the crossing is already done — see <see cref="BoundaryPlanner"/>.
///
/// <see cref="SourceLength"/> is source characters, not spoken ones. A rule
/// turning "kg" into "kilograms" produces a word event nine characters long in
/// the chunk and <b>two</b> here, because two is what the user typed and two is
/// what a client can select.
/// </summary>
/// <param name="Frame">Frames from the start of the utterance's audio stream.</param>
/// <param name="SourceOffset">Character offset in the user's original text.</param>
/// <param name="SourceLength">Characters of the user's original text this covers.</param>
/// <param name="Kind">Word or sentence.</param>
public readonly record struct BoundaryEvent(
    long Frame,
    int SourceOffset,
    int SourceLength,
    BoundaryKind Kind);

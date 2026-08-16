using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// Turns a rendered chunk into the boundary events its audio should fire.
///
/// This is a port of the Windows engine's <c>EmitWordBoundaries</c> /
/// <c>EmitSentenceBoundary</c> into frames instead of stream bytes, and it is a
/// deliberate port rather than a reimplementation: the placement rule is
/// <em>proportional</em> — a word starting 40% of the way through a chunk's
/// characters is placed 40% of the way through that chunk's audio — which is
/// crude but is what five shipped releases of the Windows product do, and what
/// users have judged acceptable. Inventing a better rule for Linux would make
/// the two platforms disagree about the same text while giving no way to tell
/// which one was wrong. <c>BoundaryPlannerTests</c> pins this to a copy of the
/// engine's algorithm so the two cannot drift apart silently.
///
/// (The proportional rule's real weakness is that it assumes even speaking rate
/// within a chunk, so a chunk with a long pause in it drifts. Chunks are
/// sentence-sized, which bounds the error. Replacing it needs per-token
/// durations out of the model, which the vendored SDK does not surface — worth
/// revisiting only if a future SDK does.)
///
/// Nothing here knows about audio devices, so it runs and is tested on any
/// platform, including the Windows CI job that has no speakers.
/// </summary>
public static class BoundaryPlanner
{
    /// <summary>
    /// What counts as part of a word. Matches <c>SapiEngine.IsWordChar</c>
    /// exactly, apostrophes and hyphens included, so "don't" and "well-known"
    /// are one highlight on both platforms rather than two or three.
    /// </summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '-';

    /// <summary>
    /// Move <paramref name="offset"/> back to the first character of the word it
    /// falls in, so a click inside "kilograms" starts at the k rather than part
    /// way through it.
    /// </summary>
    /// <remarks>
    /// <para>Here, beside <see cref="IsWordChar"/>, because it must use the same
    /// definition of a word as the highlighter. If a click resolved words
    /// differently from the boundary events that drew them, the user would click
    /// the word they can see highlighted and hear a different one start —
    /// apostrophes and hyphens being exactly where the two would part company
    /// ("well-known" is one highlight, so it must also be one click target).</para>
    /// <para>In the daemon rather than in the UI for the reason the state machine
    /// is: behaviour that decides what the product does belongs where every
    /// client gets the same of it. An offset landing on a separator is left
    /// alone — it is already between words, and the renderer starts at the next
    /// one.</para>
    /// </remarks>
    public static int SnapToWordStart(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset <= 0) return 0;
        if (offset >= text.Length) return text.Length;

        if (!IsWordChar(text[offset])) return offset;

        int start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        return start;
    }

    /// <summary>
    /// Append the events for one rendered chunk, in non-decreasing frame order.
    /// </summary>
    /// <param name="into">Collection to append to — typically fed straight to <see cref="BoundaryScheduler"/>.</param>
    /// <param name="chunkText">The chunk as it was handed to the synthesizer.</param>
    /// <param name="map">
    /// Maps an index in <paramref name="chunkText"/> back to the source text.
    /// Must be the composed map for this chunk (sanitizer ∘ pronunciation ∘
    /// chunker), which is what <c>SentenceChunker.ChunkWithOffsets</c> reports.
    /// </param>
    /// <param name="sourceBase">
    /// Where this chunk's source text begins within the whole selection. Added
    /// <em>after</em> the map, never before — adding it first is R-2.
    /// </param>
    /// <param name="chunkFrames">Frames of audio this chunk rendered to.</param>
    /// <param name="streamStartFrame">Frames already written for earlier chunks.</param>
    public static void PlanChunk(
        ICollection<BoundaryEvent> into,
        string chunkText,
        TextOffsetMap map,
        int sourceBase,
        int chunkFrames,
        long streamStartFrame)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(chunkText);
        ArgumentNullException.ThrowIfNull(map);
        if (sourceBase < 0) throw new ArgumentOutOfRangeException(nameof(sourceBase));
        if (chunkFrames < 0) throw new ArgumentOutOfRangeException(nameof(chunkFrames));
        if (streamStartFrame < 0) throw new ArgumentOutOfRangeException(nameof(streamStartFrame));

        int n = chunkText.Length;
        if (n == 0 || chunkFrames == 0) return;

        into.Add(new BoundaryEvent(
            streamStartFrame,
            sourceBase + map.ToSource(0),
            SourceLengthAt(map, 0, n),
            BoundaryKind.Sentence));

        int pos = 0;
        while (pos < n)
        {
            while (pos < n && !IsWordChar(chunkText[pos])) pos++;
            if (pos >= n) break;

            int start = pos;
            while (pos < n && IsWordChar(chunkText[pos])) pos++;
            int wordLen = pos - start;
            if (wordLen == 0) continue;

            // Integer arithmetic in this order on purpose: (frames × start) / n
            // keeps the rounding identical to the engine's byte-space version,
            // which computes (chunkAudioBytes × start) / n. Doing the division
            // first would round each word down independently and the two
            // platforms would place long chunks a frame or two apart.
            long frame = streamStartFrame + (long)chunkFrames * start / n;

            into.Add(new BoundaryEvent(
                frame,
                sourceBase + map.ToSource(start),
                SourceLengthAt(map, start, wordLen),
                BoundaryKind.Word));
        }
    }

    /// <summary>
    /// How many source characters a span of a chunk covers — the length a client
    /// should select.
    ///
    /// Mirrors <c>SapiEngine.SpeakChunkExec.SourceLengthAt</c>, including the
    /// fallback: a span sitting entirely inside one substitution covers no
    /// distinct source range of its own ("kg" → "kilo grams" is two spoken words
    /// over one source token), and reporting the honest zero gives clients a
    /// zero-width selection they render as nothing at all. One character at the
    /// right place is wrong by less.
    /// </summary>
    public static int SourceLengthAt(TextOffsetMap map, int indexInChunk, int lengthInChunk)
    {
        ArgumentNullException.ThrowIfNull(map);

        int length = map.SourceSpanLength(indexInChunk, lengthInChunk);
        if (length > 0 || lengthInChunk <= 0) return length;

        int start = map.ToSource(indexInChunk);
        return start < map.SourceLength ? 1 : 0;
    }
}

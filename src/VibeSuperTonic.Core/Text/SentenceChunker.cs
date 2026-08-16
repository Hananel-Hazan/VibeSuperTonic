using System.Text.RegularExpressions;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// One synthesis chunk and the span of input text it came from.
/// </summary>
/// <param name="Text">
/// What the model is given. Whitespace-normalised, so it is <em>not</em>
/// necessarily <c>source.Substring(Start, Length)</c>.
/// </param>
/// <param name="Start">Index in the text passed to the chunker.</param>
/// <param name="Length">
/// Characters of input this chunk covers — first through last non-whitespace
/// character inclusive, so adjacent chunks never overlap and the whitespace
/// between them belongs to neither.
/// </param>
/// <param name="Map">
/// Per-character map from an index in <paramref name="Text"/> back to an index
/// in the text passed to the chunker.
///
/// <paramref name="Start"/> is not enough on its own, which cost a release to
/// learn. The chunker collapses whitespace <em>inside</em> a chunk as well as
/// between chunks: "a.  b" becomes "a. b", so every character after the double
/// space sits one position earlier in the chunk than in the input. A caller that
/// adds an index-within-chunk to <paramref name="Start"/> is therefore correct
/// only while every separator happened to be exactly one character — which is
/// why single-space, newline and tab inputs looked fine and double spaces and
/// paragraph breaks drifted by one character per separator.
/// </param>
public readonly record struct TextChunk(string Text, int Start, int Length, TextOffsetMap Map);

/// <summary>
/// Splits text into synthesis-sized chunks.
///
/// Chunk size is a latency/quality trade, not an implementation detail: chunks
/// that are too large delay first audio, chunks that are too small waste model
/// warm-up and make prosody choppy at the seams, and chunks of *uneven* size
/// stall the render-ahead pipeline because one slow chunk starves playback.
/// Pass 3 exists entirely to keep sizes uniform.
///
/// Lifted out of <c>SupertonicAdapter</c>, which is otherwise DirectML
/// device-loss machinery with no meaning off Windows. The Linux daemon needs
/// chunking and must not drag that with it (R-12), so the chunker lives here and
/// the platform backends keep only what actually touches ONNX Runtime.
///
/// Pass 1 is a port of <c>Supertonic.Helper.ChunkText</c> from the vendored SDK
/// rather than a call into it: that file carries the ONNX Runtime types, and
/// Core deliberately has no ONNX reference at all. The port is pinned to the
/// original by a reference-oracle test — this had to reproduce the existing
/// output exactly, including its quirks, because chunk boundaries are audible.
/// </summary>
public static class SentenceChunker
{
    public const int MaxChunkChars = 200;
    public const int MinChunkChars = 100;

    /// <summary>
    /// Bounds for caller-supplied sizes. These match the launcher's Advanced-tab
    /// sliders, but the sliders are not the only way in: the settings-file
    /// import path parses the values with no range check, and
    /// <c>settings.json</c> is a plain file a user can edit. A max of 5 would
    /// turn one paragraph into hundreds of model invocations, which reads as a
    /// hang rather than as a bad setting, so the floor is enforced here — in the
    /// code whose behaviour actually degrades — rather than in each host's UI.
    /// </summary>
    public const int MinAllowedMaxChars = 80;
    public const int MaxAllowedMaxChars = 400;
    public const int MinAllowedMinChars = 0;
    public const int MaxAllowedMinChars = 200;

    /// <summary>Merging is allowed to overshoot the maximum by this much.</summary>
    private const int MergeHeadroom = 80;

    /// <summary>Pass 2 only fires above this, so near-max chunks are left alone.</summary>
    private const int SplitTolerance = 30;

    // Abbreviations that end in a period but do not end a sentence. Anchored as
    // lookbehinds so the split happens after real terminators only.
    private static readonly Regex ParagraphBreak = new(@"\n\s*\n+", RegexOptions.Compiled);
    private static readonly Regex SentenceBreak = new(
        @"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Three passes:
    /// 1. Split at paragraph then sentence boundaries, packing sentences up to <see cref="MaxChunkChars"/>.
    /// 2. Break any chunk still oversize at internal punctuation or a word boundary.
    /// 3. Merge undersize chunks into neighbours so synthesis cost per chunk is even.
    /// </summary>
    /// <param name="maxChars">
    /// Upper target for a chunk, clamped to
    /// [<see cref="MinAllowedMaxChars"/>, <see cref="MaxAllowedMaxChars"/>].
    /// </param>
    /// <param name="minChars">
    /// Below this a chunk is merged into a neighbour, clamped to
    /// [<see cref="MinAllowedMinChars"/>, <see cref="MaxAllowedMinChars"/>] and
    /// then to no more than <paramref name="maxChars"/> — the two sliders have
    /// overlapping ranges, so a user can ask for a minimum above the maximum.
    /// Rather than reject that, take it as "merge whenever you can", which is the
    /// nearest sensible reading and keeps synthesis working.
    /// </param>
    /// <param name="leadChars">
    /// Cap on the <em>first</em> chunk only, or 0 to leave it alone.
    ///
    /// <para>This exists because chunk size is also the product's
    /// press-to-speech latency, which is not obvious from anywhere else. The
    /// merge pass exists to keep chunks evenly sized, and it does that by gluing
    /// short sentences to their neighbours — so a passage beginning "The sea is
    /// everything." produces a first chunk of three sentences, and nothing is
    /// heard until all three have rendered. Measured on the Mint box: 1.45 s of
    /// silence after the key press, against a 150 ms budget.</para>
    ///
    /// <para>Splitting the first chunk back at a <em>sentence</em> boundary costs
    /// nothing audible — it is a break the text already had — and cuts the wait
    /// to the render time of one short sentence. Later chunks keep their full
    /// size, so prosody and per-chunk overhead are unaffected. Off by default:
    /// the Windows engine hands SAPI a whole stream and has no press to answer.
    /// </para>
    /// </param>
    public static List<string> Chunk(string text, int maxChars = MaxChunkChars,
        int minChars = MinChunkChars, int leadChars = 0)
    {
        maxChars = Math.Clamp(maxChars, MinAllowedMaxChars, MaxAllowedMaxChars);
        minChars = Math.Clamp(minChars, MinAllowedMinChars, MaxAllowedMinChars);
        if (minChars > maxChars) minChars = maxChars;
        int mergeCeiling = maxChars + MergeHeadroom;

        var sentenceChunks = SplitSentences(text, maxChars);

        var split = new List<string>();
        foreach (var chunk in sentenceChunks)
        {
            if (chunk.Length <= maxChars + SplitTolerance) split.Add(chunk);
            else split.AddRange(SplitLongChunk(chunk, maxChars));
        }

        var merged = new List<string>();
        foreach (var chunk in split)
        {
            if (merged.Count > 0)
            {
                int combinedLength = merged[^1].Length + 1 + chunk.Length;
                bool eitherIsTiny = merged[^1].Length < minChars || chunk.Length < minChars;
                if (eitherIsTiny && combinedLength <= mergeCeiling)
                {
                    merged[^1] = merged[^1] + " " + chunk;
                    continue;
                }
            }
            merged.Add(chunk);
        }

        // Undo the merge for the first chunk only, at a boundary the text
        // already had. SplitSentences packs rather than cuts, so if the opening
        // sentence is on its own longer than leadChars it comes back whole —
        // a long wait is better than a chunk that ends mid-clause, which is
        // audible where the wait is merely slow.
        if (leadChars > 0 && merged.Count > 0 && merged[0].Length > leadChars)
        {
            var lead = SplitSentences(merged[0], leadChars);
            if (lead.Count > 1)
            {
                merged[0] = string.Join(" ", lead.Skip(1));
                merged.Insert(0, lead[0]);
            }
        }

        return merged;
    }

    /// <summary>
    /// Same chunks as <see cref="Chunk"/>, plus each one's span in the input text.
    ///
    /// Callers need this because they report positions back to something that
    /// still holds the original string — SAPI word-boundary events, the Reader
    /// tab's highlight. Recovering the span with <c>IndexOf(chunkText)</c> does
    /// not work: the chunker normalises whitespace (it splits on <c>\s+</c>,
    /// rejoins with a single space, and trims), so a chunk built from
    /// "First one.\nSecond one." is not a substring of it. Measured on the real
    /// caller, every chunk of newline-, tab-, or double-space-separated text
    /// missed, and the fallback then accumulated in rewritten coordinates and
    /// drifted against the source. Same family of defect as R-2, one layer up.
    /// </summary>
    /// <param name="leadChars">See <see cref="Chunk"/>. 0 leaves the first chunk alone.</param>
    public static List<TextChunk> ChunkWithOffsets(
        string text, int maxChars = MaxChunkChars, int minChars = MinChunkChars, int leadChars = 0)
    {
        var chunks = Chunk(text, maxChars, minChars, leadChars);
        var result = new List<TextChunk>(chunks.Count);
        if (string.IsNullOrEmpty(text))
        {
            foreach (var c in chunks) result.Add(new TextChunk(c, 0, 0, TextOffsetMap.Identity(0)));
            return result;
        }

        int cursor = 0;
        foreach (var chunk in chunks)
        {
            result.Add(Align(text, chunk, ref cursor));
        }
        return result;
    }

    /// <summary>
    /// Locate <paramref name="chunk"/> in <paramref name="text"/> at or after
    /// <paramref name="cursor"/>, tolerating the whitespace the chunker
    /// rewrote.
    ///
    /// Relies on one invariant of the three passes: they only ever drop or
    /// substitute <em>whitespace</em>, so the non-whitespace characters of the
    /// chunks, concatenated, are exactly the non-whitespace characters of the
    /// input. Walking both strings while skipping whitespace therefore recovers
    /// the true span. <c>ChunkWithOffsets_spans_are_exact</c> pins the invariant
    /// so a future chunker change that breaks it fails loudly here rather than
    /// silently mis-highlighting.
    /// </summary>
    private static TextChunk Align(string text, string chunk, ref int cursor)
    {
        int p = cursor;
        int start = -1, end = -1;
        var origins = new int[chunk.Length];

        for (int c = 0; c < chunk.Length; c++)
        {
            char ch = chunk[c];
            if (char.IsWhiteSpace(ch))
            {
                // A space in the chunk may stand for a newline, a tab, or a run
                // of several characters in the input. Anchor it at the first of
                // them — the position just past the previous real character.
                origins[c] = Math.Min(p, text.Length);
                continue;
            }

            // Skip only whitespace. Skipping a mismatching non-whitespace
            // character would resynchronise on the wrong occurrence and hide the
            // broken invariant behind a plausible-looking offset.
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;

            if (p >= text.Length || text[p] != ch)
            {
                // Invariant broken. Fill the rest of the map with the last known
                // position and degrade to the span found so far, rather than
                // throwing: a slightly wrong highlight beats a failed Speak.
                int fallback = Math.Min(p, text.Length);
                for (int k = c; k < origins.Length; k++) origins[k] = fallback;
                if (start < 0) { start = cursor; end = cursor; }
                cursor = end;
                return new TextChunk(chunk, start, end - start,
                    TextOffsetMap.FromOrigins(origins, text.Length));
            }

            if (start < 0) start = p;
            origins[c] = p;
            end = ++p;
        }

        if (start < 0) { start = cursor; end = cursor; }  // all-whitespace chunk
        cursor = end;
        return new TextChunk(chunk, start, end - start,
            TextOffsetMap.FromOrigins(origins, text.Length));
    }

    /// <summary>
    /// Pass 1. Port of the vendored <c>Helper.ChunkText</c>; see the type remarks
    /// for why it is a port rather than a call.
    /// </summary>
    public static List<string> SplitSentences(string text, int maxLength = 300)
    {
        var chunks = new List<string>();
        if (text is null) return chunks;

        var paragraphs = ParagraphBreak.Split(text.Trim())
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        foreach (var paragraph in paragraphs)
        {
            var sentences = SentenceBreak.Split(paragraph);
            string current = "";

            foreach (var sentence in sentences)
            {
                if (string.IsNullOrEmpty(sentence)) continue;

                if (current.Length + sentence.Length + 1 <= maxLength)
                {
                    if (!string.IsNullOrEmpty(current)) current += " ";
                    current += sentence;
                }
                else
                {
                    if (!string.IsNullOrEmpty(current)) chunks.Add(current.Trim());
                    current = sentence;
                }
            }

            if (!string.IsNullOrEmpty(current)) chunks.Add(current.Trim());
        }

        if (chunks.Count == 0) chunks.Add(text.Trim());
        return chunks;
    }

    private static IEnumerable<string> SplitLongChunk(string text, int maxLength)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            if (text.Length - pos <= maxLength)
            {
                yield return text.Substring(pos).Trim();
                yield break;
            }

            int boundary = FindSoftBoundary(text, pos, pos + maxLength);
            yield return text.Substring(pos, boundary - pos).Trim();
            pos = boundary;
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }
    }

    /// <summary>
    /// Best break point at or before <paramref name="end"/>, in descending order of
    /// preference: punctuation in the latter half of the window, then any
    /// whitespace in the window, then the next whitespace after it.
    ///
    /// The whitespace fallback deliberately searches the whole window rather than
    /// just the latter half: restricting it risks a hard cut mid-word whenever the
    /// latter half happens to contain no spaces. The final fallback overshoots
    /// <paramref name="end"/> for the same reason — an oversize chunk is a
    /// pacing problem, a severed word is an audible one.
    /// </summary>
    private static int FindSoftBoundary(string text, int start, int end)
    {
        int half = start + (end - start) / 2;
        for (int i = end - 1; i > half; i--)
        {
            char c = text[i];
            if (c == ',' || c == ';' || c == ':' || c == '—') return i + 1;
        }

        for (int i = end - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1;
        }

        for (int i = end; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1;
        }

        return text.Length;
    }
}

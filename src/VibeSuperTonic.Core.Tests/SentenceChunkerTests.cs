using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Chunker coverage (R-3), and in particular proof that moving the chunker into
/// Core did not change where it breaks. Chunk boundaries are audible — each one
/// is a seam in the prosody and a gap the pipeline has to stay ahead of — so a
/// "harmless refactor" that shifts them is a change to the product.
/// </summary>
public class SentenceChunkerTests
{
    // ------------------------------------------------------------------ oracle

    // Verbatim reproduction of the pre-move implementation: SupertonicAdapter's
    // three-pass ChunkText over Supertonic.Helper.ChunkText. Kept here, in the
    // tests, precisely because it must NOT be refactored — the moment this is
    // "tidied up" it stops being evidence.
    private const int OracleMaxChunkChars = 200;
    private const int OracleMinChunkChars = 100;
    private const int OracleMergeCeiling = OracleMaxChunkChars + 80;

    private static List<string> LegacyHelperChunkText(string text, int maxLen = 300)
    {
        var chunks = new List<string>();
        var paragraphRegex = new Regex(@"\n\s*\n+");
        var paragraphs = paragraphRegex.Split(text.Trim())
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        var sentenceRegex = new Regex(@"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+");

        foreach (var paragraph in paragraphs)
        {
            var sentences = sentenceRegex.Split(paragraph);
            string currentChunk = "";
            foreach (var sentence in sentences)
            {
                if (string.IsNullOrEmpty(sentence)) continue;
                if (currentChunk.Length + sentence.Length + 1 <= maxLen)
                {
                    if (!string.IsNullOrEmpty(currentChunk)) currentChunk += " ";
                    currentChunk += sentence;
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
                    currentChunk = sentence;
                }
            }
            if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
        }

        if (chunks.Count == 0) chunks.Add(text.Trim());
        return chunks;
    }

    private static int LegacyFindSoftBoundary(string text, int start, int end)
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

    private static IEnumerable<string> LegacySplitLongChunk(string text, int maxLen)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int remaining = text.Length - pos;
            if (remaining <= maxLen)
            {
                yield return text.Substring(pos).Trim();
                yield break;
            }
            int hardEnd = pos + maxLen;
            int boundary = LegacyFindSoftBoundary(text, pos, hardEnd);
            yield return text.Substring(pos, boundary - pos).Trim();
            pos = boundary;
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }
    }

    private static List<string> LegacyChunk(string text)
    {
        var sentenceChunks = LegacyHelperChunkText(text, maxLen: OracleMaxChunkChars);

        var split = new List<string>();
        foreach (var chunk in sentenceChunks)
        {
            if (chunk.Length <= OracleMaxChunkChars + 30) split.Add(chunk);
            else split.AddRange(LegacySplitLongChunk(chunk, OracleMaxChunkChars));
        }

        var merged = new List<string>();
        foreach (var chunk in split)
        {
            if (merged.Count > 0)
            {
                int combinedLen = merged[^1].Length + 1 + chunk.Length;
                bool tinyExists = merged[^1].Length < OracleMinChunkChars || chunk.Length < OracleMinChunkChars;
                if (tinyExists && combinedLen <= OracleMergeCeiling)
                {
                    merged[^1] = merged[^1] + " " + chunk;
                    continue;
                }
            }
            merged.Add(chunk);
        }
        return merged;
    }

    // ------------------------------------------------------------------ corpus

    private const string Prose =
        "The sea is everything. It covers seven tenths of the terrestrial globe. " +
        "Its breath is pure and healthy. It is an immense desert, where man is never lonely, " +
        "for he feels life stirring on all sides. The sea is only the embodiment of a " +
        "supernatural and wonderful existence.";

    private const string Abbreviations =
        "Dr. Aronnax boarded at 3 p.m. Prof. Lidenbrock, Ph.D., disagreed. " +
        "The vessel — i.e. the Nautilus — was faster than the Abraham Lincoln Inc. fleet. " +
        "Mr. Land said so, vs. all evidence to the contrary, etc.";

    private const string Paragraphs =
        "First paragraph, short.\n\n" +
        "Second paragraph runs considerably longer and carries several sentences. " +
        "It exists to force the packer to fill a chunk. Then it stops.\n\n\n" +
        "Third.";

    private const string NoTerminator =
        "a sentence with no terminating punctuation at all which simply keeps going and going " +
        "well past the two hundred character ceiling so that pass two has to find a soft boundary " +
        "somewhere inside it without severing any of the words that it happens to contain";

    private const string CommaHeavy =
        "One, two, three, four, five, six, seven, eight, nine, ten, eleven, twelve, thirteen, " +
        "fourteen, fifteen, sixteen, seventeen, eighteen, nineteen, twenty, twenty-one, twenty-two, " +
        "twenty-three, twenty-four, twenty-five, twenty-six, twenty-seven, twenty-eight.";

    private static readonly string Unbroken = new('x', 500);

    public static TheoryData<string> Corpus() => new()
    {
        Prose, Abbreviations, Paragraphs, NoTerminator, CommaHeavy, Unbroken,
        "Short.",
        "Two sentences. That is all.",
        "   leading and trailing whitespace   ",
        "No punctuation",
        "Ellipsis... then more text follows here to make it long enough to matter at all.",
        "Exclamation! Question? Mixed. All three in one line, repeatedly! Again? Yes.",
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Chunking_matches_the_pre_move_implementation_exactly(string text)
    {
        Assert.Equal(LegacyChunk(text), SentenceChunker.Chunk(text));
    }

    [Fact]
    public void Long_generated_prose_matches_the_oracle()
    {
        // Deterministic, no RNG: repeats with varying sentence lengths so the
        // packer, the splitter and the merger are all exercised in one pass.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            sb.Append("Sentence number ").Append(i).Append(' ');
            sb.Append(new string('w', (i * 7) % 90 + 5));
            sb.Append(i % 5 == 0 ? "! " : i % 3 == 0 ? "? " : ". ");
            if (i % 11 == 10) sb.Append("\n\n");
        }
        string text = sb.ToString();

        Assert.Equal(LegacyChunk(text), SentenceChunker.Chunk(text));
    }

    // -------------------------------------------------------------- properties

    [Theory]
    [MemberData(nameof(Corpus))]
    public void No_chunk_is_empty_or_untrimmed(string text)
    {
        foreach (var chunk in SentenceChunker.Chunk(text))
        {
            Assert.NotEqual("", chunk);
            Assert.Equal(chunk.Trim(), chunk);
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Chunking_preserves_every_word(string text)
    {
        // Whitespace and chunk seams may move; words may not be invented, dropped
        // or severed. This is the property the soft-boundary fallbacks exist for.
        static IEnumerable<string> Words(string s) =>
            s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var before = Words(text).ToList();
        var after = SentenceChunker.Chunk(text).SelectMany(Words).ToList();

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_moderately_oversize_run_is_split_then_merged_back()
    {
        // Non-obvious and worth pinning. NoTerminator is ~254 chars, so pass 2
        // splits it near 200 and leaves a ~54-char tail; pass 3 then sees a chunk
        // under MinChunkChars, finds the combined length still inside the merge
        // ceiling of 280, and puts it back together.
        //
        // That is the intended trade — one slightly oversize chunk synthesises
        // more evenly than a full one followed by a runt — but it means "input
        // exceeded MaxChunkChars" does not imply "output has more than one chunk".
        Assert.True(NoTerminator.Length > SentenceChunker.MaxChunkChars);

        var chunks = SentenceChunker.Chunk(NoTerminator);

        Assert.Single(chunks);
        Assert.Equal(NoTerminator.Length, chunks[0].Length);
    }

    [Fact]
    public void A_genuinely_long_run_splits_and_every_chunk_stays_within_the_ceiling()
    {
        // Long enough that no amount of tail-merging collapses it back.
        string text = string.Join(' ', Enumerable.Range(0, 140).Select(i => $"word{i}"));
        Assert.True(text.Length > 900);

        var chunks = SentenceChunker.Chunk(text);

        Assert.True(chunks.Count > 1, $"expected a split, got one chunk of {text.Length} chars");
        foreach (var chunk in chunks)
            Assert.True(chunk.Length <= SentenceChunker.MaxChunkChars + 80,
                $"chunk of {chunk.Length} chars overshot the merge ceiling: {chunk}");
    }

    [Fact]
    public void Tiny_neighbours_are_merged_rather_than_synthesised_separately()
    {
        // Three short sentences would each be a separate ONNX call without pass 3.
        var chunks = SentenceChunker.Chunk("One. Two. Three.");
        Assert.Single(chunks);
    }

    [Fact]
    public void A_word_longer_than_the_window_is_not_severed()
    {
        var chunks = SentenceChunker.Chunk(Unbroken);

        // Nothing to break on, so it must come back whole rather than cut at 200.
        Assert.Single(chunks);
        Assert.Equal(500, chunks[0].Length);
    }

    [Fact]
    public void Empty_and_whitespace_input_do_not_throw()
    {
        Assert.All(SentenceChunker.Chunk(""), c => Assert.NotNull(c));
        Assert.All(SentenceChunker.Chunk("     "), c => Assert.NotNull(c));
    }

    // ------------------------------------------------------------ offset spans
    //
    // The engine reports SAPI word-boundary offsets, and the Linux Reader tab
    // will drive its highlight, from a chunk's position in the text it was built
    // from. That position used to be recovered with IndexOf(chunkText), which
    // misses whenever the chunker rewrote whitespace — i.e. for any text with a
    // newline, a tab, or a double space between sentences.

    public static TheoryData<string> Separators() => new()
    {
        "One sentence here. Two sentence here. Three sentence here.",     // single space
        "One sentence here.\nTwo sentence here.\nThree sentence here.",   // newline
        "One sentence here.  Two sentence here.  Three sentence here.",   // double space
        "One sentence here.\tTwo sentence here.\tThree sentence here.",   // tab
        "One sentence here.\r\nTwo sentence here.\r\nThree here.",        // CRLF
        "Para one is here.\n\nPara two is here.\n\nPara three is here.",  // paragraph breaks
        "   Leading and trailing whitespace around it all.   ",
    };

    [Theory]
    [MemberData(nameof(Separators))]
    [MemberData(nameof(Corpus))]
    public void ChunkWithOffsets_spans_are_exact(string text)
    {
        var chunks = SentenceChunker.ChunkWithOffsets(text);

        int previousEnd = 0;
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Start, previousEnd, text.Length);
            Assert.InRange(chunk.Start + chunk.Length, chunk.Start, text.Length);

            // The span is expressed in source coordinates and the chunk text is
            // whitespace-normalised, so they are equal only after normalising
            // both. This is the invariant Align relies on.
            string covered = text.Substring(chunk.Start, chunk.Length);
            Assert.Equal(NonWhitespace(chunk.Text), NonWhitespace(covered));

            previousEnd = chunk.Start + chunk.Length;
        }
    }

    [Theory]
    [MemberData(nameof(Separators))]
    [MemberData(nameof(Corpus))]
    public void ChunkWithOffsets_returns_the_same_text_as_Chunk(string text)
    {
        Assert.Equal(
            SentenceChunker.Chunk(text),
            SentenceChunker.ChunkWithOffsets(text).Select(c => c.Text).ToList());
    }

    [Fact]
    public void ChunkWithOffsets_finds_the_true_start_when_IndexOf_cannot()
    {
        // The regression itself. The sentence separator "\n" is normalised to
        // " ", so chunk 2 is not a substring of the input and the old IndexOf
        // path fell back to a running offset in rewritten coordinates.
        //
        // Both sentences are over MinChunkChars so pass 3 leaves them separate —
        // with two short ones the merger produces a single chunk and there is no
        // second offset to get wrong.
        const string A = "The first sentence has to be long enough that the merge pass leaves it alone, "
                       + "so it runs past one hundred characters.";
        const string B = "The second sentence is likewise long enough to stand on its own, "
                       + "which is what makes its start offset worth checking at all.";
        string text = A + "\n" + B;

        var chunks = SentenceChunker.ChunkWithOffsets(text);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(text.IndexOf("The second", StringComparison.Ordinal), chunks[1].Start);

        // And the old approach really does miss on this input.
        Assert.Equal(-1, text.IndexOf(string.Join(" ", chunks.Select(c => c.Text)), StringComparison.Ordinal));
    }

    [Fact]
    public void ChunkWithOffsets_handles_empty_and_whitespace_input()
    {
        Assert.All(SentenceChunker.ChunkWithOffsets(""), c => Assert.Equal(0, c.Start));
        Assert.All(SentenceChunker.ChunkWithOffsets("     "), c => Assert.InRange(c.Start, 0, 5));
    }

    // ------------------------------------------------------- configurable sizes
    //
    // MaxChunkChars/MinChunkChars were persisted settings with sliders in two
    // launcher tabs that had never reached the chunker — it read private
    // constants. Now that callers pass them, hostile values are reachable: the
    // sliders overlap (max 80-400, min 40-200, so min > max is a valid pair) and
    // the settings-file import path parses them with no range check at all.

    [Theory]
    [InlineData(80, 40)]
    [InlineData(400, 200)]
    [InlineData(80, 200)]    // min > max — reachable from the sliders alone
    [InlineData(0, 0)]       // hand-edited settings.json
    [InlineData(-5, -5)]
    [InlineData(1, 1)]       // would be hundreds of model runs per paragraph
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue)]
    public void Hostile_chunk_sizes_still_produce_usable_chunks(int maxChars, int minChars)
    {
        var chunks = SentenceChunker.Chunk(Prose, maxChars, minChars);

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.NotEqual("", chunk);
            // Clamped to at most MaxAllowedMaxChars, and the splitter is allowed
            // to overshoot rather than sever a word — hence the headroom.
            Assert.InRange(chunk.Length, 1, SentenceChunker.MaxAllowedMaxChars + 500);
        }

        // No text is lost or duplicated whatever the sizes.
        Assert.Equal(
            NonWhitespace(Prose),
            NonWhitespace(string.Concat(chunks)));
    }

    [Theory]
    [InlineData(80, 40)]
    [InlineData(400, 200)]
    [InlineData(80, 200)]
    [InlineData(1, 1)]
    public void ChunkWithOffsets_spans_stay_exact_at_any_size(int maxChars, int minChars)
    {
        int previousEnd = 0;
        foreach (var chunk in SentenceChunker.ChunkWithOffsets(Prose, maxChars, minChars))
        {
            Assert.InRange(chunk.Start, previousEnd, Prose.Length);
            Assert.Equal(
                NonWhitespace(chunk.Text),
                NonWhitespace(Prose.Substring(chunk.Start, chunk.Length)));
            previousEnd = chunk.Start + chunk.Length;
        }
    }

    [Fact]
    public void Default_arguments_reproduce_the_shipped_behaviour()
    {
        // The settings default to exactly these, so an install that never touched
        // a slider must chunk identically to every release before this one.
        Assert.Equal(
            LegacyChunk(Prose),
            SentenceChunker.Chunk(Prose, SentenceChunker.MaxChunkChars, SentenceChunker.MinChunkChars));
    }

    // ------------------------------------------------- intra-chunk offset map
    //
    // Found by the Windows TestHarness against 0.2.7.4, not by any test here.
    // ChunkWithOffsets reported the right chunk START and callers then added an
    // index-within-chunk to it — which is correct only while every separator is
    // exactly one character. The chunker collapses whitespace INSIDE a chunk too,
    // so "a.  b" becomes "a. b" and everything after the double space sits one
    // position earlier in the chunk than in the input.
    //
    // Single space, newline and tab all passed because each is 1:1. Double spaces
    // and paragraph breaks drifted one character per separator, accumulating.

    [Theory]
    [InlineData("First sentence here. Second sentence here. Third one is here.")]
    [InlineData("First sentence here.\nSecond sentence here.\nThird one is here.")]
    [InlineData("First sentence here.  Second sentence here.  Third one is here.")]
    [InlineData("First sentence here.\n\nSecond sentence here.\n\nThird one is here.")]
    [InlineData("First sentence here.\tSecond sentence here.\tThird one is here.")]
    [InlineData("Ragged   spacing\t\tbetween\n\n\nevery single    word here now.")]
    public void Chunk_map_resolves_every_character_to_its_source_position(string text)
    {
        foreach (var chunk in SentenceChunker.ChunkWithOffsets(text))
        {
            for (int i = 0; i < chunk.Text.Length; i++)
            {
                if (char.IsWhiteSpace(chunk.Text[i])) continue;   // spaces stand for runs
                int src = chunk.Map.ToSource(i);
                Assert.InRange(src, 0, text.Length - 1);
                Assert.Equal(chunk.Text[i], text[src]);
            }
        }
    }

    [Fact]
    public void Word_starts_map_back_to_word_starts_under_collapsed_whitespace()
    {
        // The harness's failing case, reduced. Every word start in the chunk must
        // land on a word start in the input — the property a highlight depends on.
        const string Text = "First sentence here.  Second sentence here.  Third one is here.";

        var wordStarts = new HashSet<int>();
        for (int i = 0; i < Text.Length; i++)
            if (!char.IsWhiteSpace(Text[i]) && (i == 0 || char.IsWhiteSpace(Text[i - 1])))
                wordStarts.Add(i);

        foreach (var chunk in SentenceChunker.ChunkWithOffsets(Text))
            for (int i = 0; i < chunk.Text.Length; i++)
                if (!char.IsWhiteSpace(chunk.Text[i]) && (i == 0 || char.IsWhiteSpace(chunk.Text[i - 1])))
                    Assert.Contains(chunk.Map.ToSource(i), wordStarts);
    }

    [Fact]
    public void Chunk_map_composes_with_a_pronunciation_map()
    {
        // The full engine path: chunk index -> rewritten -> source. Composing in
        // the wrong order, or skipping either stage, is what R-2 and R-14 were.
        const string Source = "The parcel weighs 5 kg today.  The next one weighs 9 kg also.";
        var pron = new PronunciationsConfig
        {
            Enabled = true,
            Rules = { new PronunciationRule { Match = "kg", Replace = "kilograms", WholeWord = true } },
        };

        string spoken = SynthTextPipeline.Prepare(Source, pron, null, out var pronMap);

        foreach (var chunk in SentenceChunker.ChunkWithOffsets(spoken))
        {
            var composed = TextOffsetMap.Chain(chunk.Map, pronMap);
            int at = chunk.Text.IndexOf("kilograms", StringComparison.Ordinal);
            if (at < 0) continue;

            // Must point at the "kg" the user typed, and cover two characters.
            int src = composed.ToSource(at);
            Assert.Equal("kg", Source.Substring(src, 2));
            Assert.Equal(2, composed.SourceSpanLength(at, "kilograms".Length));
        }
    }

    private static string NonWhitespace(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
}

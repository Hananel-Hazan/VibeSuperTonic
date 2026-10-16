using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Holds the Linux boundary planner against the Windows engine's shipped
/// algorithm.
///
/// The point is not that the placement rule is good — it is proportional within
/// a chunk, which assumes an even speaking rate it does not have. The point is
/// that it is the rule five shipped releases use, judged acceptable by the
/// people using it. If Linux placed boundaries by some better reasoning, the two
/// platforms would disagree about the same sentence with no way to say which was
/// wrong, and every future field report would have to establish which product
/// it came from before it could be read.
///
/// So the oracle below is <see cref="EngineWordBoundaries"/>: a transcription of
/// <c>SapiEngine.EmitWordBoundaries</c> and <c>EmitSentenceBoundary</c>,
/// arithmetic and rounding intact, working in SAPI's stream <em>bytes</em> where
/// the planner works in frames. Agreement between two independently-written
/// expressions of the same rule is worth considerably more than agreement with
/// my expectations of it — which is how R-14 passed every unit test it had while
/// shipping broken.
/// </summary>
public class BoundaryPlannerTests
{
    // --------------------------------------------------------------- the oracle

    private readonly record struct EngineEvent(
        ulong AudioStreamOffsetBytes, int SourceOffset, int SourceLength, BoundaryKind Kind);

    private static bool EngineIsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '\'' || c == '-';

    /// <summary>Transcribed from <c>SapiEngine.SpeakChunkExec.SourceLengthAt</c>.</summary>
    private static int EngineSourceLengthAt(TextOffsetMap map, int indexInChunk, int lengthInChunk)
    {
        int length = map.SourceSpanLength(indexInChunk, lengthInChunk);
        if (length > 0 || lengthInChunk <= 0) return length;
        int start = map.ToSource(indexInChunk);
        return start < map.SourceLength ? 1 : 0;
    }

    /// <summary>
    /// Transcribed from <c>SapiEngine.EmitSentenceBoundary</c> +
    /// <c>EmitWordBoundaries</c>. Byte offsets, <c>&amp;= ~1UL</c> alignment and
    /// the multiply-before-divide order are all reproduced deliberately.
    /// </summary>
    private static List<EngineEvent> EngineWordBoundaries(
        string chunkText, TextOffsetMap map, uint fragmentSourceOffset,
        int pcmSamples, ulong streamOffsetBytes)
    {
        var events = new List<EngineEvent>();
        if (chunkText.Length == 0 || pcmSamples == 0) return events;

        uint SourceOffsetAt(int i) => fragmentSourceOffset + (uint)map.ToSource(i);

        events.Add(new EngineEvent(
            streamOffsetBytes,
            (int)SourceOffsetAt(0),
            EngineSourceLengthAt(map, 0, chunkText.Length),
            BoundaryKind.Sentence));

        ulong chunkAudioBytes = (ulong)(pcmSamples * sizeof(short));
        int n = chunkText.Length;
        int pos = 0;
        while (pos < n)
        {
            while (pos < n && !EngineIsWordChar(chunkText[pos])) pos++;
            if (pos >= n) break;
            int start = pos;
            while (pos < n && EngineIsWordChar(chunkText[pos])) pos++;
            int wordLen = pos - start;
            if (wordLen == 0) continue;

            ulong wordAudioOffset = streamOffsetBytes + (ulong)((long)chunkAudioBytes * start / n);
            wordAudioOffset &= ~1UL;

            events.Add(new EngineEvent(
                wordAudioOffset,
                (int)SourceOffsetAt(start),
                EngineSourceLengthAt(map, start, wordLen),
                BoundaryKind.Word));
        }
        return events;
    }

    // ------------------------------------------------------------------- corpus

    /// <summary>
    /// Whitespace layouts first, because that is where both R-2 and R-14 hid:
    /// single spaces, newlines and tabs all looked correct while double spaces
    /// and paragraph breaks drifted one character per separator.
    /// </summary>
    private static readonly string[] CorpusTexts =
    [
        "Hello world.",
        "First one. Second one.",
        "First one.  Second one.",           // double space — R-14's actual trigger
        "First one.\nSecond one.",
        "First one.\n\nSecond one.",         // paragraph break
        "First one.\tSecond one.",
        "  Leading whitespace matters too.",
        "Don't split well-known hyphenates.",
        "Numbers 3.14 and 2026 are word characters.",
        "Punctuation! Does? It: land; right, though...",
        "A",
        "one two three four five six seven eight nine ten " +
        "eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen.",
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen " +
        "liquor jugs. How vexingly quick daft zebras jump! Sphinx of black quartz, " +
        "judge my vow. Two driven jocks help fax my big quiz.",
    ];

    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();
        foreach (var t in CorpusTexts) data.Add(t);
        return data;
    }

    private static PronunciationsConfig Rules(params (string match, string replace)[] rules) =>
        new()
        {
            Enabled = true,
            Rules = rules.Select(r => new PronunciationRule
            {
                Enabled = true,
                Match = r.match,
                Replace = r.replace,
                WholeWord = true,
            }).ToList(),
        };

    private static Regex?[] Compile(PronunciationsConfig cfg) =>
        cfg.Rules.Select(PronunciationsConfig.Compile).ToArray();

    /// <summary>
    /// Reproduces the engine's per-chunk composition:
    /// <c>Chain(chunk.Map, pipelineMap)</c>, chunk index → spoken → source. Doing
    /// this in the test rather than hand-building a map is the point — a map
    /// built to suit the assertion proves nothing about the map the product has.
    /// </summary>
    private static IEnumerable<(string text, TextOffsetMap map, int frames)> Plan(
        string source, PronunciationsConfig? pron = null)
    {
        string spoken = SynthTextPipeline.Prepare(
            source, pron, pron is null ? null : Compile(pron), out var pipelineMap);

        int frames = 20_000;
        foreach (var chunk in SentenceChunker.ChunkWithOffsets(spoken))
        {
            // Frame counts vary per chunk in reality; varying them here exercises
            // the rounding rather than letting one convenient number hide it.
            frames = frames * 7 % 91_237 + 4_096;
            yield return (chunk.Text, TextOffsetMap.Chain(chunk.Map, pipelineMap), frames);
        }
    }

    // -------------------------------------------------------------- equivalence

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Places_every_boundary_where_the_Windows_engine_places_it(string source)
    {
        const uint fragmentOffset = 0;

        long streamFrame = 0;
        foreach (var (text, map, frames) in Plan(source))
        {
            var planned = new List<BoundaryEvent>();
            BoundaryPlanner.PlanChunk(planned, text, map, (int)fragmentOffset, frames, streamFrame);

            var expected = EngineWordBoundaries(
                text, map, fragmentOffset, frames, (ulong)streamFrame * sizeof(short));

            Assert.Equal(expected.Count, planned.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                // The engine's byte offset is sample-aligned, so dividing by two
                // is lossless and the two are directly comparable.
                Assert.Equal((long)(expected[i].AudioStreamOffsetBytes / sizeof(short)), planned[i].Frame);
                Assert.Equal(expected[i].SourceOffset, planned[i].SourceOffset);
                Assert.Equal(expected[i].SourceLength, planned[i].SourceLength);
                Assert.Equal(expected[i].Kind, planned[i].Kind);
            }

            streamFrame += frames;
        }
    }

    /// <summary>
    /// The same equivalence with a length-changing pronunciation rule in play —
    /// R-2's territory, where a chunk index and a source offset differ by an
    /// amount that accumulates across the text.
    /// </summary>
    [Theory]
    [InlineData("Add 5 kg of flour, then 2 kg of sugar, then another 10 kg.")]
    [InlineData("kg at the very start, then kg again.")]
    [InlineData("A sentence with no rule matches at all.")]
    public void Agrees_with_the_engine_through_a_length_changing_rule(string source)
    {
        var pron = Rules(("kg", "kilograms"), ("Dr", "Doctor"));
        const uint fragmentOffset = 17;   // a non-zero SAPI fragment, as in real speech

        long streamFrame = 0;
        foreach (var (text, map, frames) in Plan(source, pron))
        {
            var planned = new List<BoundaryEvent>();
            BoundaryPlanner.PlanChunk(planned, text, map, (int)fragmentOffset, frames, streamFrame);

            var expected = EngineWordBoundaries(
                text, map, fragmentOffset, frames, (ulong)streamFrame * sizeof(short));

            Assert.Equal(
                expected.Select(e => ((long)(e.AudioStreamOffsetBytes / 2), e.SourceOffset, e.SourceLength, e.Kind)),
                planned.Select(e => (e.Frame, e.SourceOffset, e.SourceLength, e.Kind)));

            streamFrame += frames;
        }
    }

    // ------------------------------------------------------------- the contract

    [Fact]
    public void Reports_source_lengths_the_user_can_actually_select()
    {
        // R-2's headline case. "kilograms" is nine spoken characters over the
        // two the user typed, and a client highlighting nine would run over the
        // following words.
        const string source = "Add 5 kg now.";
        var pron = Rules(("kg", "kilograms"));
        string spoken = SynthTextPipeline.Prepare(source, pron, Compile(pron), out var map);
        Assert.Equal("Add 5 kilograms now.", spoken);

        var planned = new List<BoundaryEvent>();
        BoundaryPlanner.PlanChunk(planned, spoken, map, 0, 44_100, 0);

        var words = planned
            .Where(e => e.Kind == BoundaryKind.Word)
            .Select(e => source.Substring(e.SourceOffset, e.SourceLength))
            .ToList();

        // Four spoken words, four selectable spans — and the third is the two
        // characters that were typed, not the nine that are said.
        Assert.Equal(new[] { "Add", "5", "kg", "now" }, words);
    }

    [Fact]
    public void Every_word_event_lands_on_a_real_word_in_the_source_text()
    {
        // The property a user would state: whatever is highlighted is a word
        // they can see. Checked directly against the source string rather than
        // against the planner's own arithmetic.
        const string source = "First one.  Second one.\n\nThird one here.";

        string spoken = SynthTextPipeline.Prepare(source, null, null, out var pipelineMap);
        long frame = 0;

        foreach (var chunk in SentenceChunker.ChunkWithOffsets(spoken))
        {
            var planned = new List<BoundaryEvent>();
            BoundaryPlanner.PlanChunk(
                planned, chunk.Text, TextOffsetMap.Chain(chunk.Map, pipelineMap), 0, 44_100, frame);

            foreach (var e in planned.Where(e => e.Kind == BoundaryKind.Word))
            {
                Assert.InRange(e.SourceOffset, 0, source.Length - 1);
                int from = Math.Max(0, e.SourceOffset - 8);
                int len = Math.Min(16, source.Length - from);
                Assert.True(BoundaryPlanner.IsWordChar(source[e.SourceOffset]),
                    $"offset {e.SourceOffset} is '{source[e.SourceOffset]}', not a word character. " +
                    $"Context: '{source.Substring(from, len)}'");
            }

            frame += 44_100;
        }
    }

    [Fact]
    public void Boundaries_come_out_in_non_decreasing_frame_order()
    {
        // BoundaryScheduler.Add throws on a backwards frame, so this is the
        // planner's half of that contract rather than a restatement of it.
        foreach (var source in CorpusTexts)
        {
            long frame = 0;
            var all = new List<BoundaryEvent>();
            foreach (var (text, map, frames) in Plan(source))
            {
                BoundaryPlanner.PlanChunk(all, text, map, 0, frames, frame);
                frame += frames;
            }

            for (int i = 1; i < all.Count; i++)
                Assert.True(all[i].Frame >= all[i - 1].Frame,
                    $"'{source}': frame went backwards at event {i}");
        }
    }

    [Fact]
    public void A_silent_or_empty_chunk_plans_nothing()
    {
        var planned = new List<BoundaryEvent>();

        BoundaryPlanner.PlanChunk(planned, "", TextOffsetMap.Identity(0), 0, 44_100, 0);
        Assert.Empty(planned);

        // Zero frames is what a cancelled or failed render leaves behind.
        // Planning events onto audio that does not exist would place them all on
        // the same frame and fire the lot at once.
        BoundaryPlanner.PlanChunk(planned, "Some words here.", TextOffsetMap.Identity(16), 0, 0, 0);
        Assert.Empty(planned);
    }

    [Fact]
    public void The_first_word_of_an_utterance_is_at_frame_zero()
    {
        // Not a tautology: a first word placed even slightly late reads as the
        // highlight lagging the audio from the very first syllable, which is the
        // most-noticed moment there is.
        var planned = new List<BoundaryEvent>();
        BoundaryPlanner.PlanChunk(planned, "Hello world.", TextOffsetMap.Identity(12), 0, 44_100, 0);

        Assert.Equal(0, planned[0].Frame);
        Assert.Equal(BoundaryKind.Sentence, planned[0].Kind);
        Assert.Equal(0, planned[1].Frame);
        Assert.Equal(BoundaryKind.Word, planned[1].Kind);
    }
}

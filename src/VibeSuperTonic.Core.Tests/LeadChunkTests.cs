using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The first chunk is press-to-speech latency, which is not obvious from
/// anywhere in the chunker's own vocabulary.
///
/// Nothing is heard until chunk one has rendered, and the merge pass — whose job
/// is even chunk sizes — will glue three short opening sentences together to get
/// them. Measured on the Mint box before this existed: 1.45 s of silence after
/// the key press, against a 150 ms budget.
/// </summary>
public class LeadChunkTests
{
    private const string ThreeShortSentences =
        "The sea is everything. It covers seven tenths of the terrestrial globe. " +
        "Its breath is pure and healthy.";

    [Fact]
    public void Off_by_default_so_the_Windows_engine_is_untouched()
    {
        // SAPI is handed a whole stream and has no press to answer, so it keeps
        // the merged chunks it has shipped with for five releases.
        var chunks = SentenceChunker.Chunk(ThreeShortSentences);

        Assert.Single(chunks);
        Assert.Equal(ThreeShortSentences, chunks[0]);
    }

    [Fact]
    public void A_lead_cap_frees_the_opening_sentence()
    {
        var chunks = SentenceChunker.Chunk(ThreeShortSentences, leadChars: 64);

        Assert.True(chunks.Count > 1);
        Assert.Equal("The sea is everything.", chunks[0]);

        // Only the first chunk is affected — the remainder stays one piece, so
        // prosody and per-chunk overhead are unchanged for the other 99% of a
        // read.
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void Nothing_is_lost_or_reordered_by_the_split()
    {
        var with = SentenceChunker.Chunk(ThreeShortSentences, leadChars: 64);
        var without = SentenceChunker.Chunk(ThreeShortSentences);

        // Whitespace between chunks is the chunker's to normalise; the words
        // must be identical and in order.
        Assert.Equal(
            string.Join(' ', without).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            string.Join(' ', with).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_long_opening_sentence_is_left_whole_rather_than_cut_mid_clause()
    {
        // The wait is merely slow. A chunk ending mid-clause is audible, and a
        // seam in the wrong place is the one cost this optimisation must not pay.
        const string OneLongSentence =
            "The sea is everything and it covers seven tenths of the terrestrial globe " +
            "and its breath is pure and healthy and it is an immense desert. Short one.";

        var chunks = SentenceChunker.Chunk(OneLongSentence, leadChars: 40);

        Assert.StartsWith("The sea is everything and it covers", chunks[0]);
        Assert.EndsWith("immense desert.", chunks[0]);
    }

    [Fact]
    public void Short_text_that_already_fits_is_not_touched()
    {
        var chunks = SentenceChunker.Chunk("Just this.", leadChars: 64);
        Assert.Equal(new[] { "Just this." }, chunks);
    }

    [Fact]
    public void Offsets_still_resolve_to_the_source_after_the_split()
    {
        // The split happens before alignment, so each chunk is located in the
        // source independently. If that were wrong every highlight after the
        // first sentence would drift — the R-14 failure, reintroduced through a
        // latency optimisation.
        const string Source = "The sea is everything.  It covers seven tenths.\n\nIts breath is pure.";

        var chunks = SentenceChunker.ChunkWithOffsets(
            Source, SentenceChunker.MaxChunkChars, SentenceChunker.MinChunkChars, leadChars: 30);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            // Every chunk's first word must be findable at its reported start.
            string firstWord = chunk.Text.Split(' ')[0].TrimEnd('.', ',');
            Assert.Equal(firstWord, Source.Substring(chunk.Start, firstWord.Length));
        }
    }
}

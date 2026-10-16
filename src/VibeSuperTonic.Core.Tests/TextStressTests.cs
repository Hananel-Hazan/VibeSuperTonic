using System.Diagnostics;
using System.Text;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Randomised inputs against the text path's invariants: the chunker, its
/// offset maps, the SSML reader and the protocol decoder.
///
/// <para><b>Every generator is seeded</b>, so a failure here is a failure on the
/// next run too and the seed in the message reproduces it. A fuzz test that
/// fails once in fifty runs is a test people learn to re-run.</para>
///
/// <para>Two of these were written against bugs and seen failing first. The
/// chunk-boundary property failed on every chunk that was not the last: a span
/// ending at a chunk's last character mapped its end through the map's
/// one-past-the-end clamp, which was the length of the <em>whole</em> text, so
/// the last word of chunk 1 of a 400-character input reported a SourceLength of
/// 377. And the SSML budgets failed by two orders of magnitude: a tag scan ran to
/// the end of the string for every <c>&lt;</c> with no <c>&gt;</c> after it, so
/// <c>"&lt;speak&gt;" + "&lt;a" × 40000</c> took about eight seconds.</para>
/// </summary>
[Collection(StressCollection.Name)]
public class TextStressTests
{
    // ================================================================ chunker

    private static readonly string[] Separators =
        [" ", " ", " ", " ", "  ", "\n", "\n\n", "\t", "\u00A0", " \n \t "];

    private static readonly string[] Punct =
        [".", "!", "?", ",", ";", ":", "—", "…", "...", "?!"];

    private static readonly string[] Specials =
        ["Mr.", "Dr.", "e.g.", "i.e.", "3.14", "U.S.", "你好世界。", "日本語の文章です", "😀", "👍🏽", "a😀b", "Ph.D."];

    private static string Word(Random r)
    {
        int len = r.Next(1, 12);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append((char)(r.Next(2) == 0 ? 'a' + r.Next(26) : 'A' + r.Next(26)));
        return sb.ToString();
    }

    private static string RandomText(Random r)
    {
        int kind = r.Next(20);
        if (kind == 0) return "";
        if (kind == 1) return new string(' ', r.Next(1, 5)) + "\n\t\u00A0".Substring(0, r.Next(0, 4));

        int tokens = r.Next(1, 160);
        var sb = new StringBuilder();
        if (r.Next(6) == 0) sb.Append(Separators[r.Next(Separators.Length)]);
        for (int t = 0; t < tokens; t++)
        {
            int pick = r.Next(100);
            if (pick < 70) sb.Append(Word(r));
            else if (pick < 85) sb.Append(Specials[r.Next(Specials.Length)]);
            else if (pick < 88)
            {
                // A long unbroken token — a URL, a hash, a CJK paragraph.
                int len = r.Next(150, 1500);
                bool cjk = r.Next(2) == 0;
                for (int i = 0; i < len; i++) sb.Append(cjk ? (char)(0x4E00 + r.Next(2000)) : (char)('a' + r.Next(26)));
            }
            else sb.Append(Punct[r.Next(Punct.Length)]);

            if (r.Next(4) == 0) sb.Append(Punct[r.Next(Punct.Length)]);
            sb.Append(Separators[r.Next(Separators.Length)]);
        }
        if (r.Next(2) == 0) sb.Append(Separators[r.Next(Separators.Length)]);
        return sb.ToString();
    }

    private static string NonWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// The reviewer's case, exactly: one 80-word run with no sentence break,
    /// then a short sentence. Chunk 1 ends at "word79."; its last word and its
    /// sentence boundary must both stop there, not run on to the end of "Tail
    /// sentence here."
    /// </summary>
    [Fact]
    public void A_span_ending_at_a_chunk_end_stays_inside_that_chunk()
    {
        string text = string.Join(" ", Enumerable.Range(0, 80).Select(i => $"word{i}")) + ". Tail sentence here.";
        var chunks = SentenceChunker.ChunkWithOffsets(text);
        Assert.True(chunks.Count >= 2, $"expected at least two chunks, got {chunks.Count}");

        var first = chunks[0];
        int lastWord = first.Text.LastIndexOf(' ') + 1;
        int wordLen = first.Text.Length - lastWord;
        while (wordLen > 0 && !BoundaryPlanner.IsWordChar(first.Text[lastWord + wordLen - 1])) wordLen--;

        int src = first.Map.ToSource(lastWord);
        int len = BoundaryPlanner.SourceLengthAt(first.Map, lastWord, wordLen);
        Assert.Equal(first.Text.Substring(lastWord, wordLen), text.Substring(src, len));

        // The sentence event: the whole chunk, and not one character more.
        Assert.Equal(first.Length, BoundaryPlanner.SourceLengthAt(first.Map, 0, first.Text.Length));

        // And through the engine's composition with a pipeline map, which is how
        // both platforms actually ask.
        string spoken = SynthTextPipeline.Prepare(text, null, null, out var pipelineMap);
        var c0 = SentenceChunker.ChunkWithOffsets(spoken)[0];
        var composed = TextOffsetMap.Chain(c0.Map, pipelineMap);
        Assert.Equal(c0.Length, BoundaryPlanner.SourceLengthAt(composed, 0, c0.Text.Length));
    }

    [Fact]
    public void Chunker_holds_its_invariants_over_random_text()
    {
        var r = new Random(20261004);
        for (int iter = 0; iter < 3000; iter++)
        {
            string text = RandomText(r);
            int max = r.Next(0, 500), min = r.Next(-10, 300), lead = r.Next(4) == 0 ? r.Next(0, 300) : 0;
            string ctx = $"iter {iter}, max {max}, min {min}, lead {lead}";

            var plain = SentenceChunker.Chunk(text, max, min, lead);
            Assert.Equal(NonWhitespace(text), NonWhitespace(string.Concat(plain)));

            var chunks = SentenceChunker.ChunkWithOffsets(text, max, min, lead);
            Assert.Equal(plain, chunks.Select(c => c.Text).ToList());

            int prevEnd = 0;
            foreach (var chunk in chunks)
            {
                Assert.True(chunk.Start >= prevEnd, $"{ctx}: chunk at {chunk.Start} overlaps the previous end {prevEnd}");
                Assert.True(chunk.Length >= 0 && chunk.Start + chunk.Length <= text.Length, $"{ctx}: span out of bounds");
                prevEnd = chunk.Start + chunk.Length;

                Assert.Equal(NonWhitespace(chunk.Text), NonWhitespace(text.Substring(chunk.Start, chunk.Length)));

                if (chunk.Text.Length > 0)
                {
                    Assert.False(char.IsLowSurrogate(chunk.Text[0]), $"{ctx}: chunk starts mid-pair");
                    Assert.False(char.IsHighSurrogate(chunk.Text[^1]), $"{ctx}: chunk ends mid-pair");
                }

                AssertSpansStayInChunk(chunk, ctx);
            }
        }
    }

    /// <summary>
    /// Every word event and the sentence event, computed the way
    /// <see cref="BoundaryPlanner"/> computes them, must select source text inside
    /// the chunk that spoke it.
    /// </summary>
    private static void AssertSpansStayInChunk(TextChunk chunk, string ctx)
    {
        int lo = chunk.Start, hi = chunk.Start + chunk.Length;
        string t = chunk.Text;

        void Check(int at, int len)
        {
            int src = chunk.Map.ToSource(at);
            int srcLen = BoundaryPlanner.SourceLengthAt(chunk.Map, at, len);
            Assert.True(src >= lo && src + srcLen <= hi,
                $"{ctx}: span [{at},+{len}) of chunk [{lo},{hi}) maps to [{src},+{srcLen})");
        }

        if (t.Length > 0) Check(0, t.Length);
        int pos = 0;
        while (pos < t.Length)
        {
            while (pos < t.Length && !BoundaryPlanner.IsWordChar(t[pos])) pos++;
            int start = pos;
            while (pos < t.Length && BoundaryPlanner.IsWordChar(t[pos])) pos++;
            if (pos > start) Check(start, pos - start);
        }
    }

    [Fact]
    public void Chunking_two_megabytes_is_linear_enough_to_finish()
    {
        var r = new Random(7);
        var sb = new StringBuilder();
        while (sb.Length < 2_000_000)
        {
            sb.Append(Word(r));
            sb.Append(r.Next(12) == 0 ? ". " : r.Next(40) == 0 ? "\n\n" : " ");
        }
        string text = sb.ToString();

        var sw = Stopwatch.StartNew();
        var chunks = SentenceChunker.ChunkWithOffsets(text);
        sw.Stop();

        Assert.Equal(NonWhitespace(text).Length, chunks.Sum(c => NonWhitespace(c.Text).Length));
        Assert.True(sw.ElapsedMilliseconds < 4000, $"2 MB took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Chunk_is_safe_to_call_from_many_threads_at_once()
    {
        var r = new Random(99);
        string text = string.Concat(Enumerable.Range(0, 40).Select(_ => RandomText(r)));
        var expected = SentenceChunker.Chunk(text, 180, 60, 50);

        var results = new List<string>[64];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => results[i] = SentenceChunker.Chunk(text, 180, 60, 50));

        foreach (var got in results) Assert.Equal(expected, got);
    }

    // =================================================================== SSML

    private static readonly string[] SsmlSoup =
        ["<", ">", "\"", "'", "&", ";", "/", "=", " ", "<speak>", "</speak>", "<a", "</", "<s>", "</s>",
         "<break time=\"200ms\"/>", "<mark name=\"m\"/>", "&amp;", "&#x41;", "&#65;", "&lt;", "<!--", "-->",
         "<![CDATA[", "]]>", "<prosody rate=\"fast\">", "</prosody>", "word", "hello", "\n"];

    private static void WithinBudget(string what, int budgetMs, Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < budgetMs, $"{what} took {sw.ElapsedMilliseconds} ms (budget {budgetMs})");
    }

    private static void ReadEveryWay(string doc, string ctx, int budgetMs)
    {
        WithinBudget($"Strip, {ctx}", budgetMs, () => Ssml.Strip(doc));
        WithinBudget($"TryParse, {ctx}", budgetMs, () =>
        {
            if (SsmlDocument.TryParse(doc, out var fragments)) SsmlDocument.SpokenText(fragments);
        });
    }

    [Theory]
    [InlineData("unterminated tags", "<a", 40_000)]
    [InlineData("unterminated quoted tags", "<a \"", 25_000)]
    [InlineData("bare ampersands", "&", 100_000)]
    [InlineData("ampersand-hash", "&#", 50_000)]
    [InlineData("less-than pairs", "<<", 50_000)]
    public void Pathological_ssml_is_read_in_linear_time(string name, string unit, int repeat)
    {
        var sb = new StringBuilder("<speak>");
        for (int i = 0; i < repeat; i++) sb.Append(unit);
        string open = sb.ToString();

        ReadEveryWay(open, name, 500);
        ReadEveryWay(open + "</speak>", name + ", closed", 500);
        // One '>' at the very end, so a "is there any '>' left" shortcut alone
        // is not enough: every scan would still find it, through every quote.
        ReadEveryWay(open + ">", name + ", one late '>'", 500);
    }

    [Fact]
    public void Random_ssml_soup_never_throws_and_never_stalls()
    {
        var r = new Random(4242);
        for (int iter = 0; iter < 400; iter++)
        {
            int target = iter % 40 == 0 ? 100_000 : r.Next(0, 2_000);
            var sb = new StringBuilder(r.Next(5) == 0 ? "" : "<speak>");
            while (sb.Length < target) sb.Append(SsmlSoup[r.Next(SsmlSoup.Length)]);
            ReadEveryWay(sb.ToString(), $"iter {iter}, {sb.Length} chars", 500);
        }
    }

    // =============================================================== protocol

    [Fact]
    public void Protocol_decode_never_throws_on_garbage()
    {
        var r = new Random(31337);
        string[] fragments =
            ["{", "}", "[", "]", ":", ",", "\"", "\"verb\"", "\"text\"", "\"Speak\"", "\"Render\"", "null", "true",
             "1e999", "-0", "\"\\u0000\"", "\"\\ud800\"", "\\", "{\"verb\":", "\"rate\":", "999999999999",
             "\"offset\":-1", " ", "\n", "\"marks\":\"yes\""];

        for (int iter = 0; iter < 5000; iter++)
        {
            string line;
            switch (iter % 4)
            {
                case 0:
                    // Random BYTES, decoded the way DaemonServer's StreamReader
                    // decodes them, and on odd turns random UTF-16 units, lone
                    // surrogates included. Those made System.Text.Json throw
                    // ArgumentException rather than JsonException (seen here,
                    // 2026-10-04). No socket delivers one today, because UTF-8
                    // decoding substitutes U+FFFD, but TryDecode's contract is
                    // "never throws", not "never throws for today's callers".
                    if (iter % 8 == 0)
                    {
                        var bytes = new byte[r.Next(0, 300)];
                        r.NextBytes(bytes);
                        line = Encoding.UTF8.GetString(bytes);
                    }
                    else
                    {
                        var units = new char[r.Next(1, 120)];
                        for (int i = 0; i < units.Length; i++) units[i] = (char)r.Next(0, 0x10000);
                        line = "{\"verb\":\"Speak\",\"text\":\"" + new string(units) + "\"}";
                    }
                    break;
                case 1:
                    var sb = new StringBuilder();
                    int n = r.Next(1, 60);
                    for (int i = 0; i < n; i++) sb.Append(fragments[r.Next(fragments.Length)]);
                    line = sb.ToString();
                    break;
                case 2:
                    int depth = r.Next(1, 5000);
                    char open = r.Next(2) == 0 ? '[' : '{';
                    line = open == '['
                        ? new string('[', depth) + new string(']', depth)
                        : string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
                    break;
                default:
                    line = "{\"verb\":\"Speak\",\"text\":\"" + new string('x', r.Next(0, 5000)) + "\""
                           + (r.Next(2) == 0 ? ",\"rate\":" + r.Next() : "") + (r.Next(2) == 0 ? "}" : "");
                    break;
            }

            Protocol.TryDecode<Request>(line);
            Protocol.TryDecode<Response>(line);
        }
    }
}

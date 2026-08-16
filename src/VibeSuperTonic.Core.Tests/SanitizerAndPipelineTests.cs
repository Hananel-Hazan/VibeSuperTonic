using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Invisible characters are written as \u escapes throughout. A literal
/// zero-width space in test source is indistinguishable from a typo and does not
/// survive copy/paste — the one place where spelling it out is worth the noise.
/// </summary>
public class TextSanitizerTests
{
    [Theory]
    [InlineData("\u200B", "zero-width space")]
    [InlineData("\u200E", "left-to-right mark")]
    [InlineData("\u202E", "right-to-left override")]
    [InlineData("\u2060", "word joiner")]
    [InlineData("\uFEFF", "byte order mark")]
    [InlineData("\uE000", "private use area")]
    [InlineData("\u0000", "NUL")]
    [InlineData("\u007F", "DEL")]
    [InlineData("\u0085", "C1 control")]
    public void Invisible_characters_are_stripped(string invisible, string description)
    {
        Assert.Equal("ab", TextSanitizer.SanitizeForSynth("a" + invisible + "b"));
        Assert.NotEqual("", description); // keeps the label in the failure output
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData(" ")]
    public void Real_whitespace_survives(string keep)
    {
        Assert.Equal("a" + keep + "b", TextSanitizer.SanitizeForSynth("a" + keep + "b"));
    }

    [Fact]
    public void Clean_text_is_returned_unchanged_with_an_identity_map()
    {
        const string clean = "Nothing invisible in here.";
        string result = TextSanitizer.SanitizeForSynth(clean, out var map);

        Assert.Same(clean, result);
        Assert.True(map.IsIdentity);
    }

    [Fact]
    public void Stripping_shifts_later_offsets_back_to_the_source()
    {
        //                    0  1       2  3  4
        const string source = "a\u200Bbcd";
        string result = TextSanitizer.SanitizeForSynth(source, out var map);

        Assert.Equal("abcd", result);
        Assert.Equal(0, map.ToSource(0));   // 'a'
        Assert.Equal(2, map.ToSource(1));   // 'b' was at 2, not 1
        Assert.Equal(3, map.ToSource(2));
        Assert.Equal(4, map.ToSource(3));
    }

    [Fact]
    public void Many_stripped_characters_accumulate()
    {
        const string source = "\u200Ba\u200Bb\u200Bc";
        string result = TextSanitizer.SanitizeForSynth(source, out var map);

        Assert.Equal("abc", result);
        Assert.Equal(1, map.ToSource(0));
        Assert.Equal(3, map.ToSource(1));
        Assert.Equal(5, map.ToSource(2));
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        Assert.Equal("", TextSanitizer.SanitizeForSynth("", out var map));
        Assert.True(map.IsIdentity);
    }
}

public class TextOffsetMapTests
{
    [Fact]
    public void Identity_answers_from_the_index_and_clamps_at_the_end()
    {
        var map = TextOffsetMap.Identity(5);

        Assert.True(map.IsIdentity);
        Assert.Equal(3, map.ToSource(3));
        Assert.Equal(5, map.ToSource(5));
        Assert.Equal(5, map.ToSource(99));
        Assert.Equal(0, map.ToSource(-1));
    }

    [Fact]
    public void Chaining_two_identities_stays_identity()
    {
        var chained = TextOffsetMap.Chain(TextOffsetMap.Identity(4), TextOffsetMap.Identity(4));
        Assert.True(chained.IsIdentity);
    }

    [Fact]
    public void Chaining_composes_in_the_documented_direction()
    {
        // X -> Y drops one leading char: X[0] came from Y[1], X[1] from Y[2].
        var xToY = TextOffsetMap.FromOrigins(new[] { 1, 2 }, sourceLength: 3);
        // Y -> Z collapses everything onto Z[0].
        var yToZ = TextOffsetMap.FromOrigins(new[] { 0, 0, 0 }, sourceLength: 1);

        var xToZ = TextOffsetMap.Chain(xToY, yToZ);

        Assert.Equal(0, xToZ.ToSource(0));
        Assert.Equal(0, xToZ.ToSource(1));
        Assert.Equal(1, xToZ.SourceLength);
    }

    [Fact]
    public void Span_length_is_measured_in_source_characters()
    {
        // Three rewritten chars all originating at source index 4.
        var map = TextOffsetMap.FromOrigins(new[] { 4, 4, 4 }, sourceLength: 6);

        Assert.Equal(2, map.SourceSpanLength(0, 3));   // 6 - 4
        Assert.Equal(0, map.SourceSpanLength(0, 0));
    }
}

public class SynthTextPipelineTests
{
    [Fact]
    public void Rules_and_sanitizer_compose_into_one_map_back_to_source()
    {
        // A zero-width space ahead of the rule target, so both stages shift
        // offsets and the composition has to survive both.
        const string source = "load \u200Bis 5 kg and go";
        var cfg = new PronunciationsConfig
        {
            Enabled = true,
            Rules = { new PronunciationRule { Enabled = true, Match = "kg", Replace = "kilograms" } },
        };

        string spoken = SynthTextPipeline.Prepare(source, cfg, null, out var map);
        Assert.Equal("load is 5 kilograms and go", spoken);

        int and = spoken.IndexOf("and", StringComparison.Ordinal);
        Assert.Equal("and", source.Substring(map.ToSource(and), 3));

        int kilos = spoken.IndexOf("kilograms", StringComparison.Ordinal);
        Assert.Equal("kg", source.Substring(map.ToSource(kilos), map.SourceSpanLength(kilos, 9)));
    }

    [Fact]
    public void A_null_config_still_sanitizes()
    {
        string spoken = SynthTextPipeline.Prepare("a\u200Bb", null, null, out var map);

        Assert.Equal("ab", spoken);
        Assert.Equal(2, map.ToSource(1));
    }

    [Fact]
    public void Untouched_text_costs_nothing()
    {
        const string source = "Plain text, no rules, nothing invisible.";
        string spoken = SynthTextPipeline.Prepare(source, new PronunciationsConfig(), null, out var map);

        Assert.Same(source, spoken);
        Assert.True(map.IsIdentity);
    }

    [Fact]
    public void Every_word_in_a_realistic_sentence_maps_to_itself()
    {
        // The property that actually matters: for every word the engine will
        // speak, the offset reported back to the client selects that same word in
        // the client's own copy of the text.
        const string source = "It weighs 5 kg, costs 3 kg more, and arrives Mon.";
        var cfg = new PronunciationsConfig
        {
            Enabled = true,
            Rules =
            {
                new PronunciationRule { Enabled = true, Match = "kg",  Replace = "kilograms" },
                new PronunciationRule { Enabled = true, Match = "Mon", Replace = "Monday" },
            },
        };

        string spoken = SynthTextPipeline.Prepare(source, cfg, null, out var map);

        foreach (var (word, start) in Words(spoken))
        {
            int srcStart = map.ToSource(start);
            int srcLen = map.SourceSpanLength(start, word.Length);
            string srcWord = source.Substring(srcStart, srcLen);

            // Either the word is unchanged, or it is the source token a rule
            // rewrote — never a fragment of some later, unrelated word.
            bool ok = srcWord == word
                   || (word == "kilograms" && srcWord == "kg")
                   || (word == "Monday" && srcWord == "Mon");
            Assert.True(ok, $"'{word}' at {start} mapped to '{srcWord}' at {srcStart}");
        }
    }

    private static IEnumerable<(string Word, int Start)> Words(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && !char.IsLetterOrDigit(s[i])) i++;
            if (i >= s.Length) yield break;
            int start = i;
            while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;
            yield return (s.Substring(start, i - start), start);
        }
    }
}

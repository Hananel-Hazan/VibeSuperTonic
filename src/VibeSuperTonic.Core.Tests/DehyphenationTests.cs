using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// A word split across a line break, which is what copying out of a PDF gives
/// you.
///
/// <para><b>Reported 2026-09-06 from the running install.</b> The selection held
/// "Alternative Ground-Truth Configu-\nrations" and the reader said "configyoo"
/// and then "rations" — two words, with the pause between them that a chunk
/// boundary puts there. Nothing in the pipeline joined them: the chunker
/// collapses the newline to a space, so the phonemiser is handed "Configu-" and
/// "rations" and pronounces exactly that.</para>
///
/// <para>It is not a rare shape. Every two-column paper, every justified PDF and
/// every hard-wrapped mail hyphenates at the margin, and the product's whole
/// purpose is reading those aloud.</para>
/// </summary>
public sealed class DehyphenationTests
{
    private static string Join(string text) => Dehyphenator.Join(text, out _);

    [Fact]
    public void A_word_broken_across_a_line_is_put_back_together()
    {
        Assert.Equal(
            "Alternative Ground-Truth Configurations",
            Join("Alternative Ground-Truth Configu-\nrations"));
    }

    [Fact]
    public void Windows_line_endings_break_words_the_same_way()
    {
        Assert.Equal("Configurations", Join("Configu-\r\nrations"));
    }

    /// <summary>The indent a wrapped line carries in a quoted mail or an indented block.</summary>
    [Fact]
    public void The_next_lines_indent_goes_with_the_hyphen()
    {
        Assert.Equal("Configurations", Join("Configu-\n    rations"));
    }

    /// <summary>
    /// A hyphen INSIDE a line is a real hyphen and the word is a real compound.
    /// Joining those would turn "Ground-Truth" into a word nobody wrote.
    /// </summary>
    [Fact]
    public void A_hyphen_that_is_not_at_a_line_break_is_left_alone()
    {
        Assert.Equal("Ground-Truth well-known", Join("Ground-Truth well-known"));
    }

    /// <summary>
    /// The line break is where the evidence runs out. A capital after it is a
    /// new sentence or a proper noun far more often than it is the tail of a
    /// hyphenated word, and "Ground-\nTruth" is a compound that wrapped rather
    /// than a word that split.
    /// </summary>
    [Fact]
    public void A_capital_after_the_break_is_a_compound_that_wrapped()
    {
        Assert.Equal("Ground-\nTruth", Join("Ground-\nTruth"));
    }

    /// <summary>
    /// A blank line is a paragraph, and no word has ever been hyphenated across
    /// one. Joining there would run two paragraphs into a single word.
    /// </summary>
    [Fact]
    public void A_paragraph_break_is_never_a_hyphenation()
    {
        Assert.Equal("ends-\n\nand begins", Join("ends-\n\nand begins"));
    }

    /// <summary>A dash on its own line, an em-dash list, a stray minus.</summary>
    [Fact]
    public void A_hyphen_with_no_word_before_it_is_left_alone()
    {
        Assert.Equal("see\n-\nnext", Join("see\n-\nnext"));
        Assert.Equal("a -\nb", Join("a -\nb"));
    }

    /// <summary>
    /// U+00AD is a SOFT hyphen: invisible, and meaningful only to a renderer
    /// deciding where to break. It arrives in text copied out of a browser and
    /// out of Word, the sanitizer's ranges stop below it, and the phonemiser
    /// treats it as a word boundary — so an invisible character makes an audible
    /// word into two.
    /// </summary>
    [Fact]
    public void A_soft_hyphen_is_invisible_and_must_not_be_audible()
    {
        Assert.Equal("Configurations", Join("Configu­rations"));
    }

    /// <summary>
    /// Text with nothing to join comes back as the same instance, allocating
    /// nothing — this runs on every utterance.
    /// </summary>
    [Fact]
    public void Ordinary_text_is_returned_unchanged_and_uncopied()
    {
        string text = "The quick brown fox jumps over the lazy dog.";
        Assert.Same(text, Dehyphenator.Join(text, out var map));
        Assert.True(map.IsIdentity);
    }

    /// <summary>
    /// The offsets have to survive it, or seek and the reader's highlight land
    /// in the wrong place — R-2, and the reason this is a mapped stage rather
    /// than a string replace.
    /// </summary>
    [Fact]
    public void What_came_out_still_points_at_where_it_came_from()
    {
        const string source = "Configu-\nrations end.";
        string joined = Dehyphenator.Join(source, out var map);

        Assert.Equal("Configurations end.", joined);
        Assert.Equal(0, map.ToSource(0));                      // C
        Assert.Equal(9, map.ToSource("Configu".Length));       // r, past the hyphen and the newline
        Assert.Equal(source.Length, map.SourceLength);
    }

    /// <summary>
    /// And through the whole pipeline, which is where it actually has to work:
    /// the join must happen before the pronunciation rules, or a rule for a word
    /// cannot match one the line break has cut in half.
    /// </summary>
    [Fact]
    public void The_pipeline_joins_before_anything_else_reads_the_words()
    {
        string spoken = SynthTextPipeline.Prepare(
            "Configu-\nrations", pron: null, compiled: null, out var map);

        Assert.Equal("Configurations", spoken);
        Assert.Equal("Configu-\nrations".Length, map.SourceLength);
        Assert.Equal(9, map.ToSource("Configu".Length));
    }
}

using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// R-9's cap. The whole reason this lives in Core is that it is the half of
/// selection capture that can be tested without a display.
/// </summary>
public class TextCapTests
{
    [Fact]
    public void Text_inside_the_limit_is_returned_untouched_and_says_nothing()
    {
        string text = "The sea is everything.";
        string capped = TextCap.Apply(text, 1000, out string? notice);

        Assert.Equal(text, capped);
        Assert.Null(notice);

        // A notice on a selection that was not truncated would be a lie the
        // tray repeats on every press.
    }

    [Fact]
    public void Text_exactly_at_the_limit_is_not_truncated()
    {
        string text = new('a', 100);
        string capped = TextCap.Apply(text, 100, out string? notice);

        Assert.Equal(text, capped);
        Assert.Null(notice);
    }

    [Fact]
    public void Truncation_lands_after_a_sentence_terminator_not_mid_word()
    {
        // Two sentences; the limit falls inside the second.
        string text = "The sea is everything. It covers seven tenths of the globe.";
        string capped = TextCap.Apply(text, 30, out string? notice);

        Assert.Equal("The sea is everything.", capped);
        Assert.NotNull(notice);

        // Cutting at 30 would have produced "...It cove", which sounds like the
        // engine died rather than like a limit being applied.
    }

    [Fact]
    public void A_paragraph_break_counts_as_a_boundary()
    {
        // Selections from PDFs and code comments can run a long way without a
        // full stop. A blank line is a better cut than a hard one.
        string text = "A heading with no full stop\nand then a great deal more text after it";
        string capped = TextCap.Apply(text, 40, out _);

        Assert.Equal("A heading with no full stop\n", capped);
    }

    [Fact]
    public void A_boundary_too_far_back_is_ignored_in_favour_of_a_hard_cut()
    {
        // The pathological case the fraction floor exists for: one full stop
        // early on, then 100 KB of unbroken text. Honouring it would cut a huge
        // selection down to twelve characters and call that a sentence
        // boundary.
        string text = "Short one." + new string('x', 1000);
        string capped = TextCap.Apply(text, 500, out string? notice);

        Assert.Equal(500, capped.Length);
        Assert.NotNull(notice);
    }

    [Fact]
    public void The_notice_reports_both_what_was_read_and_what_was_dropped()
    {
        string text = new('x', 300);
        TextCap.Apply(text, 100, out string? notice);

        Assert.NotNull(notice);
        Assert.Contains("300", notice);   // how much was selected
        Assert.Contains("100", notice);   // how much is being read
        Assert.Contains("200", notice);   // how much was dropped

        // All three, because "selection truncated" tells the user nothing about
        // whether they lost a sentence or a chapter.
    }

    [Fact]
    public void No_terminator_anywhere_still_produces_exactly_the_limit()
    {
        string capped = TextCap.Apply(new string('x', 5000), 1000, out string? notice);

        Assert.Equal(1000, capped.Length);
        Assert.NotNull(notice);
    }

    [Fact]
    public void A_nonsensical_limit_is_rejected_rather_than_silently_clamped()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextCap.Apply("text", 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextCap.Apply("text", -1, out _));
    }
}

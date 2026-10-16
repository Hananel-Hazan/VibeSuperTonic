using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Trap 11 of docs/SPEECHD-PLAN.md, which the gate probe turned from a worry
/// into a measurement: a plain <c>spd-say "hello"</c> reaches a Speech
/// Dispatcher module as <c>&lt;speak&gt;hello&lt;/speak&gt;</c>. Unstripped,
/// the user hears the word "speak".
///
/// <para>The negative half of this file matters more than the positive half.
/// The cheap version of this function — delete everything between an angle
/// bracket and the next one — passes every "it strips SSML" test and quietly
/// eats the brackets out of source code, shell pipelines and mathematics, which
/// are things people highlight and ask to have read aloud. Those cases are here
/// as tests so that the narrowness is a property rather than an accident.</para>
/// </summary>
public class SsmlTests
{
    // ------------------------------------------------------------ documents

    [Fact]
    public void The_wrapper_speechd_adds_is_removed()
    {
        Assert.Equal("hello", Ssml.Strip("<speak>hello</speak>"));
    }

    [Fact]
    public void Attributes_do_not_survive_as_text()
    {
        string ssml = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" "
                      + "xml:lang=\"en-US\">Good morning.</speak>";
        Assert.Equal("Good morning.", Ssml.Strip(ssml));
    }

    [Fact]
    public void A_self_closing_tag_leaves_nothing_behind()
    {
        Assert.Equal("one two", Ssml.Strip("<speak>one<break time=\"500ms\"/>two</speak>"));
    }

    /// <summary>
    /// Two sentences, not one word. A tag is an element boundary, so it becomes
    /// a space — "OneTwo" would be a single unpronounceable token.
    /// </summary>
    [Fact]
    public void Element_boundaries_become_spaces_not_nothing()
    {
        Assert.Equal("One. Two.", Ssml.Strip("<speak><s>One.</s><s>Two.</s></speak>"));
    }

    [Fact]
    public void Nested_markup_goes_all_the_way_down()
    {
        string ssml = "<speak><p><s>Please <emphasis level=\"strong\">stop</emphasis> here.</s></p></speak>";
        Assert.Equal("Please stop here.", Ssml.Strip(ssml));
    }

    [Fact]
    public void A_mark_the_protocol_reserves_but_we_do_not_emit_is_still_removed()
    {
        Assert.Equal("before after", Ssml.Strip("<speak>before<mark name=\"m1\"/>after</speak>"));
    }

    /// <summary>An attribute value is allowed to contain the closing bracket.</summary>
    [Fact]
    public void A_bracket_inside_a_quoted_attribute_does_not_end_the_tag()
    {
        Assert.Equal("kept", Ssml.Strip("<speak><mark name=\"a>b\"/>kept</speak>"));
    }

    // ------------------------------------------------------------- entities

    [Fact]
    public void The_five_named_entities_decode()
    {
        Assert.Equal("a & b < c > d \" e ' f",
            Ssml.Strip("<speak>a &amp; b &lt; c &gt; d &quot; e &apos; f</speak>"));
    }

    [Fact]
    public void Numeric_references_decode_in_both_bases()
    {
        Assert.Equal("A A", Ssml.Strip("<speak>&#65; &#x41;</speak>"));
    }

    /// <summary>
    /// THE ORDERING BUG THIS FUNCTION CAN HAVE. A document whose text contains
    /// an escaped tag means those characters. Decoding before stripping would
    /// turn the content into markup and then delete it, so the sentence would
    /// lose the very thing it was quoting.
    /// </summary>
    [Fact]
    public void An_escaped_tag_is_content_and_survives()
    {
        Assert.Equal("the tag <speak> means", Ssml.Strip("<speak>the tag &lt;speak&gt; means</speak>"));
    }

    [Fact]
    public void A_lone_ampersand_stays_an_ampersand()
    {
        Assert.Equal("Marks & Spencer", Ssml.Strip("<speak>Marks & Spencer</speak>"));
    }

    [Fact]
    public void Something_that_is_not_a_reference_is_left_alone()
    {
        Assert.Equal("read p&l; then stop", Ssml.Strip("<speak>read p&l; then stop</speak>"));
    }

    // -------------------------------------------------- not an SSML document

    [Theory]
    [InlineData("a < b and b > c")]
    [InlineData("if (x<y) { return; }")]
    [InlineData("cat file | grep -v '<none>' > out.txt")]
    [InlineData("<html><body>Hello</body></html>")]
    [InlineData("Use <Ctrl> to cancel")]
    [InlineData("plain text with no markup at all")]
    public void Text_that_is_not_an_ssml_document_is_returned_untouched(string text)
    {
        Assert.Equal(text, Ssml.Strip(text));
        Assert.False(Ssml.IsDocument(text));
    }

    /// <summary>
    /// A tag whose name merely begins with "speak" is a different element, and a
    /// prefix test would take this document apart on the strength of it.
    /// </summary>
    [Fact]
    public void Speaker_is_not_speak()
    {
        Assert.False(Ssml.IsDocument("<speaker>Bob</speaker>"));
        Assert.Equal("<speaker>Bob</speaker>", Ssml.Strip("<speaker>Bob</speaker>"));
    }

    [Fact]
    public void Leading_whitespace_does_not_hide_the_document()
    {
        Assert.True(Ssml.IsDocument("\n  <speak>hi</speak>"));
        Assert.Equal("hi", Ssml.Strip("\n  <speak>hi</speak>"));
    }

    [Fact]
    public void A_bare_speak_element_with_no_attributes_or_content_is_still_a_document()
    {
        Assert.True(Ssml.IsDocument("<speak/>"));
        Assert.Equal("", Ssml.Strip("<speak/>"));
    }

    // ------------------------------------------------------- malformed input

    /// <summary>
    /// NOT WELL-FORMED, AND IT ARRIVES ANYWAY — a client that forgot to escape.
    /// "Delete from &lt; to the next &gt;" would swallow <c>" b&lt;/speak"</c>
    /// and the user would hear the single letter "a" where a sentence was.
    /// </summary>
    [Fact]
    public void An_unescaped_less_than_inside_a_document_does_not_eat_the_rest()
    {
        Assert.Equal("a < b", Ssml.Strip("<speak>a < b</speak>"));
    }

    [Fact]
    public void An_unterminated_tag_is_spoken_rather_than_swallowing_the_utterance()
    {
        Assert.Equal("the rest <b of it", Ssml.Strip("<speak>the rest <b of it"));
    }

    [Fact]
    public void Whitespace_only_content_strips_to_nothing_rather_than_to_spaces()
    {
        Assert.Equal("", Ssml.Strip("<speak>   \n\t  </speak>"));
    }

    [Fact]
    public void Null_and_empty_are_the_empty_string()
    {
        Assert.Equal("", Ssml.Strip(null));
        Assert.Equal("", Ssml.Strip(""));
        Assert.False(Ssml.IsDocument(null));
    }
}

using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The engine-qualified voice id P4 introduces, and the back-compatibility that
/// is the whole reason it is optional.
///
/// <para><b>What is being protected.</b> Every <c>settings.json</c> written
/// before P4 carries a bare <c>DefaultVoice</c> — "M1" — and the routing rule
/// settled in P3 says a bare id is a Piper voice exactly when it names an
/// installed one. That rule is not replaced here. The prefix is added so a
/// catalog of 43 ids and a settings file can say which engine they mean without
/// consulting the filesystem, and every test below that parses a bare id is
/// pinning the promise that adding it broke nothing.</para>
/// </summary>
public class VoiceIdTests
{
    [Theory]
    [InlineData("M1")]
    [InlineData("F5")]
    [InlineData("en_US-ljspeech-high")]
    public void A_bare_id_names_no_engine_and_survives_unchanged(string id)
    {
        var v = VoiceId.Parse(id);
        Assert.Equal(VoiceEngine.Unspecified, v.Engine);
        Assert.Equal(id, v.Bare);
        Assert.Equal(id, v.ToString());
        Assert.False(v.IsQualified);
    }

    [Fact]
    public void A_piper_prefix_names_piper_and_the_bare_id_is_what_reaches_the_store()
    {
        var v = VoiceId.Parse("piper:en_US-ljspeech-high");
        Assert.Equal(VoiceEngine.Piper, v.Engine);
        Assert.Equal("en_US-ljspeech-high", v.Bare);
        Assert.True(v.IsQualified);
    }

    [Fact]
    public void A_supertonic_prefix_names_supertonic()
    {
        var v = VoiceId.Parse("supertonic:M1");
        Assert.Equal(VoiceEngine.Supertonic, v.Engine);
        Assert.Equal("M1", v.Bare);
    }

    [Theory]
    [InlineData("PIPER:en_US-ljspeech-high", VoiceEngine.Piper, "en_US-ljspeech-high")]
    [InlineData("Supertonic:M1", VoiceEngine.Supertonic, "M1")]
    public void The_prefix_is_case_insensitive_because_people_type_it(
        string input, VoiceEngine engine, string bare)
    {
        var v = VoiceId.Parse(input);
        Assert.Equal(engine, v.Engine);
        Assert.Equal(bare, v.Bare);
    }

    [Theory]
    [InlineData("  piper:en_US-ljspeech-high  ", "en_US-ljspeech-high")]
    [InlineData("piper: en_US-ljspeech-high", "en_US-ljspeech-high")]
    public void Whitespace_around_a_hand_edited_settings_value_is_not_part_of_the_id(
        string input, string bare)
    {
        Assert.Equal(bare, VoiceId.Parse(input).Bare);
    }

    // ------------------------------------------------ the deliberate non-failures

    [Theory]
    [InlineData("pipper:x")]
    [InlineData("nonsense:M1")]
    public void An_unknown_prefix_is_part_of_the_id_rather_than_a_failure(string input)
    {
        // A typo in a settings file becomes a voice that is not found — a
        // sentence the user can act on — instead of a daemon that will not
        // start. Colons occur in neither Supertonic styles nor upstream Piper
        // keys, so nothing legitimate is swallowed by this.
        var v = VoiceId.Parse(input);
        Assert.Equal(VoiceEngine.Unspecified, v.Engine);
        Assert.Equal(input, v.Bare);
    }

    [Theory]
    [InlineData("piper:")]
    [InlineData("supertonic:   ")]
    public void A_prefix_with_no_voice_after_it_names_no_voice(string input)
    {
        // It cannot be resolved as an engine, because there is nothing to
        // resolve. Keeping the whole string as the bare id means the lookup
        // fails naming what the user actually wrote.
        var v = VoiceId.Parse(input);
        Assert.Equal(VoiceEngine.Unspecified, v.Engine);
        Assert.Equal(input.Trim(), v.Bare);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_is_absent_rather_than_an_exception(string? input)
    {
        Assert.False(VoiceId.TryParse(input, out _));
    }

    // --------------------------------------------------------------- round trip

    [Theory]
    [InlineData("M1")]
    [InlineData("piper:en_US-ljspeech-high")]
    [InlineData("supertonic:M1")]
    public void ToString_round_trips_through_Parse(string canonical)
    {
        Assert.Equal(canonical, VoiceId.Parse(canonical).ToString());
    }

    [Fact]
    public void A_constructed_piper_id_formats_qualified()
    {
        Assert.Equal("piper:de_DE-thorsten-high", VoiceId.ForPiper("de_DE-thorsten-high").ToString());
        Assert.Equal("supertonic:M1", VoiceId.ForSupertonic("M1").ToString());
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public void Only_an_explicit_supertonic_id_rules_out_asking_the_piper_store()
    {
        // The store is still the authority on a bare id. What this saves is a
        // filesystem question about an id that has already answered it.
        Assert.True(VoiceId.Parse("M1").MayBePiper);
        Assert.True(VoiceId.Parse("piper:en_US-ljspeech-high").MayBePiper);
        Assert.False(VoiceId.Parse("supertonic:M1").MayBePiper);
    }
}

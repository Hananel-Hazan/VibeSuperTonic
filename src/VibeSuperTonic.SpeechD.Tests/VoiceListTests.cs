using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// The voice list and the mapping onto speech-dispatcher's vocabulary — S3.
///
/// <para><b>Every rule in here was measured against speech-dispatcher 0.12.1
/// rather than read</b>, with a probe module that logged what the server sent
/// when a client selected a voice each of the ways it can. The two that shape
/// this file: selecting by name sends <c>synthesis_voice=&lt;name&gt;</c> and no
/// <c>voice</c>, and everything else sends <c>voice=male1</c> with
/// <c>synthesis_voice=NULL</c> — so the symbolic form is the ordinary path, not
/// the corner case.</para>
/// </summary>
public class VoiceListTests
{
    /// <summary>The ten styles Supertonic ships, as the store lists them — ordinal, so F before M.</summary>
    private static readonly string[] TenStyles =
        { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" };

    private static readonly string[] ThreeLanguages = { "en", "de", "ja" };

    // --------------------------------------------------------------- building

    [Fact]
    public void Supertonic_is_listed_once_per_style_per_language()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        Assert.Equal(30, rows.Count);
        Assert.Equal(3, rows.Count(r => r.Variant == "M1"));
        Assert.Equal(10, rows.Count(r => r.Language == "de"));
    }

    /// <summary>
    /// The name has to identify a style AND a language, because no id in this
    /// product carries both and it is what comes back as <c>synthesis_voice</c>.
    /// </summary>
    [Fact]
    public void A_supertonic_row_names_its_style_and_its_language()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        var row = rows.Single(r => r.Name == "supertonic-M1-de");
        Assert.Equal("de", row.Language);
        Assert.Equal("supertonic:M1", row.RenderVoice);
        Assert.Equal("de", row.RenderLanguage);
    }

    /// <summary>
    /// Supertonic's own style names are the only statement of gender anywhere in
    /// this product, and they are what make speechd's MALE1/FEMALE1 answerable.
    /// </summary>
    [Fact]
    public void Style_names_carry_the_gender_and_the_rank()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), new[] { "en" });

        Assert.Equal(VoiceGender.Male, rows.Single(r => r.Variant == "M1").Gender);
        Assert.Equal(1, rows.Single(r => r.Variant == "M1").Rank);
        Assert.Equal(VoiceGender.Female, rows.Single(r => r.Variant == "F2").Gender);
        Assert.Equal(2, rows.Single(r => r.Variant == "F2").Rank);
    }

    /// <summary>
    /// A style file is a file on disk and a user can put another one there. It is
    /// listed — it works — but nothing claims a gender for it.
    /// </summary>
    [Fact]
    public void A_style_that_is_not_named_like_supertonics_has_no_gender_claimed_for_it()
    {
        var rows = VoiceList.Build(new[] { "custom" }, Array.Empty<string>(), new[] { "en" });

        Assert.Equal(VoiceGender.Unknown, Assert.Single(rows).Gender);
    }

    /// <summary>
    /// RANK IS POSITION AMONG WHAT IS INSTALLED, NOT THE DIGIT IN THE NAME. A
    /// machine with only M3 on it has a perfectly good male voice, and `male1`
    /// has to find it.
    /// </summary>
    [Fact]
    public void Rank_counts_installed_styles_rather_than_reading_the_digit()
    {
        var rows = VoiceList.Build(new[] { "M3", "M4" }, Array.Empty<string>(), new[] { "en" });

        Assert.Equal(1, rows.Single(r => r.Variant == "M3").Rank);
        Assert.Equal(2, rows.Single(r => r.Variant == "M4").Rank);

        var pick = VoiceList.Resolve(rows, null, "male1", "en");
        Assert.Equal("supertonic:M3", pick!.RenderVoice);
    }

    /// <summary>
    /// From the id, not the catalog: every catalog id is prefixed by its language
    /// code — checked across all 43 — and the id is the only source that also
    /// covers a voice installed by hand, which the catalog does not describe.
    /// </summary>
    [Fact]
    public void A_piper_voices_language_comes_from_its_own_id()
    {
        var rows = VoiceList.Build(
            Array.Empty<string>(), new[] { "en_US-lessac-medium", "de_DE-thorsten-low" },
            Array.Empty<string>());

        Assert.Equal("en-us", rows.Single(r => r.Name == "en_US-lessac-medium").Language);
        Assert.Equal("de-de", rows.Single(r => r.Name == "de_DE-thorsten-low").Language);
        Assert.Equal("medium", rows.Single(r => r.Name == "en_US-lessac-medium").Variant);
    }

    /// <summary>
    /// The model IS the language for Piper, so sending one would state a second
    /// and possibly conflicting opinion about the same utterance.
    /// </summary>
    [Fact]
    public void A_piper_row_carries_no_render_language()
    {
        var rows = VoiceList.Build(
            Array.Empty<string>(), new[] { "en_US-lessac-medium" }, Array.Empty<string>());

        var row = Assert.Single(rows);
        Assert.Null(row.RenderLanguage);
        Assert.Equal("piper:en_US-lessac-medium", row.RenderVoice);
        Assert.Equal(VoiceGender.Unknown, row.Gender);
    }

    // -------------------------------------------------------------- resolving

    /// <summary>
    /// A name is the only request that names a voice rather than describing one,
    /// and it beats the description sent beside it.
    /// </summary>
    [Fact]
    public void An_exact_name_wins_over_the_language_and_the_symbolic_voice()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        var pick = VoiceList.Resolve(rows, "supertonic-M2-ja", "female1", "de");

        Assert.Equal("supertonic:M2", pick!.RenderVoice);
        Assert.Equal("ja", pick.RenderLanguage);
    }

    [Fact]
    public void A_symbolic_voice_and_a_language_pick_the_matching_style()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        var pick = VoiceList.Resolve(rows, null, "female1", "de");

        Assert.Equal("supertonic:F1", pick!.RenderVoice);
        Assert.Equal("de", pick.RenderLanguage);
    }

    /// <summary>
    /// speechd sends <c>en-us</c> and <c>pt-br</c>; Supertonic's codes are bare
    /// and Piper's are <c>en_US</c>. Comparing them without normalising is how a
    /// machine with a German voice reports that it has none.
    /// </summary>
    [Theory]
    [InlineData("en-us")]
    [InlineData("en_US")]
    [InlineData("EN")]
    public void A_regional_language_still_finds_the_voice_for_its_language(string asked)
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        var pick = VoiceList.Resolve(rows, null, "male1", asked);

        Assert.Equal("supertonic:M1", pick!.RenderVoice);
        Assert.Equal("en", pick.RenderLanguage);
    }

    /// <summary>
    /// An exact regional match is preferred over the bare language, so a machine
    /// with both en_GB and en_US Piper voices honours the one that was asked for.
    /// </summary>
    [Fact]
    public void An_exact_region_beats_a_merely_related_one()
    {
        var rows = VoiceList.Build(
            Array.Empty<string>(),
            new[] { "en_GB-alan-medium", "en_US-lessac-medium" },
            Array.Empty<string>());

        var pick = VoiceList.Resolve(rows, null, "male1", "en-us");

        Assert.Equal("piper:en_US-lessac-medium", pick!.RenderVoice);
    }

    /// <summary>
    /// `male3` on a machine with two male styles is the second one, not nothing.
    /// </summary>
    [Fact]
    public void A_rank_above_what_is_installed_falls_to_the_highest_there_is()
    {
        var rows = VoiceList.Build(new[] { "M1", "M2" }, Array.Empty<string>(), new[] { "en" });

        var pick = VoiceList.Resolve(rows, null, "male3", "en");

        Assert.Equal("supertonic:M2", pick!.RenderVoice);
    }

    /// <summary>
    /// A Piper voice states no gender. Excluding it from a `male1` request would
    /// answer "no voice" on a machine whose only installed voice is a good one,
    /// and the caller's alternative to null is the espeak buzz.
    /// </summary>
    [Fact]
    public void A_gendered_request_still_finds_a_voice_whose_gender_is_unknown()
    {
        var rows = VoiceList.Build(
            Array.Empty<string>(), new[] { "de_DE-thorsten-low" }, Array.Empty<string>());

        var pick = VoiceList.Resolve(rows, null, "female1", "de");

        Assert.Equal("piper:de_DE-thorsten-low", pick!.RenderVoice);
    }

    /// <summary>
    /// But a voice whose gender IS known and matches is preferred over one that
    /// merely does not contradict the request.
    /// </summary>
    [Fact]
    public void A_known_gender_is_preferred_over_an_unknown_one()
    {
        var rows = VoiceList.Build(TenStyles, new[] { "en_US-lessac-medium" }, new[] { "en" });

        var pick = VoiceList.Resolve(rows, null, "female1", "en");

        Assert.Equal("supertonic:F1", pick!.RenderVoice);
    }

    /// <summary>
    /// This product ships no child voice, and answering with an adult of the
    /// right gender is the never-go-silent rule applied to voice selection.
    /// </summary>
    [Theory]
    [InlineData("child_female", "supertonic:F1")]
    [InlineData("child_male", "supertonic:M1")]
    public void A_child_voice_is_answered_by_its_gender(string symbolic, string expected)
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), new[] { "en" });

        Assert.Equal(expected, VoiceList.Resolve(rows, null, symbolic, "en")!.RenderVoice);
    }

    /// <summary>
    /// MEASURED: speechd does NOT check synthesis_voice against the list it was
    /// given — `-y no-such-voice` arrives verbatim. A stale setting in a screen
    /// reader must not become a refusal, because the caller's fallback for a
    /// refusal is espeak, so one stale string would replace the neural voice
    /// everywhere with no error anyone sees.
    /// </summary>
    [Fact]
    public void An_uninstalled_name_falls_back_to_the_symbolic_request()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        var pick = VoiceList.Resolve(rows, "no-such-voice", "male1", "ja");

        Assert.Equal("supertonic:M1", pick!.RenderVoice);
        Assert.Equal("ja", pick.RenderLanguage);
    }

    /// <summary>
    /// Nothing installed is not an error either — null means "speak with whatever
    /// the daemon is configured for", which is a working voice.
    /// </summary>
    [Fact]
    public void Nothing_installed_resolves_to_the_daemons_own_default()
    {
        Assert.Null(VoiceList.Resolve(Array.Empty<SpeechdVoice>(), "anything", "male1", "en"));
    }

    /// <summary>
    /// A language nothing covers is the same case: the daemon's default speaks
    /// it badly, and speaking badly beats not speaking.
    /// </summary>
    [Fact]
    public void A_language_nothing_covers_resolves_to_the_daemons_own_default()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), new[] { "en" });

        Assert.Null(VoiceList.Resolve(rows, null, "male1", "th"));
    }


    /// <summary>
    /// MEASURED, AND IT CORRECTED THIS DESIGN. speechd sends `language=en-us` on
    /// every SET whether or not anybody chose it — it is the session default, not
    /// a request. Selecting on it alone would have every client that never picked
    /// a voice silently replace the user's own configured default with whichever
    /// row sorted first.
    /// </summary>
    [Fact]
    public void A_language_with_no_voice_request_beside_it_selects_nothing()
    {
        var rows = VoiceList.Build(TenStyles, Array.Empty<string>(), ThreeLanguages);

        Assert.Null(VoiceList.Resolve(rows, null, null, "en"));
        Assert.Null(VoiceList.Resolve(rows, "NULL-ish-nonsense", "NULL", "de"));
    }

    [Theory]
    [InlineData("en_US", "en-us")]
    [InlineData(" EN-us ", "en-us")]
    [InlineData(null, "")]
    public void Languages_are_normalised_the_way_speechd_spells_them(string? raw, string expected)
    {
        Assert.Equal(expected, VoiceList.NormaliseLanguage(raw));
    }

    /// <summary>
    /// speechd's eight, and nothing else. An unrecognised value is not a voice
    /// request and must not be treated as one.
    /// </summary>
    [Theory]
    [InlineData("NULL")]
    [InlineData("")]
    [InlineData("male4")]
    [InlineData("robot")]
    public void An_unrecognised_symbolic_name_is_not_a_voice_request(string symbolic)
    {
        Assert.Null(VoiceList.ParseSymbolic(symbolic));
    }
}

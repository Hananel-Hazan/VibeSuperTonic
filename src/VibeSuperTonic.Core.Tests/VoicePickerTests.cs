using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The voice control, as a cascade.
///
/// <para>Written 2026-08-28 from a real install that had all ten Supertonic
/// styles on disk and offered two rows — "M4" and "supertonic:M4" — then rewritten
/// the same day, because the answer to that (expand everything) does not survive
/// the English catalog: libritts and libritts_r carry 904 speakers each and vctk
/// another 109, so a flat list runs past eighteen hundred rows.</para>
/// </summary>
public sealed class VoicePickerTests
{
    private static readonly string[] Styles =
        ["F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5"];

    private static VoiceEntry Supertonic(string current = "M4") => new(
        Id: $"supertonic:{current}", Engine: "supertonic", Name: "Supertonic",
        Installed: true, IsDefault: true, Language: "31 languages",
        Speakers: Styles.Length, SpeakerNames: Styles);

    private static VoiceEntry Piper(
        string id, string lang, string language, int speakers = 1, string licenceClass = "public") => new(
        Id: $"piper:{id}", Engine: "piper", Name: id, Installed: true, IsDefault: false,
        LanguageCode: lang, Language: language, Licence: "public domain", LicenceClass: licenceClass,
        Speakers: speakers,
        SpeakerNames: speakers > 1 ? Enumerable.Range(0, speakers).Select(i => $"spk{i}").ToList() : null);

    private static readonly VoiceEntry[] Install =
    [
        Supertonic(),
        Piper("en_US-ljspeech-high", "en_US", "English (United States)"),
        Piper("en_US-libritts-high", "en_US", "English (United States)", speakers: 904, licenceClass: "by"),
        Piper("en_US-ryan-medium", "en_US", "English (United States)", licenceClass: "nc"),
        Piper("en_GB-cori-high", "en_GB", "English (Great Britain)"),
        Piper("de_DE-thorsten-low", "de_DE", "German (Germany)"),
    ];

    [Fact]
    public void The_engine_level_lists_each_engine_once()
    {
        Assert.Equal(["supertonic", "piper"], VoicePicker.Engines(Install).Select(r => r.Value));
    }

    /// <summary>Supertonic's styles, all ten, which is what the flat list lost.</summary>
    [Fact]
    public void Supertonic_offers_every_style_and_no_language_level()
    {
        // WITH a language code on the entry, deliberately. Today's daemon sends
        // none, so asserting emptiness against the real shape passes whether the
        // rule exists or not — found by sabotage, which removed the rule and
        // watched this test stay green. Supertonic has no language LEVEL because
        // it is one model set that takes its language per utterance, and that
        // has to hold whatever the entry happens to carry.
        VoiceEntry[] withCode = [Supertonic() with { LanguageCode = "en", Language = "English" }];
        Assert.Empty(VoicePicker.Languages(withCode, "supertonic"));
        Assert.Empty(VoicePicker.Languages(Install, "supertonic"));

        Assert.Equal(
            Styles.Select(s => $"supertonic:{s}"),
            VoicePicker.Voices(Install, "supertonic", "").Select(r => r.Value));
    }

    [Fact]
    public void Piper_languages_are_listed_by_their_names()
    {
        var rows = VoicePicker.Languages(Install, "piper");

        Assert.Equal(["en_GB", "en_US", "de_DE"], rows.Select(r => r.Value));
        Assert.Equal("English (Great Britain)", rows[0].Label);
    }

    /// <summary>The point of the cascade: one language at a time, never all of them.</summary>
    [Fact]
    public void A_language_narrows_the_voices_to_its_own()
    {
        var rows = VoicePicker.Voices(Install, "piper", "en_US");

        Assert.Equal(
            ["piper:en_US-ljspeech-high", "piper:en_US-libritts-high", "piper:en_US-ryan-medium"],
            rows.Select(r => r.Value));
        Assert.Single(VoicePicker.Voices(Install, "piper", "de_DE"));
    }

    /// <summary>
    /// A 904-speaker voice is ONE row here. It was 904 in the flat list, which
    /// is the reason this class was rewritten.
    /// </summary>
    [Fact]
    public void A_multi_speaker_voice_is_one_row_with_its_speakers_below()
    {
        var voices = VoicePicker.Voices(Install, "piper", "en_US");
        Assert.Equal(3, voices.Count);

        var speakers = VoicePicker.Speakers(Install, "piper:en_US-libritts-high");
        Assert.Equal(904, speakers.Count);
        Assert.Equal("0", speakers[0].Value);
        Assert.Equal("0 — spk0", speakers[0].Label);
    }

    [Fact]
    public void A_single_speaker_voice_has_no_speaker_level()
    {
        Assert.Empty(VoicePicker.Speakers(Install, "piper:en_US-ljspeech-high"));
        Assert.Empty(VoicePicker.Speakers(Install, "supertonic:M4"));
    }

    /// <summary>
    /// The licence is on the row a user chooses ON, because five of the English
    /// voices are NonCommercial and that is a choice, not a detail.
    /// </summary>
    [Fact]
    public void Every_voice_row_states_its_licence()
    {
        var rows = VoicePicker.Voices(Install, "piper", "en_US");

        Assert.Contains("NonCommercial", rows.Single(r => r.Value.Contains("ryan")).Label);
        Assert.Contains("CC BY", rows.Single(r => r.Value.Contains("libritts")).Label);
        Assert.Contains("904 speakers", rows.Single(r => r.Value.Contains("libritts")).Label);
    }

    /// <summary>The other half of the original bug: one voice, listed twice.</summary>
    [Fact]
    public void A_legacy_bare_style_locates_the_qualified_supertonic_row()
    {
        var at = VoicePicker.Locate(Install, "M4");

        Assert.Equal("supertonic", at.Engine);
        Assert.Equal("supertonic:M4", at.Voice);
        Assert.Equal("", at.Language);
        Assert.Null(at.Speaker);
    }

    [Fact]
    public void A_piper_voice_locates_its_engine_language_and_speaker()
    {
        var at = VoicePicker.Locate(Install, "piper:en_US-libritts-high#57");

        Assert.Equal("piper", at.Engine);
        Assert.Equal("en_US", at.Language);
        Assert.Equal("piper:en_US-libritts-high", at.Voice);
        Assert.Equal(57, at.Speaker);
    }

    [Fact]
    public void Composing_puts_the_speaker_back_on_the_id()
    {
        Assert.Equal("piper:en_US-libritts-high#57",
            VoicePicker.Compose("piper:en_US-libritts-high", 57));
        Assert.Equal("piper:en_US-ljspeech-high",
            VoicePicker.Compose("piper:en_US-ljspeech-high", null));

        // Supertonic's style IS the voice, so a stray speaker cannot ride along.
        Assert.Equal("supertonic:M4", VoicePicker.Compose("supertonic:M4", 3));
    }

    [Fact]
    public void A_daemon_that_did_not_answer_offers_nothing_and_claims_nothing()
    {
        Assert.Empty(VoicePicker.Engines(null));
        Assert.Empty(VoicePicker.Voices(null, "piper", "en_US"));
        Assert.Equal("supertonic", VoicePicker.Locate(null, "M4").Engine);
    }
}

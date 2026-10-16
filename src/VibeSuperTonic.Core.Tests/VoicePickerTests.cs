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

    /// <summary>
    /// Choose speaker 6, press Use, watch it snap back to 0 — reported from a
    /// real install on 2026-08-28. The row's dropdown is seeded from the row's
    /// id, so the id has to carry the speaker that is actually configured.
    /// </summary>
    [Fact]
    public void A_row_shows_the_speaker_its_id_carries()
    {
        var entry = Piper("en_US-libritts-high", "en_US", "English (United States)", speakers: 904);

        Assert.Equal(0, VoicePicker.SpeakerIndex(entry));
        Assert.Equal(6, VoicePicker.SpeakerIndex(entry with { Id = entry.Id + "#6" }));
    }

    /// <summary>
    /// A settings file is hand-editable, so it can name a speaker the voice does
    /// not have. Selecting nothing at all would be a dropdown that looks broken.
    /// </summary>
    [Fact]
    public void A_speaker_beyond_the_voices_range_is_clamped()
    {
        var entry = Piper("en_GB-aru-medium", "en_GB", "English (Great Britain)", speakers: 12);

        Assert.Equal(11, VoicePicker.SpeakerIndex(entry with { Id = entry.Id + "#950" }));
        Assert.Equal(0, VoicePicker.SpeakerIndex(null));
    }

    [Fact]
    public void A_daemon_that_did_not_answer_offers_nothing_and_claims_nothing()
    {
        Assert.Empty(VoicePicker.Engines(null));
        Assert.Empty(VoicePicker.Voices(null, "piper", "en_US"));
        Assert.Equal("supertonic", VoicePicker.Locate(null, "M4").Engine);
    }

    // ------------------------------------------------- what the control shows

    /// <summary>
    /// A ROW RENDERS AS ITS LABEL, and this is not a formatting preference.
    ///
    /// <para>Reported 2026-08-30: the Tune tab's dropdowns read
    /// <c>PickerRow { Value = All, Label = All voices }</c> — a record's
    /// compiler-generated ToString, which is what a control displays when
    /// nothing tells it otherwise. Every picker on that tab showed the
    /// debugger's view of the object while holding a perfectly good label
    /// nothing was reading.</para>
    ///
    /// <para>Nothing here could have caught it: every test asserted Values and
    /// Labels, which were right. The missing assertion was about the ONE
    /// property the toolkit actually uses.</para>
    /// </summary>
    [Fact]
    public void A_row_displays_its_label_and_nothing_of_its_own_shape()
    {
        var row = new PickerRow("All", "All voices");

        Assert.Equal("All voices", row.ToString());
        Assert.DoesNotContain("PickerRow", row.ToString());
        Assert.DoesNotContain("Value", row.ToString());
    }

    /// <summary>
    /// And every row the picker actually builds, at every level — because the
    /// bug was in the type, so one row proving it is one row, and the levels are
    /// what a user sees.
    /// </summary>
    [Fact]
    public void Every_level_renders_as_something_a_person_would_read()
    {
        IReadOnlyList<PickerRow>[] levels =
        [
            VoicePicker.Engines(Install),
            VoicePicker.Languages(Install, "piper"),
            VoicePicker.Voices(Install, "piper", "en_US"),
            VoicePicker.Voices(Install, "supertonic", ""),
            VoicePicker.Speakers(Install, "piper:en_US-libritts-high"),
        ];

        foreach (var level in levels)
        {
            Assert.NotEmpty(level);
            foreach (var row in level)
            {
                Assert.Equal(row.Label, row.ToString());
                Assert.DoesNotContain("PickerRow", row.ToString());
            }
        }
    }
}

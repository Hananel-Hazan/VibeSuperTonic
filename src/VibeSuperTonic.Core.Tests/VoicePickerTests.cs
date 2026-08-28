using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The rows the Tune tab's voice control offers.
///
/// <para>Written 2026-08-28 from a real install that had all ten Supertonic
/// styles on disk and offered two rows: "M4" and "supertonic:M4". One voice,
/// named twice, with the other nine unreachable.</para>
/// </summary>
public sealed class VoicePickerTests
{
    private static readonly string[] Styles =
        ["F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5"];

    private static VoiceEntry Supertonic(string current = "M4") => new(
        Id: $"supertonic:{current}", Engine: "supertonic", Name: "Supertonic",
        Installed: true, IsDefault: true, Language: "31 languages",
        Speakers: Styles.Length, SpeakerNames: Styles);

    private static VoiceEntry Piper(string id) => new(
        Id: $"piper:{id}", Engine: "piper", Name: id, Installed: true, IsDefault: false);

    /// <summary>The bug: ten styles installed, one offered.</summary>
    [Fact]
    public void Every_supertonic_style_is_offered()
    {
        var (ids, _) = VoicePicker.Rows([Supertonic()], "M4");

        Assert.Equal(Styles.Select(s => $"supertonic:{s}"), ids);
    }

    /// <summary>The other half of it: the same voice listed twice.</summary>
    [Fact]
    public void A_legacy_bare_style_selects_the_qualified_row_instead_of_adding_one()
    {
        var (ids, selected) = VoicePicker.Rows([Supertonic()], "M4");

        Assert.Equal("supertonic:M4", selected);
        Assert.DoesNotContain("M4", ids);
        Assert.Equal(Styles.Length, ids.Count);
    }

    [Fact]
    public void An_already_qualified_voice_selects_its_row()
    {
        var (ids, selected) = VoicePicker.Rows([Supertonic("F2")], "supertonic:F2");

        Assert.Equal("supertonic:F2", selected);
        Assert.Equal(Styles.Length, ids.Count);
    }

    /// <summary>
    /// A voice the settings name and the disk does not have is still shown, and
    /// first — that control is the only place the mismatch is visible.
    /// </summary>
    [Fact]
    public void A_voice_that_is_not_installed_is_still_listed_first()
    {
        var (ids, selected) = VoicePicker.Rows([Supertonic()], "piper:de_DE-thorsten-low");

        Assert.Equal("piper:de_DE-thorsten-low", ids[0]);
        Assert.Equal("piper:de_DE-thorsten-low", selected);
    }

    /// <summary>
    /// A single-speaker Piper voice is one row, unchanged. Expanding is for
    /// entries that actually carry speakers.
    /// </summary>
    [Fact]
    public void A_single_speaker_piper_voice_is_one_row()
    {
        var (ids, _) = VoicePicker.Rows([Piper("en_GB-cori-high")], "piper:en_GB-cori-high");

        Assert.Equal(["piper:en_GB-cori-high"], ids);
    }

    /// <summary>
    /// A multi-speaker Piper voice expands by INDEX, because index N is sid N —
    /// its speaker names are labels, not ids. Supertonic's are the ids
    /// themselves, which is why the two cannot share a rule.
    /// </summary>
    [Fact]
    public void A_multi_speaker_piper_voice_expands_by_index()
    {
        var entry = Piper("en_US-libritts-high") with
        {
            Speakers = 3,
            SpeakerNames = ["Alice", "Bob", "Carla"],
        };

        var (ids, _) = VoicePicker.Rows([entry], "piper:en_US-libritts-high#1");

        Assert.Equal(
            ["piper:en_US-libritts-high#0", "piper:en_US-libritts-high#1", "piper:en_US-libritts-high#2"],
            ids);
    }

    [Fact]
    public void Both_engines_appear_together_in_the_daemons_order()
    {
        var (ids, _) = VoicePicker.Rows([Supertonic(), Piper("nl_NL-pim-medium")], "M1");

        Assert.Equal(Styles.Length + 1, ids.Count);
        Assert.Equal("piper:nl_NL-pim-medium", ids[^1]);
    }

    /// <summary>A daemon that did not answer leaves the setting visible and alone.</summary>
    [Fact]
    public void No_answer_from_the_daemon_still_shows_the_configured_voice()
    {
        var (ids, selected) = VoicePicker.Rows(null, "M4");

        Assert.Equal(["M4"], ids);
        Assert.Equal("M4", selected);
    }
}

using System.Text.Json.Nodes;
using VibeSuperTonic.Core.Settings;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The rules both sides of the product share: the daemon resolves what a voice
/// speaks with, and the Tune tab decides where a save lands. One implementation,
/// because a settings file whose reader and writer disagree loses settings.
/// </summary>
public sealed class SettingsScopeTests
{
    private static JsonObject File(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void A_voice_beats_its_engine_which_beats_the_file()
    {
        var root = File("""
            {
              "EngineSpeed": 1.0, "TotalStep": 8,
              "PerEngine": { "piper": { "EngineSpeed": 1.4, "VolumeTrimDb": -3 } },
              "PerVoice":  { "piper:en_US-ljspeech-high": { "EngineSpeed": 0.9 } }
            }
            """);

        var merged = SettingsScope.Merge(root, "piper", "piper:en_US-ljspeech-high");

        Assert.Equal(0.9, (double)merged["EngineSpeed"]!);
        Assert.Equal(-3, (double)merged["VolumeTrimDb"]!);
        Assert.Equal(8, (int)merged["TotalStep"]!);
    }

    /// <summary>The sections themselves never survive into the merged view.</summary>
    [Fact]
    public void The_merged_view_is_one_flat_object()
    {
        var root = File("""{ "TotalStep": 8, "PerEngine": { "piper": {} }, "PerVoice": { "M1": {} } }""");
        var merged = SettingsScope.Merge(root, "piper", "M1");

        Assert.Null(merged[SettingsScope.PerEngine]);
        Assert.Null(merged[SettingsScope.PerVoice]);
    }

    /// <summary>
    /// The reason overrides are raw JSON rather than a typed record: a sentinel
    /// scheme cannot say "0 dB", which is the value someone picks when they want
    /// no trim at all.
    /// </summary>
    [Fact]
    public void A_zero_override_is_a_value_not_an_absence()
    {
        var root = File("""{ "VolumeTrimDb": -6, "PerVoice": { "M4": { "VolumeTrimDb": 0 } } }""");

        Assert.Equal(0, (double)SettingsScope.Merge(root, "supertonic", "M4")["VolumeTrimDb"]!);
    }

    [Fact]
    public void An_override_cannot_carry_an_unscoped_key()
    {
        var root = File("""
            {
              "VoiceId": "supertonic:M4", "Provider": "cpu",
              "PerEngine": { "supertonic": { "VoiceId": "piper:sneaky", "Provider": "gpu", "TotalStep": 4 } }
            }
            """);

        var merged = SettingsScope.Merge(root, "supertonic", "M4");

        Assert.Equal(4, (int)merged["TotalStep"]!);
        Assert.Equal("supertonic:M4", (string)merged["VoiceId"]!);
        Assert.Equal("cpu", (string)merged["Provider"]!);
    }

    [Fact]
    public void Supertonic_is_keyed_by_style_and_piper_by_qualified_id_without_the_speaker()
    {
        Assert.Equal("M4", SettingsScope.KeyFor(VoiceId.Parse("supertonic:M4")));
        Assert.Equal("M4", SettingsScope.KeyFor(VoiceId.Parse("M4")));
        Assert.Equal("piper:en_US-libritts-high",
            SettingsScope.KeyFor(VoiceId.Parse("piper:en_US-libritts-high#57")));
    }

    [Fact]
    public void A_target_section_is_created_only_when_something_is_written()
    {
        var root = File("""{ "TotalStep": 8 }""");

        Assert.False(SettingsScope.Has(root, SettingsScopeKind.Voice, "piper", "M4"));

        var target = SettingsScope.Target(root, SettingsScopeKind.Voice, "piper", "M4");
        target["TotalStep"] = 4;

        Assert.True(SettingsScope.Has(root, SettingsScopeKind.Voice, "piper", "M4"));
        Assert.Equal(4, (int)root[SettingsScope.PerVoice]!["M4"]!["TotalStep"]!);
    }

    /// <summary>
    /// Clearing takes the empty section with it. A file should not accumulate
    /// the shape of choices somebody has undone.
    /// </summary>
    [Fact]
    public void Clearing_removes_the_section_when_it_empties()
    {
        var root = File("""{ "PerVoice": { "M4": { "TotalStep": 4 }, "M1": { "TotalStep": 6 } } }""");

        SettingsScope.Clear(root, SettingsScopeKind.Voice, "supertonic", "M4");
        Assert.NotNull(root[SettingsScope.PerVoice]);

        SettingsScope.Clear(root, SettingsScopeKind.Voice, "supertonic", "M1");
        Assert.Null(root[SettingsScope.PerVoice]);
    }

    [Fact]
    public void Clearing_all_voices_does_nothing_because_there_is_nothing_to_clear()
    {
        var root = File("""{ "TotalStep": 8 }""");
        SettingsScope.Clear(root, SettingsScopeKind.All, "supertonic", "M4");

        Assert.Equal(8, (int)root["TotalStep"]!);
    }

    [Fact]
    public void An_empty_section_does_not_count_as_an_override()
    {
        var root = File("""{ "PerVoice": { "M4": {} } }""");

        Assert.False(SettingsScope.Has(root, SettingsScopeKind.Voice, "supertonic", "M4"));
    }
}

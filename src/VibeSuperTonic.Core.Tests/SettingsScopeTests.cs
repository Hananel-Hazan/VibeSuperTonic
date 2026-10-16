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

    /// <summary>
    /// One function answers "which object do the boxes show", so the two callers
    /// that used to answer it differently cannot.
    /// </summary>
    [Fact]
    public void All_voices_resolves_to_the_file_itself_and_the_others_to_the_merge()
    {
        var root = File("""{ "TotalStep": 8, "PerEngine": { "supertonic": { "TotalStep": 6 } } }""");

        Assert.Same(root, SettingsScope.Resolve(root, SettingsScopeKind.All, "supertonic", "M4"));
        Assert.Equal(8, (int)SettingsScope.Resolve(root, SettingsScopeKind.All, "supertonic", "M4")["TotalStep"]!);
        Assert.Equal(6, (int)SettingsScope.Resolve(root, SettingsScopeKind.Engine, "supertonic", "M4")["TotalStep"]!);
        Assert.Equal(6, (int)SettingsScope.Resolve(root, SettingsScopeKind.Voice, "supertonic", "M4")["TotalStep"]!);
    }

    /// <summary>
    /// What "all voices" cannot change for the voice that is selected. Without
    /// this the tab has no way to say why a saved value changed nothing
    /// audible — and a file gets into that state from one deliberate scope save,
    /// because saving a scope writes every field into it.
    /// </summary>
    [Fact]
    public void A_shadowed_key_is_one_this_voice_does_not_take_from_the_file()
    {
        var root = File("""
            {
              "VolumeTrimDb": 0, "EngineSpeed": 1.3, "TotalStep": 8,
              "PerEngine": { "supertonic": { "VolumeTrimDb": 5 } },
              "PerVoice":  { "F1": { "EngineSpeed": 1.1 } }
            }
            """);

        Assert.Equal(["EngineSpeed", "VolumeTrimDb"], SettingsScope.Shadowed(root, "supertonic", "F1"));

        // Another engine's section shadows nothing here, and neither does
        // another voice's.
        Assert.Empty(SettingsScope.Shadowed(root, "piper", "piper:x"));
        Assert.Equal(["VolumeTrimDb"], SettingsScope.Shadowed(root, "supertonic", "M4"));
    }

    /// <summary>
    /// An unscoped key in a section shadows nothing, because the merge ignores
    /// it — saying otherwise would explain a symptom the user does not have.
    /// </summary>
    [Fact]
    public void An_unscoped_key_in_a_section_shadows_nothing()
    {
        var root = File("""{ "Provider": "cpu", "PerEngine": { "piper": { "Provider": "gpu" } } }""");

        Assert.Empty(SettingsScope.Shadowed(root, "piper", "piper:x"));
    }

    // ------------------------------- keys that belong to one engine only

    /// <summary>
    /// REPORTED 2026-09-06. <c>TotalStep</c> is the Supertonic model's diffusion
    /// step count and a Piper voice has none — the Tune tab greys the field out
    /// for exactly that reason, and then saved it into the Piper scope anyway.
    ///
    /// <para>What it cost: the benchmark's staleness guard asks the voice in
    /// force for its step count, got a Piper scope's 6 against a Supertonic
    /// profile's 12, and put up a warning that nothing could clear. A key that
    /// means nothing to an engine must not shadow one that means something to
    /// another.</para>
    ///
    /// <para>Filtering it in the merge rather than only in the writer is what
    /// fixes the files that already carry it — an install does not have to be
    /// re-saved to stop reporting a number nothing runs at.</para>
    /// </summary>
    [Fact]
    public void A_piper_scope_cannot_shadow_the_supertonic_only_keys()
    {
        var root = File("""
            {
              "TotalStep": 12, "Language": "en", "EngineSpeed": 1.0,
              "PerEngine": { "piper": { "TotalStep": 6, "Language": "de", "EngineSpeed": 1.2 } }
            }
            """);

        var merged = SettingsScope.Merge(root, "piper", "piper:en_US-hfc_male-medium");

        Assert.Equal(12, (int)merged["TotalStep"]!);
        Assert.Equal("en", (string)merged["Language"]!);

        // Everything that DOES describe how a Piper voice sounds still applies.
        Assert.Equal(1.2, (double)merged["EngineSpeed"]!);
    }

    /// <summary>The same key in a Supertonic scope is exactly what scoping is for.</summary>
    [Fact]
    public void A_supertonic_scope_may_of_course_set_its_own_step_count()
    {
        var root = File("""
            {
              "TotalStep": 12,
              "PerEngine": { "supertonic": { "TotalStep": 6 } }
            }
            """);

        Assert.Equal(6, (int)SettingsScope.Merge(root, "supertonic", "F5")["TotalStep"]!);
    }

    /// <summary>
    /// And it is not "shadowed" either: the Tune tab would otherwise tell a user
    /// that changing the global step count will not reach their Piper voice,
    /// which is true of every Piper voice and has nothing to do with the
    /// override.
    /// </summary>
    [Fact]
    public void A_key_an_engine_cannot_use_is_not_reported_as_shadowing()
    {
        var root = File("""
            { "PerEngine": { "piper": { "TotalStep": 6, "EngineSpeed": 1.2 } } }
            """);

        Assert.Equal(["EngineSpeed"], SettingsScope.Shadowed(root, "piper", "piper:x"));
        Assert.Equal(["TotalStep"], SettingsScope.Shadowed(
            File("""{ "PerEngine": { "supertonic": { "TotalStep": 6 } } }"""), "supertonic", "F1"));
    }

    // -------------------------------------------- what a save puts in a scope

    /// <summary>
    /// REPORTED 2026-09-06 as "piper does not obey the speed change", and the
    /// user's settings.json is the evidence: <c>PerEngine.piper</c> held
    /// <c>EngineSpeed</c>, <c>DspRate</c>, <c>VolumeTrimDb</c>, both chunk sizes,
    /// <c>InterChunkSilenceMs</c> and a <c>TotalStep</c> — a complete snapshot of
    /// the tab, written because the tab saved every field into whichever scope
    /// was selected.
    ///
    /// <para>One deliberate override therefore pinned all nine. After that the
    /// "All voices" boxes were dead for every Piper voice: the value saved
    /// correctly, read back correctly, and changed nothing anybody could hear.
    /// The tab's own note already said this was the worst outcome it had.</para>
    ///
    /// <para>An override now says only what it says, which is what makes the
    /// global setting reachable again — and what makes a scope undoable by
    /// setting the value back.</para>
    /// </summary>
    [Fact]
    public void A_scope_keeps_only_what_it_does_not_already_inherit()
    {
        var root = File("""{ "EngineSpeed": 1.0, "DspRate": 1.0, "VolumeTrimDb": 0 }""");

        SettingsScope.Save(root, SettingsScopeKind.Engine, "piper", "piper:x", new Dictionary<string, JsonNode?>
        {
            ["EngineSpeed"] = JsonValue.Create(1.2),
            ["DspRate"] = JsonValue.Create(1.0),     // the same as the file's
            ["VolumeTrimDb"] = JsonValue.Create(0),  // and so is this
        });

        var section = SettingsScope.Section(root, SettingsScope.PerEngine, "piper");
        Assert.NotNull(section);
        Assert.Equal(["EngineSpeed"], section!.Select(kv => kv.Key));
    }

    /// <summary>
    /// Setting a scoped value back to the inherited one removes the override, so
    /// the voice follows the global setting again. Without this a person can
    /// create an override and has no way to undo it but the Clear button.
    /// </summary>
    [Fact]
    public void Putting_a_value_back_makes_the_scope_follow_again()
    {
        var root = File("""
            {
              "EngineSpeed": 1.0,
              "PerEngine": { "piper": { "EngineSpeed": 1.2 } }
            }
            """);

        SettingsScope.Save(root, SettingsScopeKind.Engine, "piper", "piper:x",
            new Dictionary<string, JsonNode?> { ["EngineSpeed"] = JsonValue.Create(1.0) });

        // The section emptied, so it is gone rather than left as {}.
        Assert.Null(root[SettingsScope.PerEngine]);
        Assert.Equal(1.0, (double)root["EngineSpeed"]!);
    }

    /// <summary>
    /// A voice scope inherits through its ENGINE, not straight from the file. A
    /// voice saved at its engine's value has nothing of its own to say.
    /// </summary>
    [Fact]
    public void A_voice_inherits_through_its_engine()
    {
        var root = File("""
            {
              "EngineSpeed": 1.0,
              "PerEngine": { "piper": { "EngineSpeed": 1.2 } }
            }
            """);

        SettingsScope.Save(root, SettingsScopeKind.Voice, "piper", "piper:x", new Dictionary<string, JsonNode?>
        {
            ["EngineSpeed"] = JsonValue.Create(1.2),  // its engine's value: nothing of its own
            ["DspRate"] = JsonValue.Create(1.4),      // the file's is 1.0 by default: its own
        });

        var section = SettingsScope.Section(root, SettingsScope.PerVoice, "piper:x");
        Assert.NotNull(section);
        Assert.Equal(["DspRate"], section!.Select(kv => kv.Key));
    }

    /// <summary>
    /// THE KEY THAT POISONED THE BENCHMARK. The Tune tab greys <c>TotalStep</c>
    /// out for a Piper voice and then saved it into the Piper scope anyway, where
    /// it shadowed the Supertonic step count the benchmark compares against. A
    /// scope cannot hold a key its engine cannot act on, and saving removes one
    /// that is already there.
    /// </summary>
    [Fact]
    public void A_piper_scope_will_not_take_a_supertonic_only_key()
    {
        var root = File("""
            {
              "TotalStep": 12,
              "PerEngine": { "piper": { "TotalStep": 6, "EngineSpeed": 1.2 } }
            }
            """);

        SettingsScope.Save(root, SettingsScopeKind.Engine, "piper", "piper:x", new Dictionary<string, JsonNode?>
        {
            ["TotalStep"] = JsonValue.Create(6),
            ["EngineSpeed"] = JsonValue.Create(1.2),
        });

        var section = SettingsScope.Section(root, SettingsScope.PerEngine, "piper");
        Assert.Equal(["EngineSpeed"], section!.Select(kv => kv.Key));
        Assert.Equal(12, (int)root["TotalStep"]!);
    }

    /// <summary>
    /// "All voices" is the file itself, which has nothing above it — every value
    /// is written, and a blank box clears the key.
    /// </summary>
    [Fact]
    public void The_file_itself_takes_every_value_it_is_given()
    {
        var root = File("""{ "EngineSpeed": 1.0, "DspRate": 1.4 }""");

        SettingsScope.Save(root, SettingsScopeKind.All, "supertonic", "F1", new Dictionary<string, JsonNode?>
        {
            ["EngineSpeed"] = JsonValue.Create(1.0),
            ["DspRate"] = null,
        });

        Assert.Equal(1.0, (double)root["EngineSpeed"]!);
        Assert.False(root.ContainsKey("DspRate"));
    }

    /// <summary>
    /// A hand-edited file holds "1.2" and 1.2 for the same key, and the boxes
    /// show both as 1.2. Neither is an override of the other.
    /// </summary>
    [Fact]
    public void A_quoted_number_and_a_bare_one_are_the_same_value()
    {
        var root = File("""{ "EngineSpeed": "1.2" }""");

        SettingsScope.Save(root, SettingsScopeKind.Engine, "piper", "piper:x",
            new Dictionary<string, JsonNode?> { ["EngineSpeed"] = JsonValue.Create(1.2) });

        Assert.Null(root[SettingsScope.PerEngine]);
    }
}

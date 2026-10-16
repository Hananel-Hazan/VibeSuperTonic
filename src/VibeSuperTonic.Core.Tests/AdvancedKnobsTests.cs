using System.Text.Json.Nodes;
using VibeSuperTonic.Core.Settings;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The three Windows Advanced-tab knobs the Tune tab gained — rate clamp ceiling,
/// silence inside a chunk, inter-op threads — and the scope-aware reset.
///
/// <para>The rules here are the ones the text boxes cannot enforce on their own:
/// a text box has no min or max, a reset has no idea what a default is, and a
/// scope that quietly takes a setting its engine cannot use is a setting that
/// saves, reads back and changes nothing anybody can hear.</para>
/// </summary>
public sealed class AdvancedKnobsTests
{
    private static JsonObject File(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ---------------------------------------------------------------- ranges

    [Theory]
    [InlineData("RateClampCeiling", 1.10, true)]
    [InlineData("RateClampCeiling", 1.3, true)]
    [InlineData("RateClampCeiling", 1.50, true)]
    [InlineData("RateClampCeiling", 1.09, false)]
    [InlineData("RateClampCeiling", 1.51, false)]
    [InlineData("RateClampCeiling", 13, false)]       // the 1.3 typo that garbles every utterance
    [InlineData("SynthesisSilenceSec", 0, true)]
    [InlineData("SynthesisSilenceSec", 1, true)]
    [InlineData("SynthesisSilenceSec", -0.1, false)]
    [InlineData("SynthesisSilenceSec", 1.01, false)]
    [InlineData("OnnxInterOpThreads", 1, true)]
    [InlineData("OnnxInterOpThreads", 16, true)]
    [InlineData("OnnxInterOpThreads", 0, false)]
    [InlineData("OnnxInterOpThreads", 17, false)]
    [InlineData("OnnxInterOpThreads", 2.5, false)]
    [InlineData("TotalStep", 9999, true)]              // no range here: someone else's key
    public void Each_knob_accepts_exactly_the_windows_range(string key, double value, bool ok) =>
        Assert.Equal(ok, KnobRanges.Check(key, value) is null);

    [Fact]
    public void Not_a_number_is_refused_with_a_sentence()
    {
        Assert.NotNull(KnobRanges.Check("RateClampCeiling", double.NaN));
        Assert.Contains("between 1.1 and 1.5", KnobRanges.Check("RateClampCeiling", 9)!);
    }

    // ------------------------------------------------------------- inter-op

    [Theory]
    [InlineData(0, 1)]       // never edited
    [InlineData(-3, 1)]      // nonsense is not "no threads"
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(16, 16)]
    [InlineData(400, 16)]    // a hand-edited typo is held to the ceiling
    public void Inter_op_threads_default_to_one_and_stop_at_sixteen(int setting, int expected) =>
        Assert.Equal(expected, CpuBudget.InterOpThreads(setting));

    // --------------------------------------------------------------- scoping

    [Fact]
    public void A_piper_scope_does_not_take_a_rate_clamp_ceiling()
    {
        var root = File("""
            {
              "RateClampCeiling": 1.3,
              "PerEngine": { "piper": { "RateClampCeiling": 1.5, "SynthesisSilenceSec": 0.1 } }
            }
            """);

        var merged = SettingsScope.Merge(root, "piper", "piper:en_US-ljspeech-high");

        // The ceiling is the Supertonic model's; Piper takes its rate through
        // length_scale. The silence is shared by both engines and does apply.
        Assert.Equal(1.3, (double)merged["RateClampCeiling"]!);
        Assert.Equal(0.1, (double)merged["SynthesisSilenceSec"]!);
    }

    [Fact]
    public void A_supertonic_scope_does_take_it()
    {
        var root = File("""{ "RateClampCeiling": 1.3, "PerEngine": { "supertonic": { "RateClampCeiling": 1.2 } } }""");
        Assert.Equal(1.2, (double)SettingsScope.Merge(root, "supertonic", "M1")["RateClampCeiling"]!);
    }

    [Fact]
    public void Saving_a_ceiling_into_a_piper_scope_writes_nothing()
    {
        var root = File("{}");

        SettingsScope.Save(root, SettingsScopeKind.Engine, "piper", "piper:x",
            new Dictionary<string, JsonNode?> { ["RateClampCeiling"] = JsonValue.Create(1.4) });

        Assert.Null(root["PerEngine"]);
    }

    [Fact]
    public void The_new_knobs_are_scoped_but_inter_op_belongs_to_the_machine()
    {
        Assert.True(SettingsScope.IsScoped("RateClampCeiling"));
        Assert.True(SettingsScope.IsScoped("SynthesisSilenceSec"));
        Assert.False(SettingsScope.IsScoped("OnnxInterOpThreads"));
    }

    // ----------------------------------------------------------------- reset

    private const string Everything = """
        {
          "VoiceId": "piper:en_US-lessac-medium", "DefaultVoice": "M1",
          "Language": "de", "TotalStep": 12, "EngineSpeed": 1.2, "DspRate": 1.1, "VolumeTrimDb": -2,
          "RateClampCeiling": 1.4, "SynthesisSilenceSec": 0.5, "MaxChunkChars": 150, "MinChunkChars": 50,
          "InterChunkSilenceMs": 100, "MaxCpuPercent": 50, "OnnxInterOpThreads": 3,
          "Provider": "cpu", "ClipboardFallback": true, "GpuOnBattery": true,
          "UseDirectML": true, "OnnxThreads": 6,
          "PerEngine": { "piper": { "EngineSpeed": 1.5 }, "supertonic": { "TotalStep": 4 } },
          "PerVoice": { "M1": { "VolumeTrimDb": 3 }, "M2": { "DspRate": 0.9 } }
        }
        """;

    [Fact]
    public void Resetting_all_voices_removes_the_tuning_values_and_nothing_else()
    {
        var root = File(Everything);

        var removed = SettingsReset.Apply(root, SettingsScopeKind.All, "supertonic", "M1");

        foreach (string key in SettingsScope.Keys.Concat(SettingsReset.MachineKeys))
            Assert.False(root.ContainsKey(key), key);
        Assert.Equal(SettingsScope.Keys.Length + SettingsReset.MachineKeys.Length, removed.Count);

        // The voice choice, the Windows-only keys this build does not own, and the
        // overrides on an engine or a voice all survive.
        Assert.Equal("piper:en_US-lessac-medium", (string)root["VoiceId"]!);
        Assert.Equal("M1", (string)root["DefaultVoice"]!);
        Assert.True((bool)root["UseDirectML"]!);
        Assert.Equal(6, (int)root["OnnxThreads"]!);
        Assert.NotNull(root["PerEngine"]);
        Assert.NotNull(root["PerVoice"]);
    }

    [Fact]
    public void Resetting_an_engine_clears_only_that_engines_section()
    {
        var root = File(Everything);

        var removed = SettingsReset.Apply(root, SettingsScopeKind.Engine, "piper", "piper:en_US-lessac-medium");

        Assert.Equal(["EngineSpeed"], removed);
        Assert.Null(root["PerEngine"]!["piper"]);
        Assert.NotNull(root["PerEngine"]!["supertonic"]);
        Assert.Equal(12, (int)root["TotalStep"]!);      // the file's own values are untouched
        Assert.NotNull(root["PerVoice"]);
    }

    [Fact]
    public void Resetting_one_voice_clears_only_that_voice()
    {
        var root = File(Everything);

        var removed = SettingsReset.Apply(root, SettingsScopeKind.Voice, "supertonic", "M1");

        Assert.Equal(["VolumeTrimDb"], removed);
        Assert.Null(root["PerVoice"]!["M1"]);
        Assert.NotNull(root["PerVoice"]!["M2"]);
        Assert.NotNull(root["PerEngine"]);
        Assert.Equal(-2, (int)root["VolumeTrimDb"]!);
    }

    [Fact]
    public void The_last_override_takes_its_section_with_it()
    {
        var root = File("""{ "PerVoice": { "M1": { "DspRate": 1.5 } } }""");

        SettingsReset.Apply(root, SettingsScopeKind.Voice, "supertonic", "M1");

        Assert.Null(root["PerVoice"]);   // a file should not keep the shape of undone choices
    }

    [Theory]
    [InlineData(SettingsScopeKind.All)]
    [InlineData(SettingsScopeKind.Engine)]
    [InlineData(SettingsScopeKind.Voice)]
    public void Resetting_what_was_never_set_removes_nothing_and_says_so(SettingsScopeKind kind)
    {
        var root = File("""{ "VoiceId": "M1" }""");

        Assert.Empty(SettingsReset.Apply(root, kind, "supertonic", "M1"));
        Assert.Equal("""{"VoiceId":"M1"}""", root.ToJsonString());
    }

    [Fact]
    public void The_confirmation_names_what_each_scope_will_and_will_not_touch()
    {
        string all = SettingsReset.Describe(SettingsScopeKind.All, "Piper", "x");
        Assert.Contains("overrides on an engine or a single voice", all);
        Assert.Contains("voice choice is kept", all);

        string voice = SettingsReset.Describe(SettingsScopeKind.Voice, "Piper", "cori-high");
        Assert.Contains("cori-high", voice);
        Assert.Contains("Other voices are not touched", voice);

        string engine = SettingsReset.Describe(SettingsScopeKind.Engine, "Piper", "x");
        Assert.Contains("Piper voices", engine);
        Assert.Contains("single voice are not touched", engine);
    }
}

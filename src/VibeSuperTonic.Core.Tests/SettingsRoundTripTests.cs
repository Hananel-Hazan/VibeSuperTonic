using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Pins the mechanism behind mechanic 4 of docs/LINUX-PORT-PLAN.md: a
/// source-generated <see cref="JsonSerializerContext"/> must carry unknown
/// properties through a read/write cycle.
///
/// The real <c>EngineSettings</c> lives twice, in the Windows engine and the
/// Windows launcher, and neither can be referenced from here — both are internal
/// to <c>net10.0-windows</c> projects. What is tested instead is the thing that
/// can break invisibly: <c>[JsonExtensionData]</c> is not supported by the source
/// generator's fast serialization path, so the generator has to fall back to
/// metadata mode for a type that declares it. If a future SDK, a
/// <c>JsonSourceGenerationMode.Serialization</c> annotation, or a trimming
/// setting changes that, the round-trip stops happening — silently, with the
/// build still green and every declared property still saving correctly. Then
/// the launcher erases the Linux daemon's settings the first time a user touches
/// a slider, and nothing anywhere reports an error.
///
/// The shape below mirrors the real one: same JsonSourceGenerationOptions, a
/// nested dictionary of the same type (PerVoice), and extension data on both
/// levels.
/// </summary>
internal sealed class Settings
{
    public int TotalStep { get; set; } = 8;
    public string DefaultVoice { get; set; } = "M1";
    public Dictionary<string, Settings> PerVoice { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// Declared at namespace level, not nested in the test class: the source
// generator requires every containing type to be partial, and a partial test
// class is a footgun waiting for the next person who adds a file.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsContext : JsonSerializerContext { }

public class SettingsRoundTripTests
{
    private static Settings Read(string json) =>
        JsonSerializer.Deserialize(json, SettingsContext.Default.Settings)!;

    private static string Write(Settings s) =>
        JsonSerializer.Serialize(s, SettingsContext.Default.Settings);

    [Fact]
    public void Unknown_top_level_keys_survive_a_read_write_cycle()
    {
        // "HotkeyToggle" and "ReleaseModelAfterIdleMinutes" are the shape of what
        // the Linux daemon will add: keys the Windows launcher has never heard of.
        const string Json = """
        {
          "TotalStep": 12,
          "DefaultVoice": "F2",
          "HotkeyToggle": "<Super>s",
          "ReleaseModelAfterIdleMinutes": 15,
          "SelectionCapKb": 100
        }
        """;

        var settings = Read(Json);
        Assert.Equal(12, settings.TotalStep);
        Assert.Equal("F2", settings.DefaultVoice);

        using var reparsed = JsonDocument.Parse(Write(settings));
        var root = reparsed.RootElement;

        Assert.Equal("<Super>s", root.GetProperty("HotkeyToggle").GetString());
        Assert.Equal(15, root.GetProperty("ReleaseModelAfterIdleMinutes").GetInt32());
        Assert.Equal(100, root.GetProperty("SelectionCapKb").GetInt32());
        Assert.Equal(12, root.GetProperty("TotalStep").GetInt32());
    }

    [Fact]
    public void Unknown_keys_survive_inside_PerVoice()
    {
        // PerVoice nests the same type, so it inherits the behaviour — but only
        // if the generator applied extension data to the nested case too.
        const string Json = """
        {
          "TotalStep": 8,
          "PerVoice": { "M1": { "TotalStep": 4, "LinuxOnlyKnob": "yes" } }
        }
        """;

        using var reparsed = JsonDocument.Parse(Write(Read(Json)));
        var m1 = reparsed.RootElement.GetProperty("PerVoice").GetProperty("M1");

        Assert.Equal(4, m1.GetProperty("TotalStep").GetInt32());
        Assert.Equal("yes", m1.GetProperty("LinuxOnlyKnob").GetString());
    }

    [Fact]
    public void Editing_a_known_key_does_not_disturb_unknown_ones()
    {
        // The actual failure: the user opens the Control Panel, moves one slider,
        // and Save() writes the whole object back.
        const string Json = """{ "TotalStep": 8, "HotkeyToggle": "<Super>s" }""";

        var settings = Read(Json);
        settings.TotalStep = 4;

        using var reparsed = JsonDocument.Parse(Write(settings));
        Assert.Equal(4, reparsed.RootElement.GetProperty("TotalStep").GetInt32());
        Assert.Equal("<Super>s", reparsed.RootElement.GetProperty("HotkeyToggle").GetString());
    }

    [Fact]
    public void A_file_with_no_unknown_keys_gains_nothing()
    {
        // Extension data must not materialise an empty "Extra" property in the
        // output — that would be a visible, confusing diff in a hand-edited file.
        const string Json = """{ "TotalStep": 8, "DefaultVoice": "M1" }""";

        using var reparsed = JsonDocument.Parse(Write(Read(Json)));
        Assert.False(reparsed.RootElement.TryGetProperty("Extra", out _));
        Assert.False(reparsed.RootElement.TryGetProperty("extra", out _));
    }

    [Fact]
    public void Nulls_and_nested_objects_in_unknown_keys_survive()
    {
        const string Json = """
        {
          "TotalStep": 8,
          "DaemonState": { "LastVoice": "M1", "Nested": { "Deep": [1, 2, 3] } },
          "NothingHere": null
        }
        """;

        using var reparsed = JsonDocument.Parse(Write(Read(Json)));
        var root = reparsed.RootElement;

        Assert.Equal("M1", root.GetProperty("DaemonState").GetProperty("LastVoice").GetString());
        Assert.Equal(3, root.GetProperty("DaemonState").GetProperty("Nested").GetProperty("Deep").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("NothingHere").ValueKind);
    }
}

using System.Text.Json.Nodes;
using VibeSuperTonic.Core.Settings;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The Tune tab's boxes, as a state machine with no toolkit in it.
///
/// <para>Three bugs in three days had one shape — the display disagreeing with
/// what was saved — and each was a second place that filled the same boxes. The
/// tab now owns none of that decision, so these are the checks that stand
/// between it and a fourth: the file it reads, the scope it is on and the
/// things somebody typed, in the orders a real tab puts them in.</para>
/// </summary>
public sealed class ScopedFieldsTests
{
    private static JsonObject File(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static ScopedFields Fields() =>
        new("Language", "EngineSpeed", "VolumeTrimDb", "TotalStep");

    /// <summary>
    /// The report, 2026-08-30: "set a higher volume for the Supertonic voices,
    /// press Save, and it goes back to 0."
    ///
    /// <para>It was never the save. The file held the 5 the whole time — the
    /// refresh that ran after the save filled the boxes from the file's TOP
    /// LEVEL while the scope selector said "all Supertonic voices", so the box
    /// showed the global 0. Pressing Save again then wrote that 0 into the
    /// section, which is where the setting was actually lost.</para>
    /// </summary>
    [Fact]
    public void The_volume_that_would_not_stick()
    {
        var saved = File("""
            {
              "VolumeTrimDb": 0, "EngineSpeed": 1.3,
              "PerEngine": { "supertonic": { "VolumeTrimDb": 5, "EngineSpeed": 1.3 } }
            }
            """);

        var fields = Fields();
        fields.Load(saved);
        fields.Scoped(SettingsScopeKind.Engine, "supertonic", "F1");
        Assert.Equal("5", fields["VolumeTrimDb"]);

        // What a save does: write, then re-read the file and refresh. The scope
        // has not moved, so neither may the number.
        fields.Committed();
        fields.Load(saved);

        Assert.Equal("5", fields["VolumeTrimDb"]);
    }

    /// <summary>The same mechanism, at its smallest: a read cannot un-scope the boxes.</summary>
    [Fact]
    public void A_reload_shows_the_chosen_scope_not_the_file()
    {
        var root = File("""{ "TotalStep": 8, "PerVoice": { "M4": { "TotalStep": 4 } } }""");

        var fields = Fields();
        fields.Scoped(SettingsScopeKind.Voice, "supertonic", "M4");
        fields.Load(root);

        Assert.Equal("4", fields["TotalStep"]);
    }

    /// <summary>
    /// Reported 2026-08-28: typing a rate the moment the tab opens and pressing
    /// Save wrote the old one, because the asynchronous read landed in between.
    /// </summary>
    [Fact]
    public void A_reload_does_not_discard_what_was_typed()
    {
        var root = File("""{ "EngineSpeed": 1.1 }""");

        var fields = Fields();
        fields.Load(root);
        fields.Typed("EngineSpeed", "1.4");
        fields.Load(root);

        Assert.True(fields.Touched);
        Assert.Equal("1.4", fields["EngineSpeed"]);
    }

    /// <summary>
    /// Nor does moving the scope. It is the one refill a user might expect to
    /// win — but an edit is worth more than a selection, and the tab says on
    /// screen which of the two is showing.
    /// </summary>
    [Fact]
    public void A_scope_change_does_not_discard_what_was_typed()
    {
        var root = File("""{ "TotalStep": 8, "PerEngine": { "piper": { "TotalStep": 6 } } }""");

        var fields = Fields();
        fields.Load(root);
        fields.Typed("TotalStep", "3");
        fields.Scoped(SettingsScopeKind.Engine, "piper", "piper:x");

        Assert.Equal("3", fields["TotalStep"]);
    }

    /// <summary>A save ends the edit: the file is authoritative again.</summary>
    [Fact]
    public void A_save_hands_authority_back_to_the_file()
    {
        var fields = Fields();
        fields.Load(File("""{ "TotalStep": 8 }"""));
        fields.Typed("TotalStep", "3");
        fields.Committed();
        fields.Load(File("""{ "TotalStep": 3 }"""));

        Assert.False(fields.Touched);
        Assert.Equal("3", fields["TotalStep"]);
    }

    /// <summary>And Revert is the one button that throws edits away on purpose.</summary>
    [Fact]
    public void Revert_shows_the_file_again()
    {
        var fields = Fields();
        fields.Load(File("""{ "TotalStep": 8 }"""));
        fields.Typed("TotalStep", "3");
        fields.Discard();

        Assert.False(fields.Touched);
        Assert.Equal("8", fields["TotalStep"]);
    }

    /// <summary>
    /// An unset key is a blank box — not a zero, which would make "clear it to
    /// get the default" indistinguishable from "it is 0".
    /// </summary>
    [Fact]
    public void An_unset_key_is_blank()
    {
        var fields = Fields();
        fields.Load(File("""{ "TotalStep": 8 }"""));

        Assert.Equal("", fields["VolumeTrimDb"]);
        Assert.Equal("", fields["Language"]);
    }

    /// <summary>
    /// A file people edit by hand holds integers, decimals and quoted numbers
    /// for the same key. All three are the same box.
    /// </summary>
    [Fact]
    public void Hand_written_values_all_read_back_as_typed_text()
    {
        var fields = Fields();
        fields.Load(File("""{ "TotalStep": 8, "EngineSpeed": 1.05, "VolumeTrimDb": "-3", "Language": "en" }"""));

        Assert.Equal("8", fields["TotalStep"]);
        Assert.Equal("1.05", fields["EngineSpeed"]);
        Assert.Equal("-3", fields["VolumeTrimDb"]);
        Assert.Equal("en", fields["Language"]);
    }

    /// <summary>
    /// A voice's own section beats its engine's, which beats the file — the same
    /// order the daemon resolves in, because the tab showing one order and the
    /// daemon speaking another is the bug this class exists to make impossible.
    /// </summary>
    [Fact]
    public void The_boxes_resolve_in_the_daemons_order()
    {
        var root = File("""
            {
              "EngineSpeed": 1.0,
              "PerEngine": { "piper": { "EngineSpeed": 1.4, "VolumeTrimDb": -3 } },
              "PerVoice":  { "piper:en_US-cori-high": { "EngineSpeed": 0.9 } }
            }
            """);

        var fields = Fields();
        fields.Load(root);
        fields.Scoped(SettingsScopeKind.Voice, "piper", "piper:en_US-cori-high");

        Assert.Equal("0.9", fields["EngineSpeed"]);
        Assert.Equal("-3", fields["VolumeTrimDb"]);
    }
}

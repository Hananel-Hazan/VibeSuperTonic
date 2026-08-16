using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The Linux half of mechanic 4, and the half that had no discipline at all until
/// Phase 6 was about to need it.
///
/// <para><b>Why a second copy of a test that already exists.</b>
/// <see cref="SettingsRoundTripTests"/> pins the Windows side, whose context is
/// declared with <c>WriteIndented</c> and nothing else. The daemon's
/// <c>HostConfigJsonContext</c> is declared with
/// <c>ReadCommentHandling = Skip</c> and <c>AllowTrailingCommas = true</c> — a
/// different generator configuration, and the generator's decision about whether
/// it may use the fast-serialization path is made per context. A context that
/// round-trips unknown keys is not evidence that another one does.</para>
///
/// <para><b>What is being defended.</b> The daemon only reads
/// <c>settings.json</c>, so today nothing here can fail in production. Phase 6's
/// Tune tab makes the UI the second writer of a file that crosses platforms, on
/// the medium where that matters — a portable folder on a stick, a dual-boot
/// mount, a synced directory. Without extension data the first Linux save erases
/// <c>UseDirectML</c>, <c>DirectMLDeviceId</c>, <c>OnnxThreads</c>,
/// <c>PerVoice</c> and anything a later Windows release adds. It fails silently:
/// the build is green, every declared property saves correctly, and the user
/// finds out on the other operating system.</para>
///
/// <para><b>The replica, and its limit.</b> The real <c>LinuxSettings</c> lives in
/// <c>VibeSuperTonic.Daemon</c>, which this project deliberately cannot reference
/// — the daemon pulls in ONNX Runtime and libpulse, and Core.Tests is
/// package-free precisely so it runs on both CI runners with no native
/// libraries (R-3). So the shape below mirrors it: same source-generation
/// options, same extension-data declaration. What that pins is the mechanism,
/// which is the part that breaks invisibly under an SDK bump, a
/// <c>JsonSourceGenerationMode.Serialization</c> annotation or a trimming
/// setting. What it cannot catch is the attribute being deleted from the real
/// type — that is what the <c>Extra</c> property's own documentation is for.</para>
/// </summary>
internal sealed class LinuxSettingsReplica
{
    public string DefaultVoice { get; set; } = "M1";
    public int TotalStep { get; set; } = 8;
    public float DspRate { get; set; } = 1.0f;
    public int InterChunkSilenceMs { get; set; } = 200;
    public int MaxCpuPercent { get; set; } = 20;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

// The daemon's options, copied exactly. If HostConfigJsonContext's options
// change, these must change with them or this test is pinning a context nobody
// ships.
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(LinuxSettingsReplica))]
internal partial class LinuxSettingsContext : JsonSerializerContext { }

public class LinuxSettingsRoundTripTests
{
    private static LinuxSettingsReplica Read(string json) =>
        JsonSerializer.Deserialize(json, LinuxSettingsContext.Default.LinuxSettingsReplica)!;

    private static string Write(LinuxSettingsReplica s) =>
        JsonSerializer.Serialize(s, LinuxSettingsContext.Default.LinuxSettingsReplica);

    [Fact]
    public void Windows_only_keys_survive_a_Linux_read_write_cycle()
    {
        // Exactly the keys the Linux side is documented never to read or write.
        // They are not "unknown" by accident — the daemon refuses to express
        // them, and that is the whole reason they are at risk.
        const string Json = """
        {
          "DefaultVoice": "F2",
          "TotalStep": 12,
          "UseDirectML": true,
          "DirectMLDeviceId": 1,
          "OnnxThreads": 4,
          "PerVoice": { "M1": { "TotalStep": 4, "VolumeTrimDb": -3.5 } }
        }
        """;

        var settings = Read(Json);
        Assert.Equal("F2", settings.DefaultVoice);
        Assert.Equal(12, settings.TotalStep);

        using var reparsed = JsonDocument.Parse(Write(settings));
        var root = reparsed.RootElement;

        Assert.True(root.GetProperty("UseDirectML").GetBoolean());
        Assert.Equal(1, root.GetProperty("DirectMLDeviceId").GetInt32());
        Assert.Equal(4, root.GetProperty("OnnxThreads").GetInt32());
        Assert.Equal(4, root.GetProperty("PerVoice").GetProperty("M1").GetProperty("TotalStep").GetInt32());
    }

    [Fact]
    public void Saving_one_slider_leaves_the_other_platforms_keys_alone()
    {
        // The actual Phase 6 failure, written out: the user opens the Tune tab on
        // Linux, moves the speed, and the save writes the whole object back.
        const string Json = """{ "DspRate": 1.0, "UseDirectML": true, "OnnxThreads": 4 }""";

        var settings = Read(Json);
        settings.DspRate = 1.35f;

        using var reparsed = JsonDocument.Parse(Write(settings));
        var root = reparsed.RootElement;

        Assert.Equal(1.35f, root.GetProperty("DspRate").GetSingle(), precision: 4);
        Assert.True(root.GetProperty("UseDirectML").GetBoolean());
        Assert.Equal(4, root.GetProperty("OnnxThreads").GetInt32());
    }

    [Fact]
    public void A_file_with_no_unknown_keys_gains_no_Extra_property()
    {
        // settings.json is hand-edited on this platform far more than on Windows
        // — there is no config set verb, by decision, so editing the file IS the
        // CLI. An "Extra": {} appearing after the first save would be a confusing
        // diff in a file the user is reading.
        const string Json = """{ "DefaultVoice": "M1", "TotalStep": 8 }""";

        using var reparsed = JsonDocument.Parse(Write(Read(Json)));
        Assert.False(reparsed.RootElement.TryGetProperty("Extra", out _));
        Assert.False(reparsed.RootElement.TryGetProperty("extra", out _));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_still_accepted()
    {
        // The daemon's context allows both, because this file is meant to be
        // hand-edited. Extension data must not have quietly cost that: a comment
        // is not a key and must not end up in Extra.
        const string Json = """
        {
          // the voice this install speaks with
          "DefaultVoice": "F1",
          "InterChunkSilenceMs": 350,
        }
        """;

        var settings = Read(Json);
        Assert.Equal("F1", settings.DefaultVoice);
        Assert.Equal(350, settings.InterChunkSilenceMs);

        using var reparsed = JsonDocument.Parse(Write(settings));
        Assert.False(reparsed.RootElement.TryGetProperty("Extra", out _));
    }
}

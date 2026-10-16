using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The engine-qualified <c>VoiceId</c> setting P4 adds, and — more importantly —
/// the promise that adding it changed nothing for a file that does not use it.
///
/// <para><b>What could go wrong here is silent.</b> Every <c>settings.json</c>
/// written before P4 carries a bare <c>DefaultVoice</c>, and the Windows engine
/// reads the same file and knows nothing about Piper. If the new key were read
/// wrongly the symptom would not be an exception: it would be a daemon speaking
/// in the wrong voice, or the right voice through the wrong engine, which sounds
/// like a voice rather than like a bug — the same class as
/// <see href="../../docs/PIPER-PLAN.md">trap 1</see>.</para>
/// </summary>
public class QualifiedVoiceSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-qvoice-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public QualifiedVoiceSettingTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Settings(string json) =>
        File.WriteAllText(Path.Combine(Data, "settings.json"), json);

    private void InstallVoice(string id)
    {
        string dir = Path.Combine(Models, PiperVoiceStore.FolderName, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".onnx"), "not a graph");
        File.WriteAllText(Path.Combine(dir, id + ".onnx.json"), """
            {
              "audio": { "sample_rate": 22050, "quality": "medium" },
              "espeak": { "voice": "en-us" },
              "inference": { "noise_scale": 0.6, "length_scale": 1, "noise_w": 0.75 },
              "phoneme_id_map": { "_": [0], "^": [1], "$": [2] },
              "num_speakers": 1
            }
            """);
    }

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    // ------------------------------------------------------ nothing moved

    [Fact]
    public void A_file_with_only_DefaultVoice_behaves_exactly_as_it_did()
    {
        Settings("""{ "DefaultVoice": "F3" }""");
        var config = Loaded();

        Assert.Equal("F3", config.ConfiguredVoice);
        var options = Assert.IsType<SupertonicOptions>(config.Utterance(null, null).Synthesis);
        Assert.Equal("F3", options.VoiceId);
    }

    [Fact]
    public void A_blank_VoiceId_means_unset_rather_than_empty()
    {
        // The file is hand-editable, and a key someone cleared should behave like
        // a key they deleted. Without this the daemon would ask for the voice ""
        // and refuse to speak.
        Settings("""{ "DefaultVoice": "M1", "VoiceId": "   " }""");
        Assert.Equal("M1", Loaded().ConfiguredVoice);
    }

    // ---------------------------------------------------- the new key wins

    [Fact]
    public void VoiceId_supersedes_DefaultVoice_when_it_is_set()
    {
        InstallVoice("en_US-lessac-medium");
        Settings("""
            { "DefaultVoice": "M1", "VoiceId": "piper:en_US-lessac-medium" }
            """);

        var plan = Loaded().Utterance(null, null);
        Assert.Equal("piper", plan.Engine);

        // The options carry the BARE id, because that is what names a directory
        // and a calibration file. "piper:en_US-lessac-medium" is not a folder.
        var options = Assert.IsType<PiperOptions>(plan.Synthesis);
        Assert.Equal("en_US-lessac-medium", options.VoiceId);
    }

    [Fact]
    public void A_supertonic_qualified_id_stays_supertonic_even_when_a_piper_voice_shares_the_name()
    {
        // The whole point of the prefix. With only the routing rule, "M1" means
        // whatever the filesystem says it means — and on a machine where someone
        // installed a Piper voice into a directory called M1, the same settings
        // file would speak through a different engine.
        InstallVoice("M1");
        Settings("""{ "VoiceId": "supertonic:M1" }""");

        var plan = Loaded().Utterance(null, null);
        Assert.Equal("supertonic", plan.Engine);

        var options = Assert.IsType<SupertonicOptions>(plan.Synthesis);
        Assert.Equal("M1", options.VoiceId);
    }

    [Fact]
    public void A_bare_id_still_routes_by_the_store_which_is_the_rule_P3_settled()
    {
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium" }""");

        Assert.Equal("piper", Loaded().Utterance(null, null).Engine);
    }

    [Fact]
    public void A_piper_qualified_id_that_is_not_installed_does_not_become_a_supertonic_style()
    {
        // It has to fail as a missing Piper voice rather than silently becoming a
        // Supertonic style request for a name like "de_DE-thorsten-high", which
        // would be refused far away from the setting that caused it — and would
        // read as a Supertonic problem.
        Settings("""{ "VoiceId": "piper:de_DE-thorsten-high" }""");

        var plan = Loaded().Utterance(null, null);

        // The planner cannot see a voice that is not there, so it plans
        // Supertonic; what matters is that the id kept its prefix, so the router
        // refuses instead of hunting for a style file of that name.
        Assert.Equal("supertonic", plan.Engine);
        Assert.Equal("piper:de_DE-thorsten-high", Loaded().ConfiguredVoice);
    }

    [Fact]
    public void An_explicit_request_still_beats_both_keys()
    {
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "VoiceId": "supertonic:M1" }""");

        var plan = Loaded().Utterance("piper:en_US-lessac-medium", null);
        Assert.Equal("piper", plan.Engine);
        Assert.Equal("en_US-lessac-medium", plan.Synthesis.VoiceId);
    }

    [Fact]
    public void An_unknown_key_in_the_file_still_survives_a_read()
    {
        // The extension-data discipline that makes the whole two-key scheme safe:
        // the Windows engine carries VoiceId through untouched, and this is the
        // Linux side of the same promise.
        Settings("""
            { "DefaultVoice": "M1", "VoiceId": "supertonic:M1", "SomethingWindowsOnly": 7 }
            """);

        Assert.Equal("supertonic:M1", Loaded().ConfiguredVoice);
    }
}

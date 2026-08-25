using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// How one utterance is planned — which options record, and how the requested
/// rate is split between the model and the DSP stage. The split is different per
/// engine, and getting it wrong is inaudible, which is why it is asserted.
/// </summary>
public class UtterancePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-plan-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public UtterancePlanTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
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

    /// <summary>lessac-medium's measured curve, trimmed to the rungs these tests reach.</summary>
    private void Calibrate(string id) =>
        new PiperRateCalibration(
        [
            new(1.50f, 0.733), new(1.00f, 1.000), new(0.85f, 1.071),
            new(0.70f, 1.288), new(0.60f, 1.485), new(0.50f, 1.578), new(0.20f, 1.985),
        ], PiperCalibrator.ProbeText, DateTimeOffset.UnixEpoch)
        .TrySave(Path.Combine(Models, PiperVoiceStore.FolderName, id, PiperVoiceStore.CalibrationFileName),
            out _);

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    [Fact]
    public void A_supertonic_voice_gets_supertonic_options_and_the_configured_split()
    {
        // 1.0 x 1.6 asked for, the model clamped to its ceiling of 1.3, and the
        // stretch carrying the rest. This is the shipped behaviour and it must
        // not have moved.
        Settings("""{ "EngineSpeed": 1.0, "DspRate": 1.6, "RateClampCeiling": 1.3 }""");
        var plan = Loaded().Utterance(null, null);

        var options = Assert.IsType<SupertonicOptions>(plan.Synthesis);
        Assert.Equal("M1", options.VoiceId);
        Assert.Equal(1.0f, options.Speed, 3);
        Assert.Equal("supertonic", plan.Engine);
        Assert.Equal(1.6, plan.StretchFactor, 3);
    }

    [Fact]
    public void A_piper_voice_gets_the_calibrated_length_scale_and_no_stretch()
    {
        InstallVoice("en_US-lessac-medium");
        Calibrate("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium", "EngineSpeed": 1.0, "DspRate": 1.35 }""");

        var plan = Loaded().Utterance(null, null);

        var options = Assert.IsType<PiperOptions>(plan.Synthesis);
        // Between the 1.288 and 1.485 rungs — and a long way from 1/1.35 = 0.74,
        // which is the whole reason the curve is measured.
        Assert.InRange(options.LengthScale, 0.60f, 0.70f);
        Assert.Equal(1.0, plan.StretchFactor);
        Assert.Equal("piper", plan.Engine);
        Assert.Null(plan.Note);

        // The voice's own inference defaults, not the record's placeholders.
        Assert.Equal(0.6f, options.NoiseScale);
        Assert.Equal(0.75f, options.NoiseW);
    }

    [Fact]
    public void Past_the_wall_the_stretch_carries_what_the_model_cannot()
    {
        InstallVoice("en_US-lessac-medium");
        Calibrate("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium", "EngineSpeed": 1.0, "DspRate": 2.4 }""");

        var plan = Loaded().Utterance(null, null);

        Assert.Equal(0.20f, Assert.IsType<PiperOptions>(plan.Synthesis).LengthScale, 3);
        Assert.Equal(2.4 / 1.985, plan.StretchFactor, 3);
    }

    [Fact]
    public void An_unmeasured_voice_speaks_and_says_its_rate_is_approximate()
    {
        // The fallback is upstream's reciprocal, which is up to 20% out. Speaking
        // beats refusing; saying nothing about it does not.
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium", "EngineSpeed": 1.0, "DspRate": 1.6 }""");

        var plan = Loaded().Utterance(null, null);

        Assert.Equal(0.625f, Assert.IsType<PiperOptions>(plan.Synthesis).LengthScale, 3);
        Assert.NotNull(plan.Note);
        Assert.Contains("approximate", plan.Note);
    }

    [Fact]
    public void At_1x_an_unmeasured_voice_has_nothing_to_apologise_for()
    {
        // length_scale 1.0 is exact by construction — the curve's own reference
        // point — so a note there would be noise on every default install.
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium", "EngineSpeed": 1.0, "DspRate": 1.0 }""");

        var plan = Loaded().Utterance(null, null);

        Assert.Equal(1.0f, Assert.IsType<PiperOptions>(plan.Synthesis).LengthScale, 3);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void The_request_wins_over_the_file_and_that_is_how_the_engine_changes()
    {
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "M1" }""");
        var config = Loaded();

        Assert.Equal("supertonic", config.Utterance(null, null).Engine);
        Assert.Equal("piper", config.Utterance("en_US-lessac-medium", null).Engine);
    }

    [Fact]
    public void A_calibration_written_while_the_daemon_runs_is_picked_up()
    {
        // The background measurement finishes mid-session and writes the file;
        // the next utterance has to plan from it with nothing to invalidate by
        // hand, or a voice stays uncalibrated until the daemon restarts.
        InstallVoice("en_US-lessac-medium");
        Settings("""{ "DefaultVoice": "en_US-lessac-medium", "EngineSpeed": 1.0, "DspRate": 1.35 }""");
        var config = Loaded();

        Assert.NotNull(config.Utterance(null, null).Note);

        Calibrate("en_US-lessac-medium");

        Assert.Null(config.Utterance(null, null).Note);
    }
}

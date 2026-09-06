using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What the benchmark measures, and what it therefore records and re-tests.
///
/// <para><b>Reported 2026-09-06 from the running install</b>, as a warning that
/// could not be cleared: "the recorded measurements no longer describe this
/// machine — the benchmark was measured at TotalStep 12, this daemon runs 6",
/// with a Re-measure button beside it. Pressing it did nothing, three times, and
/// the daemon log says why:</para>
///
/// <code>
/// benchmark failed: InvalidOperationException: no configuration could be
/// measured: FileNotFoundException: Voice style not found for 'en_US-hfc_male-medium'
/// </code>
///
/// <para><b>The sweep is a Supertonic measurement</b> — <c>MachineFacts.Current</c>
/// stamps every profile <c>Engine: "supertonic"</c> and there is no Piper sweep —
/// but it took its voice and its step count from whatever voice was IN FORCE. So
/// with a Piper voice selected it asked for a Supertonic style named after a Piper
/// voice, every row failed, and the staleness guard compared the profile's
/// Supertonic <c>TotalStep</c> against a Piper scope's, which is a number no
/// Piper voice has ever used. A warning with an unusable remedy, and the user
/// pressing the button was right.</para>
///
/// <para>This came out of the same family as the bug 0.2.13 fixed. That one
/// changed what the sweep RECORDS to the effective voice's step count and left
/// what it SWEEPS reading the file's top level, so the two halves were measuring
/// and reporting different configurations even before Piper made it fail
/// outright.</para>
/// </summary>
public sealed class BenchmarkVoiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-bench-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public BenchmarkVoiceTests()
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

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    /// <summary>
    /// THE REPORTED FILE, reduced. A Piper voice in force, and a
    /// <c>PerEngine.piper</c> section holding a <c>TotalStep</c> the Tune tab
    /// wrote while showing that field greyed out.
    /// </summary>
    private const string PiperInForce = """
        {
          "TotalStep": 12,
          "DefaultVoice": "F1",
          "VoiceId": "piper:en_US-hfc_male-medium",
          "PerEngine": {
            "piper": { "TotalStep": 6, "EngineSpeed": 1.2 }
          }
        }
        """;

    /// <summary>
    /// The sweep must name a Supertonic style. Handing it a Piper voice id is
    /// what produced "Voice style not found" on every row of every sweep the
    /// user ran.
    /// </summary>
    [Fact]
    public void The_benchmark_measures_a_supertonic_voice_even_when_piper_speaks()
    {
        Settings(PiperInForce);
        var config = Loaded();

        Assert.Equal("supertonic:F1", config.BenchmarkVoice);

        var options = Assert.IsType<SupertonicOptions>(config.Synthesis(config.BenchmarkVoice, "en"));
        Assert.Equal("F1", options.VoiceId);
    }

    /// <summary>
    /// And the number it records is that voice's, so the guard compares like
    /// with like. Six was a Piper scope's, and no Piper voice has diffusion
    /// steps at all — it is the file's 12 that describes what a sweep would
    /// measure.
    /// </summary>
    [Fact]
    public void The_step_count_the_benchmark_records_is_the_supertonic_one()
    {
        Settings(PiperInForce);
        var config = Loaded();

        Assert.Equal(12, config.TotalStepFor(config.BenchmarkVoice));
    }

    /// <summary>
    /// A Supertonic voice in force is unchanged: the benchmark voice IS the
    /// voice, scoped step count and all. This is what 0.2.13 fixed and it must
    /// stay fixed — reverting it to the file's top level fails here.
    /// </summary>
    [Fact]
    public void A_supertonic_voice_in_force_is_measured_as_itself()
    {
        Settings("""
            {
              "TotalStep": 12,
              "VoiceId": "supertonic:F5",
              "PerEngine": { "supertonic": { "TotalStep": 6 } }
            }
            """);
        var config = Loaded();

        Assert.Equal("supertonic:F5", config.BenchmarkVoice);
        Assert.Equal(6, config.TotalStepFor(config.BenchmarkVoice));
    }

    /// <summary>
    /// THE HALF 0.2.13 LEFT BEHIND. It changed what the sweep records and not
    /// what it runs, so a scoped step count made the profile say one number
    /// while the rows measured another. The options the sweep is handed have to
    /// be the ones the recorded facts describe.
    /// </summary>
    [Fact]
    public void What_the_sweep_runs_is_what_the_profile_says_it_ran()
    {
        Settings("""
            {
              "TotalStep": 12,
              "VoiceId": "supertonic:F5",
              "PerEngine": { "supertonic": { "TotalStep": 6, "EngineSpeed": 1.1 } }
            }
            """);
        var config = Loaded();

        var options = Assert.IsType<SupertonicOptions>(config.Synthesis("supertonic:F5", "en"));
        Assert.Equal(config.TotalStepFor("supertonic:F5"), options.TotalStep);
        Assert.Equal(6, options.TotalStep);
    }

    /// <summary>
    /// A Piper-only install has never chosen a Supertonic style, so
    /// <c>DefaultVoice</c> is its own default. That is still a style, which is
    /// all the sweep needs — and if the models for it are absent, the sweep
    /// fails with a style name a person can look for rather than with a Piper
    /// voice id that was never going to be there.
    /// </summary>
    [Fact]
    public void With_no_supertonic_voice_ever_chosen_the_default_style_is_measured()
    {
        Settings("""{ "VoiceId": "piper:en_GB-alba-medium" }""");
        var config = Loaded();

        Assert.Equal("supertonic:M1", config.BenchmarkVoice);
    }

    /// <summary>
    /// A <c>--voice</c> override is the voice in force, so a sweep run under one
    /// records it — and only falls back when that override is a Piper voice too.
    /// </summary>
    [Fact]
    public void A_supertonic_override_is_what_gets_measured()
    {
        Settings(PiperInForce);
        var config = Loaded();

        Assert.Equal("supertonic:M4", config.BenchmarkVoiceFor("supertonic:M4"));
        Assert.Equal("supertonic:F1", config.BenchmarkVoiceFor("piper:en_GB-alba-medium"));
        Assert.Equal("supertonic:F1", config.BenchmarkVoiceFor(null));
    }
}

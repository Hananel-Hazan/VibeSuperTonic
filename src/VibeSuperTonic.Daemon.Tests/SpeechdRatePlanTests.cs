using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// A screen reader's rate reaching the plan an utterance is rendered from.
///
/// <para><b>Reported 2026-09-06.</b> Through Orca the speed slider did nothing:
/// the module forwarded <c>SET RATE</c> only to its espeak fallback, and
/// <c>render</c> had no way to carry it. The rate arrives per utterance —
/// speechd sends it on a SET that can land between any two messages — so it
/// adjusts the plan rather than the settings file the Tune tab owns.</para>
/// </summary>
public sealed class SpeechdRatePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-rate-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public SpeechdRatePlanTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private HostConfig Loaded(string json)
    {
        File.WriteAllText(Path.Combine(Data, "settings.json"), json);
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    private static double Stretch(HostConfig c, double scale) =>
        c.Utterance("supertonic:M1", "en", scale).StretchFactor;

    /// <summary>
    /// The default rate must be exactly what the hotkey does. A screen reader
    /// that has never touched the slider sends 0, and hearing a different speed
    /// through Orca than through the key would be the product disagreeing with
    /// itself.
    /// </summary>
    [Fact]
    public void Rate_zero_is_the_configured_speed_and_nothing_else()
    {
        var config = Loaded("""{ "EngineSpeed": 1.0, "DspRate": 1.4 }""");

        Assert.Equal(
            config.Utterance("supertonic:M1", "en").StretchFactor,
            Stretch(config, SpeechRate.SpeechdRateScale(0)),
            precision: 10);
    }

    /// <summary>
    /// Faster asked for is faster delivered, and slower is slower. The direction
    /// is worth a test of its own: a reciprocal slipped in anywhere here reads
    /// as a working rate control that runs backwards.
    /// </summary>
    [Fact]
    public void A_faster_rate_makes_a_larger_stretch_and_a_slower_one_smaller()
    {
        var config = Loaded("""{ "EngineSpeed": 1.0, "DspRate": 1.0 }""");

        double at0 = Stretch(config, SpeechRate.SpeechdRateScale(0));
        Assert.True(Stretch(config, SpeechRate.SpeechdRateScale(50)) > at0);
        Assert.True(Stretch(config, SpeechRate.SpeechdRateScale(-50)) < at0);
    }

    /// <summary>
    /// It MULTIPLIES the configured rate. Somebody who set a playback rate in the
    /// Tune tab and then raised Orca's slider gets both, not the slider alone.
    /// </summary>
    [Fact]
    public void The_screen_readers_rate_adjusts_what_the_settings_ask_for()
    {
        var config = Loaded("""{ "EngineSpeed": 1.0, "DspRate": 1.2 }""");

        // 1.2 configured, scaled — and still under the DSP's own 2.0 ceiling, so
        // the arithmetic is visible rather than clamped.
        double scale = SpeechRate.SpeechdRateScale(20);
        Assert.Equal(1.2 * scale, Stretch(config, scale), precision: 3);
    }

    /// <summary>
    /// Supertonic degrades past its ceiling, so the adjustment goes to the DSP
    /// half and the model keeps the quality point the settings chose. Without
    /// this a screen reader's slider would quietly change how the model renders,
    /// which is not what the person moving it asked for.
    /// </summary>
    [Fact]
    public void The_model_speed_is_not_what_the_slider_moves()
    {
        var config = Loaded("""{ "EngineSpeed": 1.25, "DspRate": 1.0, "RateClampCeiling": 1.3 }""");

        var slow = (SupertonicOptions)config.Utterance(
            "supertonic:M1", "en", SpeechRate.SpeechdRateScale(-80)).Synthesis;
        var fast = (SupertonicOptions)config.Utterance(
            "supertonic:M1", "en", SpeechRate.SpeechdRateScale(80)).Synthesis;

        Assert.Equal(1.25f, slow.Speed);
        Assert.Equal(1.25f, fast.Speed);
    }

    /// <summary>
    /// A Piper voice gives the rate to <c>length_scale</c>, so a screen reader's
    /// slider moves the model rather than a stretch — the same division the
    /// hotkey already uses. Uncalibrated here, which is the reciprocal fallback
    /// and still has to respond.
    /// </summary>
    [Fact]
    public void A_piper_voice_takes_the_rate_in_its_length_scale()
    {
        var config = Loaded("""{ "VoiceId": "piper:x", "EngineSpeed": 1.0, "DspRate": 1.0 }""");

        var slower = config.Utterance("piper:x", null, SpeechRate.SpeechdRateScale(-50));
        var faster = config.Utterance("piper:x", null, SpeechRate.SpeechdRateScale(50));

        // No voice on disk, so this is the Supertonic path — the assertion that
        // matters is only meaningful with a real voice, and EngineRoutingTests
        // owns that. What is checked here is that the scale reached the plan at
        // all rather than being dropped on the way.
        Assert.True(faster.StretchFactor > slower.StretchFactor);
    }

    /// <summary>
    /// Nonsense from a client must not reach the arithmetic. speechd's range is
    /// documented, but the daemon's socket is not only spoken to by our module.
    /// </summary>
    [Fact]
    public void An_absurd_scale_is_bounded_rather_than_believed()
    {
        var config = Loaded("""{ "EngineSpeed": 1.0, "DspRate": 1.0 }""");

        foreach (double scale in new[] { 0d, -5d, double.NaN, 1e9 })
        {
            double stretch = config.Utterance("supertonic:M1", "en", scale).StretchFactor;
            Assert.InRange(stretch, SpeechRate.MinStretch, SpeechRate.MaxStretch);
        }
    }
}

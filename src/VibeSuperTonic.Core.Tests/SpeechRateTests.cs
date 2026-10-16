using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// <see cref="SpeechRate"/> is a port of <c>SapiEngine.ComputeSpeed</c> and
/// <c>ApplyVolume</c>, so what it needs is an oracle rather than a set of
/// opinions about what the right speed is.
///
/// <para>The reference below is a transcription of the engine's version at the
/// time of the port. If someone changes either side, this fails — which is the
/// entire point: two implementations of one rule is the drift Core exists to
/// prevent, and until the Windows convergence lands there genuinely are two.
/// The same pattern as <c>BoundaryPlannerTests</c>.</para>
/// </summary>
public class SpeechRateTests
{
    /// <summary>Transcribed from SapiEngine.ComputeSpeed. Do not "tidy".</summary>
    private static (float synthSpeed, double stretchFactor) Reference(
        int siteRate, int fragRate, float engineSpeed, float dspRate, float rateClampCeiling)
    {
        int rateAdjust = Math.Clamp(siteRate + fragRate, -10, 10);
        float baseEngine = engineSpeed > 0 ? engineSpeed : 1.05f;
        float ratePower = (float)Math.Pow(1.5, rateAdjust / 10.0);
        float ceiling = rateClampCeiling > 1.0f ? rateClampCeiling : 1.3f;

        float dsp = dspRate > 0 ? dspRate : 1.0f;
        float requestedTotal = baseEngine * dsp * ratePower;

        float synthSpeed = Math.Clamp(baseEngine * ratePower, 0.9f, ceiling);
        double stretchFactor = synthSpeed > 0 ? requestedTotal / synthSpeed : 1.0;
        stretchFactor = Math.Clamp(stretchFactor, 0.5, 2.0);
        return (synthSpeed, stretchFactor);
    }

    public static TheoryData<int, float, float, float> Cases()
    {
        var data = new TheoryData<int, float, float, float>();
        foreach (int rate in new[] { -10, -5, 0, 3, 10, 25 })          // 25 exercises the clamp
            foreach (float engine in new[] { 0f, 0.8f, 1.0f, 1.05f, 1.4f })  // 0 exercises the fallback
                foreach (float dsp in new[] { 0f, 0.5f, 1.0f, 1.35f, 2.5f })
                    foreach (float ceiling in new[] { 0f, 1.3f, 1.6f })
                        data.Add(rate, engine, dsp, ceiling);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_engine_across_the_grid(int rate, float engine, float dsp, float ceiling)
    {
        var expected = Reference(rate, 0, engine, dsp, ceiling);
        var actual = SpeechRate.Compute(engine, dsp, ceiling, rate);

        Assert.Equal(expected.synthSpeed, actual.SynthSpeed, 5);
        Assert.Equal(expected.stretchFactor, actual.StretchFactor, 5);
    }

    [Fact]
    public void The_real_installs_settings_produce_the_expected_split()
    {
        // The portable install on the development machine: EngineSpeed 1.0,
        // DspRate 1.35, ceiling 1.3. Linux ignored DspRate entirely before the
        // DSP stage existed, so this is the case that was audibly wrong.
        var (synth, stretch) = SpeechRate.Compute(1.0f, 1.35f, 1.3f);

        Assert.Equal(1.0f, synth, 5);          // inside the model's safe range, untouched
        Assert.Equal(1.35, stretch, 5);        // the whole 1.35x falls to the stretch
    }

    [Fact]
    public void Speed_beyond_the_models_ceiling_falls_to_the_stretch()
    {
        // The reason the split exists: the model is clamped and the DSP takes
        // the remainder, which is how the knob reaches past 1.3x at all.
        var (synth, stretch) = SpeechRate.Compute(2.0f, 1.0f, 1.3f);

        Assert.Equal(1.3f, synth, 5);
        Assert.Equal(2.0 / 1.3, stretch, 5);
    }

    [Theory]
    [InlineData(0f, 1.0f)]
    [InlineData(6f, 1.9952624f)]
    [InlineData(-12f, 0.2511886f)]
    [InlineData(60f, 1.9952624f)]     // clamped to +6
    [InlineData(-60f, 0.2511886f)]    // clamped to -12
    public void Volume_trim_converts_and_clamps(float db, float expected)
        => Assert.Equal(expected, SpeechRate.VolumeScale(db), 5);

    [Fact]
    public void Gain_saturates_rather_than_wrapping()
    {
        // A wrap turns a loud passage into noise, which is far worse than the
        // clipping it replaces — and it is what unchecked short arithmetic does.
        var pcm = new short[] { 20000, -20000, 100, -100 };
        SpeechRate.ApplyGain(pcm, 4f);

        Assert.Equal(short.MaxValue, pcm[0]);
        Assert.Equal(short.MinValue, pcm[1]);
        Assert.Equal(400, pcm[2]);
        Assert.Equal(-400, pcm[3]);
    }

    [Fact]
    public void Unity_gain_leaves_the_buffer_alone()
    {
        var pcm = new short[] { 1, -2, 3 };
        SpeechRate.ApplyGain(pcm, 1f);
        Assert.Equal(new short[] { 1, -2, 3 }, pcm);
    }
}

using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Coverage for the DSP rate path (R-3's "Sonic output length at 0.5×/1.0×/2.0×").
///
/// This is the third implementation of time-stretch in this engine — WSOLA and a
/// phase vocoder were both abandoned after perceptual A/B — and each replacement
/// was judged by ear. Ears do not catch a length regression, and a length
/// regression is what desynchronises the SAPI word-boundary events from the
/// audio: the engine reports boundaries against the *stretched* buffer, so an
/// output that is 5% long puts every later highlight 5% late.
///
/// Test signal is a synthetic voiced sound rather than noise or a pure tone.
/// Sonic finds its pitch period by AMDF over 65–400 Hz and its whole strategy is
/// pitch-synchronous, so material with no detectable period exercises only the
/// fallback path.
/// </summary>
public class TimeStretchTests
{
    private const int SampleRate = 44100;

    /// <summary>Sawtooth-ish harmonic stack at <paramref name="f0"/> Hz — a periodic signal with real harmonic content.</summary>
    private static short[] Voiced(int samples, double f0 = 120.0)
    {
        var buf = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SampleRate;
            double v = 0;
            for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * f0 * h * t) / h;
            buf[i] = (short)(v * 8000);
        }
        return buf;
    }

    private static double Rms(short[] b)
    {
        if (b.Length == 0) return 0;
        double sum = 0;
        foreach (short s in b) sum += (double)s * s;
        return Math.Sqrt(sum / b.Length);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Output_length_tracks_the_requested_factor(double factor)
    {
        var input = Voiced(SampleRate * 2);            // 2 seconds
        var output = TimeStretch.Stretch(input, factor);

        int expected = (int)(input.Length / factor);
        // Stretch truncates anything past `expected`, so the output can only be
        // short. 5% is generous for a pitch-synchronous algorithm that has to land
        // on whole periods, and still tight enough to catch a real regression.
        Assert.InRange(output.Length, (int)(expected * 0.95), expected);
    }

    [Fact]
    public void Factor_of_one_returns_the_input_untouched()
    {
        var input = Voiced(SampleRate);
        // Same instance, not merely equal: 1.0× is the default and runs on every
        // utterance, so it must not cost a copy of the buffer.
        Assert.Same(input, TimeStretch.Stretch(input, 1.0));
    }

    [Fact]
    public void Buffers_too_short_to_grip_are_passed_through()
    {
        // Below ~2x the maximum pitch period Sonic has nothing to align, so the
        // engine hands the buffer back rather than emitting noise.
        var input = Voiced(2048);
        Assert.Same(input, TimeStretch.Stretch(input, 2.0));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void Stretching_preserves_signal_energy(double factor)
    {
        // The failure this catches is the quiet one: a stretch that returns the
        // right NUMBER of samples and fills them with silence or with a fraction
        // of the original amplitude. Length alone would pass that.
        var input = Voiced(SampleRate * 2);
        var output = TimeStretch.Stretch(input, factor);

        double before = Rms(input), after = Rms(output);
        Assert.InRange(after, before * 0.7, before * 1.3);
    }

    [Fact]
    public void Stretched_output_is_not_silence()
    {
        var output = TimeStretch.Stretch(Voiced(SampleRate * 2), 1.5);
        Assert.Contains(output, s => Math.Abs((int)s) > 1000);
    }

    [Fact]
    public void Sonic_round_trips_write_flush_read()
    {
        // The layer underneath, exercised directly: TimeStretch is a thin wrapper
        // and a fault in this sequence would look like a wrapper bug.
        var input = Voiced(SampleRate);
        var sonic = new Sonic(SampleRate, 2.0f);
        sonic.WriteSamples(input, 0, input.Length);
        sonic.Flush();

        var output = new short[input.Length];
        int total = 0, read;
        while ((read = sonic.ReadSamples(output, total, output.Length - total)) > 0) total += read;

        Assert.InRange(total, input.Length / 2 * 9 / 10, input.Length / 2 * 11 / 10);
    }

    [Fact]
    public void Extreme_factors_do_not_throw_or_return_empty()
    {
        // The engine clamps DSP rate to [0.5, 2.0], but the clamp lives in the
        // caller and both branches of Sonic are implemented, so the bounds are
        // worth holding independently of whoever is enforcing them today.
        var input = Voiced(SampleRate);
        foreach (double f in new[] { 0.25, 0.5, 2.0, 4.0 })
        {
            var output = TimeStretch.Stretch(input, f);
            Assert.NotEmpty(output);
        }
    }
}

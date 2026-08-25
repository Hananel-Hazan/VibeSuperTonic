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
    private const int SampleRate = TimeStretch.SupertonicSampleRate;

    /// <summary>A Piper voice's rate. 16000 is the `low` tier; 22050 is everything above it.</summary>
    private const int PiperSampleRate = 22050;

    /// <summary>Sawtooth-ish harmonic stack at <paramref name="f0"/> Hz — a periodic signal with real harmonic content.</summary>
    private static short[] Voiced(int samples, double f0 = 120.0, int sampleRate = SampleRate)
    {
        var buf = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
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
        var output = TimeStretch.Stretch(input, factor, SampleRate);

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
        Assert.Same(input, TimeStretch.Stretch(input, 1.0, SampleRate));
    }

    [Fact]
    public void Buffers_too_short_to_grip_are_passed_through()
    {
        // Below ~2x the maximum pitch period Sonic has nothing to align, so the
        // engine hands the buffer back rather than emitting noise.
        var input = Voiced(2048);
        Assert.Same(input, TimeStretch.Stretch(input, 2.0, SampleRate));
    }

    [Fact]
    public void The_grip_floor_scales_with_the_rate()
    {
        // 2048 samples is a twentieth of a second at 44.1 kHz and a tenth at
        // 22.05 kHz — the same buffer is below the floor at one rate and above
        // it at the other, because the floor is about pitch PERIODS and a
        // period is a count of samples. A fixed 4096 would silently refuse to
        // stretch the first tenth-second of every Piper utterance.
        var input = Voiced(2048, f0: 200.0, sampleRate: PiperSampleRate);
        Assert.Same(input, TimeStretch.Stretch(input, 1.5, SampleRate));
        Assert.NotSame(input, TimeStretch.Stretch(input, 1.5, PiperSampleRate));
    }

    [Fact]
    public void The_rate_reaches_Sonic_and_changes_what_it_does()
    {
        // THIS IS THE TEST THE PARAMETER EXISTS FOR. Sonic's pitch search is a
        // band of 65-400 Hz expressed in SAMPLES, so the rate decides which
        // periods it can even consider: at 44100 the shortest is 110 samples,
        // which is longer than the true period of a 250 Hz voice at 22050
        // (88 samples). Told the wrong rate, it stretches that voice with an
        // algorithm that never sees its pitch.
        //
        // The assertion is that the two renders DIFFER, and it is deliberately
        // not stronger than that. Measured while writing this: on a synthetic
        // signal the wrong rate costs essentially nothing — same dominant
        // period to within a sample, same periodicity to four decimals, same
        // maximum sample-to-sample jump on a pitch glide. Which is the point.
        // It is inaudible on real speech too ([P2's listening
        // test](../../docs/PIPER-PLAN.md#speed-verdict): 35,156 of 35,675
        // samples different and the user could not tell), so nothing but the
        // signature was ever going to catch it.
        var input = Voiced(PiperSampleRate * 2, f0: 250.0, sampleRate: PiperSampleRate);

        var right = TimeStretch.Stretch(input, 1.5, PiperSampleRate);
        var wrong = TimeStretch.Stretch(input, 1.5, SampleRate);

        Assert.False(right.AsSpan().SequenceEqual(wrong),
            "the sample rate did not reach Sonic — the parameter is decorative");

        // And the half that IS checkable: at its own rate the stretch preserves
        // the pitch it is supposed to preserve.
        Assert.Equal(PiperSampleRate / 250, DominantPeriod(right, 40, 400), tolerance: 4);
    }

    [Fact]
    public void A_zero_or_negative_rate_is_refused()
    {
        // Rather than dividing by it inside the grip floor and stretching at a
        // rate nobody chose. The rate arrives from a voice config file on the
        // Piper path, so "0" is a plausible value for a truncated download.
        var input = Voiced(SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeStretch.Stretch(input, 1.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeStretch.Stretch(input, 1.5, -44100));
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
        var output = TimeStretch.Stretch(input, factor, SampleRate);

        double before = Rms(input), after = Rms(output);
        Assert.InRange(after, before * 0.7, before * 1.3);
    }

    [Fact]
    public void Stretched_output_is_not_silence()
    {
        var output = TimeStretch.Stretch(Voiced(SampleRate * 2), 1.5, SampleRate);
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
            var output = TimeStretch.Stretch(input, f, SampleRate);
            Assert.NotEmpty(output);
        }
    }

    /// <summary>
    /// Lag of the strongest autocorrelation peak, in samples — the fundamental
    /// period of a voiced signal. Searched over a band WIDER than either rate's
    /// Sonic band, so the answer is the signal's rather than the algorithm's.
    /// </summary>
    private static int DominantPeriod(short[] b, int minLag, int maxLag)
    {
        // A window past the transient at the start, long enough to hold several
        // periods of the longest lag under test.
        int start = Math.Min(b.Length / 4, 4000);
        int window = Math.Min(b.Length - start - maxLag, 8000);
        Assert.True(window > maxLag * 2, "signal too short to measure");

        var score = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = start; i < start + window; i++) sum += (double)b[i] * b[i + lag];
            score[lag] = sum / window;
        }

        // The SHORTEST lag that is essentially as strong as the best one.
        // Autocorrelation is just as high at every multiple of the period, so
        // taking the maximum outright answers "three periods" as readily as
        // "one" — which is how this first reported 265 for a signal at 88.
        double peak = double.NegativeInfinity;
        for (int lag = minLag; lag <= maxLag; lag++) peak = Math.Max(peak, score[lag]);
        for (int lag = minLag; lag <= maxLag; lag++)
            if (score[lag] >= peak * 0.9) return lag;
        return minLag;
    }
}

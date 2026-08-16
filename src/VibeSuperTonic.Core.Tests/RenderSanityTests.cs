using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The render-sanity test Phase 0 asked for, folded into Core.Tests as it
/// specified — "against a committed reference envelope rather than a committed
/// WAV".
///
/// Phase 0 cleared its gate on RTF, peak amplitude, RMS and duration, then
/// raised the objection those numbers cannot answer: <em>a fast render of
/// garbage would pass every one of them.</em> It settled the question by
/// comparing against a Windows render and confirming by ear. Neither is
/// available in CI, and neither catches a regression that arrives later.
///
/// The negative controls are the point of this file. Each one is a signal that
/// satisfies every measurement Phase 0's gate actually took — right duration,
/// right peak, no clipping, plausible RMS — and is not speech. A vocoder that
/// silently degrades produces something from this family, and so does a chunker
/// that stops emitting boundaries.
///
/// To regenerate the reference after a deliberate model or sample-text change:
/// render with <c>spike/linux-render</c>, then take
/// <c>RenderSanity.ComputeEnvelope(pcm, 44100)</c> and write one frame per line.
/// The header of the resource file records which text and voice produced it.
/// </summary>
public class RenderSanityTests
{
    private const int Rate = 44100;

    // ------------------------------------------------------- reference render

    private static SpeechEnvelope LoadReference()
    {
        // Copied next to the test assembly by the csproj.
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "reference-envelope.txt");
        var frames = File.ReadLines(path)
            .Where(l => !l.StartsWith('#') && l.Trim().Length > 0)
            .Select(l => float.Parse(l.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        return new SpeechEnvelope(frames, RenderSanity.DefaultHopMs);
    }

    [Fact]
    public void The_reference_render_is_recognised_as_speech()
    {
        var env = LoadReference();
        var (rate, strength) = RenderSanity.Modulation(env);

        Assert.InRange(rate, RenderSanity.MinModulationHz, RenderSanity.MaxModulationHz);
        Assert.True(strength >= RenderSanity.MinModulationStrength,
            $"modulation strength {strength:F4} below floor {RenderSanity.MinModulationStrength}");
        Assert.True(RenderSanity.SilentFrameFraction(env) >= RenderSanity.MinSilentFraction);
    }

    [Fact]
    public void The_reference_render_has_the_pause_structure_Phase_0_measured()
    {
        var env = LoadReference();
        var pauses = RenderSanity.FindPauses(env);

        // Four sentences plus leading and trailing silence. Asserting a range
        // rather than an exact count: the sampler is stochastic, so a re-render
        // of the same text moves pause edges by tens of milliseconds and can
        // merge or split a marginal one. The property that matters is that
        // phrase boundaries exist and are not one continuous blob.
        Assert.InRange(pauses.Count, 4, 12);

        // Sentence boundaries in this text fall near 1.5 s, 4.2 s and 6.4 s.
        foreach (double expected in new[] { 1.54, 4.22, 6.44 })
            Assert.Contains(pauses, p => Math.Abs(p.StartSeconds - expected) < 0.30);
    }

    [Fact]
    public void The_reference_render_runs_the_expected_duration()
    {
        // Guards against a chunker change that drops or duplicates a chunk —
        // the audible failure that RTF and peak amplitude cannot see.
        Assert.InRange(LoadReference().DurationSeconds, 11.5, 14.0);
    }

    // -------------------------------------------------------------- controls
    //
    // Every one of these has the right duration, a healthy peak and no clipping.

    public static TheoryData<string, short[]> NotSpeech() => new()
    {
        { "white noise", Generate(i => (short)(new Random(7 + i / 512).NextDouble() * 20000 - 10000)) },
        { "sustained tone", Generate(i => (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 10000)) },
        { "digital silence", Generate(_ => (short)0) },
        { "square wave at full scale", Generate(i => (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) > 0 ? short.MaxValue : short.MinValue)) },
        // The hardest one: a tone gated at exactly the syllable rate. Right
        // duration, right modulation frequency, textbook modulation strength —
        // and it never pauses, because real gaps between phrases are irregular
        // and longer than a 4 Hz trough.
        { "tone gated at 4 Hz", Generate(i =>
            (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 10000
                  * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * i / (double)Rate)))) },
    };

    [Theory]
    [MemberData(nameof(NotSpeech))]
    public void Plausible_looking_garbage_is_rejected(string label, short[] pcm)
    {
        var report = RenderSanity.Check(pcm, Rate);
        Assert.False(report.IsSpeechLike,
            $"{label} was accepted as speech — mod {report.ModulationRateHz:F2} Hz, " +
            $"strength {report.ModulationStrength:F3}, silence {report.SilentFrameFraction:P1}, " +
            $"{report.Pauses.Count} pause(s)");
        Assert.NotEmpty(report.Failures);
    }

    [Fact]
    public void A_rejection_says_which_criterion_failed()
    {
        // "Not speech" is not actionable; the report has to say which end of the
        // pipeline to look at.
        var report = RenderSanity.Check(Generate(_ => (short)0), Rate);
        Assert.Contains(report.Failures, f => f.Contains("silent", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------- primitive checks

    [Theory]
    [InlineData(3.0)]
    [InlineData(4.0)]
    [InlineData(6.0)]
    public void Modulation_rate_recovers_a_known_gate_frequency(double hz)
    {
        // The measurement a DSP-rate regression would move: audio played 1.5x
        // fast has its syllable rate 1.5x high.
        var pcm = Generate(i =>
            (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 10000
                  * (0.5 + 0.5 * Math.Sin(2 * Math.PI * hz * i / (double)Rate))));
        Assert.Equal(hz, RenderSanity.ModulationRateHz(RenderSanity.ComputeEnvelope(pcm, Rate)), precision: 1);
    }

    [Fact]
    public void Pauses_are_found_where_they_were_inserted()
    {
        // Tone, 1 s silence at 2 s, tone. One pause, at 2 s, one second long.
        var pcm = Generate(i =>
        {
            double t = i / (double)Rate;
            if (t >= 2.0 && t < 3.0) return (short)0;
            return (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 10000);
        }, seconds: 5);

        var pauses = RenderSanity.FindPauses(RenderSanity.ComputeEnvelope(pcm, Rate));
        Assert.Single(pauses);
        Assert.Equal(2.0, pauses[0].StartSeconds, precision: 1);
        Assert.Equal(1.0, pauses[0].DurationSeconds, precision: 1);
    }

    [Fact]
    public void Silences_shorter_than_a_phrase_boundary_are_not_pauses()
    {
        // 40 ms of silence is a stop consonant, not a pause. Counting it would
        // make every utterance look like it had dozens.
        var pcm = Generate(i =>
        {
            double t = i / (double)Rate;
            if (t >= 2.0 && t < 2.04) return (short)0;
            return (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 10000);
        }, seconds: 5);

        Assert.Empty(RenderSanity.FindPauses(RenderSanity.ComputeEnvelope(pcm, Rate)));
    }

    [Fact]
    public void Clipping_and_peak_are_measured()
    {
        Assert.Equal(0, RenderSanity.ClippedSamples(Generate(i => (short)(i % 1000 - 500))));
        Assert.True(RenderSanity.ClippedSamples(Generate(_ => short.MaxValue)) > 0);

        Assert.Equal(0.0, RenderSanity.PeakDbfs(Generate(_ => short.MaxValue)), precision: 1);
        Assert.Equal(double.NegativeInfinity, RenderSanity.PeakDbfs(Generate(_ => (short)0)));
    }

    [Fact]
    public void Envelope_is_volume_independent()
    {
        // Normalised, so a quieter render is the same render. Without this every
        // comparison would be dominated by the volume trim.
        //
        // The amplitude envelope here swings at 0.5 Hz — a two-second period, so
        // 500 ms buckets still see it vary. A faster envelope averages flat
        // inside the bucket and correlation becomes undefined; see
        // Correlation_is_undefined_not_zero_when_the_window_hides_the_structure.
        var loud = Generate(i =>
            (short)(Math.Sin(2 * Math.PI * 220 * i / (double)Rate) * 20000
                  * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.5 * i / (double)Rate))));
        var quiet = loud.Select(s => (short)(s / 8)).ToArray();

        Assert.Equal(1.0,
            RenderSanity.Correlate(
                RenderSanity.ComputeEnvelope(loud, Rate),
                RenderSanity.ComputeEnvelope(quiet, Rate)),
            precision: 2);
    }

    [Fact]
    public void Correlation_is_undefined_not_zero_when_the_window_hides_the_structure()
    {
        // A sharp edge worth pinning, because the return value lies about itself.
        // A 3 Hz signal averaged into 500 ms buckets is flat, so Pearson has no
        // variance to work with and the result is 0 — for two inputs that are
        // bit-identical. Read as "unrelated" that is exactly backwards.
        var pcm = Generate(i => (short)(Math.Sin(2 * Math.PI * 3 * i / (double)Rate) * 20000));
        var env = RenderSanity.ComputeEnvelope(pcm, Rate);

        Assert.Equal(0.0, RenderSanity.Correlate(env, env, windowMs: 500));

        // Narrow the window until the structure survives bucketing and the same
        // pair correlates perfectly.
        Assert.Equal(1.0, RenderSanity.Correlate(env, env, windowMs: 40), precision: 3);
    }

    [Fact]
    public void Correlation_separates_the_same_utterance_from_a_different_one()
    {
        var reference = LoadReference();

        // Same envelope with per-frame jitter — a second render of the same text
        // from a stochastic sampler. Must still read as the same utterance.
        var rng = new Random(11);
        var jittered = new SpeechEnvelope(
            reference.Frames.Select(f => (float)Math.Clamp(f + (rng.NextDouble() - 0.5) * 0.15, 0, 1)).ToArray(),
            reference.HopMs);
        Assert.True(RenderSanity.Correlate(reference, jittered) > 0.8);

        // Reversed: identical duration, identical amplitude distribution,
        // identical pause count — and a different utterance. Anything comparing
        // only summary statistics would call these equal.
        var reversed = new SpeechEnvelope(reference.Frames.Reverse().ToArray(), reference.HopMs);
        Assert.True(RenderSanity.Correlate(reference, reversed) < 0.8);
    }

    [Fact]
    public void Degenerate_inputs_do_not_throw()
    {
        Assert.Empty(RenderSanity.ComputeEnvelope([], Rate).Frames);
        Assert.Equal(0, RenderSanity.ModulationRateHz(new SpeechEnvelope([], RenderSanity.DefaultHopMs)));
        Assert.Empty(RenderSanity.FindPauses(new SpeechEnvelope([], RenderSanity.DefaultHopMs)));
        Assert.False(RenderSanity.Check([], Rate).IsSpeechLike);
    }

    private static short[] Generate(Func<int, short> f, double seconds = 12.9)
    {
        var buf = new short[(int)(seconds * Rate)];
        for (int i = 0; i < buf.Length; i++) buf[i] = f(i);
        return buf;
    }
}

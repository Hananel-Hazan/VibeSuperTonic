using VibeSuperTonic.Core.Synthesis.Piper;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The <c>length_scale</c> curve and its inversion.
///
/// <para>The numbers in the fixture are lessac-medium's, measured. They are not
/// arbitrary: the whole reason this class exists is that the obvious control —
/// pass the reciprocal of the rate — is 13% wrong at 1.6x and 20% wrong at 2x.
/// </para>
/// </summary>
public class PiperRateTests
{
    /// <summary>Measured on en_US-lessac-medium, 2026-08-25, noise off.</summary>
    private static PiperRateCalibration Lessac() => new(
    [
        new(2.00f, 0.568), new(1.50f, 0.733), new(1.25f, 0.887), new(1.00f, 1.000),
        new(0.85f, 1.071), new(0.70f, 1.288), new(0.60f, 1.485), new(0.50f, 1.578),
        new(0.40f, 1.739), new(0.30f, 1.867), new(0.20f, 1.985),
    ], PiperCalibrator.ProbeText, DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_reciprocal_is_what_the_calibration_is_correcting()
    {
        // 1/1.6 = 0.625, which this voice renders at about 1.45x — the fallback
        // is here so an unmeasured voice speaks, not because it is right.
        Assert.Equal(0.625f, PiperRateCalibration.Reciprocal(1.6).LengthScale, 3);
        Assert.Equal(1.0, PiperRateCalibration.Reciprocal(1.6).StretchFactor);

        // And the calibrated answer is a long way from it.
        Assert.True(Lessac().Plan(1.6).LengthScale < 0.55f);
    }

    [Theory]
    [InlineData(1.000, 1.00f)]
    [InlineData(1.071, 0.85f)]
    [InlineData(1.485, 0.60f)]
    public void A_measured_point_is_returned_exactly(double rate, float expected)
        => Assert.Equal(expected, Lessac().Plan(rate).LengthScale, 3);

    [Fact]
    public void Between_points_it_interpolates_and_the_stretch_stays_off()
    {
        var plan = Lessac().Plan(1.35);

        // Between the 1.288 and 1.485 rungs, so between their scales.
        Assert.InRange(plan.LengthScale, 0.60f, 0.70f);
        Assert.Equal(1.0, plan.StretchFactor);
    }

    [Fact]
    public void Past_the_wall_the_stretch_takes_the_remainder()
    {
        // The one case the "speed comes from Piper" decision cannot cover: the
        // model physically will not go faster, so 2.5x can only be served by the
        // stretch — which is why the shared stretch had to learn about sample
        // rates before this phase could use it.
        var plan = Lessac().Plan(2.5);

        Assert.Equal(0.20f, plan.LengthScale, 3);       // the fastest measured
        Assert.Equal(2.5 / 1.985, plan.StretchFactor, 3);
    }

    [Fact]
    public void Slower_than_anything_measured_is_symmetric()
    {
        var plan = Lessac().Plan(0.4);
        Assert.Equal(2.00f, plan.LengthScale, 3);
        Assert.Equal(0.4 / 0.568, plan.StretchFactor, 3);
        Assert.True(plan.StretchFactor < 1.0);
    }

    [Fact]
    public void A_curve_that_is_not_invertible_is_refused()
    {
        // Two rungs delivering the same rate is the saturation point, and it
        // belongs to the measurement rather than to the table: kept, it would
        // make the inversion ambiguous and claim a rate the model cannot reach.
        var ex = Assert.Throws<ArgumentException>(() => new PiperRateCalibration(
            [new(1.0f, 1.0), new(0.5f, 1.6), new(0.4f, 1.6)],
            "probe", DateTimeOffset.UnixEpoch));
        Assert.Contains("strictly increasing", ex.Message);
    }

    // ------------------------------------------------------------- measurement

    /// <summary>
    /// A model that saturates: duration falls with length_scale until it stops
    /// moving, which is what every voice measured actually does.
    /// </summary>
    private static double SaturatingSeconds(float scale)
    {
        double effective = Math.Max(scale, 0.35f);
        return 2.5 * (0.25 + 0.75 * effective);
    }

    [Fact]
    public void The_wall_is_found_rather_than_assumed()
    {
        var curve = PiperCalibrator.Measure(SaturatingSeconds, DateTimeOffset.UnixEpoch);

        // Everything below 0.35 renders identically, so the table stops there
        // rather than recording rates the model cannot deliver.
        Assert.All(curve.Points, p => Assert.True(p.LengthScale >= 0.3f));
        Assert.Equal(SaturatingSeconds(1.0f) / SaturatingSeconds(0.1f), curve.MaxRate, 3);
    }

    [Fact]
    public void A_flat_pair_mid_curve_is_skipped_and_not_read_as_the_wall()
    {
        // THIS COST A MEASUREMENT RUN. With a coarse ladder a flat pair only
        // happens at the wall, so "stop here" looked right; with a dense one two
        // adjacent rungs can render identically in the middle of the curve, and
        // the first German voice measured that way reported its wall at 1.000x.
        var curve = PiperCalibrator.Measure(
            scale => scale is > 0.79f and < 0.91f ? 2.5 * 0.85 : SaturatingSeconds(scale),
            DateTimeOffset.UnixEpoch);

        Assert.True(curve.MaxRate > 1.9, $"the wall came out at {curve.MaxRate:F3}x");
    }

    [Fact]
    public void One_times_means_the_voices_own_speed_not_length_scale_one()
    {
        // THIS IS A DEFECT THE SIXTH VOICE FOUND. en_GB-vctk-medium ships
        // inference.length_scale 1.4 — its natural speed is 40% slower than
        // length_scale 1.0 — and the five voices measured before it all shipped
        // 1.0, so an absolute ladder looked correct. Against vctk it produced a
        // uniform 18.7% error at EVERY rate including 1.0x, which cannot be a
        // curve problem: it is the anchor.
        const float voiceDefault = 1.4f;

        var curve = PiperCalibrator.Measure(
            scale => SaturatingSeconds(scale / voiceDefault),   // the model, expressed in this voice's terms
            DateTimeOffset.UnixEpoch,
            voiceDefault);

        // Asking for no speed change returns the voice's own length_scale, so
        // the voice sounds the way upstream meant it to.
        Assert.Equal(voiceDefault, curve.Plan(1.0).LengthScale, 3);

        // And every other rate is relative to that, not to 1.0.
        Assert.True(curve.Plan(1.35).LengthScale < voiceDefault);
        Assert.True(curve.Plan(0.9).LengthScale > voiceDefault);
    }

    [Fact]
    public void The_uncalibrated_fallback_is_anchored_the_same_way()
    {
        // Or an unmeasured vctk speaks 40% faster than an unmeasured lessac at
        // the same setting — which is worse than the 20% the fallback already
        // costs, and would not look like a rate bug at all.
        Assert.Equal(1.4f, PiperRateCalibration.Reciprocal(1.0, 1.4f).LengthScale, 3);
        Assert.Equal(0.875f, PiperRateCalibration.Reciprocal(1.6, 1.4f).LengthScale, 3);
    }

    [Fact]
    public void A_probe_that_renders_nothing_is_an_error_rather_than_a_curve()
    {
        Assert.Throws<InvalidOperationException>(
            () => PiperCalibrator.Measure(_ => 0, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_curve_survives_a_round_trip_through_a_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vst-piper-cal-{Guid.NewGuid():N}.json");
        try
        {
            var original = Lessac();
            Assert.True(original.TrySave(path, out _));

            var loaded = PiperRateCalibration.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(original.MaxRate, loaded.MaxRate, 6);
            Assert.Equal(original.Plan(1.35).LengthScale, loaded.Plan(1.35).LengthScale, 6);
            Assert.Equal(PiperCalibrator.ProbeText, loaded.ProbeText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_calibration_that_will_not_parse_is_no_calibration_rather_than_a_throw()
    {
        // Same bargain BenchmarkStore makes: a measurement that will not read is
        // a reason to measure again, never a reason to refuse to speak.
        string path = Path.Combine(Path.GetTempPath(), $"vst-piper-cal-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(PiperRateCalibration.Load(path));
            Assert.Null(PiperRateCalibration.Load(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

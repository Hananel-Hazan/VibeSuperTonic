using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The median and the spread.
///
/// <para><b>Why the spread carries its own tests.</b> It is the number that makes
/// every other number auditable, and it was added because its absence let a 5% tie
/// band ship against 15–18% noise — a band that discriminated on luck while every
/// test passed. A spread that is quietly wrong would restore exactly that
/// situation, with a reassuring percentage on screen.</para>
/// </summary>
public class MeasurementTests
{
    [Fact]
    public void The_middle_of_three_is_the_median()
    {
        Assert.Equal(5, Measurement.Median([9, 5, 1]));
    }

    [Fact]
    public void An_even_count_averages_the_middle_pair()
    {
        Assert.Equal(3, Measurement.Median([1, 2, 4, 8]));
    }

    [Fact]
    public void One_slow_run_cannot_carry_a_row()
    {
        // The reason it is a median and not a mean. A run can be arbitrarily
        // slowed by something else on the machine and cannot be made faster than
        // the hardware, so the noise is one-sided and a mean chases it.
        double median = Measurement.Median([100, 104, 900]);
        double mean = (100 + 104 + 900) / 3.0;

        Assert.Equal(104, median);
        Assert.True(median < mean / 3);
    }

    [Fact]
    public void The_input_is_not_reordered()
    {
        // The caller keeps the runs in the order they happened, and a median that
        // sorted in place would silently rewrite the record it was handed.
        var values = new double[] { 9, 5, 1 };
        Measurement.Median(values);

        Assert.Equal([9, 5, 1], values);
    }

    [Fact]
    public void Nothing_to_measure_is_an_error_rather_than_a_zero()
    {
        // Zero is the fastest number there is; returning it for "no data" is how a
        // failed row wins a sweep.
        Assert.Throws<ArgumentException>(() => Measurement.Median([]));
    }

    [Fact]
    public void Spread_is_the_full_range_over_the_median()
    {
        // 8-18% is the shape that actually turned up on real hardware, so the
        // arithmetic is pinned against a case in that range rather than a tidy one.
        Assert.Equal(0.20, Measurement.Spread([90, 100, 110]), 3);
    }

    [Fact]
    public void Identical_runs_have_no_spread()
    {
        Assert.Equal(0, Measurement.Spread([100, 100, 100]));
    }

    [Fact]
    public void A_single_run_reports_no_spread_rather_than_a_small_one()
    {
        // One run has no spread, which is a different thing from having a small
        // one. Reporting a small number here would make a single-run measurement
        // look like the most reliable row on the board.
        Assert.Equal(0, Measurement.Spread([100]));
    }

    [Fact]
    public void The_spread_of_the_two_real_sweeps_exceeds_the_band_that_failed()
    {
        // The actual rows from the two consecutive sweeps that picked 2 and then
        // 3. This is the comparison nobody could make at the time, because the
        // spread was not recorded — and it is the whole reason the field exists.
        var rowTwo = new double[] { 1194, 1407 };
        var rowThree = new double[] { 1223, 1295 };

        Assert.True(Measurement.Spread(rowTwo) > 0.05);
        Assert.True(Measurement.Spread(rowTwo) > Measurement.Spread(rowThree));
    }
}

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// The two numbers every timed row in this repository is reduced to, in one place
/// so the platforms cannot compute them differently.
///
/// <para><b>Why a spread is not optional here.</b> 8a shipped a 5% tie band that
/// was narrower than the 15–18% run-to-run variation it had to see through, so it
/// discriminated on noise and picked the luckiest configuration — twice, with a
/// timestamp on it, while every test passed. The fix was to widen the band; the
/// lesson was that a threshold is only as fine as the noise it is measured
/// through, and the only way anyone can check that is if the noise is reported
/// beside the number. A median with no spread beside it is a claim nobody can
/// audit.</para>
/// </summary>
public static class Measurement
{
    /// <summary>
    /// Middle value, so one scheduling hiccup cannot carry a row.
    ///
    /// <para>Median rather than mean because the noise here is one-sided: a run
    /// can be arbitrarily slowed by something else on the machine and cannot be
    /// made faster than the hardware. A mean chases the outlier; the middle of
    /// three does not.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="values"/> is empty.</exception>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) throw new ArgumentException("no values to take a median of", nameof(values));

        var sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// How far apart the runs were, as a fraction of the median — the noise floor
    /// this row was measured through.
    ///
    /// <para>Full range rather than a standard deviation, and on purpose: with
    /// three samples a standard deviation is a number with more precision than
    /// evidence, while "the runs spanned 18% of the middle one" is exactly the
    /// statement a tie band has to be compared against. Returns 0 for a single
    /// sample — one run has no spread, which is a different thing from having a
    /// small one, and callers that care should say how many runs they took.</para>
    /// </summary>
    public static double Spread(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2) return 0;

        double median = Median(values);
        if (median <= 0) return 0;

        double min = values[0], max = values[0];
        foreach (double v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (max - min) / median;
    }
}

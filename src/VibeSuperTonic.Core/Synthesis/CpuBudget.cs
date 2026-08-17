namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// How many cores inference may use.
///
/// <para>ONNX Runtime's default is to size its thread pool to every core it can
/// see, which is the right answer for a benchmark and the wrong one for a
/// read-aloud tool: the user asked for speech while they carry on working, and
/// a synthesiser that saturates the machine makes the desktop stutter every time
/// the hotkey is pressed. Reported from daily use on 2026-08-16, on a 20-thread
/// i7-12800H — a machine fast enough that throughput was never the problem.</para>
///
/// <para><b>It costs nothing, which was not the expected answer.</b> Phase 0
/// measured manual thread counts at roughly 2x worse than ORT's pick and
/// concluded the setting was not worth having. Re-measured on 2026-08-16 across
/// the whole range, median of three runs against 28.4 s of audio on a 20-thread
/// i7-12800H:</para>
///
/// <code>
/// threads   median wall   RTF     avg cores   core-seconds
/// auto      5.64 s        0.199   14.3        81
/// 8        11.78 s        0.415    8.2        97
/// 6         6.12 s        0.216    6.4        39
/// 4         5.08 s        0.179    4.3        22
/// 2         5.21 s        0.184    2.1        11
/// 1         9.22 s        0.325    1.0         9
/// </code>
///
/// <para>Four threads is <em>faster</em> than the auto pick and uses a quarter
/// of the machine. The model does not scale past a handful of threads, so beyond
/// that ORT spends the extra cores synchronising — which is why 8 is the worst
/// row on the board, slower even than 2. Phase 0's finding was real but its
/// conclusion was drawn from the wrong end of the range: it compared auto
/// against large explicit counts, where the penalty is genuine.</para>
///
/// <para>The floor is two threads. One measured 1.8x slower than two for a
/// saving of two core-seconds, which is the wrong trade on any machine.</para>
///
/// <para><b>A percentage is the wrong unit, and this is now the fallback rather
/// than the answer.</b> The curve above does not scale with the machine — 20% of
/// a 64-core server is 12 threads, which is past the knee and into the slow
/// region. The default here is correct for the machine it was measured on and is
/// a guess everywhere else, so the knee is measured per machine by
/// <c>vst-ctl benchmark</c> (<see cref="BenchmarkSweep"/>) and recorded in
/// <c>data/benchmark.json</c>. <see cref="CpuProfileDecision.Decide"/> prefers
/// that measurement whenever it still describes the machine and falls back to
/// this percentage when it does not — a machine that has never been swept, or one
/// whose profile arrived with a copied folder.</para>
///
/// <para><b>A stale profile is never scaled to fit.</b> There is no honest way to
/// turn someone else's thread count into this machine's, because the curve is not
/// monotonic — which is the whole reason the sweep exists rather than a formula.</para>
/// </summary>
public static class CpuBudget
{
    /// <summary>ORT's own choice — every core. What <c>IntraOpNumThreads = 0</c> means.</summary>
    public const int Auto = 0;

    /// <summary>
    /// Threads for a given share of the machine.
    /// </summary>
    /// <param name="maxCpuPercent">
    /// Percentage of logical processors inference may use. 100 or more hands the
    /// decision back to ORT rather than passing an explicit count equal to the
    /// core count: those are not the same thing, and the measured-best case is
    /// the one where nothing is passed at all.
    /// </param>
    /// <param name="processorCount">Logical processors on this machine.</param>
    /// <returns>A thread count for <c>IntraOpNumThreads</c>, or <see cref="Auto"/>.</returns>
    public static int IntraOpThreads(int maxCpuPercent, int processorCount)
    {
        if (processorCount < 1) processorCount = 1;

        // Non-positive is "unset" in a settings file that has never been edited,
        // and must not mean "no threads at all".
        if (maxCpuPercent <= 0 || maxCpuPercent >= 100) return Auto;

        // Floor, not round: 80% of 20 is 16 and 80% of 5 is 4, and the point is
        // to leave something over. Rounding up on 3 cores would hand back all 3.
        int threads = (int)Math.Floor(processorCount * (maxCpuPercent / 100.0));

        // Two is the floor, not one: a single thread measured 1.8x slower than
        // two while saving almost nothing. On a single-core machine the ceiling
        // wins, because a thread count above the core count helps no one.
        return Math.Clamp(threads, Math.Min(2, processorCount), processorCount);
    }
}

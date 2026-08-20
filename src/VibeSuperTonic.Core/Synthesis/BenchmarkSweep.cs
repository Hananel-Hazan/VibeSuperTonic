using System.Diagnostics;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>How far through a sweep, for a caller that wants to say so.</summary>
/// <param name="Index">1-based position of the row just finished.</param>
/// <param name="Total">Rows in the sweep.</param>
/// <param name="Row">What that row measured.</param>
public sealed record BenchmarkProgress(int Index, int Total, BenchmarkRow Row);

/// <summary>
/// The sweep a machine runs on itself to find its own thread count.
///
/// <para><b>Why this is measured rather than derived.</b> The cost curve is not
/// monotonic — on the machine that motivated this phase, 4 threads beat ORT's own
/// pick on wall clock while using a quarter of the machine, and 8 was the worst
/// row on the board, slower than 2 and burning more total CPU than auto. No
/// amount of reasoning from a core count recovers that shape, which is why
/// <see cref="CpuBudget"/>'s percentage is documented as a placeholder for this.</para>
///
/// <para><b>Core owns the algorithm and none of the machinery.</b> Building a
/// session means ONNX Runtime, and Core takes no packages at all (R-13), so the
/// host passes a factory. That is also what lets the whole sweep be tested with a
/// fake synthesizer whose timings are scripted — the pick rule is the part that
/// can be wrong in a way nobody notices.</para>
/// </summary>
public static class BenchmarkSweep
{
    /// <summary>
    /// The fixed sample. Short on purpose: the whole sweep has to finish inside a
    /// minute, and ~5 s of audio is already long enough that per-call noise is
    /// small against it. Fixed on purpose too — a sweep whose sample varies
    /// cannot be compared with the one it replaces.
    /// </summary>
    public const string SampleText =
        "This machine is measuring itself. The middle of three runs is the number that counts.";

    /// <summary>Timed runs per configuration. Median of three, so one hiccup cannot carry a row.</summary>
    public const int RunsPerCandidate = 3;

    /// <summary>
    /// How close to the fastest row still counts as "as fast as". Within this
    /// band the sweep prefers the row that uses less of the machine.
    ///
    /// <para><b>15%, and it was 5% first — the correction is the interesting
    /// part.</b> Two consecutive sweeps on an idle development machine picked 2
    /// threads and then 3. Both applied the rule correctly; the rule was wrong.
    /// Rows 2, 3 and 4 sat within 2% of each other while run-to-run noise on the
    /// same rows measured 15–18% (and one row moved 39%), so a 5% band was
    /// <em>narrower than the measurement error</em> and was therefore
    /// discriminating on noise. A tie band below the noise floor does not select
    /// the best configuration, it selects the luckiest one — and then writes it
    /// to a file with a timestamp.</para>
    ///
    /// <para>Widening it is also what the rule already says it wants. The band's
    /// whole purpose is to trade wall clock for cores, and the region it now
    /// spans is one where every row is above RTF 0.25 — five times faster than
    /// real time, so the difference is inaudible while the difference in cores
    /// occupied is a factor of seven.</para>
    /// </summary>
    public const double TieBandFraction = 0.15;

    /// <summary>
    /// What the sweep did not vary, recorded onto every profile it writes.
    ///
    /// <para>Inter-op threads stay at 1 because this model has no parallel
    /// branches to speak of. Spin behaviour is the interesting omission: a
    /// long-lived warm daemon is the one context where an ORT thread pool
    /// spinning between utterances is not free, and whether it costs anything
    /// measurable here is simply unknown.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> NotVaried =
    [
        "inter-op threads (held at 1)",
        "ORT thread spin behaviour",
        "TotalStep (taken from settings)",
    ];

    /// <summary>
    /// Thread counts worth trying on a machine this size, plus
    /// <see cref="CpuBudget.Auto"/> last.
    ///
    /// <para><c>auto</c> is kept even when an explicit count equals the core
    /// count, because they are genuinely different requests: passing nothing lets
    /// ORT size and shape the pool itself, and the measured-best case on the
    /// development machine was not the one where the count was spelled out.</para>
    /// </summary>
    public static IReadOnlyList<int> Candidates(int processorCount)
    {
        if (processorCount < 1) processorCount = 1;

        var counts = new List<int>();
        foreach (int n in (ReadOnlySpan<int>)[1, 2, 3, 4, 6, 8])
            if (n <= processorCount) counts.Add(n);

        // A single-core machine would otherwise sweep nothing but auto.
        if (counts.Count == 0) counts.Add(1);

        counts.Add(CpuBudget.Auto);
        return counts;
    }

    /// <summary>
    /// Measure every candidate and pick one.
    /// </summary>
    /// <param name="machine">
    /// What this is being measured against. Stamped onto the profile unchanged —
    /// gathering it means reading <c>/proc</c>, which is platform policy and does
    /// not belong in Core (R-12).
    /// </param>
    /// <param name="synthesizerFor">
    /// Builds a synthesizer for a given intra-op thread count. Called once per
    /// candidate and disposed before the next, so peak memory is one session
    /// rather than one per row.
    /// </param>
    /// <param name="options">Voice, language and TotalStep to render the sample with.</param>
    /// <param name="candidates">Defaults to <see cref="Candidates"/> for the machine's core count.</param>
    /// <param name="onProgress">Called after each row, on the sweeping thread.</param>
    /// <exception cref="InvalidOperationException">Every candidate failed, so there is nothing to pick.</exception>
    public static BenchmarkProfile Run(
        BenchmarkMachine machine,
        Func<int, ISynthesizer> synthesizerFor,
        SynthesisOptions options,
        IReadOnlyList<int>? candidates = null,
        Action<BenchmarkProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(synthesizerFor);
        ArgumentNullException.ThrowIfNull(options);

        candidates ??= Candidates(machine.LogicalProcessors);

        var rows = new List<BenchmarkRow>(candidates.Count);
        double sampleSeconds = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (row, seconds) = Measure(candidates[i], synthesizerFor, options, cancellationToken);
            rows.Add(row);
            if (seconds > 0) sampleSeconds = seconds;

            onProgress?.Invoke(new BenchmarkProgress(i + 1, candidates.Count, row));
        }

        var pick = Pick(rows, machine.LogicalProcessors)
            ?? throw new InvalidOperationException(
                "no configuration could be measured: " +
                (rows.FirstOrDefault(r => r.Error is not null)?.Error ?? "every candidate failed"));

        return new BenchmarkProfile(
            Threads: pick.Threads,
            Provider: pick.Provider,
            // Round-trip format: this is read back by a machine before it is read
            // by a person, and a locale-formatted date in a portable folder is a
            // date that parses differently on the machine it moves to.
            MeasuredUtc: DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Machine: machine,
            Table: rows,
            NotVaried: NotVaried,
            SampleSeconds: sampleSeconds,
            TieBand: TieBandFraction);
    }

    /// <summary>
    /// Fastest, then least of the machine — in that order, and the order is the
    /// point.
    ///
    /// <para>The goal is not peak speed. It is peak speed that leaves the desktop
    /// alone: the user asked to be read to <em>while they carry on working</em>,
    /// so between two configurations that finish together, the one occupying a
    /// quarter of the cores is strictly better and the sweep should never pick
    /// the other one.</para>
    ///
    /// <para>Returns null when nothing was measurable.</para>
    /// </summary>
    public static BenchmarkRow? Pick(IReadOnlyList<BenchmarkRow> rows, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var usable = rows.Where(r => !r.Failed && r.MedianWallMs > 0).ToList();
        if (usable.Count == 0) return null;

        double best = usable.Min(r => r.MedianWallMs);
        double band = best * (1 + TieBandFraction);

        return usable
            .Where(r => r.MedianWallMs <= band)
            // Auto is "every core" as far as the desktop is concerned, so it
            // sorts last among equals rather than winning by having no number.
            .OrderBy(r => r.Threads == CpuBudget.Auto ? processorCount : r.Threads)
            .ThenBy(r => r.MedianWallMs)
            .First();
    }

    private static (BenchmarkRow Row, double SampleSeconds) Measure(
        int threads, Func<int, ISynthesizer> synthesizerFor, SynthesisOptions options,
        CancellationToken cancellationToken)
    {
        string label = threads == CpuBudget.Auto ? "auto" : threads.ToString();

        ISynthesizer? synth = null;
        try
        {
            synth = synthesizerFor(threads);

            // Warm-up, discarded. It pays the model load and lets ORT's arena
            // reach steady state, neither of which the user pays per utterance on
            // a daemon that stays warm — so timing them would measure a cost the
            // product does not have.
            synth.Synthesize(SampleText, options, cancellationToken);

            int sampleRate = synth.SampleRate;
            var walls = new double[RunsPerCandidate];
            var cpuSeconds = new double[RunsPerCandidate];
            double audioSeconds = 0;

            for (int run = 0; run < RunsPerCandidate; run++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan cpuBefore = ProcessCpuTime();
                long start = Stopwatch.GetTimestamp();

                short[] pcm = synth.Synthesize(SampleText, options, cancellationToken);

                walls[run] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                cpuSeconds[run] = (ProcessCpuTime() - cpuBefore).TotalSeconds;

                if (sampleRate > 0) audioSeconds = (double)pcm.Length / sampleRate;
            }

            double medianWall = Measurement.Median(walls);
            double medianCpu = Measurement.Median(cpuSeconds);
            double wallSeconds = medianWall / 1000.0;

            return (new BenchmarkRow(
                Label: label,
                Threads: threads,
                Provider: "cpu",
                MedianWallMs: medianWall,
                Rtf: audioSeconds > 0 ? wallSeconds / audioSeconds : 0,
                AvgCores: wallSeconds > 0 ? medianCpu / wallSeconds : 0,
                CoreSeconds: medianCpu,
                // The noise this row was measured through, carried alongside it so
                // the tie band can be compared against something rather than
                // trusted. This is the number whose absence let a 5% band ship.
                Spread: Measurement.Spread(walls)), audioSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One configuration failing is not the sweep failing. A machine that
            // cannot build a session at 8 threads still deserves an answer about
            // the seven counts that worked, and the row records why rather than
            // vanishing — a gap in the table reads as "not tried".
            return (new BenchmarkRow(label, threads, "cpu", 0, 0, 0, 0, Spread: 0,
                Failed: true, Error: $"{ex.GetType().Name}: {ex.Message}"), 0);
        }
        finally
        {
            synth?.Dispose();
        }
    }

    /// <summary>
    /// Process-wide CPU time. Process-wide is the honest unit here — ORT's pool
    /// threads are not ours to enumerate — which is also why the sweep refuses to
    /// run while the daemon is doing anything else.
    /// </summary>
    private static TimeSpan ProcessCpuTime()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.TotalProcessorTime;
        }
        catch
        {
            // Cost accounting is a nice-to-have; the wall clock is the measurement.
            return TimeSpan.Zero;
        }
    }

}

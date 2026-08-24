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
        Func<int, string, ISynthesizer> synthesizerFor,
        SynthesisOptions options,
        IReadOnlyList<int>? candidates = null,
        Action<BenchmarkProgress>? onProgress = null,
        IReadOnlyList<string>? gpuProviders = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(synthesizerFor);
        ArgumentNullException.ThrowIfNull(options);

        candidates ??= Candidates(machine.LogicalProcessors);
        gpuProviders ??= [];

        var rows = new List<BenchmarkRow>(candidates.Count + gpuProviders.Count);
        double sampleSeconds = 0;
        int total = candidates.Count + gpuProviders.Count;

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (row, seconds) = Measure(
                candidates[i], ExecutionProviders.Cpu, synthesizerFor, options, cancellationToken);
            rows.Add(row);
            if (seconds > 0) sampleSeconds = seconds;

            onProgress?.Invoke(new BenchmarkProgress(i + 1, total, row));
        }

        // GPU rows run at the thread count the CPU rows just chose, not at auto
        // and not at one. ORT leaves shape-related and unassigned nodes on the CPU
        // — the Phase 8b spike counted 46 to 80 Memcpy nodes added per graph — so
        // a GPU row still has a CPU side, and measuring it with a thread count the
        // daemon would never use measures a configuration nobody will run.
        int gpuThreads = Pick(rows, machine.LogicalProcessors)?.Threads ?? CpuBudget.Auto;

        for (int i = 0; i < gpuProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (row, seconds) = Measure(
                gpuThreads, gpuProviders[i], synthesizerFor, options, cancellationToken);
            rows.Add(row);
            if (seconds > 0) sampleSeconds = seconds;

            onProgress?.Invoke(new BenchmarkProgress(candidates.Count + i + 1, total, row));
        }

        var pick = PickAcross(rows, machine.LogicalProcessors)
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
    /// The pick across every provider the sweep measured: the best CPU row, and
    /// the GPU only when it beats that row by more than the tie band.
    ///
    /// <para><b>A GPU has to win, not tie.</b> It costs a 330 MB provider
    /// library, a gigabyte-scale CUDA dependency, VRAM, and a laptop's battery —
    /// so a dead heat should leave the machine on the provider that needs none of
    /// those. Reusing <see cref="TieBandFraction"/> as the margin keeps one number
    /// answering one question: what counts as a real difference rather than
    /// measurement noise.</para>
    ///
    /// <para>Measured 2026-08-24 on an RTX A2000, this is not a close call — CUDA
    /// took first audio from 802 ms to 77 ms and RTF from 0.180 to 0.018 — which
    /// is exactly why the margin is written down now, while nothing depends on
    /// where it sits.</para>
    /// </summary>
    public static BenchmarkRow? PickAcross(IReadOnlyList<BenchmarkRow> rows, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var cpuPick = Pick(rows.Where(r => r.Provider == ExecutionProviders.Cpu).ToList(), processorCount);

        var gpuPick = rows
            .Where(r => r.Provider != ExecutionProviders.Cpu && !r.Failed && r.MedianWallMs > 0)
            .OrderBy(r => r.MedianWallMs)
            .FirstOrDefault();

        if (cpuPick is null) return gpuPick;
        if (gpuPick is null) return cpuPick;

        return gpuPick.MedianWallMs < cpuPick.MedianWallMs * (1 - TieBandFraction)
            ? gpuPick
            : cpuPick;
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
            // "Least of the machine", MEASURED rather than assumed.
            //
            // This used to sort on the requested thread count, with Auto mapped to
            // the processor count so it could not win by having no number. That was
            // right about CPU auto and wrong about everything else with Threads = 0
            // — which is every DirectML row. Measured 2026-08-20 on a quiet
            // i7-12800H: DirectML was the FASTEST row on the board (1159 ms against
            // 6 threads at 1201) using 0.6 cores against 6.1, and lost, because a
            // rule about CPU auto sorted it as though it had occupied twenty.
            //
            // AvgCores is what the old key was a proxy for, and it gets both cases
            // right without a special case: CPU auto measures ~13 cores here and
            // still sorts last, a GPU row measures ~0.6 and sorts first. Falls back
            // to the old proxy when a row carries no core figure — a profile stored
            // before this field meant anything, or a scripted synthesizer in a test
            // that never burned any CPU.
            .OrderBy(r => r.AvgCores > 0
                ? r.AvgCores
                : (r.Threads == CpuBudget.Auto ? processorCount : r.Threads))
            .ThenBy(r => r.MedianWallMs)
            .First();
    }

    private static (BenchmarkRow Row, double SampleSeconds) Measure(
        int threads, string provider, Func<int, string, ISynthesizer> synthesizerFor,
        SynthesisOptions options, CancellationToken cancellationToken)
    {
        string count = threads == CpuBudget.Auto ? "auto" : threads.ToString();
        string label = provider == ExecutionProviders.Cpu ? count : $"{provider} ({count})";

        ISynthesizer? synth = null;
        try
        {
            synth = synthesizerFor(threads, provider);

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
                Provider: provider,
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
            return (new BenchmarkRow(label, threads, provider, 0, 0, 0, 0, Spread: 0,
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

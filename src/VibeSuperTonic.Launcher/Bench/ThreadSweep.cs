using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Launcher.Host;

namespace VibeSuperTonic.Launcher.Bench;

/// <summary>One configuration the sweep will try.</summary>
/// <param name="Threads">Intra-op threads to ask ORT for. 0 is <see cref="CpuBudget.Auto"/>.</param>
/// <param name="Provider">"cpu" or "directml".</param>
internal sealed record SweepCandidate(int Threads, string Provider)
{
    public bool IsGpu => Provider == ThreadSweep.DirectMlProvider;

    /// <summary>How this row is named to a human.</summary>
    public string Label => IsGpu
        ? "DirectML"
        : Threads == CpuBudget.Auto ? "auto" : Threads.ToString();
}

/// <summary>What a sweep produced, including the reasons it produced nothing.</summary>
/// <param name="Profile">Null when the sweep refused to run or nothing could be measured.</param>
/// <param name="Notes">Everything the user needs to know to read the table honestly.</param>
/// <param name="Refusal">Set when the sweep declined to start, with the reason.</param>
internal sealed record SweepOutcome(
    BenchmarkProfile? Profile,
    IReadOnlyList<string> Notes,
    string? Refusal = null);

/// <summary>
/// The sweep a Windows machine runs on itself to find its own thread count and
/// execution provider — the Windows half of
/// <see href="LINUX-PORT-PLAN.md">Phase 8a</see>, and phase W1 of the Windows plan.
///
/// <para><b>Why this is measured rather than derived.</b> The cost curve is not
/// monotonic. Measured by hand on a 20-thread i7-12800H, four threads beat ORT's
/// own pick on wall clock while using a quarter of the machine, and eight was the
/// worst row on the board — slower than two. No amount of reasoning from a core
/// count recovers that shape, which is why <c>OnnxThreads</c> defaulting to 0 has
/// been a guess since the first release.</para>
///
/// <para><b>Why it cannot reuse <see cref="BenchmarkSweep.Run"/>.</b> Core's sweep
/// times <c>ISynthesizer.Synthesize</c> with a stopwatch, and Windows has nothing
/// to time: the engine is a COM in-process server, this process is self-contained
/// single-file, and driving ONNX here access-violates
/// (<see cref="RenderHostProcess"/>). Worse, a stopwatch would be the wrong
/// instrument even if it worked — the engine paces its writes to real time so live
/// playback keeps its trailing words, and it cannot tell a file render from an
/// audio device, so wall-clock time through SAPI is pinned near the audio length
/// for every configuration. The engine measures its own throughput and publishes
/// it as <c>RollingRtf</c>; that is what these rows are built from.</para>
///
/// <para>What <em>is</em> shared with Linux is everything that can be wrong in a
/// way nobody notices: the candidate list, the pick rule, the tie band, the
/// staleness rules, the median and the spread, and the file the answer is written
/// to. Those are Core's, unchanged.</para>
///
/// <para><b>One process per candidate, and that is not laziness.</b> The engine's
/// ONNX session is a process-wide singleton built once from <c>settings.json</c>
/// at first activation (<c>SupertonicAdapter._sharedTts</c>). A sweep that rewrote
/// the thread count between rows inside one host would measure the first
/// configuration seven times and produce a complete, plausible, entirely fictional
/// table — with a timestamp on it. A fresh process per row cannot do that, and the
/// engine's reported thread count is checked against the request anyway, because
/// the cost of being wrong here is a wrong answer that looks like a measurement.</para>
/// </summary>
internal static class ThreadSweep
{
    public const string CpuProvider = "cpu";
    public const string DirectMlProvider = "directml";

    /// <summary>
    /// Timed runs per configuration.
    ///
    /// <para><b>Three, and five was tried on hardware and made it worse.</b>
    /// Linux times <c>Synthesize</c> directly and measured 15–18% run-to-run.
    /// Windows can only see the engine's own <c>RollingRtf</c> through a paced SAPI
    /// render, and three sweeps on real hardware measured row spreads of 3–556%.
    /// The CPU rows did not reproduce: the fastest was 2 threads, then 3, then 4.
    /// That is 8a's failure on a new platform for a new reason.</para>
    ///
    /// <para>Raising the count only helps when the noise is sampling variance, and
    /// it is not. Over a multi-minute sweep the ambient load on a working desktop
    /// drifts, so a longer sweep straddles more of that drift: the five-run attempt
    /// produced the worst row of all three (556% spread, one run having stalled
    /// outright) and took 442 s against 327 s. "Take more samples" is the reflex
    /// this paragraph exists to stop the next person repeating.</para>
    ///
    /// <para><b>DirectML was the steady row throughout</b> — 1405, 1370 and 1292 ms
    /// across the three sweeps, fastest overall in two of them — precisely because
    /// the GPU is the one part of this machine nothing else on the desktop is
    /// competing for.</para>
    ///
    /// <para><b>The real fix is not a sample count.</b> Every render is paced to
    /// real time by the engine, so a 4.9 s sample costs ~6 s of wall clock to
    /// measure ~1.2 s of compute — which is also why the sweep is long enough to
    /// drift. An unpaced bench path would buy fifteen runs in less time than three
    /// cost here, and shrink the window over which the machine can change its mind.
    /// That is engine work, on the shipping platform, and it belongs in its own
    /// change rather than at the end of this one.</para>
    /// </summary>
    public const int RunsPerCandidate = 3;

    /// <summary>
    /// Untimed full-length renders before timing starts, on top of the render
    /// helper's own short warm-up.
    ///
    /// <para><b>Two, and the number was measured rather than chosen.</b> Seven
    /// consecutive timed runs of the same sample in one warm process, i7-12800H,
    /// 2026-08-19: 0.287, 0.261, 0.250, 0.248, 0.233, 0.248, 0.232. The first two
    /// are still settling — the helper's short warm-up phrase pays COM activation
    /// and the model load, but not the arena growth and clock ramp a full-length
    /// utterance provokes. From run 3 the numbers hold to about 7%.</para>
    ///
    /// <para>Without this the first timed run of every row is inflated by the same
    /// decay, the medians all land mid-curve, and the sweep orders its rows by how
    /// far through settling each one happened to be. It would have looked exactly
    /// like a measurement — and the spread would have reported ~50–100% for every
    /// row, which is the only reason it was caught.</para>
    /// </summary>
    public const int WarmupsPerCandidate = 2;

    /// <summary>
    /// Busy-percentage at or above which the sweep refuses to run unaided.
    ///
    /// <para><b>25, and it is deliberately NOT the Linux guard's 15.</b> It shipped
    /// as 15 on 2026-08-19 to match, and refused two of the first three sweeps on
    /// an ordinary working desktop — the exact failure the Linux note warns about,
    /// where a guard that fires on an idle machine gets its override ticked
    /// permanently and stops guarding anything.</para>
    ///
    /// <para>Measured with this guard's own instrument, twelve consecutive 1 s
    /// windows on the development machine at rest (browser and a dictionary open,
    /// nothing running):</para>
    ///
    /// <code>
    /// 12.5  12.2  10.6  10.6  14.6  16.6  11.1  11.6  9.7  8.4  12.8  15.0
    /// min 8.4   median 11.6   max 16.6
    /// </code>
    ///
    /// <para>A 15% threshold sits inside that distribution and fires on roughly one
    /// resting sample in six. 25% clears the observed maximum with margin while
    /// staying far below anything that actually competes for cores — a compile, an
    /// encode or a game pegs several of them and reads far higher.</para>
    ///
    /// <para><b>Why the platforms legitimately differ.</b> Windows measured 8–17%
    /// where Linux measured 5–9% over the same window length, and the two are not
    /// even the same instrument: <c>GetSystemTimes</c> against <c>/proc/stat</c>.
    /// Perfmon's own counter reported a 10.3% median on this machine at the same
    /// moment this one read 11.6%, so the number is instrument-specific as well as
    /// platform-specific. Copying the constant across would have been the tidier
    /// mistake — [trap 15] in the port plan is precisely that a threshold is a
    /// claim about the world, and only the world can check it.</para>
    /// </summary>
    public const double LoadGuardPercent = 25;

    /// <summary>
    /// What the sweep did not vary, recorded onto every profile it writes.
    ///
    /// <para>A number with no stated scope reads as a number that considered
    /// everything. This one did not consider inter-op threads, ORT's spin
    /// behaviour, or the DirectML device — and it deliberately tries only one
    /// thread count on the GPU, because the question asked of a GPU row is whether
    /// the provider wins at all, not which thread count suits it.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> NotVaried =
    [
        "inter-op threads (held at the configured value)",
        "ORT thread spin behaviour",
        "TotalStep (taken from settings)",
        "DirectML device id (held at the configured value)",
        "intra-op threads on the DirectML row (auto only)",
    ];

    /// <summary>
    /// Every configuration worth trying on this machine.
    ///
    /// <para>The CPU counts are Core's list, so the two platforms sweep the same
    /// shape. DirectML gets exactly one row, at auto: its ops run on the GPU, so
    /// the intra-op count mostly governs the CPU fallback ops, and sweeping seven
    /// GPU rows would double a sweep that is already minutes long to answer a
    /// question nobody asked.</para>
    /// </summary>
    public static IReadOnlyList<SweepCandidate> Candidates(int processorCount, bool includeGpu)
    {
        var list = BenchmarkSweep.Candidates(processorCount)
            .Select(t => new SweepCandidate(t, CpuProvider))
            .ToList();

        if (includeGpu) list.Add(new SweepCandidate(CpuBudget.Auto, DirectMlProvider));
        return list;
    }

    /// <summary>
    /// Measure every candidate, pick one, and write the profile.
    ///
    /// <para>The caller owns settings restoration — see
    /// <see cref="EngineSettingsRegistry.BeginTemporaryChange"/> — because a sweep
    /// applies each candidate in turn and a per-row restore would snapshot an
    /// already-modified state and put back the wrong values.</para>
    /// </summary>
    public static async Task<SweepOutcome> RunAsync(
        string voiceId,
        bool includeGpu,
        bool force,
        IProgress<string>? log,
        IProgress<BenchmarkProgress>? onRow,
        CancellationToken ct)
    {
        RenderHostProcess.EnsureAvailable();

        var notes = new List<string>();

        // Measure the machine BEFORE touching anything. A sweep run while a build
        // is going picks a profile shaped by the build and then keeps it, with a
        // timestamp, looking every bit as authoritative as a good one.
        double load = await MachineFacts.IdleCpuPercentAsync(cancellationToken: ct);
        if (load >= LoadGuardPercent && !force)
        {
            return new SweepOutcome(null, notes,
                $"This machine is {load:F0}% busy. A sweep run now measures whatever else is running, " +
                "and the result would be saved with a timestamp as if it were sound. " +
                "Close what is working and try again, or tick \"Measure anyway\".");
        }

        if (load >= LoadGuardPercent)
            notes.Add($"Measured with the machine {load:F0}% busy (forced): treat the table as indicative.");
        else if (load < 0)
            notes.Add("The machine's load before the sweep could not be read, so it is unknown rather than low.");

        var settings = EngineSettingsRegistry.Load();
        var machine = MachineFacts.Current(
            MachineFacts.ModelsRoot, settings.TotalStep, voiceId, settings.Language, Math.Max(load, 0));

        var candidates = Candidates(machine.LogicalProcessors, includeGpu);
        log?.Report($"Sweeping {candidates.Count} configurations on {machine.LogicalProcessors} logical processors " +
                    $"({machine.Cpu}), TotalStep {machine.TotalStep}, voice {voiceId}.");

        var rows = new List<BenchmarkRow>(candidates.Count);
        double sampleSeconds = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (row, seconds) = await Task.Run(
                () => Measure(candidates[i], voiceId, log, ct), ct);

            rows.Add(row);
            if (seconds > 0) sampleSeconds = seconds;
            onRow?.Report(new BenchmarkProgress(i + 1, candidates.Count, row));
        }

        foreach (var failed in rows.Where(r => r.Failed))
            notes.Add($"{failed.Label}: {failed.Error}");

        var pick = BenchmarkSweep.Pick(rows, machine.LogicalProcessors);
        if (pick is null)
        {
            notes.Add("Nothing could be measured, so there is nothing to pick and nothing was saved.");
            return new SweepOutcome(null, notes);
        }

        var profile = new BenchmarkProfile(
            Threads: pick.Threads,
            Provider: pick.Provider,
            // Round-trip format: read by a machine before it is read by a person,
            // and a locale-formatted date in a portable folder is a date that
            // parses differently on the machine it moves to.
            MeasuredUtc: DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Machine: machine,
            Table: rows,
            NotVaried: NotVaried,
            SampleSeconds: sampleSeconds,
            TieBand: BenchmarkSweep.TieBandFraction);

        // The comparison 8a got wrong, made by looking rather than by remembering.
        if (!profile.BandClearsNoise)
            notes.Add(
                $"The runs varied by up to {profile.MaxSpread:P0} while the tie band is " +
                $"{profile.TieBand:P0} — so rows inside the band were separated by noise, not by speed. " +
                "The pick is still the quietest of the fast rows, but re-run on a quieter machine " +
                "before treating a close result as a finding.");

        string path = DataPaths.BenchmarkFilePath;
        if (!BenchmarkStore.TrySave(path, profile, out string? saveError))
            notes.Add($"Could not save the profile to {path}: {saveError}. " +
                      "The measurement above is still correct; it just will not survive a restart.");
        else
            notes.Add($"Saved to {path}.");

        notes.Add(
            "ORT sizes its thread pool when the session is built, so this applies to SAPI clients " +
            "started from now on. A reader that is already running keeps the session it has.");

        return new SweepOutcome(profile, notes);
    }

    /// <summary>
    /// One configuration, measured in its own render-helper process.
    /// </summary>
    private static (BenchmarkRow Row, double SampleSeconds) Measure(
        SweepCandidate candidate, string voiceId, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"— {candidate.Label} ({candidate.Provider}) …");

        // Apply the candidate. The engine re-reads settings.json on mtime and
        // builds its session from it at first activation, so this is how a thread
        // count reaches ORT at all. Every SAPI host on the machine sees these
        // values for the duration of the sweep, which is why the caller holds a
        // restore scope and why that scope also writes a sidecar the next launch
        // reconciles.
        var settings = EngineSettingsRegistry.Load();
        settings.OnnxThreads = candidate.Threads;
        settings.UseDirectML = candidate.IsGpu;
        EngineSettingsRegistry.Save(settings);

        var metrics = new Benchmark.Metrics();
        bool dmlAppended = false;
        string? dmlFailure = null;

        void OnLine(string line)
        {
            metrics.Absorb(line);

            // The engine announces its provider on stdout and falls back to CPU in
            // silence otherwise. Without reading this back, a machine with no DX12
            // device produces a "DirectML" row that is really a CPU row — the one
            // outcome that would make the GPU question look answered when it was
            // not even asked.
            if (line.Contains("DirectML provider appended", StringComparison.OrdinalIgnoreCase))
                dmlAppended = true;
            else if (line.Contains("DirectML init failed", StringComparison.OrdinalIgnoreCase))
                dmlFailure = line.Trim();
        }

        try
        {
            RenderHostProcess.RunWithText(
                new[]
                {
                    "--mode", "bench",
                    "--voice", voiceId,
                    "--telemetry", DataPaths.SessionsDir,
                    "--runs", RunsPerCandidate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--warmups", WarmupsPerCandidate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                BenchmarkSweep.SampleText, log, ct, progress: null, onLine: OnLine);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // One configuration failing is not the sweep failing. A machine that
            // cannot build a session at 8 threads still deserves an answer about
            // the counts that worked, and the row records why rather than
            // vanishing — a gap in the table reads as "not tried".
            return (Failed(candidate, $"{ex.GetType().Name}: {ex.Message}"), 0);
        }

        if (candidate.IsGpu && !dmlAppended)
            return (Failed(candidate, dmlFailure ?? "DirectML did not initialise on this machine, so the run fell back to the CPU. Nothing was measured about the GPU."), 0);

        var rtfs = metrics.GetAll("engineRtf").Where(v => v > 0).ToList();
        var cpuSeconds = metrics.GetAll("cpuSeconds").ToList();
        double audioSeconds = metrics.Get("audioSeconds");

        if (rtfs.Count == 0)
            return (Failed(candidate,
                "the engine reported no synthesis rate — its telemetry was unreadable, " +
                "so this configuration produced audio but no measurement"), audioSeconds);

        // A row with no audio behind it would compute a cost of zero, and zero is
        // the fastest number there is. Pick already refuses rows at zero, but a
        // zero row still SHOWS as the best line in the table, which is worse than
        // showing as the failure it is.
        if (audioSeconds <= 0)
            return (Failed(candidate,
                "the rendered sample had no audio in it, so there is nothing to have been fast at"), 0);

        // The guard against the shared-session trap. Every way in which the
        // requested thread count could fail to reach ORT has the same symptom: a
        // full table whose rows all describe one configuration. Auto is exempt
        // because the engine reports the count ORT chose, not the 0 we asked for.
        double reported = metrics.Get("onnxThreads");
        if (!candidate.IsGpu && candidate.Threads != CpuBudget.Auto && reported > 0 && (int)reported != candidate.Threads)
            return (Failed(candidate,
                $"asked the engine for {candidate.Threads} threads and it reported building with {reported:F0}. " +
                "The row is discarded rather than recorded, because a sweep that measures the same " +
                "configuration repeatedly looks exactly like a sweep that worked."), audioSeconds);

        // Cost of the sample, from the engine's own rate rather than from a wall
        // clock the pacing has flattened. Same units as the Linux table so the two
        // can be read side by side; the ranking is unchanged by the conversion
        // because the sample is fixed.
        var computeMs = rtfs.Select(rtf => rtf * audioSeconds * 1000.0).ToList();
        double medianComputeMs = Measurement.Median(computeMs);
        double medianCpu = cpuSeconds.Count > 0 ? Measurement.Median(cpuSeconds) : 0;
        double computeSeconds = medianComputeMs / 1000.0;

        return (new BenchmarkRow(
            Label: candidate.Label,
            Threads: candidate.Threads,
            Provider: candidate.Provider,
            MedianWallMs: medianComputeMs,
            Rtf: Measurement.Median(rtfs),
            AvgCores: computeSeconds > 0 ? medianCpu / computeSeconds : 0,
            CoreSeconds: medianCpu,
            Spread: Measurement.Spread(computeMs)), audioSeconds);
    }

    private static BenchmarkRow Failed(SweepCandidate candidate, string error) =>
        new(candidate.Label, candidate.Threads, candidate.Provider, 0, 0, 0, 0,
            Spread: 0, Failed: true, Error: error);
}

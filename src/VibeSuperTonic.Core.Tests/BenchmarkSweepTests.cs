using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The sweep, and above all the rule it picks with.
///
/// <para><b>Why this is tested and the timings are not.</b> A benchmark cannot be
/// unit-tested against real numbers — that is the whole point of it, and any
/// assertion about how long ORT takes would be an assertion about the machine CI
/// happens to run on. What <em>can</em> be wrong in a way nobody notices is the
/// rule applied to the numbers: "fastest, then fewest cores" has a tie band, an
/// ordering, and a special case for auto, and every one of those is a place where
/// a plausible implementation silently returns the wrong row for years.</para>
/// </summary>
public class BenchmarkSweepTests
{
    private static BenchmarkRow Row(string label, int threads, double ms, bool failed = false) =>
        new(label, threads, "cpu", ms, ms / 5000.0, threads == 0 ? 8 : threads, ms / 1000.0 * threads,
            Failed: failed, Error: failed ? "boom" : null);

    private static BenchmarkMachine Machine(int cores = 20, int totalStep = 8) =>
        new("machine-a", "Test CPU", cores, "models-a", totalStep, "M1", "en", "ac", 2.0);

    private static readonly SupertonicOptions Options = new("M1", "en");

    // ------------------------------------------------------------- the pick rule

    [Fact]
    public void The_fastest_row_wins_when_it_is_clearly_fastest()
    {
        var rows = new[] { Row("1", 1, 9220), Row("4", 4, 5080), Row("auto", 0, 8000) };

        Assert.Equal(4, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    [Fact]
    public void Inside_the_tie_band_the_row_using_less_of_the_machine_wins()
    {
        // 2 is 2% slower than 4, which is inside the band — so it wins on cores.
        // This is the case the whole rule exists for: the user asked to be read to
        // while they carry on working, so between two rows that finish together
        // the quiet one is strictly better.
        var rows = new[] { Row("2", 2, 5180), Row("4", 4, 5080), Row("8", 8, 5090) };

        Assert.Equal(2, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    /// <summary>
    /// A GPU row carries <c>Threads = 0</c> because ORT's intra-op count is
    /// meaningless for it — the same spelling CPU "auto" uses, and the two mean
    /// opposite things about cost.
    ///
    /// <para>Measured on a quiet i7-12800H, 2026-08-20: DirectML finished in
    /// 1159 ms using 0.6 cores while six threads took 1201 ms using 6.1 — and
    /// DirectML lost, because the tie-break mapped its 0 to the processor count
    /// and sorted the cheapest row on the board as the most expensive. The
    /// fastest and cheapest row must win.</para>
    /// </summary>
    [Fact]
    public void A_gpu_row_is_ranked_by_what_it_costs_not_by_its_thread_count()
    {
        var directml = new BenchmarkRow("DirectML", 0, "directml", 1159, 0.24, AvgCores: 0.6, CoreSeconds: 0.7);
        var sixThreads = new BenchmarkRow("6", 6, "cpu", 1201, 0.25, AvgCores: 6.1, CoreSeconds: 7.3);

        var pick = BenchmarkSweep.Pick(new[] { sixThreads, directml }, 20);

        Assert.Equal("directml", pick!.Provider);
    }

    /// <summary>
    /// The other half of the same rule, and the reason it cannot simply be
    /// "prefer the GPU": CPU auto also spells its thread count 0, and it really
    /// does occupy the machine. It must still sort last among equals.
    /// </summary>
    [Fact]
    public void Cpu_auto_still_sorts_last_among_equals()
    {
        var auto = new BenchmarkRow("auto", 0, "cpu", 5080, 1.0, AvgCores: 12.9, CoreSeconds: 65);
        var four = new BenchmarkRow("4", 4, "cpu", 5180, 1.0, AvgCores: 3.7, CoreSeconds: 19);

        var pick = BenchmarkSweep.Pick(new[] { auto, four }, 20);

        Assert.Equal("4", pick!.Label);
    }

    [Fact]
    public void Outside_the_tie_band_speed_still_decides()
    {
        // 40% slower is well outside the band. A rule that always preferred the
        // smaller thread count would pick 2 here and make the product slow.
        var rows = new[] { Row("2", 2, 7100), Row("4", 4, 5080) };

        Assert.Equal(4, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    [Fact]
    public void The_tie_band_is_wider_than_the_measured_run_to_run_noise()
    {
        // The rule this pins is not a number, it is a relationship: a band
        // narrower than the noise selects the luckiest configuration rather than
        // the best one, and then records it with a timestamp. Two consecutive
        // sweeps on an idle machine picked 2 and then 3 under a 5% band, with the
        // same three rows moving 15-18% between runs.
        Assert.True(BenchmarkSweep.TieBandFraction >= 0.15,
            "the tie band must stay at or above the ~15% run-to-run noise measured on real hardware");
    }

    [Fact]
    public void Two_sweeps_whose_rows_differ_by_measurement_noise_agree_on_the_pick()
    {
        // The real numbers from the two sweeps that disagreed, before the band was
        // widened. Under the corrected band both pick 2, which is the property the
        // "reproduces its own measurement" exit criterion is actually about.
        var first = new[] { Row("2", 2, 1194), Row("3", 3, 1223), Row("4", 4, 1204), Row("6", 6, 1653) };
        var second = new[] { Row("2", 2, 1407), Row("3", 3, 1295), Row("4", 4, 1386), Row("6", 6, 1767) };

        Assert.Equal(2, BenchmarkSweep.Pick(first, 20)!.Threads);
        Assert.Equal(2, BenchmarkSweep.Pick(second, 20)!.Threads);
    }

    [Fact]
    public void Auto_loses_a_tie_to_an_explicit_count()
    {
        // Auto is "every core" as far as the desktop is concerned. Left to sort
        // by its raw value of 0 it would win every tie by having the smallest
        // number, which is exactly backwards.
        var rows = new[] { Row("auto", 0, 5080), Row("4", 4, 5090) };

        Assert.Equal(4, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    [Fact]
    public void Auto_still_wins_when_it_is_genuinely_faster()
    {
        var rows = new[] { Row("auto", 0, 4000), Row("4", 4, 5080) };

        Assert.Equal(CpuBudget.Auto, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    [Fact]
    public void Failed_rows_cannot_be_picked()
    {
        // A failed row carries a median of 0, which is the fastest number there
        // is. Filtering on the flag rather than on the time is what stops the
        // sweep from recommending the configuration that could not run.
        var rows = new[] { Row("8", 8, 0, failed: true), Row("4", 4, 5080) };

        Assert.Equal(4, BenchmarkSweep.Pick(rows, 20)!.Threads);
    }

    [Fact]
    public void Nothing_measurable_picks_nothing()
    {
        var rows = new[] { Row("8", 8, 0, failed: true), Row("4", 4, 0, failed: true) };

        Assert.Null(BenchmarkSweep.Pick(rows, 20));
    }

    // ------------------------------------------------------------- the candidates

    [Theory]
    [InlineData(20, new[] { 1, 2, 3, 4, 6, 8, 0 })]
    [InlineData(4, new[] { 1, 2, 3, 4, 0 })]
    [InlineData(2, new[] { 1, 2, 0 })]
    [InlineData(1, new[] { 1, 0 })]
    public void Candidates_stop_at_the_core_count_and_always_end_with_auto(int cores, int[] expected)
    {
        // Asking for 8 threads on a 4-core machine measures oversubscription,
        // which is a real effect and not one anybody wants recorded as their
        // profile. Auto stays regardless: it is a different request from an
        // explicit count equal to the core count, and on the development machine
        // it was not the same answer.
        Assert.Equal(expected, BenchmarkSweep.Candidates(cores));
    }

    [Fact]
    public void A_machine_reporting_no_processors_still_sweeps_something()
    {
        Assert.Equal(new[] { 1, 0 }, BenchmarkSweep.Candidates(0));
    }

    // -------------------------------------------------------------- running it

    [Fact]
    public void Every_candidate_is_warmed_once_and_then_timed_three_times()
    {
        // The warm-up pays the model load and lets ORT's arena settle. Timing it
        // would measure a cost the product does not have, because the daemon
        // stays warm between utterances by design.
        var built = new List<ScriptedSynthesizer>();
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => { var s = new ScriptedSynthesizer(threads, 0); built.Add(s); return s; },
            Options, candidates: [1, 2]);

        Assert.Equal(2, built.Count);
        Assert.All(built, s => Assert.Equal(1 + BenchmarkSweep.RunsPerCandidate, s.Calls));
        Assert.Equal(2, profile.Table.Count);
    }

    [Fact]
    public void Each_synthesizer_is_disposed_before_the_next_is_built()
    {
        // Peak memory is one session, not one per row. A sweep that leaked them
        // would need ~380 MB times seven on a machine the user is still using.
        ScriptedSynthesizer? previous = null;
        bool overlapped = false;

        BenchmarkSweep.Run(Machine(), (threads, _) =>
        {
            if (previous is { Disposed: false }) overlapped = true;
            previous = new ScriptedSynthesizer(threads, 0);
            return previous;
        }, Options, candidates: [1, 2, 4]);

        Assert.False(overlapped);
        Assert.True(previous!.Disposed);
    }

    [Fact]
    public void A_candidate_that_cannot_be_built_becomes_a_failed_row_rather_than_ending_the_sweep()
    {
        // A gap in the table reads as "not tried". Recording why is what makes
        // the difference between a profile that can be argued with and one that
        // quietly omits the interesting case.
        var profile = BenchmarkSweep.Run(Machine(), (threads, _) =>
            threads == 2 ? throw new InvalidOperationException("no session at 2")
                         : new ScriptedSynthesizer(threads, 0),
            Options, candidates: [1, 2, 4]);

        var failed = Assert.Single(profile.Table, r => r.Failed);
        Assert.Equal("2", failed.Label);
        Assert.Contains("no session at 2", failed.Error);
        Assert.Equal(3, profile.Table.Count);
        Assert.NotEqual(2, profile.Threads);
    }

    [Fact]
    public void A_sweep_where_nothing_works_says_so_rather_than_picking_a_failure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BenchmarkSweep.Run(
            Machine(), (_, _) => throw new InvalidOperationException("no models"),
            Options, candidates: [1, 2]));

        Assert.Contains("no models", ex.Message);
    }

    [Fact]
    public void Progress_arrives_once_per_row_in_order()
    {
        var seen = new List<BenchmarkProgress>();

        BenchmarkSweep.Run(Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0),
            Options, candidates: [1, 2, 4], onProgress: seen.Add);

        Assert.Equal(3, seen.Count);
        Assert.Equal([1, 2, 3], seen.Select(p => p.Index));
        Assert.All(seen, p => Assert.Equal(3, p.Total));
        Assert.Equal(["1", "2", "4"], seen.Select(p => p.Row.Label));
    }

    [Fact]
    public void Cancellation_stops_the_sweep_where_it_stands()
    {
        using var cts = new CancellationTokenSource();
        int built = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => BenchmarkSweep.Run(
            Machine(),
            (threads, _) =>
            {
                if (++built == 2) cts.Cancel();
                return new ScriptedSynthesizer(threads, 0);
            },
            Options, candidates: [1, 2, 4], cancellationToken: cts.Token));

        Assert.Equal(2, built);
    }

    [Fact]
    public void The_faster_configuration_wins_a_real_sweep()
    {
        // Coarse on purpose: the spread is 40 ms against 2 ms, which no amount of
        // scheduler noise on a CI runner can invert. This is the one test that
        // exercises timing at all, and it asserts the ordering rather than any
        // number.
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, threads == 4 ? 2 : 40),
            Options, candidates: [1, 4]);

        Assert.Equal(4, profile.Threads);
        Assert.Equal("cpu", profile.Provider);
        Assert.True(profile.Table.Single(r => r.Label == "1").MedianWallMs >
                    profile.Table.Single(r => r.Label == "4").MedianWallMs);
    }

    [Fact]
    public void The_profile_records_the_sample_size_and_what_was_held_fixed()
    {
        // A number with no stated scope reads as a number that considered
        // everything. This sweep did not vary inter-op threads or ORT's spin
        // behaviour, and the profile has to say so where someone reading it later
        // will look.
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0, framesPerRender: 22050),
            Options, candidates: [1]);

        Assert.Equal(0.5, profile.SampleSeconds, 3);
        Assert.Contains(profile.NotVaried, n => n.Contains("inter-op", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(profile.NotVaried, n => n.Contains("spin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(8, profile.Machine.TotalStep);
    }

    [Fact]
    public void The_measurement_timestamp_round_trips_as_utc()
    {
        // Written into a folder designed to be copied between machines, where a
        // locale-formatted date parses differently on arrival — or not at all.
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0), Options, candidates: [1]);

        var parsed = DateTime.Parse(profile.MeasuredUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.True((DateTime.UtcNow - parsed).Duration() < TimeSpan.FromMinutes(1));
    }

    // -------------------------------------------------- what the row has to carry

    [Fact]
    public void A_row_records_the_spread_of_its_own_runs()
    {
        // The number whose absence let a 5% tie band ship against 15-18% noise.
        // Without it nothing on screen or on disk could say that two rows 2% apart
        // were separated by luck, and the sweep picked the lucky one twice.
        //
        // Coarse bounds on purpose: this asserts that variance reaches the field at
        // all, not any particular value, because the value is the scheduler's.
        var profile = BenchmarkSweep.Run(
            Machine(), (_, _) => new ScriptedSynthesizer(1, 0, delays: [0, 60, 0]),
            Options, candidates: [1]);

        Assert.True(profile.Table[0].Spread > 0.5,
            $"a row whose runs differed sixfold reported a spread of {profile.Table[0].Spread}");
    }

    [Fact]
    public void Runs_that_agree_report_almost_no_spread()
    {
        var profile = BenchmarkSweep.Run(
            Machine(), (_, _) => new ScriptedSynthesizer(1, 40), Options, candidates: [1]);

        Assert.True(profile.Table[0].Spread < 0.5);
    }

    [Fact]
    public void The_profile_records_the_band_it_was_picked_under()
    {
        // The band is not a constant of nature. It is derived from measured noise,
        // it has been changed once already, and a profile that does not say which
        // rule chose it cannot be re-argued a year later.
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0), Options, candidates: [1, 2]);

        Assert.Equal(BenchmarkSweep.TieBandFraction, profile.TieBand);
    }

    [Fact]
    public void A_band_narrower_than_the_noise_is_reported_as_such()
    {
        // The self-check 8a could not make, because neither number was recorded.
        // Both directions matter: a profile must not cry wolf on a clean sweep, and
        // must not stay quiet on a noisy one.
        var clean = new BenchmarkProfile(4, "cpu", "2026-08-19T00:00:00.0000000Z", Machine(),
            [Row("4", 4, 5000) with { Spread = 0.05 }], BenchmarkSweep.NotVaried, 5.0, TieBand: 0.15);
        var noisy = new BenchmarkProfile(4, "cpu", "2026-08-19T00:00:00.0000000Z", Machine(),
            [Row("4", 4, 5000) with { Spread = 0.40 }], BenchmarkSweep.NotVaried, 5.0, TieBand: 0.15);

        Assert.True(clean.BandClearsNoise);
        Assert.False(noisy.BandClearsNoise);
        Assert.Equal(0.40, noisy.MaxSpread, 3);
    }

    [Fact]
    public void A_failed_row_does_not_drag_the_reported_noise_down()
    {
        // A failed row has a spread of 0 because it has no runs. Averaging it in,
        // or letting it participate in the maximum, would make a sweep look
        // steadier the more of it broke.
        var profile = new BenchmarkProfile(4, "cpu", "2026-08-19T00:00:00.0000000Z", Machine(),
            [
                Row("4", 4, 5000) with { Spread = 0.30 },
                Row("8", 8, 0, failed: true),
            ],
            BenchmarkSweep.NotVaried, 5.0, TieBand: 0.15);

        Assert.Equal(0.30, profile.MaxSpread, 3);
    }

    /// <summary>
    /// A synthesizer that takes a scripted number of milliseconds, so a sweep can
    /// be run without a model.
    /// </summary>
    /// <param name="delays">
    /// Per-call delays, cycled. Lets a test script a row whose runs disagree, which
    /// is the only way to exercise the spread without a real machine.
    /// </param>
    private sealed class ScriptedSynthesizer(
        int threads, int millisecondsPerRender, int framesPerRender = 44100, int[]? delays = null)
        : ISynthesizer
    {
        public int Threads { get; } = threads;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public int SampleRate => 44100;

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The warm-up is call 1 and is not timed, so the scripted delays start
            // with the first measured run.
            int delay = delays is { Length: > 0 } && Calls > 0
                ? delays[(Calls - 1) % delays.Length]
                : millisecondsPerRender;

            Calls++;
            if (delay > 0) Thread.Sleep(delay);
            return new short[framesPerRender];
        }

        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => Disposed = true;
    }

    // ------------------------------------------------------- across providers

    private static BenchmarkRow Gpu(int threads, double ms) =>
        new($"cuda ({threads})", threads, ExecutionProviders.Cuda, ms, ms / 5000.0, 1.2, ms / 1000.0 * 1.2);

    [Fact]
    public void A_GPU_that_wins_by_a_mile_is_picked()
    {
        // The measured shape on an RTX A2000: RTF 0.180 -> 0.018.
        var rows = new[] { Row("4", 4, 5080), Row("2", 2, 5180), Gpu(4, 520) };

        Assert.Equal(ExecutionProviders.Cuda, BenchmarkSweep.PickAcross(rows, 20)!.Provider);
    }

    [Fact]
    public void A_GPU_that_merely_ties_does_not_earn_its_cost()
    {
        // Inside the tie band the CPU keeps it. A GPU costs a 330 MB provider
        // library, a gigabyte-scale CUDA dependency, VRAM and a laptop battery,
        // and a dead heat does not buy any of that back.
        var rows = new[] { Row("4", 4, 5080), Gpu(4, 4600) };

        Assert.Equal(ExecutionProviders.Cpu, BenchmarkSweep.PickAcross(rows, 20)!.Provider);
    }

    [Fact]
    public void A_failed_GPU_row_leaves_the_CPU_pick_standing()
    {
        var rows = new[]
        {
            Row("4", 4, 5080),
            new BenchmarkRow("cuda (4)", 4, ExecutionProviders.Cuda, 0, 0, 0, 0, Failed: true, Error: "no device"),
        };

        var pick = BenchmarkSweep.PickAcross(rows, 20)!;
        Assert.Equal(ExecutionProviders.Cpu, pick.Provider);
        Assert.Equal(4, pick.Threads);
    }

    [Fact]
    public void GPU_rows_are_measured_at_the_thread_count_the_CPU_rows_chose()
    {
        // A GPU row still has a CPU side — ORT leaves shape-related and
        // unassigned nodes there — so measuring it at a thread count the daemon
        // would never run measures a configuration nobody will use.
        var asked = new List<(int Threads, string Provider)>();

        var profile = BenchmarkSweep.Run(
            Machine(),
            (threads, provider) =>
            {
                asked.Add((threads, provider));
                // 2 is the fastest CPU row here, so the GPU row must be asked for 2.
                return new ScriptedSynthesizer(threads, threads == 2 ? 2 : 40);
            },
            Options, candidates: [1, 2], gpuProviders: [ExecutionProviders.Cuda]);

        Assert.Equal(3, profile.Table.Count);
        Assert.Equal((2, ExecutionProviders.Cuda), asked[^1]);
    }

    [Fact]
    public void Progress_counts_the_GPU_rows_too()
    {
        // A progress bar that says 2 of 2 and then keeps going is worse than none.
        var seen = new List<BenchmarkProgress>();

        BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0), Options,
            candidates: [1, 2], onProgress: seen.Add, gpuProviders: [ExecutionProviders.Cuda]);

        Assert.All(seen, p => Assert.Equal(3, p.Total));
        Assert.Equal([1, 2, 3], seen.Select(p => p.Index));
    }

    [Fact]
    public void No_GPU_providers_means_the_sweep_is_exactly_what_it_was()
    {
        var profile = BenchmarkSweep.Run(
            Machine(), (threads, _) => new ScriptedSynthesizer(threads, 0), Options, candidates: [1, 2]);

        Assert.All(profile.Table, r => Assert.Equal(ExecutionProviders.Cpu, r.Provider));
    }
}

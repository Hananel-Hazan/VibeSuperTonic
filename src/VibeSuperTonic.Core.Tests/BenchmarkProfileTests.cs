using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Staleness, and what is done about it.
///
/// <para><b>Why this carries its own weight.</b> A profile that applies when it
/// should not is worse than no profile: it is a wrong thread count wearing a
/// timestamp and a machine name, which is precisely the shape nobody re-checks.
/// The plan's readiness pass caught two of these — the model set and
/// <c>TotalStep</c> — after the first draft recorded only the machine, and the
/// live install runs a different <c>TotalStep</c> from the one every number in
/// the phase was measured at.</para>
/// </summary>
public class BenchmarkProfileTests
{
    private static BenchmarkMachine Machine(
        string id = "machine-a", int cores = 20, string models = "models-a", int totalStep = 8) =>
        new(id, "Test CPU", cores, models, totalStep, "M1", "en", "ac", 2.0);

    private static BenchmarkProfile Profile(int threads = 4, BenchmarkMachine? machine = null) =>
        new(threads, "cpu", "2026-08-16T10:00:00.0000000Z", machine ?? Machine(),
            [new BenchmarkRow("4", 4, "cpu", 5080, 0.179, 4.3, 22)],
            BenchmarkSweep.NotVaried, SampleSeconds: 5.0);

    // ------------------------------------------------------------- staleness

    [Fact]
    public void A_profile_measured_on_this_machine_is_not_stale()
    {
        Assert.Empty(Profile().StalenessAgainst(Machine()));
    }

    [Fact]
    public void A_profile_from_another_machine_is_stale()
    {
        // The portable-folder case, and the one the plan names explicitly: a
        // profile measured on someone else's hardware is exactly the guess this
        // phase exists to remove.
        var reasons = Profile().StalenessAgainst(Machine(id: "machine-b"));

        Assert.Contains(reasons, r => r.Contains("different machine"));
    }

    [Fact]
    public void The_same_machine_id_with_a_different_core_count_is_still_stale()
    {
        // Two installs of one OS image can share a machine-id, and the core count
        // is what the thread pick is actually about. Belt and braces, deliberately.
        var reasons = Profile().StalenessAgainst(Machine(cores: 8));

        Assert.Contains(reasons, r => r.Contains("logical processors"));
    }

    [Fact]
    public void A_changed_model_set_is_stale()
    {
        // The curve is a property of these weights. A voice pack update or a
        // model bump moves it, and nothing else would notice.
        var reasons = Profile().StalenessAgainst(Machine(models: "models-b"));

        Assert.Contains(reasons, r => r.Contains("model set"));
    }

    [Fact]
    public void A_changed_totalStep_is_stale()
    {
        // The purely local case: nothing about the machine moved, the user edited
        // settings.json. A profile measured at 8 and applied at 6 is an
        // extrapolation nobody performed.
        var reasons = Profile().StalenessAgainst(Machine(totalStep: 6));

        Assert.Contains(reasons, r => r.Contains("TotalStep"));
    }

    [Fact]
    public void Every_reason_that_applies_is_reported_not_just_the_first()
    {
        // Three causes want three different things from the user — re-run, re-run,
        // or put the setting back — so one boolean could not say it and one
        // reason would send them round twice.
        var reasons = Profile().StalenessAgainst(Machine(id: "machine-b", models: "models-b", totalStep: 6));

        Assert.Equal(3, reasons.Count);
    }

    [Fact]
    public void The_winning_row_is_findable_for_reporting()
    {
        Assert.Equal("4", Profile().Winner!.Label);
    }

    // -------------------------------------------------------------- the decision

    [Fact]
    public void A_profile_that_still_applies_decides_the_thread_count()
    {
        var decision = CpuProfileDecision.Decide(Profile(threads: 4), Machine(), maxCpuPercent: 20, processorCount: 20);

        Assert.Equal(4, decision.Threads);
        Assert.True(decision.FromProfile);
        Assert.Contains("benchmark 2026-08-16", decision.Reason);
    }

    [Fact]
    public void A_stale_profile_is_ignored_rather_than_adjusted()
    {
        // There is no honest way to scale a thread count from someone else's core
        // count: the curve is not monotonic, which is the whole reason the phase
        // exists. The fallback is a guess, and it is labelled as one.
        var decision = CpuProfileDecision.Decide(
            Profile(threads: 4), Machine(id: "machine-b"), maxCpuPercent: 20, processorCount: 40);

        Assert.False(decision.FromProfile);
        Assert.Equal(CpuBudget.IntraOpThreads(20, 40), decision.Threads);
        Assert.Contains("does not apply", decision.Reason);
    }

    [Fact]
    public void Never_having_benchmarked_says_so_rather_than_looking_like_a_measurement()
    {
        var decision = CpuProfileDecision.Decide(null, Machine(), maxCpuPercent: 20, processorCount: 20);

        Assert.False(decision.FromProfile);
        Assert.Contains("never benchmarked", decision.Reason);
    }

    [Fact]
    public void A_profile_can_choose_auto_and_the_description_says_so()
    {
        var decision = CpuProfileDecision.Decide(
            Profile(threads: CpuBudget.Auto), Machine(), maxCpuPercent: 20, processorCount: 20);

        Assert.Equal(CpuBudget.Auto, decision.Threads);
        Assert.Contains("auto", decision.Describe());
        Assert.DoesNotContain("0 threads", decision.Describe());
    }

    [Fact]
    public void The_description_carries_the_reason_so_config_can_print_it_whole()
    {
        // "CPU, 4 threads (benchmark 2026-08-16)" and "CPU, 4 threads (20% of 20
        // logical processors)" are the same number arrived at two different ways,
        // and only one of them is a measurement.
        string measured = CpuProfileDecision.Decide(Profile(), Machine(), 20, 20).Describe();
        string guessed = CpuProfileDecision.Decide(null, Machine(), 20, 20).Describe();

        Assert.Contains("4 threads", measured);
        Assert.Contains("benchmark", measured);
        Assert.Contains("20%", guessed);
        Assert.NotEqual(measured, guessed);
    }
}

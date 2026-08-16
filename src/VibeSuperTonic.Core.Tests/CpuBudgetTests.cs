using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

public class CpuBudgetTests
{
    [Fact]
    public void The_default_share_puts_the_reporters_machine_on_four_threads()
    {
        // The case this was written for: a 20-thread i7-12800H whose desktop
        // stuttered on every read. Four threads measured FASTER than ORT's own
        // pick while using 4.3 cores instead of 14.3.
        Assert.Equal(4, CpuBudget.IntraOpThreads(20, 20));
    }

    [Theory]
    [InlineData(80, 20, 16)]
    [InlineData(80, 8, 6)]      // 6.4 -> 6
    [InlineData(80, 4, 3)]      // 3.2 -> 3
    [InlineData(50, 20, 10)]
    [InlineData(25, 20, 5)]
    public void A_share_is_floored_rather_than_rounded(int percent, int cores, int expected)
    {
        // Rounding up defeats the purpose: on 3 cores, 80% rounded is 3, which
        // hands back the whole machine.
        Assert.Equal(expected, CpuBudget.IntraOpThreads(percent, cores));
    }

    [Fact]
    public void A_hundred_percent_hands_the_choice_back_to_ORT()
    {
        // Not the same as passing the core count. Phase 0 measured ORT's own
        // pick as roughly twice as fast as any explicit value, so "all of it"
        // has to mean "do not pass anything".
        Assert.Equal(CpuBudget.Auto, CpuBudget.IntraOpThreads(100, 20));
        Assert.Equal(CpuBudget.Auto, CpuBudget.IntraOpThreads(150, 20));
    }

    [Fact]
    public void An_unset_or_nonsense_percentage_means_auto()
    {
        // 0 is what a settings file that predates this option deserializes to,
        // and it must not mean "no threads".
        Assert.Equal(CpuBudget.Auto, CpuBudget.IntraOpThreads(0, 20));
        Assert.Equal(CpuBudget.Auto, CpuBudget.IntraOpThreads(-10, 20));
    }

    [Fact]
    public void A_share_too_small_to_compute_a_thread_gets_the_floor_of_two()
    {
        // 1% of 20 floors to 0. One thread measured 1.8x slower than two for a
        // saving of two core-seconds, so the floor is two rather than one.
        Assert.Equal(2, CpuBudget.IntraOpThreads(1, 20));
        Assert.Equal(2, CpuBudget.IntraOpThreads(5, 20));
    }

    [Fact]
    public void A_single_core_machine_still_speaks_and_asks_for_one_thread()
    {
        // The floor cannot exceed the machine: two threads on one core buys
        // context switching and nothing else.
        Assert.Equal(1, CpuBudget.IntraOpThreads(80, 1));
        Assert.Equal(1, CpuBudget.IntraOpThreads(20, 1));
    }

    [Fact]
    public void A_nonsense_processor_count_does_not_produce_zero_threads()
    {
        // Environment.ProcessorCount cannot return 0, but this is the value that
        // would silently disable synthesis if it ever did.
        Assert.Equal(1, CpuBudget.IntraOpThreads(80, 0));
    }

    [Fact]
    public void The_budget_never_exceeds_the_machine_and_never_reaches_zero()
    {
        for (int cores = 1; cores <= 64; cores++)
            for (int percent = 1; percent < 100; percent++)
                Assert.InRange(CpuBudget.IntraOpThreads(percent, cores), 1, cores);
    }
}

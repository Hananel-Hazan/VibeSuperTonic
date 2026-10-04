using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The Windows engine's provider choice: the Advanced tab's checkbox, a stored
/// benchmark, and the battery rule, composed in one place.
///
/// <para>The engine and the Control Panel both call <see cref="DirectMlPolicy"/>,
/// so these tests are the only thing that can run on a machine without Windows.
/// What they cannot reach — the <c>GetSystemPowerStatus</c> call, the checkbox,
/// the session build — is listed in the commit message rather than assumed.</para>
/// </summary>
public class DirectMlPolicyTests
{
    private static BenchmarkMachine Machine() =>
        new("machine-a", "Test CPU", 20, "models-a", 8, "M1", "en", "ac", 2.0);

    private static BenchmarkProfile DmlProfile() =>
        new(0, ExecutionProviders.DirectMl, "2026-09-01T10:00:00.0000000Z", Machine(),
            [
                new BenchmarkRow("2", 2, "cpu", 5600, 0.197, 2.1, 11),
                new BenchmarkRow("4", 4, "cpu", 5080, 0.179, 4.3, 22),
                new BenchmarkRow("directml", 0, ExecutionProviders.DirectMl, 520, 0.018, 1.2, 6),
            ],
            BenchmarkSweep.NotVaried, SampleSeconds: 5.0);

    private static BenchmarkProfile CpuProfile() =>
        new(4, "cpu", "2026-09-01T10:00:00.0000000Z", Machine(),
            [new BenchmarkRow("4", 4, "cpu", 5080, 0.179, 4.3, 22)],
            BenchmarkSweep.NotVaried, SampleSeconds: 5.0);

    // ------------------------------------------------ the explicit opt-out

    [Fact]
    public void Unticking_DirectML_wins_over_a_profile_that_picked_it()
    {
        // The bug this guards: with OnnxThreads = 0 a measured profile saying
        // "directml" used to switch the GPU on for a user who had unticked the box.
        var d = DirectMlPolicy.Decide(DmlProfile(), useDirectMl: false, PowerStates.Ac, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
    }

    [Fact]
    public void Unticked_on_battery_is_not_blamed_on_the_battery()
    {
        // "DirectML skipped: on battery" must mean the battery did it. A user who
        // turned the GPU off themselves would otherwise be told a lie about why.
        var d = DirectMlPolicy.Decide(DmlProfile(), useDirectMl: false, PowerStates.Battery, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.False(d.SkippedForBattery);
    }

    // -------------------------------------------------- what is left alone

    [Fact]
    public void Ticked_with_no_measurement_is_DirectML_as_it_always_was()
    {
        var d = DirectMlPolicy.Decide(null, useDirectMl: true, PowerStates.Ac, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.DirectMl, d.Provider);
        Assert.False(d.FromProfile);
    }

    [Fact]
    public void Ticked_with_a_profile_that_picked_DirectML_applies_it()
    {
        var d = DirectMlPolicy.Decide(DmlProfile(), useDirectMl: true, PowerStates.Ac, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.DirectMl, d.Provider);
        Assert.True(d.FromProfile);
        Assert.False(d.SkippedForBattery);
    }

    [Fact]
    public void Ticked_with_a_profile_that_picked_CPU_follows_the_measurement()
    {
        // Unchanged behaviour: the box permits the GPU, the benchmark declined it.
        var d = DirectMlPolicy.Decide(CpuProfile(), useDirectMl: true, PowerStates.Ac, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.Equal(4, d.Threads);
        Assert.False(d.SkippedForBattery);
    }

    // -------------------------------------------------------- the battery

    [Fact]
    public void On_battery_DirectML_is_skipped_and_the_decision_says_so()
    {
        var d = DirectMlPolicy.Decide(null, useDirectMl: true, PowerStates.Battery, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.True(d.SkippedForBattery);
        Assert.Contains("on battery", d.Reason);
    }

    [Fact]
    public void On_battery_a_DirectML_profile_lands_on_a_measured_CPU_thread_count()
    {
        var d = DirectMlPolicy.Decide(DmlProfile(), useDirectMl: true, PowerStates.Battery, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.True(d.SkippedForBattery);
        Assert.True(d.FromProfile);
        Assert.True(d.Threads is 2 or 4);
    }

    [Fact]
    public void GpuOnBattery_keeps_DirectML_unplugged()
    {
        var d = DirectMlPolicy.Decide(DmlProfile(), useDirectMl: true, PowerStates.Battery, gpuOnBattery: true);

        Assert.Equal(ExecutionProviders.DirectMl, d.Provider);
        Assert.False(d.SkippedForBattery);
    }

    [Theory]
    [InlineData(PowerStates.Unknown)]
    [InlineData(PowerStates.Ac)]
    public void Only_battery_changes_anything_unknown_power_is_todays_behaviour(string power)
    {
        // Fail safe: a VM, a sandbox that denies the API, or a desktop that
        // reports nothing must behave exactly as before the rule existed.
        var d = DirectMlPolicy.Decide(null, useDirectMl: true, power, gpuOnBattery: false);

        Assert.Equal(ExecutionProviders.DirectMl, d.Provider);
        Assert.False(d.SkippedForBattery);
    }

    [Theory]
    [InlineData((byte)0, PowerStates.Battery)]
    [InlineData((byte)1, PowerStates.Ac)]
    [InlineData((byte)255, PowerStates.Unknown)]   // what a VM says
    [InlineData((byte)2, PowerStates.Unknown)]     // anything unexpected is not "battery"
    public void Windows_AC_line_status_maps_to_a_power_state(byte status, string expected)
    {
        Assert.Equal(expected, PowerStates.FromAcLineStatus(status));
    }

    // ------------------------------------------------- the Linux side stays put

    [Fact]
    public void DirectML_is_not_a_provider_the_Linux_backend_accepts()
    {
        // OrtSynthesizer refuses names this returns false for; the Linux ORT
        // package has no DirectML, so adding the constant must not widen the gate.
        Assert.False(ExecutionProviders.IsKnown(ExecutionProviders.DirectMl));
    }

    [Fact]
    public void The_default_GPU_provider_is_still_CUDA_for_the_Linux_daemon()
    {
        var d = ExecutionDecision.Decide(
            null, Machine(), 20, 20, preference: ProviderPreference.Gpu);

        Assert.Equal(ExecutionProviders.Cuda, d.Provider);
    }
}

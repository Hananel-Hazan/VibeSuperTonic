namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// Which provider the Windows engine builds its session with.
///
/// <para>This is the one place the rule lives, called by the engine when it
/// builds a session and by the Control Panel when it predicts what the engine
/// will do, so the two cannot drift. It is a thin mapping onto
/// <see cref="ExecutionDecision.Decide"/> rather than a second implementation
/// of the battery rule.</para>
///
/// <para><b>The checkbox is a veto, not a vote.</b> <c>UseDirectML = false</c>
/// means CPU, whatever a stored benchmark says: a measurement may refine what
/// the user allowed, never override what they refused. Until this existed, a
/// profile whose winner was DirectML switched it on for a user who had
/// unticked the box.</para>
///
/// <para>Ticked (the default) means "use the provider the benchmark liked, or
/// DirectML if nothing has been measured" — which is what <c>auto</c> already
/// means on Linux, so a tri-state would add nothing here. The one thing the
/// checkbox cannot say, "DirectML even though the benchmark picked CPU", is
/// expressed by setting the thread count by hand, which has always bypassed the
/// profile.</para>
/// </summary>
public static class DirectMlPolicy
{
    /// <param name="applicableProfile">
    /// A stored profile that still describes this machine, or null — including
    /// when the thread count is set by hand, which switches profiles off.
    /// </param>
    /// <param name="useDirectMl">The Advanced tab's checkbox.</param>
    /// <param name="powerState"><see cref="PowerStates"/>; unknown changes nothing.</param>
    /// <param name="gpuOnBattery">Keep DirectML on battery.</param>
    public static ExecutionDecision Decide(
        BenchmarkProfile? applicableProfile,
        bool useDirectMl,
        string powerState,
        bool gpuOnBattery)
    {
        string preference = !useDirectMl
            ? ProviderPreference.Cpu
            : applicableProfile is not null
                ? ProviderPreference.Auto     // the measurement picks the provider
                : ProviderPreference.Gpu;     // nothing measured: DirectML, as always

        // Staleness was settled by the caller (a profile that does not apply is
        // passed as null), so the machine handed on is the profile's own and
        // cannot make it stale. The percentage and processor count only feed the
        // "never benchmarked" thread fallback, which the engine does not use: it
        // leaves ORT's own pick in place unless the decision is FromProfile.
        var machine = applicableProfile?.Machine
            ?? new BenchmarkMachine("", "", 1, "", 0, "", "", PowerStates.Unknown, 0);

        return ExecutionDecision.Decide(
            applicableProfile,
            machine,
            maxCpuPercent: 100,
            processorCount: Math.Max(1, machine.LogicalProcessors),
            preference: preference,
            gpuUnavailable: null,
            powerState: powerState,
            gpuOnBattery: gpuOnBattery,
            gpuProvider: ExecutionProviders.DirectMl);
    }
}

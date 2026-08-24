namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// The execution providers this product knows how to ask ONNX Runtime for.
///
/// <para>Strings rather than an enum because they are written into
/// <c>benchmark.json</c>, into <c>settings.json</c> and onto every IPC reply — a
/// renamed enum member is a silently unreadable profile, and these names have to
/// survive a portable folder moving between releases.</para>
/// </summary>
public static class ExecutionProviders
{
    /// <summary>ORT's default provider. Always present; the fallback everything else falls back to.</summary>
    public const string Cpu = "cpu";

    /// <summary>
    /// NVIDIA CUDA. Present only when the optional provider pack is installed —
    /// 330 MB of provider library plus CUDA and cuDNN, which is why it cannot
    /// ride in the default download.
    /// </summary>
    public const string Cuda = "cuda";

    public static bool IsKnown(string provider) =>
        provider is Cpu or Cuda;

    /// <summary>How a provider name reads in a sentence written for a person.</summary>
    public static string Display(string provider) => provider switch
    {
        Cpu => "CPU",
        Cuda => "CUDA",
        _ => provider,
    };
}

/// <summary>
/// What the user asked for, before the machine gets a say.
///
/// <para><c>auto</c> means "whatever the benchmark chose, subject to the battery
/// rule". The other two are a person overriding a measurement, which they are
/// entitled to do — someone permanently plugged into a dock can set
/// <c>gpu</c> and mean it.</para>
/// </summary>
public static class ProviderPreference
{
    public const string Auto = "auto";
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";

    public static bool IsKnown(string preference) =>
        preference is Auto or Cpu or Gpu;
}

/// <summary>
/// Where the machine's power is coming from, as
/// <c>/sys/class/power_supply/AC*/online</c> reports it.
/// </summary>
public static class PowerStates
{
    public const string Ac = "ac";
    public const string Battery = "battery";

    /// <summary>A desktop with no AC node, or a kernel that names it something unexpected.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Which provider and how many threads are in force — and, the half that makes
/// it reviewable, where that came from.
///
/// <para>"CUDA (benchmark 2026-08-24)", "CPU, 4 threads (on battery)", "CPU, 4
/// threads (no GPU on this machine)" and "CPU, 4 threads (20% of 20 logical
/// processors, never benchmarked)" are four different answers to one question,
/// and only the daemon can tell them apart. Three of them are the same symptom —
/// the GPU is not being used — with entirely different fixes, which is exactly
/// the shape that made the audio-device loss invisible for five hours.</para>
/// </summary>
/// <param name="Threads">Intra-op threads to build the session with.</param>
/// <param name="Provider">Execution provider in force.</param>
/// <param name="Reason">One phrase, suitable for appending in brackets.</param>
/// <param name="FromProfile">True when a stored measurement decided it.</param>
public sealed record ExecutionDecision(int Threads, string Provider, string Reason, bool FromProfile)
{
    /// <summary>
    /// Apply a stored profile if it still describes this machine, veto the GPU if
    /// the machine or the user says so, and fall back to <see cref="CpuBudget"/>'s
    /// percentage when there is no measurement to apply.
    ///
    /// <para><b>A stale profile is ignored rather than adjusted.</b> There is no
    /// honest way to scale a thread count from someone else's core count: the
    /// curve is not monotonic, which is the entire reason Phase 8 exists. Falling
    /// back to the percentage is a guess, but it is the guess the product already
    /// shipped, and it is labelled as one.</para>
    ///
    /// <para><b>The vetoes compose in one direction only.</b> Preference picks
    /// what to aim for, availability can knock the GPU out, and the battery rule
    /// can knock it out again — but nothing can put the GPU back, so a machine
    /// with no provider pack cannot be talked onto CUDA by a settings file.</para>
    /// </summary>
    /// <param name="stored">The profile on disk, or null.</param>
    /// <param name="now">What this machine is, for the staleness test.</param>
    /// <param name="maxCpuPercent">The pre-benchmark fallback, still the answer for a machine never swept.</param>
    /// <param name="processorCount">Logical processors, for that fallback.</param>
    /// <param name="preference"><see cref="ProviderPreference"/>. An unknown value is treated as auto.</param>
    /// <param name="gpuUnavailable">
    /// Why the GPU cannot be used at all, or null when it can. This is the probe's
    /// answer, not a guess from hardware inventory — see
    /// <c>OrtSynthesizer.ProbeCuda</c>.
    /// </param>
    /// <param name="powerState"><see cref="PowerStates"/>, read per decision.</param>
    /// <param name="gpuOnBattery">The setting that turns the battery rule off.</param>
    public static ExecutionDecision Decide(
        BenchmarkProfile? stored,
        BenchmarkMachine now,
        int maxCpuPercent,
        int processorCount,
        string preference = ProviderPreference.Auto,
        string? gpuUnavailable = null,
        string powerState = PowerStates.Unknown,
        bool gpuOnBattery = false)
    {
        ArgumentNullException.ThrowIfNull(now);

        bool profileApplies = stored is not null && stored.StalenessAgainst(now).Count == 0;
        string measured = profileApplies && stored is not null
            ? (stored.MeasuredUtc.Length >= 10 ? stored.MeasuredUtc[..10] : stored.MeasuredUtc)
            : "";

        // 1 · What to aim for. `auto` defers to the measurement; anything else is
        //     a person overriding one, which is allowed and is recorded as such.
        string wanted = preference switch
        {
            ProviderPreference.Cpu => ExecutionProviders.Cpu,
            ProviderPreference.Gpu => ExecutionProviders.Cuda,
            _ => profileApplies && stored is not null ? stored.Provider : ExecutionProviders.Cpu,
        };

        // 2 · The machine's veto, then the power lead's. Order matters only for
        //     the sentence: a machine with no GPU should say so rather than
        //     blaming the battery for a choice it never had.
        string? cpuBecause = null;

        if (wanted == ExecutionProviders.Cuda && gpuUnavailable is not null)
        {
            cpuBecause = preference == ProviderPreference.Gpu
                ? $"GPU requested but unavailable — {gpuUnavailable}"
                : $"no GPU available — {gpuUnavailable}";
        }
        else if (wanted == ExecutionProviders.Cuda
                 && powerState == PowerStates.Battery && !gpuOnBattery)
        {
            // Requested explicitly, and the right default: a discrete GPU on a
            // laptop is the difference between a machine that lasts an afternoon
            // and one that does not, while the CPU path is five times faster than
            // real time. Set GpuOnBattery to keep the GPU on a dock.
            cpuBecause = "on battery";
        }

        string provider = cpuBecause is null ? wanted : ExecutionProviders.Cpu;

        // 3 · Threads, from the rows that actually describe the provider in force.
        //     A profile whose winner was a GPU row still measured the CPU ones, so
        //     a battery veto lands on a measured thread count rather than back on
        //     the percentage — which would be throwing away a measurement because
        //     of an unrelated one.
        if (profileApplies && stored is not null)
        {
            int? threadsFromProfile = ThreadsFor(stored, provider, processorCount);
            if (threadsFromProfile is { } profileThreads)
            {
                string why = cpuBecause is not null
                    ? $"{cpuBecause}, benchmark {measured}"
                    : GpuLostToCpu(stored, provider, gpuUnavailable)
                        ? $"benchmark {measured}, GPU measured slower"
                        : $"benchmark {measured}";

                if (preference is ProviderPreference.Cpu or ProviderPreference.Gpu && cpuBecause is null)
                    why = $"{preference} requested, benchmark {measured}";

                return new ExecutionDecision(profileThreads, provider, why, FromProfile: true);
            }
        }

        // 4 · No usable measurement. The percentage is the shipped guess, and it
        //     is labelled as one so nobody mistakes it for a number a machine
        //     arrived at.
        int threads = CpuBudget.IntraOpThreads(maxCpuPercent, processorCount);
        string fallback = stored is null
            ? $"{maxCpuPercent}% of {processorCount} logical processors, never benchmarked"
            : $"{maxCpuPercent}% of {processorCount} logical processors, stored benchmark does not apply";

        if (cpuBecause is not null) fallback = $"{cpuBecause}, {fallback}";

        return new ExecutionDecision(threads, provider, fallback, FromProfile: false);
    }

    /// <summary>
    /// The thread count that describes running on <paramref name="provider"/>:
    /// the profile's own pick when it is for that provider, and otherwise the
    /// best row the sweep measured for it.
    ///
    /// <para>The second case is what a vetoed GPU lands on. A profile whose
    /// winner was a CUDA row still measured every CPU row, so unplugging the
    /// power lead moves the daemon to a MEASURED thread count rather than back to
    /// the percentage — which would be discarding one measurement because of an
    /// unrelated one. Null when the sweep has nothing to say about this provider,
    /// and the percentage is then the honest answer.</para>
    /// </summary>
    private static int? ThreadsFor(BenchmarkProfile stored, string provider, int processorCount)
    {
        if (stored.Provider == provider) return stored.Threads;

        var forProvider = stored.Table
            .Where(r => !r.Failed && r.Provider == provider)
            .ToList();

        return forProvider.Count == 0 ? null : BenchmarkSweep.Pick(forProvider, processorCount)?.Threads;
    }

    /// <summary>
    /// True when the sweep tried a GPU, could have used one, and picked the CPU
    /// anyway. Worth saying out loud: it is the one case where "why is my GPU
    /// idle" has an answer the user might disagree with and can act on.
    /// </summary>
    private static bool GpuLostToCpu(BenchmarkProfile stored, string provider, string? gpuUnavailable) =>
        provider == ExecutionProviders.Cpu
        && gpuUnavailable is null
        && stored.Table.Any(r => r.Provider != ExecutionProviders.Cpu && !r.Failed);

    /// <summary>How the decision reads in a log line or a status field.</summary>
    public string Describe() =>
        $"{ExecutionProviders.Display(Provider)}, " +
        $"{(Threads == CpuBudget.Auto ? "auto" : Threads + " threads")} ({Reason})";
}

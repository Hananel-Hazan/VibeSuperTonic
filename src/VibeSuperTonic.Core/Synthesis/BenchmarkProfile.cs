namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// One configuration, measured.
///
/// <para><see cref="Threads"/> is what was asked of ORT;
/// <see cref="AvgCores"/> is what the machine actually did about it, and the two
/// are not the same number — that gap is the whole finding behind
/// <see cref="CpuBudget"/>, where asking for 8 produced 8.2 busy cores and the
/// worst wall clock on the board.</para>
/// </summary>
/// <param name="Label">How this row is named to a human — a count, or "auto".</param>
/// <param name="Threads">Intra-op threads requested. 0 is <see cref="CpuBudget.Auto"/>.</param>
/// <param name="Provider">Execution provider. "cpu" today; Phase 8b adds the GPU rows.</param>
/// <param name="MedianWallMs">Median of the timed runs. Median, not mean, so one scheduling hiccup cannot carry a row.</param>
/// <param name="Rtf">Wall time over audio produced. Below 1.0 is faster than real time.</param>
/// <param name="AvgCores">Process CPU time over wall time: how much of the machine this row occupied.</param>
/// <param name="CoreSeconds">Total CPU consumed for one sample. The cost the rest of the desktop pays.</param>
/// <param name="Spread">
/// How far the timed runs of this row spanned, as a fraction of its own median —
/// the noise this row was measured through. Added 2026-08-19 and it is the number
/// that makes the rest of the row auditable: a 2% difference between two rows
/// whose spreads are 18% is not a difference, and until this was recorded nothing
/// on screen or on disk said so. See <see cref="Measurement.Spread"/>.
/// </param>
/// <param name="Failed">True when this configuration could not be measured at all.</param>
/// <param name="Error">Why, when <paramref name="Failed"/>.</param>
public sealed record BenchmarkRow(
    string Label,
    int Threads,
    string Provider,
    double MedianWallMs,
    double Rtf,
    double AvgCores,
    double CoreSeconds,
    double Spread = 0,
    bool Failed = false,
    string? Error = null);

/// <summary>
/// What was true of the machine when a profile was measured — and therefore
/// what has to still be true for it to mean anything.
///
/// <para><b>Three of these are staleness triggers and the rest are provenance.</b>
/// The plan's readiness pass caught the two local ones: a profile is a property
/// of <em>this model set at this totalStep</em>, not only of this machine, and
/// the development box runs <c>TotalStep: 6</c> while every number that motivated
/// the phase was measured at 8. A profile measured at one and applied at the
/// other is an extrapolation nobody performed.</para>
/// </summary>
/// <param name="MachineId">
/// Keyed hash of <c>/etc/machine-id</c>, never the id itself — systemd documents
/// that value as confidential and asks callers to derive an application-specific
/// one. Its only job here is equality, which a hash does exactly as well.
/// </param>
/// <param name="Cpu">Model name, for a human reading the file.</param>
/// <param name="LogicalProcessors">Cores ORT could see.</param>
/// <param name="ModelSet">Fingerprint of the ONNX files. Catches a model swap, ignores a folder copy.</param>
/// <param name="TotalStep">Diffusion steps the sweep ran at. Changes the cost curve, so it changes the answer.</param>
/// <param name="Voice">Voice the sample was rendered with — provenance, not a trigger.</param>
/// <param name="Language">Language the sample was rendered in — provenance, not a trigger.</param>
/// <param name="PowerState">"ac", "battery" or "unknown" at the time of the sweep.</param>
/// <param name="IdleCpuPercent">How busy the machine was before the sweep started. See the guard in <see cref="BenchmarkSweep"/>.</param>
public sealed record BenchmarkMachine(
    string MachineId,
    string Cpu,
    int LogicalProcessors,
    string ModelSet,
    int TotalStep,
    string Voice,
    string Language,
    string PowerState,
    double IdleCpuPercent);

/// <summary>
/// The answer a machine reached about itself, with everything needed to judge
/// whether it is still the answer.
///
/// <para><b>Why the whole table is kept and not only the winner.</b> A number in
/// a settings file with no provenance is a number nobody dares change. The row
/// that won is rarely interesting on its own — what makes the file actionable is
/// seeing that 4 beat 8 by a factor of two, which is the shape nobody predicts
/// and everybody wants to check before overriding.</para>
/// </summary>
/// <param name="Threads">The pick. An absolute count, or <see cref="CpuBudget.Auto"/>.</param>
/// <param name="Provider">The pick's execution provider.</param>
/// <param name="MeasuredUtc">Round-trip UTC timestamp, so the file sorts and parses.</param>
/// <param name="Machine">What it was measured against.</param>
/// <param name="Table">Every row, in the order swept.</param>
/// <param name="NotVaried">
/// What the sweep held fixed. A number with no stated scope reads as a number
/// that considered everything, and this one did not consider inter-op threads or
/// ORT's spin behaviour — the latter being the one thing a long-lived warm daemon
/// might actually care about.
/// </param>
/// <param name="SampleSeconds">Audio produced per timed run, so the sample size is on the record.</param>
/// <param name="TieBand">
/// The tie band this pick was made under, as a fraction. Recorded 2026-08-19 for
/// the same reason the table is: the band is not a constant of nature but a number
/// derived from measured noise, it has already been changed once, and a profile
/// that does not say which rule chose it cannot be re-argued a year later. Zero on
/// profiles written before it was recorded.
/// </param>
public sealed record BenchmarkProfile(
    int Threads,
    string Provider,
    string MeasuredUtc,
    BenchmarkMachine Machine,
    IReadOnlyList<BenchmarkRow> Table,
    IReadOnlyList<string> NotVaried,
    double SampleSeconds,
    double TieBand = 0)
{
    /// <summary>
    /// Why this profile does not describe <paramref name="now"/>, or empty when
    /// it does.
    ///
    /// <para>Returned as sentences rather than a bool because the three causes
    /// want different things from the user: a different machine means re-run, a
    /// changed model set means re-run, and a changed <c>TotalStep</c> means
    /// re-run <em>or</em> put the setting back. One boolean cannot say that.</para>
    /// </summary>
    public IReadOnlyList<string> StalenessAgainst(BenchmarkMachine now)
    {
        ArgumentNullException.ThrowIfNull(now);
        var reasons = new List<string>();

        if (!string.Equals(Machine.MachineId, now.MachineId, StringComparison.Ordinal))
            reasons.Add("measured on a different machine");

        // Belt and braces with the id above: a folder copied between two installs
        // of the same OS image can carry the same machine-id, and the core count
        // is what the thread pick is actually about.
        else if (Machine.LogicalProcessors != now.LogicalProcessors)
            reasons.Add($"measured on {Machine.LogicalProcessors} logical processors, this machine has {now.LogicalProcessors}");

        if (!string.Equals(Machine.ModelSet, now.ModelSet, StringComparison.Ordinal))
            reasons.Add("the model set has changed since it was measured");

        if (Machine.TotalStep != now.TotalStep)
            reasons.Add($"measured at TotalStep {Machine.TotalStep}, this daemon runs {now.TotalStep}");

        return reasons;
    }

    /// <summary>The winning row, for reporting alongside the pick.</summary>
    public BenchmarkRow? Winner =>
        Table.FirstOrDefault(r => !r.Failed && r.Threads == Threads && r.Provider == Provider);

    /// <summary>
    /// The noisiest row's spread among the rows the tie band actually
    /// adjudicates — the floor the band has to clear.
    ///
    /// <para>Reported so the comparison that 8a got wrong can be made by looking
    /// rather than by remembering: if this exceeds <see cref="TieBand"/>, the band
    /// is discriminating on noise and the pick is a coin toss between the rows
    /// inside it. Zero when nothing measurable was recorded.</para>
    ///
    /// <para><b>Contenders only, and the restriction is the point.</b> This used
    /// to take the worst spread on the whole board, which made the check answer a
    /// question nobody asked: a row that finished 69% behind cannot be picked no
    /// matter how much it wobbles, so its noise says nothing about whether the
    /// band is choosing on signal. Measured 2026-08-20, three sweeps on a rested
    /// i7-12800H: the contending rows sat at 1–11% while <c>auto</c> — 69% off the
    /// pace and never a candidate — swung 18%, and dragged the whole table into
    /// "band narrower than the noise" on its own.</para>
    ///
    /// <para>A contender is a row inside the band, or one its own spread could
    /// carry into the band on another run. That second clause matters: a row
    /// sitting just outside is exactly the one that flips a pick between sweeps,
    /// so excluding it would hide the instability this property exists to
    /// expose.</para>
    /// </summary>
    public double MaxSpread
    {
        get
        {
            var usable = Table.Where(r => !r.Failed && r.MedianWallMs > 0).ToList();
            if (usable.Count == 0) return 0;

            double band = usable.Min(r => r.MedianWallMs) * (1 + TieBand);
            var contenders = usable
                .Where(r => r.MedianWallMs * (1 - r.Spread) <= band)
                .Select(r => r.Spread)
                .ToList();

            // No contender can only happen when the band is degenerate; fall back
            // to the whole board rather than reporting a reassuring zero.
            return contenders.Count > 0
                ? contenders.Max()
                : usable.Select(r => r.Spread).Max();
        }
    }

    /// <summary>
    /// True when the tie band is at or above the worst row's spread — i.e. when
    /// the band is wide enough to be seeing signal rather than noise.
    ///
    /// <para>Both zero (an older profile, or a single-run sweep) reads as
    /// trustworthy, because there is nothing to contradict. That is deliberate:
    /// this is a check on a measurement that was taken, not an accusation against
    /// one that was not.</para>
    /// </summary>
    public bool BandClearsNoise => TieBand >= MaxSpread;
}


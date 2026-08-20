using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Telemetry;
using VibeSuperTonic.Launcher.Bench;
using VibeSuperTonic.Launcher.Telemetry;

namespace VibeSuperTonic.Launcher.Integrity;

/// <summary>
/// One labelled line of the inference report.
/// </summary>
/// <param name="Label">Left column — what is being reported.</param>
/// <param name="Value">Right column — the answer, with its reason in brackets.</param>
internal sealed record InferenceFact(string Label, string Value);

/// <summary>
/// What this machine is about to do for inference, and why it thinks so.
///
/// <para><b>The problem this solves.</b> A number in a settings file with no
/// provenance is a number nobody dares change, and until now Windows showed no
/// number at all: the Status tab was registration checks, and "CPU or GPU, how
/// many threads, decided by what" was answerable only by reading
/// <c>SupertonicAdapter</c>. Linux has reported this since Phase 8a and Windows
/// is the platform where the question is harder, because the provider can be
/// overridden by a measurement and then again by a driver that failed.</para>
///
/// <para><b>One report, two surfaces, and that is the exit criterion.</b> The
/// Status tab and <c>--config</c> both render what <see cref="Build"/> returns.
/// Two renderers computing the same answer independently is how they come to
/// disagree, and a field report that contradicts the window the user is looking
/// at is worse than no field report.</para>
///
/// <para><b>What it can and cannot know.</b> This runs in the Control Panel,
/// which is not a SAPI host — so the configuration half is a <em>prediction</em>
/// of what the next engine session will be built with, computed by mirroring
/// <c>SupertonicAdapter.GetSharedTtsLocked</c>. Whether DirectML then actually
/// appended, and whether a driver failure latched it off, only the engine knows;
/// that half comes from telemetry when a session is live, and is reported
/// separately rather than folded in. Saying "DirectML" when the engine quietly
/// fell back to CPU is exactly the failure W1 found in its own sweep.</para>
/// </summary>
internal sealed class InferenceReport
{
    private InferenceReport(
        IReadOnlyList<InferenceFact> configured,
        IReadOnlyList<InferenceFact> live,
        string summary)
    {
        Configured = configured;
        Live = live;
        Summary = summary;
    }

    /// <summary>What the next engine session will be built with, and why.</summary>
    public IReadOnlyList<InferenceFact> Configured { get; }

    /// <summary>
    /// What engine sessions running right now actually got. Empty when no SAPI
    /// host has the engine loaded, which is the normal case.
    /// </summary>
    public IReadOnlyList<InferenceFact> Live { get; }

    /// <summary>One line: provider, threads, and the reason in brackets.</summary>
    public string Summary { get; }

    /// <summary>
    /// Read settings, the stored profile and any live telemetry, and work out
    /// what the engine will do.
    ///
    /// <para>Nothing here throws. This feeds a status pane and a CLI flag, and
    /// both have to answer even when the thing being described is broken — an
    /// unreadable <c>settings.json</c> is a fact to report, not a reason to
    /// report nothing.</para>
    /// </summary>
    public static InferenceReport Build()
    {
        var configured = new List<InferenceFact>();

        EngineSettings? settings = null;
        try { settings = EngineSettingsRegistry.Load(); }
        catch (Exception ex)
        {
            configured.Add(new("Settings", $"unreadable — {ex.Message}"));
        }

        if (settings is null)
            return new InferenceReport(configured, ReadLive(), "unknown (settings unreadable)");

        // ---- Mirror of the engine's decision, in the engine's order.
        //
        // SupertonicAdapter reads both values from settings, then — and only when
        // OnnxThreads is 0 — lets an applicable profile replace BOTH the thread
        // count and the provider. Reproducing that order matters: a profile that
        // measured CPU as the winner turns DirectML off even though the Advanced
        // tab still shows the box ticked, and a report that read the checkbox
        // alone would confidently name the wrong provider.
        int threads = settings.OnnxThreads;
        bool useDml = settings.UseDirectML;

        BenchmarkProfile? stored = null;
        try { stored = BenchmarkStore.Load(DataPaths.BenchmarkFilePath); }
        catch { /* a damaged file is "no profile", never a broken report */ }

        IReadOnlyList<string> stale = Array.Empty<string>();
        string? describeFailure = null;
        if (stored is not null)
        {
            try
            {
                var now = MachineFacts.Current(
                    MachineFacts.ModelsRoot, settings.TotalStep,
                    settings.DefaultVoice, settings.Language);
                stale = stored.StalenessAgainst(now);
            }
            catch (Exception ex)
            {
                // Could not describe this machine, so the profile cannot be
                // judged. Treat it as not applying — the engine does the same,
                // and guessing the other way would report a thread count that
                // never reaches ORT.
                describeFailure = ex.Message;
                stale = new[] { $"this machine could not be described to compare it against ({ex.Message})" };
            }
        }

        bool profileApplies = stored is not null && stale.Count == 0;
        bool byHand = threads != CpuBudget.Auto;
        string reason;

        if (byHand)
        {
            // A knob that silently loses to a measurement is a knob that generates
            // bug reports — the precedence decision recorded in W1, and the one
            // place Windows deliberately differs from Linux.
            reason = "set by hand on the Advanced tab";
        }
        else if (profileApplies)
        {
            threads = stored!.Threads;
            useDml = stored.Provider == "directml";
            reason = $"benchmark {ShortDate(stored.MeasuredUtc)}";
        }
        else
        {
            reason = stored is null
                ? "ONNX Runtime's own pick — this machine has never been measured"
                : "ONNX Runtime's own pick — the stored benchmark does not apply here";
        }

        string providerText = useDml ? "DirectML (GPU)" : "CPU";

        // A DirectML row carries Threads = 0 because the ops run on the GPU, so a
        // GPU profile applies "auto" as its thread count. Reporting that as "ONNX
        // Runtime decides" is true and reads as though the benchmark declined to
        // choose, when in fact it chose the provider and the CPU thread count
        // stopped being the lever.
        string threadsText = threads != CpuBudget.Auto
            ? $"{threads} intra-op"
            : useDml
                ? "auto — inference runs on the GPU, so the CPU thread count is not the lever"
                : "auto — ONNX Runtime decides";

        configured.Add(new("Provider", $"{providerText} ({(byHand || !profileApplies ? "from settings" : reason)})"));
        configured.Add(new("Threads", $"{threadsText} ({reason})"));
        configured.Add(new("Inter-op threads", settings.OnnxInterOpThreads.ToString()));
        configured.Add(new("Quality", $"TotalStep {settings.TotalStep} ({settings.Preset})"));

        if (useDml)
        {
            // Three causes, one symptom — and only the engine can tell them apart.
            // W4 is where the fallback gets named; until then, say plainly that
            // this line is a request rather than an observation.
            configured.Add(new("Note",
                "DirectML is what will be requested. The engine falls back to CPU silently if the "
              + "device is missing or the driver fails; the live rows below are the only place that shows."));
        }

        // ---- The measurement's provenance.
        if (stored is null)
        {
            configured.Add(new("Benchmark", "none — this machine has never measured itself"));
        }
        else
        {
            string pick = stored.Threads == CpuBudget.Auto ? "auto threads" : $"{stored.Threads} threads";
            configured.Add(new("Benchmark",
                $"{stored.Provider.ToUpperInvariant()}, {pick}, measured {ShortDate(stored.MeasuredUtc)}"
              + $" on {stored.Machine.Cpu} ({stored.Machine.LogicalProcessors} logical processors)"));

            if (stale.Count > 0)
                configured.Add(new("  Applies here?",
                    describeFailure is null
                        ? $"NO — {string.Join("; ", stale)}"
                        : $"unknown — {stale[0]}"));
            else if (byHand)
                configured.Add(new("  Applies here?",
                    "yes, but it is not in force — ONNX threads is set by hand, which wins by design"));
            else
                configured.Add(new("  Applies here?", "yes"));

            // The band and the noise it was measured through. This is the pair W1
            // exists to keep honest: a pick made inside a band narrower than the
            // spread of the rows it adjudicated is a coin toss with a timestamp.
            if (stored.TieBand > 0)
                configured.Add(new("  Confidence",
                    stored.BandClearsNoise
                        ? $"tie band {stored.TieBand:P0}, worst contending row varied {stored.MaxSpread:P0} — the band clears the noise"
                        : $"tie band {stored.TieBand:P0} is NARROWER than the {stored.MaxSpread:P0} the contending rows varied by — "
                        + "close rows in this table are not distinguishable, so re-measure on an idle machine before trusting the order"));
        }

        configured.Add(new("Data folder", DataPaths.DataDir));

        string summary = $"{providerText}, {threadsText} ({reason})";
        return new InferenceReport(configured, ReadLive(), summary);
    }

    /// <summary>
    /// What live engine sessions are actually running with.
    ///
    /// <para>This is the half that cannot be predicted: <c>EffectiveIntraOpThreads</c>
    /// is what the session was really built with, and <c>ProfileApplied</c> says
    /// whether a stored measurement is the reason. A host showing a different
    /// thread count from the configured one above is a host that was started
    /// before the setting changed — the engine builds its session once per
    /// process and never rebuilds it.</para>
    /// </summary>
    private static IReadOnlyList<InferenceFact> ReadLive()
    {
        var rows = new List<InferenceFact>();
        List<SessionSnapshot> sessions;
        try { sessions = TelemetryReader.ListLiveSessions(); }
        catch { return rows; }

        foreach (var s in sessions)
        {
            string threads = s.OnnxThreads > 0 ? $"{s.OnnxThreads} threads" : "auto";
            string why = s.ProfileApplied ? "from the stored benchmark" : "from settings";
            string dml = s.DmlLatchedOff
                ? ", DirectML LATCHED OFF after repeated device loss"
                : "";
            string loss = s.DeviceLossCount > 0 ? $", {s.DeviceLossCount} device loss(es)" : "";

            rows.Add(new(
                $"{s.ProcessName} (pid {s.Pid})",
                $"{threads} {why}{dml}{loss}"));
        }
        return rows;
    }

    /// <summary>
    /// The whole report as text, for <c>--config</c> and for pasting into a field
    /// report. Identical content to the Status tab, by construction.
    /// </summary>
    public string ToPlainText()
    {
        var sb = new System.Text.StringBuilder();

        // The INSTALL's base dir, not this exe's. They are usually the same folder
        // and the case where they are not is the one worth printing: a Control
        // Panel run from somewhere else describes an engine that SAPI hosts do not
        // load, and a field report that quietly named the wrong folder's binaries
        // would send the reader hunting the wrong version.
        string baseDir = DataPaths.BaseDir;
        string ownDir = Registration.DefaultBaseDir;

        sb.AppendLine($"VibeSuperTonic {VersionInfo.Own()}");
        sb.AppendLine($"  BaseDir:      {baseDir}");
        if (!string.Equals(baseDir.TrimEnd('\\'), ownDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"  (running from {ownDir} — the versions below are the installed engine's, not this exe's)");
        foreach (var (label, path) in VersionInfo.Components(baseDir))
            sb.AppendLine($"  {label,-13} {VersionInfo.OfFile(path) ?? "(not installed here)"}");
        sb.AppendLine($"  ONNX Runtime: {VersionInfo.OnnxRuntime(baseDir)}");
        sb.AppendLine();

        sb.AppendLine("Inference — what the next engine session will use");
        foreach (var f in Configured)
            sb.AppendLine($"  {f.Label,-17} {f.Value}");

        sb.AppendLine();
        if (Live.Count == 0)
        {
            sb.AppendLine("Live engine sessions: none (no SAPI host has the engine loaded right now)");
        }
        else
        {
            sb.AppendLine("Live engine sessions — what they are actually running with");
            foreach (var f in Live)
                sb.AppendLine($"  {f.Label,-28} {f.Value}");
        }

        return sb.ToString();
    }

    /// <summary>The date half of a round-trip UTC stamp, for a human reading a row.</summary>
    private static string ShortDate(string measuredUtc) =>
        measuredUtc.Length >= 10 ? measuredUtc[..10] : measuredUtc;
}

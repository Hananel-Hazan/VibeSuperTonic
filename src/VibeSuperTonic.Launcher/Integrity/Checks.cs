using Microsoft.Win32;
using VibeSuperTonic.Core.Models;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Launcher.Bench;

namespace VibeSuperTonic.Launcher.Integrity;

internal enum CheckSeverity { Info, Warning, Error }

internal sealed record CheckResult(
    string Title,
    CheckSeverity Severity,
    bool Ok,
    string Detail,
    string? FixHint,
    Func<IProgress<string>?, CancellationToken, Task<bool>>? Repair,
    /// <summary>
    /// True when <see cref="Repair"/> changes something outside this install —
    /// currently only "install a .NET runtime machine-wide". Callers must ask
    /// first: "Repair all" writing registry keys and fetching models is what the
    /// button promises, silently installing a system runtime is not.
    /// </summary>
    bool NeedsConsent = false);

internal static class Checks
{
    public static IReadOnlyList<CheckResult> RunAll(string? baseDirOverride = null)
    {
        var results = new List<CheckResult>();
        var state = Registration.Inspect(baseDirOverride);
        results.Add(BaseDirCheck(state));
        results.Add(DotNetX64Check(state));
        results.Add(DotNetX86Check(state));
        results.Add(VcRuntimeCheck(state, DotNetRuntime.Arch.X64));
        results.Add(VcRuntimeCheck(state, DotNetRuntime.Arch.X86));
        results.Add(X64ComHostFileCheck(state));
        results.Add(X86ComHostFileCheck(state));
        results.Add(HklmTokensCheck(state));
        results.Add(HkcuClsidX64Check(state));
        results.Add(HkcuClsidX86Check(state));
        results.Add(Sapi32BitCheck(state));
        results.Add(BaseDirRecordedCheck(state));
        results.Add(ModelsCheck(state));
        results.Add(VoiceStylesCheck(state));
        results.Add(EngineDllWritableCheck(state));
        // LAST, and the position is the point. "Repair all" walks this list in
        // order, and a sweep cannot run until the voices are registered and the
        // models are on disk — both of which are repairs earlier in this list.
        // Put it anywhere else and a fresh install measures nothing, reports a
        // failure, and blames the machine.
        results.Add(BenchmarkCheck());
        return results;
    }

    /// <summary>
    /// Has this machine ever measured itself?
    ///
    /// <para>A fresh install runs at ONNX Runtime's own thread pick, which on the
    /// machine that motivated this work was the <em>worst</em> configuration
    /// available: eleven times the cores to finish later than two threads. That
    /// is not a defect the user can see — everything works, it is merely slower
    /// and noisier than the same hardware can be — so it has to be something the
    /// install asks about rather than something the user discovers.</para>
    ///
    /// <para>It is a Warning, not an Error: an unmeasured install speaks
    /// perfectly well. What it must not be is silent.</para>
    /// </summary>
    private static CheckResult BenchmarkCheck()
    {
        const string title = "Machine benchmark";

        EngineSettings settings;
        try { settings = EngineSettingsRegistry.Load(); }
        catch (Exception ex)
        {
            return new(title, CheckSeverity.Info, true, $"settings unreadable ({ex.Message})", null, null);
        }

        // A hand-set thread count beats any measurement by design — see the
        // precedence note in SupertonicAdapter. There is nothing for a profile to
        // change here, so "never measured" is not a finding.
        if (settings.OnnxThreads != CpuBudget.Auto)
            return new(title, CheckSeverity.Info, true,
                $"not used — ONNX threads is set to {settings.OnnxThreads} by hand on the Advanced tab",
                null, null);

        BenchmarkProfile? stored = null;
        try { stored = BenchmarkStore.Load(DataPaths.BenchmarkFilePath); }
        catch { /* a damaged file is "no profile", never a broken Status tab */ }

        if (stored is null)
            return new(title, CheckSeverity.Warning, Ok: false,
                "never measured — the engine is using ONNX Runtime's own thread pick, "
              + "which measured as the worst configuration on the machine that motivated this feature",
                "Double-click to measure this machine (about a minute and a half). "
              + "Best done while the machine is otherwise idle.",
                Repair: MeasureThisMachineAsync);

        IReadOnlyList<string> stale;
        try
        {
            var now = MachineFacts.Current(
                MachineFacts.ModelsRoot, settings.TotalStep, Voices.All[0].Id, settings.Language);
            stale = stored.StalenessAgainst(now);
        }
        catch (Exception ex)
        {
            return new(title, CheckSeverity.Info, true,
                $"a profile exists but this machine could not be described to compare it against ({ex.Message})",
                null, null);
        }

        string measured = stored.MeasuredUtc.Length >= 10 ? stored.MeasuredUtc[..10] : stored.MeasuredUtc;
        string pick = stored.Threads == CpuBudget.Auto ? "auto threads" : $"{stored.Threads} threads";

        if (stale.Count > 0)
            return new(title, CheckSeverity.Warning, Ok: false,
                $"the stored profile ({stored.Provider.ToUpperInvariant()}, {pick}, {measured}) "
              + $"does not apply here — {string.Join("; ", stale)}",
                "Double-click to measure this machine again. A profile is never scaled to fit: "
              + "the cost curve is not monotonic, so another machine's thread count cannot be converted into this one's.",
                Repair: MeasureThisMachineAsync);

        return new(title, CheckSeverity.Info, true,
            $"{stored.Provider.ToUpperInvariant()}, {pick}, measured {measured}",
            null, null);
    }

    /// <summary>
    /// Measure this machine and store the profile. Used as the repair for
    /// <see cref="BenchmarkCheck"/>, so it must return false rather than throw:
    /// a refused or failed sweep is one unfixed row, not a failed install.
    /// </summary>
    private static async Task<bool> MeasureThisMachineAsync(IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            // The sweep writes a thread count into settings.json for EVERY SAPI
            // client on the machine while it runs. The scope puts them back on
            // dispose and leaves a sidecar the next launch reconciles if this
            // process dies first — not a nicety, and not optional here just
            // because the caller is an installer rather than a tab.
            using var restore = EngineSettingsRegistry.BeginTemporaryChange(log);

            var outcome = await ThreadSweep.RunAsync(
                Voices.All[0].Id, includeGpu: true, force: false, log, onRow: null, ct);

            // Refusal is the load guard: the machine was too busy for the answer
            // to mean anything. Deliberately NOT forced. A measurement taken
            // through someone else's build gets saved with a timestamp and looks
            // exactly as authoritative as a good one, and this is an unattended
            // caller with nobody watching to discount it.
            if (outcome.Refusal is not null)
            {
                log?.Report(outcome.Refusal);
                log?.Report("Nothing was saved. Re-run the benchmark from the Status tab, or Benchmark → This machine, when the machine is quiet.");
                return false;
            }

            foreach (var note in outcome.Notes) log?.Report(note);
            return outcome.Profile is not null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Report($"The benchmark could not run: {ex.Message}");
            return false;
        }
    }

    private static CheckResult BaseDirCheck(Registration.State s) => new(
        "Install location",
        CheckSeverity.Info,
        Ok: Directory.Exists(s.BaseDir),
        Detail: s.BaseDir,
        FixHint: null,
        Repair: null);

    // Named ".NET 10 Runtime", not "Desktop Runtime". The engine targets
    // net10.0-windows but sets neither UseWindowsForms nor UseWPF, so its
    // runtimeconfig.json asks only for Microsoft.NETCore.App. Recommending the
    // Desktop Runtime worked, but sent users after a much larger download than
    // they need and misdescribed what was actually missing.
    private static CheckResult DotNetX64Check(Registration.State s)
    {
        bool ok = s.X64RuntimeInstalled;
        return new(
            $".NET {DotNetRuntime.RequiredMajor} Runtime (x64)",
            CheckSeverity.Error,
            ok,
            ok ? DotNetRuntime.Describe(DotNetRuntime.Arch.X64)
               : $"{DotNetRuntime.Describe(DotNetRuntime.Arch.X64)} — 64-bit SAPI clients cannot load the engine",
            $"Double-click this row to install it, or run: {DotNetRuntime.WingetCommand(DotNetRuntime.Arch.X64)}",
            Repair: (log, ct) => RuntimeInstaller.InstallAsync(DotNetRuntime.Arch.X64, log, ct),
            NeedsConsent: true);
    }

    private static CheckResult DotNetX86Check(Registration.State s)
    {
        if (!s.X86ComHostExists)
            return new($".NET {DotNetRuntime.RequiredMajor} Runtime (x86)", CheckSeverity.Info, true,
                "not required (no x86 engine shipped)", null, null);

        bool ok = s.X86RuntimeInstalled;
        return new(
            $".NET {DotNetRuntime.RequiredMajor} Runtime (x86)",
            // Error, not Warning. This build ships an x86 engine, which is a
            // promise that 32-bit hosts work; without the runtime they do not,
            // and a yellow "!" in a list of green ticks reads as a footnote
            // rather than as the reason nothing works.
            CheckSeverity.Error,
            ok,
            ok ? DotNetRuntime.Describe(DotNetRuntime.Arch.X86)
               : $"{DotNetRuntime.Describe(DotNetRuntime.Arch.X86)} — 32-bit clients (Balabolka, Lingoes, NVDA) see NO voices",
            $"Double-click this row to install it, or run: {DotNetRuntime.WingetCommand(DotNetRuntime.Arch.X86)}",
            Repair: (log, ct) => RuntimeInstaller.InstallAsync(DotNetRuntime.Arch.X86, log, ct),
            NeedsConsent: true);
    }

    /// <summary>
    /// The Visual C++ runtime that ONNX Runtime's native DLL links against.
    ///
    /// Missing, the engine fails at the first ORT call with
    /// "Unable to load DLL '…\onnxruntime.dll' or one of its dependencies
    /// (0x8007007E)" — naming a file that is present and not naming the one that
    /// is not. It is worth a check row of its own precisely because that error
    /// sends everyone who reads it to look at the wrong file.
    ///
    /// The x64 redistributable is on most machines already, dragged in by
    /// something else; the x86 one usually is not. So this reproduces the .NET
    /// split exactly — 64-bit hosts fine, 32-bit hosts silent.
    /// </summary>
    private static CheckResult VcRuntimeCheck(Registration.State s, DotNetRuntime.Arch arch)
    {
        bool x86 = arch == DotNetRuntime.Arch.X86;
        string title = $"Visual C++ runtime ({(x86 ? "x86" : "x64")})";

        if (x86 && !s.X86ComHostExists)
            return new(title, CheckSeverity.Info, true, "not required (no x86 engine shipped)", null, null);

        bool ok = VcRuntime.IsInstalled(arch);
        return new(
            title,
            CheckSeverity.Error,
            ok,
            ok ? VcRuntime.Describe(arch)
               : $"{VcRuntime.Describe(arch)} — ONNX Runtime cannot load, so {(x86 ? "32" : "64")}-bit clients get no audio",
            $"Double-click this row to install it, or run: {VcRuntime.WingetCommand(arch)}",
            Repair: (log, ct) => RuntimeInstaller.InstallVcRuntimeAsync(arch, log, ct),
            NeedsConsent: true);
    }

    /// <summary>
    /// The bottom line for a 32-bit host, stated once and plainly.
    ///
    /// Every other row here reports on a step; this one reports on the outcome.
    /// It exists because the Status tab could show eleven green ticks on a
    /// machine where Balabolka and Lingoes listed no VibeSuperTonic voices at
    /// all: the token check only inspected the 32-bit registry view when we had
    /// decided to populate it, and the x86 CLSID row rendered "(skipped)" as a
    /// pass. Both were self-consistent and together they told the user the
    /// install was fine.
    /// </summary>
    private static CheckResult Sapi32BitCheck(Registration.State s)
    {
        if (!s.X86ComHostExists)
            return new("32-bit SAPI clients", CheckSeverity.Info, true,
                "not supported by this build (no x86 engine shipped)", null, null);

        // Listed and usable are different claims. Registration can be perfect
        // while the engine still cannot synthesise a sample, which is exactly what
        // a missing x86 Visual C++ runtime produces: the voices appear in the
        // reader, the user picks one, and nothing is ever heard. Reporting "all
        // voices visible" there would be true and useless.
        if (s.Sapi32BitWorks && !VcRuntime.IsInstalled(DotNetRuntime.Arch.X86))
            return new(
                "32-bit SAPI clients",
                CheckSeverity.Error,
                Ok: false,
                Detail: "voices are listed but produce NO audio — Visual C++ runtime (x86) is missing",
                FixHint: $"Install it, then restart your reader:  {VcRuntime.WingetCommand(DotNetRuntime.Arch.X86)}",
                Repair: (log, ct) => RuntimeInstaller.InstallVcRuntimeAsync(DotNetRuntime.Arch.X86, log, ct),
                NeedsConsent: true);

        if (s.Sapi32BitWorks)
            return new("32-bit SAPI clients", CheckSeverity.Error, true,
                $"all {Voices.All.Length} voices visible to 32-bit hosts", null, null);

        string why = !s.X86RuntimeInstalled
            ? $"the 32-bit .NET {DotNetRuntime.RequiredMajor} runtime is not installed"
            : !s.X86TokensPresent
                ? "voice tokens are missing from the 32-bit registry view"
                : "the engine is not registered in the 32-bit CLSID view";

        return new(
            "32-bit SAPI clients",
            CheckSeverity.Error,
            Ok: false,
            Detail: $"NO voices — {why}",
            FixHint: !s.X86RuntimeInstalled
                ? $"Install the x86 runtime, then press Repair all:  {DotNetRuntime.WingetCommand(DotNetRuntime.Arch.X86)}"
                : "Press Repair all (UAC required to write the 32-bit voice tokens).",
            Repair: s.X86RuntimeInstalled
                ? async (log, ct) => await Task.Run(() => Registration.Register(msg => log?.Report(msg)) == 0, ct)
                // No Repair while the runtime is absent: registering would make the
                // voices appear and then fail on first Speak, which sends the user
                // hunting an engine bug instead of a missing download.
                : null);
    }

    private static CheckResult X64ComHostFileCheck(Registration.State s) => new(
        "x64 engine COM host",
        CheckSeverity.Error,
        File.Exists(s.X64ComHostPath),
        File.Exists(s.X64ComHostPath) ? s.X64ComHostPath : $"missing: {s.X64ComHostPath}",
        "Reinstall: re-extract the ZIP over this folder.",
        Repair: null);

    private static CheckResult X86ComHostFileCheck(Registration.State s) => new(
        "x86 engine COM host",
        s.X86ComHostExists ? CheckSeverity.Warning : CheckSeverity.Info,
        Ok: !s.X86ComHostExists || File.Exists(s.X86ComHostPath),
        Detail: s.X86ComHostExists ? s.X86ComHostPath : "(not shipped)",
        FixHint: null,
        Repair: null);

    private static CheckResult HklmTokensCheck(Registration.State s) => new(
        "HKLM voice tokens (SAPI5 + OneCore)",
        CheckSeverity.Error,
        s.TokensComplete,
        s.TokensComplete
            // Says which views, because "all 10 voices registered" was true of the
            // 64-bit view alone and read as "registered everywhere" — on a machine
            // where no 32-bit client could see a single one.
            ? $"all {Voices.All.Length} voices registered — 64-bit{(s.X86TokensPresent ? " and 32-bit" : " only, NOT 32-bit")}"
            : "missing or stale — run Repair (UAC required)",
        "Repair will elevate and rewrite the tokens.",
        Repair: async (log, ct) => await Task.Run(() =>
        {
            int rc = Registration.Register(msg => log?.Report(msg));
            return rc == 0;
        }, ct));

    private static CheckResult HkcuClsidX64Check(Registration.State s) => new(
        "HKCU CLSID InprocServer32 (x64)",
        CheckSeverity.Error,
        s.ClsidX64Correct,
        s.ClsidX64Correct ? s.X64ComHostPath : "missing or pointing to wrong path",
        "Repair will rewrite this from BaseDir (no UAC).",
        Repair: async (log, ct) => await Task.Run(() =>
        {
            int rc = Registration.Register(msg => log?.Report(msg));
            return rc == 0;
        }, ct));

    private static CheckResult HkcuClsidX86Check(Registration.State s) => new(
        "HKCU CLSID InprocServer32 (x86)",
        s.X86Available ? CheckSeverity.Warning : CheckSeverity.Info,
        Ok: s.ClsidX86Correct,
        // "(skipped)" used to render as a pass. It is a pass for *this* row — we
        // did what we intended — but the reason belongs in the text, because the
        // consequence is that 32-bit hosts get nothing. The blunt version of that
        // is the "32-bit SAPI clients" row below.
        Detail: !s.X86Available
            ? (s.X86ComHostExists
                ? "not registered — 32-bit runtime missing (see below)"
                : "(skipped — no x86 engine shipped)")
            : s.ClsidX86Correct ? s.X86ComHostPath : "missing or pointing to wrong path",
        FixHint: "Repair will rewrite from BaseDir.",
        Repair: async (log, ct) => await Task.Run(() =>
        {
            int rc = Registration.Register(msg => log?.Report(msg));
            return rc == 0;
        }, ct));

    private static CheckResult BaseDirRecordedCheck(Registration.State s) => new(
        "HKCU BaseDir tracking",
        CheckSeverity.Warning,
        s.BaseDirRecorded,
        s.BaseDirRecorded ? s.BaseDir : "out of sync with current location",
        "Repair will record the current folder.",
        Repair: async (log, ct) => await Task.Run(() =>
        {
            int rc = Registration.Register(msg => log?.Report(msg));
            return rc == 0;
        }, ct));

    private static CheckResult ModelsCheck(Registration.State s)
    {
        var manifest = Manifest.TryLoad(s.BaseDir);
        if (manifest is null)
            return new(
                "ONNX models",
                CheckSeverity.Warning,
                Ok: HasAnyOnnx(s.BaseDir),
                Detail: HasAnyOnnx(s.BaseDir) ? "present (no manifest to verify hashes)" : "missing",
                FixHint: "Manifest not bundled with this build — cannot auto-download. Add models-manifest.json or copy models manually.",
                Repair: null);

        var missing = manifest.Files
            .Where(f => !File.Exists(Path.Combine(s.BaseDir, f.Path)))
            .ToArray();
        bool ok = missing.Length == 0;
        return new(
            "ONNX models",
            CheckSeverity.Error,
            ok,
            ok ? $"{manifest.Files.Count} files present" : $"{missing.Length}/{manifest.Files.Count} missing",
            "Repair will download missing files (with SHA-256 verify).",
            Repair: async (log, ct) =>
            {
                // Core has no way to ask Windows who holds a file, so the
                // Restart Manager probe is supplied from this side (R-12: the
                // Windows-only machinery stays Windows-side).
                var dl = new ModelDownloader(s.BaseDir, manifest, DescribeLockHolders);
                return await dl.EnsureAllAsync(log, ct);
            });
    }

    private static CheckResult VoiceStylesCheck(Registration.State s)
    {
        string dir = Path.Combine(s.BaseDir, "models", "voice_styles");
        if (!Directory.Exists(dir))
            return new("Voice style files", CheckSeverity.Error, false, $"directory missing: {dir}", null, null);
        var present = new HashSet<string>(
            Directory.GetFiles(dir, "*.json").Select(p => Path.GetFileNameWithoutExtension(p)),
            StringComparer.OrdinalIgnoreCase);
        var missing = Voices.All.Where(v => !present.Contains(v.Id)).Select(v => v.Id).ToArray();
        bool ok = missing.Length == 0;
        return new(
            "Voice style files",
            CheckSeverity.Error,
            ok,
            ok ? $"all {Voices.All.Length} voices have style JSON" : $"missing: {string.Join(", ", missing)}",
            null, null);
    }

    private static CheckResult EngineDllWritableCheck(Registration.State s)
    {
        var paths = new[] { s.X64ComHostPath, s.X86ComHostPath }.Where(File.Exists).ToArray();
        if (paths.Length == 0)
            return new("Engine DLLs writable", CheckSeverity.Info, true, "(no engine DLLs present)", null, null);

        var holders = LockProbe.GetHolders(paths);
        if (holders.Count == 0)
            return new("Engine DLLs writable", CheckSeverity.Info, true, "no SAPI client is using the engine", null, null);

        var who = string.Join(", ", holders.Select(h => $"{h.FriendlyName} (PID {h.Pid})"));
        return new(
            "Engine DLLs writable",
            CheckSeverity.Warning,
            Ok: false,
            Detail: $"In use by: {who}",
            FixHint: "Close the listed program(s) before running Repair or replacing model files.",
            Repair: null);
    }

    /// <summary>
    /// Who is holding a model file open, or null when nobody is. Shaped for
    /// Core's <c>FileLockDescriber</c>.
    /// </summary>
    private static string? DescribeLockHolders(string path)
    {
        if (LockProbe.IsWritable(path)) return null;
        var holders = LockProbe.GetHolders(path);
        return holders.Count > 0
            ? string.Join(", ", holders.Select(h => $"{h.FriendlyName} (PID {h.Pid})"))
            : "(unknown processes)";
    }

    private static bool HasAnyOnnx(string baseDir)
    {
        string dir = Path.Combine(baseDir, "models", "onnx");
        return Directory.Exists(dir) && Directory.GetFiles(dir, "*.onnx").Length > 0;
    }
}

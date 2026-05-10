using Microsoft.Win32;

namespace VibeSuperTonic.Launcher.Integrity;

internal enum CheckSeverity { Info, Warning, Error }

internal sealed record CheckResult(
    string Title,
    CheckSeverity Severity,
    bool Ok,
    string Detail,
    string? FixHint,
    Func<IProgress<string>?, CancellationToken, Task<bool>>? Repair);

internal static class Checks
{
    public static IReadOnlyList<CheckResult> RunAll(string? baseDirOverride = null)
    {
        var results = new List<CheckResult>();
        var state = Registration.Inspect(baseDirOverride);
        results.Add(BaseDirCheck(state));
        results.Add(DotNetX64Check());
        results.Add(DotNetX86Check(state));
        results.Add(X64ComHostFileCheck(state));
        results.Add(X86ComHostFileCheck(state));
        results.Add(HklmTokensCheck(state));
        results.Add(HkcuClsidX64Check(state));
        results.Add(HkcuClsidX86Check(state));
        results.Add(BaseDirRecordedCheck(state));
        results.Add(ModelsCheck(state));
        results.Add(VoiceStylesCheck(state));
        results.Add(EngineDllWritableCheck(state));
        return results;
    }

    private static CheckResult BaseDirCheck(Registration.State s) => new(
        "Install location",
        CheckSeverity.Info,
        Ok: Directory.Exists(s.BaseDir),
        Detail: s.BaseDir,
        FixHint: null,
        Repair: null);

    private static CheckResult DotNetX64Check()
    {
        bool ok = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        return new(
            ".NET 10 Desktop Runtime (x64)",
            CheckSeverity.Error,
            ok,
            ok ? "installed" : "missing — required for the engine to run",
            "winget install Microsoft.DotNet.DesktopRuntime.10",
            Repair: null);
    }

    private static CheckResult DotNetX86Check(Registration.State s)
    {
        if (!s.X86ComHostExists)
            return new(".NET 10 Desktop Runtime (x86)", CheckSeverity.Info, true, "not required (no x86 engine)", null, null);
        bool ok = s.X86RuntimeInstalled;
        return new(
            ".NET 10 Desktop Runtime (x86)",
            CheckSeverity.Warning,
            ok,
            ok ? "installed" : "missing — 32-bit SAPI clients (Balabolka 32-bit, etc.) cannot use the engine",
            "winget install Microsoft.DotNet.DesktopRuntime.10 --architecture x86 --force",
            Repair: null);
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
            ? $"all {Voices.All.Length} voices registered"
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
        Detail: !s.X86Available ? "(skipped — no x86 engine or runtime)" :
                s.ClsidX86Correct ? s.X86ComHostPath : "missing or pointing to wrong path",
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
                var dl = new ModelDownloader(s.BaseDir, manifest);
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

    private static bool HasAnyOnnx(string baseDir)
    {
        string dir = Path.Combine(baseDir, "models", "onnx");
        return Directory.Exists(dir) && Directory.GetFiles(dir, "*.onnx").Length > 0;
    }
}

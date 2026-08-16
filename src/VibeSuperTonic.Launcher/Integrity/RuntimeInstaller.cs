using System.Diagnostics;

namespace VibeSuperTonic.Launcher.Integrity;

/// <summary>
/// Installs the .NET runtime the engine needs, so the user does not have to
/// leave the app, find the right download among several that look alike, and
/// pick the right architecture.
///
/// Delegates to <c>winget</c> rather than downloading and running an installer
/// ourselves. That is deliberate: winget verifies the package against
/// Microsoft's manifest, so we never fetch an executable over a connection we
/// control and run it with administrator rights. The cost is a dependency on
/// winget being present — it ships with Windows 11 and current Windows 10, and
/// when it is missing we say so and fall back to the download page.
///
/// This is the one place the app installs software onto the machine, so it is
/// only ever reached from an explicit user action and always raises UAC.
/// </summary>
internal static class RuntimeInstaller
{
    /// <summary>Install the .NET runtime for <paramref name="arch"/>.</summary>
    public static Task<bool> InstallAsync(
        DotNetRuntime.Arch arch, IProgress<string>? log, CancellationToken ct) =>
        RunAsync(
            $".NET {DotNetRuntime.RequiredMajor} runtime ({ArchName(arch)})",
            DotNetRuntime.WingetCommand(arch),
            DotNetRuntime.DownloadUrl,
            () => DotNetRuntime.IsInstalled(arch),
            log, ct);

    /// <summary>
    /// Install the Visual C++ runtime for <paramref name="arch"/> — the
    /// dependency ONNX Runtime's native DLL needs and Windows refuses to name.
    /// </summary>
    public static Task<bool> InstallVcRuntimeAsync(
        DotNetRuntime.Arch arch, IProgress<string>? log, CancellationToken ct) =>
        RunAsync(
            $"Visual C++ runtime ({ArchName(arch)})",
            VcRuntime.WingetCommand(arch),
            VcRuntime.DownloadUrl,
            () => VcRuntime.IsInstalled(arch),
            log, ct);

    private static string ArchName(DotNetRuntime.Arch arch) => arch == DotNetRuntime.Arch.X86 ? "x86" : "x64";

    /// <summary>
    /// Runs <paramref name="command"/> elevated and waits, then confirms with
    /// <paramref name="verify"/>.
    /// </summary>
    private static async Task<bool> RunAsync(
        string label, string command, string downloadUrl,
        Func<bool> verify, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"Installing the {label}.");
        log?.Report($"  {command}");
        log?.Report("A UAC prompt will appear — the installer needs administrator rights.");

        if (!WingetPresent())
        {
            log?.Report("");
            log?.Report("winget was not found on this machine, so it cannot be run for you.");
            log?.Report($"Install manually from {downloadUrl}");
            return false;
        }

        int exitCode;
        try
        {
            exitCode = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    // winget is an execution alias, which does not resolve reliably
                    // as a ShellExecute target. PowerShell does, and it is present
                    // on every machine that has winget.
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                return p.ExitCode;
            }, ct);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log?.Report("");
            log?.Report("The UAC prompt was declined, so nothing was installed.");
            return false;
        }
        catch (Exception ex)
        {
            log?.Report("");
            log?.Report($"Could not start the installer: {ex.Message}");
            log?.Report($"Install manually from {downloadUrl}");
            return false;
        }

        // Trust the probe, not the exit code. winget returns non-zero for
        // "already installed" among other benign outcomes, and a zero exit does
        // not prove the files landed where the loader will look for them.
        bool installed = verify();
        log?.Report("");
        log?.Report(installed
            ? $"Done — the {label} is now present."
            : $"winget exited with code {exitCode} and it is still not detected.");
        if (!installed) log?.Report($"Install manually from {downloadUrl}");
        return installed;
    }

    private static bool WingetPresent()
    {
        try
        {
            // Probing PATH rather than running `winget --version`: launching a
            // process to answer "can I launch this process" is slow enough to be
            // felt on a Status-tab refresh.
            string paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { if (File.Exists(Path.Combine(dir.Trim(), "winget.exe"))) return true; }
                catch { /* malformed PATH entry */ }
            }
            // The execution alias lives here and is not always on PATH for a
            // process started outside a normal shell.
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias);
        }
        catch { return false; }
    }
}

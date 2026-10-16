using Microsoft.Win32;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// Detects whether the .NET runtime the engine needs is installed, per
/// architecture.
///
/// The engine is a framework-dependent COM in-proc server and cannot be anything
/// else: <c>dotnet publish --self-contained</c> on a project with
/// <c>EnableComHosting</c> emits <c>NETSDK1128 — COM hosting does not support
/// self-contained deployments</c>. So the runtime is a genuine prerequisite, in
/// the same bitness as the SAPI client that loads the engine, and detecting it
/// correctly is the difference between "no voices" and "voices that throw".
///
/// This replaces a check for <c>dotnet.exe</c> existing under Program Files,
/// which was wrong in both directions:
///
/// - <b>False positive.</b> Any .NET install at all — .NET 8, the ASP.NET Core
///   runtime, an SDK — put <c>dotnet.exe</c> there. The launcher then registered
///   the voices, a client listed them, and the first Speak failed to load the
///   runtime. Voices that appear and then error are worse than voices that never
///   appear, because the user blames the engine rather than a missing download.
/// - <b>False negative.</b> A runtime installed anywhere other than the default
///   location read as missing.
///
/// What is actually required is a <c>Microsoft.NETCore.App</c> 10.x shared
/// framework. Note <b>not</b> <c>Microsoft.WindowsDesktop.App</c>: the engine
/// targets <c>net10.0-windows</c> but sets neither <c>UseWindowsForms</c> nor
/// <c>UseWPF</c>, so its <c>runtimeconfig.json</c> asks only for
/// <c>Microsoft.NETCore.App</c>. The Desktop Runtime satisfies that because it
/// contains NETCore.App, but so does the plain .NET Runtime, which is a
/// substantially smaller download to ask a user for.
/// </summary>
internal static class DotNetRuntime
{
    public const int RequiredMajor = 10;

    private const string FrameworkName = "Microsoft.NETCore.App";

    /// <summary>Where the .NET installer records each architecture's root.</summary>
    private const string InstalledVersionsKey = @"SOFTWARE\dotnet\Setup\InstalledVersions";

    public enum Arch { X64, X86 }

    /// <summary>
    /// True when a <c>Microsoft.NETCore.App</c> of at least
    /// <see cref="RequiredMajor"/> is installed for <paramref name="arch"/>.
    /// </summary>
    public static bool IsInstalled(Arch arch) => InstalledMajors(arch).Any(m => m >= RequiredMajor);

    /// <summary>
    /// Human-readable summary for the Status tab — the installed majors, so a
    /// user who has .NET 8 but not 10 sees why they are being asked to install
    /// something they believe they already have.
    /// </summary>
    public static string Describe(Arch arch)
    {
        var majors = InstalledMajors(arch).Distinct().OrderBy(m => m).ToArray();
        if (majors.Length == 0) return "no .NET runtime found";
        if (majors.Any(m => m >= RequiredMajor)) return $"installed (.NET {string.Join(", ", majors)})";
        return $"found .NET {string.Join(", ", majors)}, but {RequiredMajor} is required";
    }

    /// <summary>
    /// Major versions of <c>Microsoft.NETCore.App</c> present for this
    /// architecture. Empty when the runtime is absent or unreadable.
    /// </summary>
    private static IEnumerable<int> InstalledMajors(Arch arch)
    {
        foreach (string root in CandidateRoots(arch))
        {
            string dir = Path.Combine(root, "shared", FrameworkName);
            string[] versions;
            try
            {
                if (!Directory.Exists(dir)) continue;
                versions = Directory.GetDirectories(dir);
            }
            catch { continue; }   // permissions, or the path vanished mid-probe

            foreach (string v in versions)
            {
                // Folder names are full versions: "10.0.0", "10.0.3-preview.1".
                string name = Path.GetFileName(v);
                int dot = name.IndexOf('.');
                string head = dot > 0 ? name[..dot] : name;
                if (int.TryParse(head, out int major)) yield return major;
            }
        }
    }

    /// <summary>
    /// Roots to probe, most authoritative first: the location the .NET installer
    /// recorded, then the default. Both are checked because the registry value is
    /// the truth hostfxr uses, while the Program Files path is what exists on a
    /// machine whose registry entry was lost or never written.
    /// </summary>
    private static IEnumerable<string> CandidateRoots(Arch arch)
    {
        string? recorded = RecordedInstallLocation(arch);
        if (!string.IsNullOrWhiteSpace(recorded)) yield return recorded!;

        // ProgramFiles resolves per the CURRENT process (x64), so the x86 root
        // must come from ProgramFilesX86 rather than from any notion of "the
        // other one".
        string programFiles = Environment.GetFolderPath(arch == Arch.X64
            ? Environment.SpecialFolder.ProgramFiles
            : Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet");
    }

    private static string? RecordedInstallLocation(Arch arch)
    {
        string archKey = arch == Arch.X64 ? "x64" : "x86";
        // Both views: the x86 runtime's installer may write through the WOW64
        // redirect depending on its own bitness, so neither view alone is
        // reliable for the x86 entry.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = baseKey.OpenSubKey($@"{InstalledVersionsKey}\{archKey}");
                if (k?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                    return loc.TrimEnd('\\');
            }
            catch { /* fall through to the next view, then to Program Files */ }
        }
        return null;
    }

    /// <summary>
    /// The command that installs what is missing. The plain runtime is enough —
    /// see the type remarks — and is a much smaller download than the Desktop
    /// Runtime this used to recommend.
    /// </summary>
    public static string WingetCommand(Arch arch) => arch == Arch.X64
        ? $"winget install Microsoft.DotNet.Runtime.{RequiredMajor}"
        : $"winget install Microsoft.DotNet.Runtime.{RequiredMajor} --architecture x86 --force";

    public static string DownloadUrl =>
        $"https://dotnet.microsoft.com/download/dotnet/{RequiredMajor}.0";
}

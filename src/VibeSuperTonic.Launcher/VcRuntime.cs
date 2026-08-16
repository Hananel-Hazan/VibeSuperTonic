namespace VibeSuperTonic.Launcher;

/// <summary>
/// Detects the Visual C++ runtime, per architecture.
///
/// ONNX Runtime's native DLL is built with MSVC and links against the VC++
/// redistributable. Without it, <c>onnxruntime.dll</c> is present on disk and
/// still fails to load, with the least helpful error Windows produces:
///
///   Unable to load DLL '…\engine\x86\onnxruntime.dll' or one of its
///   dependencies: The specified module could not be found. (0x8007007E)
///
/// Every instinct that message triggers is wrong. It names a file that exists,
/// so the reader checks the file, finds it, and starts looking for a corrupted
/// download or a registration problem. The actual missing module is unnamed —
/// 0x8007007E is ERROR_MOD_NOT_FOUND, and the operative words are "or one of
/// its dependencies".
///
/// It also splits by architecture in the same way the .NET runtime does, and for
/// the same reason: the x64 redistributable is on most machines because
/// something else installed it, while the x86 one is genuinely rare on a modern
/// box. So the 64-bit engine works, the 32-bit engine does not, and the symptom
/// is once again "my reader is silent but the Control Panel speaks fine".
///
/// Found from a real field report against 0.2.7.2, where exactly this happened.
/// </summary>
internal static class VcRuntime
{
    /// <summary>
    /// Presence of the runtime DLLs themselves, rather than the registry keys the
    /// installer writes.
    ///
    /// Deliberate: the registry under
    /// <c>HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{x86,x64}</c>
    /// records that an installer ran, while what actually decides whether
    /// <c>onnxruntime.dll</c> loads is whether the loader can find these files.
    /// Those can disagree — a repaired or partially removed install being the
    /// usual way — and when they do, the files are the half that is true.
    /// </summary>
    private static readonly string[] Required = ["vcruntime140.dll", "msvcp140.dll"];

    public static bool IsInstalled(DotNetRuntime.Arch arch)
    {
        string dir = SystemDirFor(arch);
        if (string.IsNullOrEmpty(dir)) return false;
        try { return Required.All(f => File.Exists(Path.Combine(dir, f))); }
        catch { return false; }
    }

    public static string Describe(DotNetRuntime.Arch arch)
    {
        string dir = SystemDirFor(arch);
        if (string.IsNullOrEmpty(dir)) return "cannot determine";
        try
        {
            var missing = Required.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray();
            return missing.Length == 0 ? "installed" : $"missing {string.Join(", ", missing)}";
        }
        catch { return "cannot determine"; }
    }

    /// <summary>
    /// Where the loader looks for each architecture's copy.
    ///
    /// This launcher is a 64-bit process, so it sees the real directories rather
    /// than the WOW64 redirection a 32-bit process would get: System32 holds the
    /// 64-bit DLLs and SysWOW64 holds the 32-bit ones. The naming is famously
    /// backwards and is the reason this is a named method rather than an inline
    /// ternary — "SysWOW64 means 32-bit" is worth stating once, in the place that
    /// depends on it.
    /// </summary>
    private static string SystemDirFor(DotNetRuntime.Arch arch) =>
        Environment.GetFolderPath(arch == DotNetRuntime.Arch.X64
            ? Environment.SpecialFolder.System        // C:\Windows\System32  → 64-bit
            : Environment.SpecialFolder.SystemX86);   // C:\Windows\SysWOW64  → 32-bit

    /// <summary>winget package id. One package carries 2015 through 2022 — they share a runtime.</summary>
    public static string WingetPackageId(DotNetRuntime.Arch arch) =>
        arch == DotNetRuntime.Arch.X64 ? "Microsoft.VCRedist.2015+.x64" : "Microsoft.VCRedist.2015+.x86";

    public static string WingetCommand(DotNetRuntime.Arch arch) =>
        $"winget install {WingetPackageId(arch)}";

    public const string DownloadUrl =
        "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist";
}

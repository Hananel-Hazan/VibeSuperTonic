using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace VibeSuperTonic.Launcher;

internal static class Program
{
    private const string EngineClsid = "{F2A8C7B1-1234-5678-9ABC-DEF012345678}";
    private const string LanguageHexLcid = "409"; // en-US
    private const string TokenSchemaVersion = "4";

    private record Voice(string Id, string DisplayName, string Gender);

    private static readonly Voice[] Voices =
    {
        new("M1", "VibeSuperTonic M1", "Male"),
        new("M2", "VibeSuperTonic M2", "Male"),
        new("M3", "VibeSuperTonic M3", "Male"),
        new("M4", "VibeSuperTonic M4", "Male"),
        new("M5", "VibeSuperTonic M5", "Male"),
        new("F1", "VibeSuperTonic F1", "Female"),
        new("F2", "VibeSuperTonic F2", "Female"),
        new("F3", "VibeSuperTonic F3", "Female"),
        new("F4", "VibeSuperTonic F4", "Female"),
        new("F5", "VibeSuperTonic F5", "Female"),
    };

    // Tokens we previously registered that we now retire and clean up on (re)register/--unregister.
    private static readonly string[] LegacyTokenNames = { "VibeSuperTonic_Spike" };

    private static readonly string[] VoiceTokenRoots =
    {
        @"SOFTWARE\Microsoft\Speech\Voices\Tokens",
        @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens",
    };

    private static bool X86RuntimeInstalled() => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe"));

    private static int Main(string[] args)
    {
        try
        {
            bool unregister = args.Any(a => a.Equals("--unregister", StringComparison.OrdinalIgnoreCase));
            bool elevatedChild = args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase));
            string baseDir = AppContext.BaseDirectory.TrimEnd('\\');

            if (unregister) return DoUnregister(elevatedChild);

            string x64ComHost = Path.Combine(baseDir, "engine", "x64", "VibeSuperTonic.Engine.comhost.dll");
            string x86ComHost = Path.Combine(baseDir, "engine", "x86", "VibeSuperTonic.Engine.comhost.dll");
            if (!File.Exists(x64ComHost)) { Console.Error.WriteLine($"x64 comhost not found: {x64ComHost}"); return 2; }

            bool x86Available = File.Exists(x86ComHost) && X86RuntimeInstalled();
            bool x86Skipped = File.Exists(x86ComHost) && !X86RuntimeInstalled();
            bool isElevated = IsElevated();
            bool tokensComplete = AllVoiceTokensCurrent(includeX86: x86Available);

            if (!tokensComplete)
            {
                if (!isElevated)
                {
                    Console.WriteLine("HKLM voice tokens need refresh — relaunching elevated (UAC)...");
                    return RelaunchElevated("--elevated");
                }
                CleanLegacyTokens();
                WriteAllVoiceTokens(baseDir, includeX86: x86Available);
                Console.WriteLine($"Wrote {Voices.Length} voice tokens to HKLM (SAPI5+OneCore, x86 mirror: {(x86Available ? "yes" : "skipped")}).");
            }

            WriteHkcuClsid(RegistryView.Registry64, x64ComHost);
            if (x86Available) WriteHkcuClsid(RegistryView.Registry32, x86ComHost);
            WriteHkcuSettings(baseDir);

            Console.WriteLine();
            Console.WriteLine($"VibeSuperTonic registered ({Voices.Length} voices):");
            foreach (var v in Voices) Console.WriteLine($"  - {v.DisplayName} [{v.Gender}, en-US]");
            Console.WriteLine();
            Console.WriteLine($"  HKCU CLSID x64 : {x64ComHost}");
            Console.WriteLine($"  HKCU CLSID x86 : {(x86Available ? x86ComHost : "(skipped)")}");
            Console.WriteLine($"  HKCU BaseDir   : {baseDir}");
            if (x86Skipped)
            {
                Console.WriteLine();
                Console.WriteLine("NOTE: x86 engine present but x86 .NET 10 Desktop Runtime is missing.");
                Console.WriteLine("      32-bit SAPI clients won't see the voices until you install it:");
                Console.WriteLine("      winget install Microsoft.DotNet.DesktopRuntime.10 --architecture x86 --force");
            }
            Console.WriteLine();
            Console.WriteLine("Move folder anywhere; re-run to update HKCU CLSID paths (no admin).");
            Console.WriteLine("Run --unregister to remove (admin needed for HKLM cleanup).");

            if (elevatedChild) { Console.WriteLine(); Console.WriteLine("Press Enter to close..."); Console.ReadLine(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 1; }
    }

    private static int DoUnregister(bool elevatedChild)
    {
        bool isElevated = IsElevated();
        CleanHkcu(RegistryView.Registry64);
        CleanHkcu(RegistryView.Registry32);
        DeleteSubKeyTree(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\VibeSuperTonic");
        Console.WriteLine("Removed HKCU entries.");

        if (AnyHklmTokenExists())
        {
            if (!isElevated)
            {
                Console.WriteLine("HKLM tokens present; relaunching elevated...");
                return RelaunchElevated("--unregister", "--elevated");
            }
            foreach (var token in AllTokenNames())
                foreach (var root in VoiceTokenRoots)
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                        DeleteSubKeyTree(RegistryHive.LocalMachine, view, $@"{root}\{token}");
            Console.WriteLine($"Removed HKLM voice tokens ({Voices.Length} current + {LegacyTokenNames.Length} legacy, both bitnesses, both categories).");
        }
        if (elevatedChild) { Console.WriteLine(); Console.WriteLine("Press Enter to close..."); Console.ReadLine(); }
        return 0;
    }

    private static IEnumerable<string> AllTokenNames()
    {
        foreach (var v in Voices) yield return $"VibeSuperTonic_{v.Id}";
        foreach (var t in LegacyTokenNames) yield return t;
    }

    private static bool AllVoiceTokensCurrent(bool includeX86)
    {
        var views = includeX86 ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry64 };
        foreach (var v in Voices)
            foreach (var root in VoiceTokenRoots)
                foreach (var view in views)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var attrs = baseKey.OpenSubKey($@"{root}\VibeSuperTonic_{v.Id}\Attributes");
                    if (attrs is null) return false;
                    if ((attrs.GetValue("VibeSuperTonicSchemaVersion") as string) != TokenSchemaVersion) return false;
                }
        return true;
    }

    private static bool AnyHklmTokenExists()
    {
        foreach (var token in AllTokenNames())
            foreach (var root in VoiceTokenRoots)
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = baseKey.OpenSubKey($@"{root}\{token}");
                    if (k is not null) return true;
                }
        return false;
    }

    private static void CleanLegacyTokens()
    {
        foreach (var t in LegacyTokenNames)
            foreach (var root in VoiceTokenRoots)
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    DeleteSubKeyTree(RegistryHive.LocalMachine, view, $@"{root}\{t}");
    }

    private static void WriteAllVoiceTokens(string baseDir, bool includeX86)
    {
        foreach (var v in Voices)
            foreach (var root in VoiceTokenRoots)
            {
                WriteVoiceToken(baseDir, v, root, RegistryView.Registry64, "x64");
                if (includeX86) WriteVoiceToken(baseDir, v, root, RegistryView.Registry32, "x86");
            }
    }

    private static void WriteVoiceToken(string baseDir, Voice v, string root, RegistryView view, string archSubdir)
    {
        bool isOneCore = root.Contains("Speech_OneCore", StringComparison.OrdinalIgnoreCase);
        string engineDir = Path.Combine(baseDir, "engine", archSubdir);
        string voicePath = engineDir;
        string langDataPath = Path.Combine(engineDir, "VibeSuperTonic.Engine.dll");
        string localized = $"{v.DisplayName} - English (United States)";
        string tokenName = $"VibeSuperTonic_{v.Id}";

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var tokenKey = baseKey.CreateSubKey($@"{root}\{tokenName}", writable: true);
        tokenKey.SetValue(null, localized, RegistryValueKind.String);
        tokenKey.SetValue(LanguageHexLcid, localized, RegistryValueKind.String);
        tokenKey.SetValue("CLSID", EngineClsid, RegistryValueKind.String);
        tokenKey.SetValue("LangDataPath", langDataPath, RegistryValueKind.ExpandString);
        tokenKey.SetValue("VoicePath", voicePath, RegistryValueKind.ExpandString);

        using var attrs = tokenKey.CreateSubKey("Attributes", writable: true);
        attrs.SetValue("Age", "Adult", RegistryValueKind.String);
        attrs.SetValue("Gender", v.Gender, RegistryValueKind.String);
        attrs.SetValue("Language", LanguageHexLcid, RegistryValueKind.String);
        attrs.SetValue("Name", v.DisplayName, RegistryValueKind.String);
        attrs.SetValue("SharedPronunciation", "", RegistryValueKind.String);
        attrs.SetValue("Vendor", "VibeSuperTonic", RegistryValueKind.String);
        attrs.SetValue("Version", "1.0", RegistryValueKind.String);
        attrs.SetValue("SupertonicVoiceId", v.Id, RegistryValueKind.String);
        attrs.SetValue("VibeSuperTonicSchemaVersion", TokenSchemaVersion, RegistryValueKind.String);
        if (isOneCore)
        {
            attrs.SetValue("DataVersion", "1.0.0.0", RegistryValueKind.String);
            attrs.SetValue("SayAsSupport",
                "spell=NativeSupported; cardinal=GlobalSupported; ordinal=NativeSupported; date=GlobalSupported; time=GlobalSupported",
                RegistryValueKind.String);
        }
    }

    private static void WriteHkcuClsid(RegistryView view, string dllPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var clsidKey = baseKey.CreateSubKey($@"SOFTWARE\Classes\CLSID\{EngineClsid}", writable: true);
        clsidKey.SetValue(null, "VibeSuperTonic SAPI Engine", RegistryValueKind.String);
        using var inprocKey = clsidKey.CreateSubKey("InprocServer32", writable: true);
        inprocKey.SetValue(null, dllPath, RegistryValueKind.String);
        inprocKey.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
    }

    private static void CleanHkcu(RegistryView view) =>
        DeleteSubKeyTree(RegistryHive.CurrentUser, view, $@"SOFTWARE\Classes\CLSID\{EngineClsid}");

    private static void WriteHkcuSettings(string baseDir)
    {
        using var settingsKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\VibeSuperTonic", writable: true);
        settingsKey.SetValue("BaseDir", baseDir, RegistryValueKind.String);
        settingsKey.SetValue("Consent", 1, RegistryValueKind.DWord);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(params string[] args)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = true, Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
        };
        try { using var p = Process.Start(psi)!; p.WaitForExit(); return p.ExitCode; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { Console.Error.WriteLine("Elevation cancelled by user."); return 5; }
    }

    private static void DeleteSubKeyTree(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            baseKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) { Console.Error.WriteLine($"  warn: failed to delete '{path}' ({hive}/{view}): {ex.Message}"); }
    }
}

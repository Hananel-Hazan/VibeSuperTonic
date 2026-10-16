using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// All Windows registry / SAPI registration logic. Extracted from the original
/// Program.cs so both CLI flags (--register, --unregister) and the GUI Status
/// tab's "Repair" button call the same code.
/// </summary>
internal static class Registration
{
    public sealed record State(
        string BaseDir,
        string X64ComHostPath,
        string X86ComHostPath,
        bool X86ComHostExists,
        bool X64RuntimeInstalled,
        bool X86RuntimeInstalled,
        bool X86Available,
        bool TokensComplete,
        bool ClsidX64Correct,
        bool ClsidX86Correct,
        bool BaseDirRecorded,
        bool X86TokensPresent,
        bool ClsidX86Registered)
    {
        /// <summary>
        /// What a 32-bit SAPI client can actually do right now. Both halves are
        /// required: the voice tokens must exist in the 32-bit registry view for
        /// the client to list them, and the CLSID must exist there for it to
        /// create the engine once listed.
        ///
        /// Deliberately independent of <see cref="X86Available"/>. That flag says
        /// "we are willing to register x86"; this says "a 32-bit client sees
        /// voices". Conflating the two is how the Status tab came to report a
        /// clean bill of health on a machine where Balabolka and Lingoes saw
        /// nothing at all.
        /// </summary>
        /// <remarks>
        /// <c>ClsidX86Registered</c> is the plain fact — is the engine in the
        /// 32-bit CLSID view? — where <c>ClsidX86Correct</c> is vacuously true
        /// whenever we decided not to register x86 at all.
        /// </remarks>
        public bool Sapi32BitWorks => X86TokensPresent && ClsidX86Registered;
    }

    public static string DefaultBaseDir => AppContext.BaseDirectory.TrimEnd('\\');

    public static string X64ComHostPathFor(string baseDir) =>
        Path.Combine(baseDir, "engine", "x64", "VibeSuperTonic.Engine.comhost.dll");

    public static string X86ComHostPathFor(string baseDir) =>
        Path.Combine(baseDir, "engine", "x86", "VibeSuperTonic.Engine.comhost.dll");

    /// <summary>
    /// Whether a 32-bit SAPI host can load the engine. See <see cref="DotNetRuntime"/>
    /// — this used to test for <c>dotnet.exe</c> under Program Files (x86), which
    /// any .NET install of any version satisfied.
    /// </summary>
    public static bool X86RuntimeInstalled() => DotNetRuntime.IsInstalled(DotNetRuntime.Arch.X86);

    public static bool X64RuntimeInstalled() => DotNetRuntime.IsInstalled(DotNetRuntime.Arch.X64);

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static State Inspect(string? baseDirOverride = null)
    {
        string baseDir = baseDirOverride ?? DefaultBaseDir;
        string x64 = X64ComHostPathFor(baseDir);
        string x86 = X86ComHostPathFor(baseDir);
        bool x86Exists = File.Exists(x86);
        bool x86Rt = X86RuntimeInstalled();
        bool x86Avail = x86Exists && x86Rt;
        return new State(
            baseDir,
            x64,
            x86,
            x86Exists,
            X64RuntimeInstalled(),
            x86Rt,
            x86Avail,
            AllVoiceTokensCurrent(includeX86: x86Avail),
            ClsidPathMatches(RegistryView.Registry64, x64),
            !x86Avail || ClsidPathMatches(RegistryView.Registry32, x86),
            BaseDirRecorded() == baseDir,
            // Both asked unconditionally, so the Status tab can report what a
            // 32-bit client actually sees rather than what we intended it to see.
            VoiceTokensCurrentInView(RegistryView.Registry32),
            ClsidPathMatches(RegistryView.Registry32, x86));
    }

    /// <summary>
    /// Performs a full register pass. Returns 0 on success.
    /// If HKLM tokens need refresh and we are not elevated, relaunches elevated.
    /// </summary>
    public static int Register(Action<string>? log = null, bool elevatedChild = false)
    {
        log ??= _ => { };
        var s = Inspect();
        if (!File.Exists(s.X64ComHostPath))
        {
            log($"x64 comhost not found: {s.X64ComHostPath}");
            return 2;
        }

        if (!s.TokensComplete)
        {
            if (!IsElevated())
            {
                log("HKLM voice tokens need refresh — relaunching elevated (UAC)...");
                return RelaunchElevated("--register", "--elevated");
            }
            CleanLegacyTokens();
            WriteAllVoiceTokens(s.BaseDir, includeX86: s.X86Available);
            log($"Wrote {Voices.All.Length} voice tokens to HKLM (SAPI5+OneCore, x86 mirror: {(s.X86Available ? "yes" : "skipped")}).");
        }

        WriteHkcuClsid(RegistryView.Registry64, s.X64ComHostPath);
        if (s.X86Available) WriteHkcuClsid(RegistryView.Registry32, s.X86ComHostPath);
        WriteHkcuSettings(s.BaseDir);

        log($"VibeSuperTonic registered ({Voices.All.Length} voices).");
        log($"  HKCU CLSID x64 : {s.X64ComHostPath}");
        log($"  HKCU CLSID x86 : {(s.X86Available ? s.X86ComHostPath : "(skipped)")}");
        log($"  HKCU BaseDir   : {s.BaseDir}");
        if (s.X86ComHostExists && !s.X86RuntimeInstalled)
        {
            log("");
            log($"WARNING: the 32-bit .NET {DotNetRuntime.RequiredMajor} runtime is missing ({DotNetRuntime.Describe(DotNetRuntime.Arch.X86)}).");
            log("         32-bit SAPI clients — Balabolka, Lingoes, NVDA and most older");
            log("         readers — will NOT list the VibeSuperTonic voices at all. This is");
            log("         not a partial degradation: nothing was registered for 32-bit, so");
            log("         those programs show no VibeSuperTonic entries whatsoever.");
            log("         64-bit clients are unaffected.");
            log("");
            log($"         Fix:  {DotNetRuntime.WingetCommand(DotNetRuntime.Arch.X86)}");
            log($"         or:   {DotNetRuntime.DownloadUrl}  (x86 Runtime)");
            log("         Then re-run this program to finish registration.");
        }
        return 0;
    }

    public static int Unregister(Action<string>? log = null, bool elevatedChild = false)
    {
        log ??= _ => { };
        CleanHkcu(RegistryView.Registry64);
        CleanHkcu(RegistryView.Registry32);
        DeleteSubKeyTree(RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\VibeSuperTonic");
        log("Removed HKCU entries.");

        if (AnyHklmTokenExists())
        {
            if (!IsElevated())
            {
                log("HKLM tokens present; relaunching elevated...");
                return RelaunchElevated("--unregister", "--elevated");
            }
            foreach (var token in Voices.AllTokenNames())
                foreach (var root in Voices.VoiceTokenRoots)
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                        DeleteSubKeyTree(RegistryHive.LocalMachine, view, $@"{root}\{token}");
            log($"Removed HKLM voice tokens ({Voices.All.Length} current + {Voices.LegacyTokenNames.Length} legacy, both bitnesses, both categories).");
        }
        return 0;
    }

    public static int RelaunchElevated(params string[] args)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
        };
        try { using var p = Process.Start(psi)!; p.WaitForExit(); return p.ExitCode; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return 5; }
    }

    // ---------------------------------------------------------- inspection

    public static bool AllVoiceTokensCurrent(bool includeX86) =>
        VoiceTokensCurrentInView(RegistryView.Registry64)
        && (!includeX86 || VoiceTokensCurrentInView(RegistryView.Registry32));

    /// <summary>
    /// Are all voice tokens present and current in one registry view? Split out
    /// so a caller can ask about the 32-bit view on its own — a 32-bit SAPI
    /// client reads only that view, and whether we chose to populate it is not
    /// the same question as whether it is populated.
    /// </summary>
    public static bool VoiceTokensCurrentInView(RegistryView view)
    {
        foreach (var v in Voices.All)
            foreach (var root in Voices.VoiceTokenRoots)
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var attrs = baseKey.OpenSubKey($@"{root}\VibeSuperTonic_{v.Id}\Attributes");
                if (attrs is null) return false;
                if ((attrs.GetValue("VibeSuperTonicSchemaVersion") as string) != Voices.TokenSchemaVersion) return false;
            }
        return true;
    }

    public static bool AnyHklmTokenExists()
    {
        foreach (var token in Voices.AllTokenNames())
            foreach (var root in Voices.VoiceTokenRoots)
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = baseKey.OpenSubKey($@"{root}\{token}");
                    if (k is not null) return true;
                }
        return false;
    }

    public static bool ClsidPathMatches(RegistryView view, string expectedDllPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var k = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}\InprocServer32");
        if (k is null) return false;
        var actual = k.GetValue(null) as string;
        return string.Equals(actual, expectedDllPath, StringComparison.OrdinalIgnoreCase);
    }

    public static string? BaseDirRecorded()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
        return k?.GetValue("BaseDir") as string;
    }

    // ---------------------------------------------------------- mutation

    private static void CleanLegacyTokens()
    {
        foreach (var t in Voices.LegacyTokenNames)
            foreach (var root in Voices.VoiceTokenRoots)
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    DeleteSubKeyTree(RegistryHive.LocalMachine, view, $@"{root}\{t}");
    }

    private static void WriteAllVoiceTokens(string baseDir, bool includeX86)
    {
        foreach (var v in Voices.All)
            foreach (var root in Voices.VoiceTokenRoots)
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
        // A SAPI token names itself once per language it claims: the value NAME is
        // the hex LCID, the value DATA is what a client shows for that language.
        // Without a per-LCID entry a client filtering on, say, 407 finds the voice
        // in the Language attribute but has no display string for it.
        foreach (var l in SupertonicLanguages.All)
            tokenKey.SetValue(l.HexLcid, $"{v.DisplayName} - {l.DisplayName}", RegistryValueKind.String);
        tokenKey.SetValue("CLSID", Voices.EngineClsid, RegistryValueKind.String);
        tokenKey.SetValue("LangDataPath", langDataPath, RegistryValueKind.ExpandString);
        tokenKey.SetValue("VoicePath", voicePath, RegistryValueKind.ExpandString);

        using var attrs = tokenKey.CreateSubKey("Attributes", writable: true);
        attrs.SetValue("Age", "Adult", RegistryValueKind.String);
        attrs.SetValue("Gender", v.Gender, RegistryValueKind.String);
        // Semicolon-separated LCID list — the standard way a SAPI engine says
        // "I speak all of these." Clients that filter the voice list by language
        // (NVDA's picker, SpVoice.GetVoices("Language=40C")) only offer this
        // voice for a language listed here, so advertising just 409 hid the
        // other 30 languages the model has always been able to speak.
        attrs.SetValue("Language", SupertonicLanguages.AllHexLcidsSemicolonSeparated, RegistryValueKind.String);
        attrs.SetValue("Name", v.DisplayName, RegistryValueKind.String);
        attrs.SetValue("SharedPronunciation", "", RegistryValueKind.String);
        attrs.SetValue("Vendor", "VibeSuperTonic", RegistryValueKind.String);
        attrs.SetValue("Version", "1.0", RegistryValueKind.String);
        attrs.SetValue("SupertonicVoiceId", v.Id, RegistryValueKind.String);
        attrs.SetValue("VibeSuperTonicSchemaVersion", Voices.TokenSchemaVersion, RegistryValueKind.String);
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
        using var clsidKey = baseKey.CreateSubKey($@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}", writable: true);
        clsidKey.SetValue(null, "VibeSuperTonic SAPI Engine", RegistryValueKind.String);
        using var inprocKey = clsidKey.CreateSubKey("InprocServer32", writable: true);
        inprocKey.SetValue(null, dllPath, RegistryValueKind.String);
        inprocKey.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
    }

    private static void CleanHkcu(RegistryView view) =>
        DeleteSubKeyTree(RegistryHive.CurrentUser, view, $@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}");

    private static void WriteHkcuSettings(string baseDir)
    {
        using var settingsKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\VibeSuperTonic", writable: true);
        settingsKey.SetValue("BaseDir", baseDir, RegistryValueKind.String);
        settingsKey.SetValue("Consent", 1, RegistryValueKind.DWord);
    }

    private static void DeleteSubKeyTree(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            baseKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch { /* swallow — caller logs if needed */ }
    }
}

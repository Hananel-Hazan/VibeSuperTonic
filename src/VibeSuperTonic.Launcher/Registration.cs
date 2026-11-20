using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// Who can use the voices once registered. Review item 11, decided 2026-11-09:
/// the installer asks, and the default is the user who runs it.
///
/// <para>The voice tokens are in HKLM either way. Measured 2026-11-20 on
/// Windows 10 LTSC 2021: a token copied to HKCU\…\Speech\Voices\Tokens is
/// listed by neither SpVoice (x64, x86) nor System.Speech, so a voice that is
/// visible at all is visible to every account. What the scope decides is where
/// the engine's COM class and BaseDir go: HKCU for this user, HKLM as well for
/// all users — and an account that finds the voice but not the class gets a
/// voice that is listed and silent.</para>
/// </summary>
internal enum RegistrationScope { CurrentUser, AllUsers }

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
    /// How the window asks "this user or all users". Set by the Control Panel;
    /// null on the command line, where <c>--all-users</c> and <c>--this-user</c>
    /// say it instead. Returns null when the user cancels.
    /// </summary>
    public static Func<RegistrationScope?>? AskScope { get; set; }

    /// <summary>
    /// The scope already on this machine: all users when the engine class is in
    /// HKLM, this user when it is only in HKCU, null when it is in neither.
    /// </summary>
    public static RegistrationScope? RegisteredScope()
    {
        if (HklmClsidExists()) return RegistrationScope.AllUsers;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var k = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}");
        return k is null ? null : RegistrationScope.CurrentUser;
    }

    /// <summary>
    /// Performs a full register pass. Returns 0 on success, 5 when the user
    /// declined the UAC prompt or the scope question.
    ///
    /// <para><paramref name="scope"/> null keeps the scope already registered,
    /// and when there is none asks through <see cref="AskScope"/> — or, with no
    /// one to ask, takes this user. Elevation is needed for the HKLM voice
    /// tokens (either scope) and for the HKLM class (all users); this relaunches
    /// elevated for whichever is out of date.</para>
    /// </summary>
    public static int Register(Action<string>? log = null, bool elevatedChild = false, RegistrationScope? scope = null)
    {
        log ??= _ => { };
        var s = Inspect();
        if (!File.Exists(s.X64ComHostPath))
        {
            log($"x64 comhost not found: {s.X64ComHostPath}");
            return 2;
        }

        if (scope is null)
        {
            scope = RegisteredScope();
            if (scope is null && !elevatedChild && AskScope is not null)
            {
                scope = AskScope();
                if (scope is null) { log("Registration cancelled."); return 5; }
            }
            scope ??= RegistrationScope.CurrentUser;
        }
        bool allUsers = scope == RegistrationScope.AllUsers;
        string scopeFlag = allUsers ? "--all-users" : "--this-user";

        bool hklmClassCurrent = HklmClassCurrent(s);
        // HKLM work: the tokens, always; the class, to write it (all users) or to
        // take it away (this user, after an all-users install).
        bool needHklm = !s.TokensComplete || (allUsers ? !hklmClassCurrent : HklmClsidExists());
        bool hklmDoneByChild = false;
        if (needHklm && !IsElevated())
        {
            log(allUsers
                ? "Registering for all users needs administrator rights — relaunching elevated (UAC)..."
                : !s.TokensComplete
                    ? "HKLM voice tokens need refresh — relaunching elevated (UAC)..."
                    : "Removing the all-users registration needs administrator rights — relaunching elevated (UAC)...");
            int rc = RelaunchElevated("--register", scopeFlag, "--elevated");
            if (rc != 0) return rc;
            // The elevated child did the HKLM work. Its HKCU is not necessarily
            // ours: on a standard account the UAC prompt takes an administrator's
            // password, and the child then runs as that administrator, writing
            // the engine class and BaseDir into the administrator's HKCU. Write
            // this account's own below, or a "this user" install leaves the user
            // who ran it with voices that are listed and silent.
            hklmDoneByChild = true;
        }

        if (!hklmDoneByChild && !s.TokensComplete)
        {
            CleanLegacyTokens();
            WriteAllVoiceTokens(s.BaseDir, includeX86: s.X86Available);
            log($"Wrote {Voices.All.Length} voice tokens to HKLM (SAPI5+OneCore, x86 mirror: {(s.X86Available ? "yes" : "skipped")}).");
        }

        if (hklmDoneByChild)
        {
            // The HKLM class, BaseDir and tokens were the child's; it logged them.
        }
        else if (allUsers)
        {
            WriteClsid(RegistryHive.LocalMachine, RegistryView.Registry64, s.X64ComHostPath);
            if (s.X86Available) WriteClsid(RegistryHive.LocalMachine, RegistryView.Registry32, s.X86ComHostPath);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    .CreateSubKey(@"SOFTWARE\VibeSuperTonic", writable: true);
                k.SetValue("BaseDir", s.BaseDir, RegistryValueKind.String);
            }
            log($"Engine class and BaseDir written to HKLM: every account on this PC can use the voices.");
            string profiles = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "";
            if (profiles.Length > 0 && s.BaseDir.StartsWith(profiles + "\\", StringComparison.OrdinalIgnoreCase))
            {
                log($"WARNING: {s.BaseDir} is inside a user profile, which other accounts");
                log("         usually cannot read. Their voices will be listed and silent.");
                log("         Move the folder somewhere every account can read (for example");
                log("         C:\\VibeSuperTonic), run VibeSuperTonic.exe there, and Repair all.");
            }
        }
        else if (HklmClsidExists())
        {
            RemoveHklmClass();
            log("Removed the all-users engine class from HKLM: only this account can use the voices now.");
        }

        WriteClsid(RegistryHive.CurrentUser, RegistryView.Registry64, s.X64ComHostPath);
        if (s.X86Available) WriteClsid(RegistryHive.CurrentUser, RegistryView.Registry32, s.X86ComHostPath);
        WriteHkcuSettings(s.BaseDir);

        log($"VibeSuperTonic registered ({Voices.All.Length} voices) for {(allUsers ? "all users" : "this user only")}.");
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

        if (AnyHklmTokenExists() || HklmClsidExists())
        {
            if (!IsElevated())
            {
                log("HKLM tokens present; relaunching elevated...");
                return RelaunchElevated("--unregister", "--elevated");
            }
            if (HklmClsidExists())
            {
                RemoveHklmClass();
                log("Removed the all-users engine class and BaseDir from HKLM.");
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

    public static bool HklmClsidExists()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}");
            if (k is not null) return true;
        }
        return false;
    }

    /// <summary>The HKLM class points at this folder's engines, and HKLM's BaseDir is this folder.</summary>
    private static bool HklmClassCurrent(State s)
    {
        bool Matches(RegistryView view, string dll)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = baseKey.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}\InprocServer32");
            return string.Equals(k?.GetValue(null) as string, dll, StringComparison.OrdinalIgnoreCase);
        }
        bool BaseDirMatches(RegistryView view)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = baseKey.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
            return string.Equals(k?.GetValue("BaseDir") as string, s.BaseDir, StringComparison.OrdinalIgnoreCase);
        }
        return Matches(RegistryView.Registry64, s.X64ComHostPath)
            && (!s.X86Available || Matches(RegistryView.Registry32, s.X86ComHostPath))
            && BaseDirMatches(RegistryView.Registry64) && BaseDirMatches(RegistryView.Registry32);
    }

    private static void RemoveHklmClass()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            DeleteSubKeyTree(RegistryHive.LocalMachine, view, $@"SOFTWARE\Classes\CLSID\{Voices.EngineClsid}");
            DeleteSubKeyTree(RegistryHive.LocalMachine, view, @"SOFTWARE\VibeSuperTonic");
        }
    }

    private static void WriteClsid(RegistryHive hive, RegistryView view, string dllPath)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
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

using Microsoft.Win32;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// Single source of truth for "where this VibeSuperTonic install writes its
/// stuff." Portable by default — everything (logs, telemetry, settings) lives
/// under <c>&lt;BaseDir&gt;\data</c> so a user can ZIP up the program folder
/// and move it without losing state.
///
/// The user can override the location from the Control Panel's Advanced tab.
/// The override is stored in <c>HKCU\SOFTWARE\VibeSuperTonic\DataDir</c> as a
/// REG_SZ. The string accepts:
///   • An absolute path (<c>D:\my-portable-data</c>)
///   • A relative path, resolved against <c>BaseDir</c> (<c>data</c>, <c>..\shared</c>)
///   • Environment-variable references (<c>%LOCALAPPDATA%\VibeSuperTonic</c>)
///
/// SAPI voice tokens (HKLM) and the CLSID InprocServer32 entry (HKCU\Classes)
/// stay in the registry — the OS requires them there for COM activation. The
/// only registry entry under our own key after migration is the small
/// <c>BaseDir</c> + <c>DataDir</c> pointer pair.
///
/// IMPORTANT — keep this file in sync with the launcher's mirror copy at
/// <c>src\VibeSuperTonic.Launcher\DataPaths.cs</c>. The two implementations
/// must agree on resolution rules so a setting written by the launcher is
/// read consistently by every host process the engine loads into.
/// </summary>
internal static class DataPaths
{
    public const string DefaultFolderName = "data";
    private const string RegRoot = @"SOFTWARE\VibeSuperTonic";

    /// <summary>
    /// Folder that contains the engine assemblies — used as the anchor for
    /// relative DataDir paths and as the default location of the data folder.
    /// In a portable install this is the program directory.
    /// </summary>
    public static string BaseDir
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegRoot);
                if (k?.GetValue("BaseDir") is string s && !string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
                    return s.TrimEnd('\\');
            }
            catch { }
            return AppContext.BaseDirectory.TrimEnd('\\');
        }
    }

    /// <summary>
    /// Resolved data root. Reads <c>DataDir</c> from registry; on empty / unset
    /// returns <c>&lt;BaseDir&gt;\data</c>. Always returns an absolute path.
    /// Idempotent: safe to call from any thread, any host process.
    /// </summary>
    public static string DataDir
    {
        get
        {
            string? raw = ReadDataDirRegistryValue();
            return ResolveDataDir(raw, BaseDir);
        }
    }

    public static string LogsDir     => Path.Combine(DataDir, "logs");
    public static string SessionsDir => Path.Combine(DataDir, "sessions");
    public static string SettingsFilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>
    /// Pure resolution function — same input always yields same output. Exposed
    /// so the launcher's preview UI can show the user what their override will
    /// resolve to without writing it first.
    /// </summary>
    public static string ResolveDataDir(string? raw, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Path.Combine(baseDir, DefaultFolderName);
        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        // Path.IsPathRooted handles both "C:\..." and "\\server\share" — both are
        // taken at face value. A bare relative segment ("data", "..\shared")
        // anchors against BaseDir so portable moves stay portable.
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Best-effort directory creation. Callers that care should still wrap their
    /// own writes in try/catch — a misconfigured DataDir (eg. a path on a USB
    /// drive that's been unplugged) shouldn't kill the engine, just disable
    /// telemetry/logging.
    /// </summary>
    public static void EnsureExists()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
        try { Directory.CreateDirectory(LogsDir); } catch { }
        try { Directory.CreateDirectory(SessionsDir); } catch { }
    }

    private static string? ReadDataDirRegistryValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RegRoot);
            return k?.GetValue("DataDir") as string;
        }
        catch { return null; }
    }
}

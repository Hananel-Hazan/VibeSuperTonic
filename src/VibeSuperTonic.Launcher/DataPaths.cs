using Microsoft.Win32;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// Mirror of <c>VibeSuperTonic.Engine.Settings.DataPaths</c> — intentionally
/// duplicated rather than shared via project reference because the engine is
/// loaded as a COM in-proc DLL inside arbitrary host processes (Lingoes, NVDA,
/// Word…) and pulling the launcher in via project reference would balloon
/// every host's working set.
///
/// Keep the resolution rules identical — see the engine-side file for the
/// authoritative documentation.
/// </summary>
internal static class DataPaths
{
    public const string DefaultFolderName = "data";
    private const string RegRoot = @"SOFTWARE\VibeSuperTonic";

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

    public static string DataDir => ResolveDataDir(ReadDataDirRegistryValue(), BaseDir);
    public static string LogsDir     => Path.Combine(DataDir, "logs");
    public static string SessionsDir => Path.Combine(DataDir, "sessions");
    public static string SettingsFilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>The exact string the user typed (or null/empty if unset).</summary>
    public static string? RawDataDirOverride => ReadDataDirRegistryValue();

    public static string ResolveDataDir(string? raw, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Path.Combine(baseDir, DefaultFolderName);
        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Persists the override. Empty/null clears it (data dir reverts to default).
    /// Callers should normally ensure the directory is writable before saving.
    /// </summary>
    public static void SetDataDirOverride(string? raw)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RegRoot, writable: true);
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { k.DeleteValue("DataDir", throwOnMissingValue: false); } catch { }
        }
        else
        {
            k.SetValue("DataDir", raw.Trim(), RegistryValueKind.String);
        }
    }

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

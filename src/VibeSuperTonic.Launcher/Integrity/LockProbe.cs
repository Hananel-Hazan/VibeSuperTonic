using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeSuperTonic.Launcher.Integrity;

internal sealed record ProcessHolder(int Pid, string ImageName, string FriendlyName);

/// <summary>
/// Wraps Restart Manager (rstrtmgr.dll) to identify which processes hold a
/// given file open. Used before any operation that overwrites engine binaries
/// or model files so we can show the user "close X first" instead of failing
/// with a generic sharing-violation.
/// </summary>
internal static class LockProbe
{
    public static IReadOnlyList<ProcessHolder> GetHolders(params string[] filePaths)
    {
        var existing = filePaths.Where(File.Exists).ToArray();
        if (existing.Length == 0) return Array.Empty<ProcessHolder>();

        uint sessionHandle = 0;
        var sessionKey = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        int rc = RmStartSession(out sessionHandle, 0, sessionKey);
        if (rc != 0) return Array.Empty<ProcessHolder>();

        try
        {
            rc = RmRegisterResources(sessionHandle, (uint)existing.Length, existing, 0, null, 0, null);
            if (rc != 0) return Array.Empty<ProcessHolder>();

            uint pnProcInfoNeeded = 0;
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = RmRebootReasonNone;

            rc = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);
            if (rc == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
            {
                var procInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;
                rc = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, procInfo, ref lpdwRebootReasons);
                if (rc == 0)
                {
                    var holders = new List<ProcessHolder>((int)pnProcInfo);
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        var pid = (int)procInfo[i].Process.dwProcessId;
                        string imageName;
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            imageName = p.ProcessName;
                        }
                        catch { imageName = procInfo[i].strAppName ?? "(unknown)"; }
                        holders.Add(new ProcessHolder(pid, imageName, FriendlyName(imageName)));
                    }
                    return holders;
                }
            }
            else if (rc == 0) return Array.Empty<ProcessHolder>();
            return Array.Empty<ProcessHolder>();
        }
        finally
        {
            RmEndSession(sessionHandle);
        }
    }

    public static string FriendlyName(string imageName)
    {
        var key = imageName.ToLowerInvariant();
        if (key.EndsWith(".exe", StringComparison.Ordinal)) key = key[..^4];
        return key switch
        {
            "balabolka"      => "Balabolka",
            "balabolka_x86"  => "Balabolka (32-bit)",
            "nvda"           => "NVDA Screen Reader",
            "nvda_uiaccess"  => "NVDA Screen Reader",
            "narrator"       => "Windows Narrator",
            "natspeak"       => "Dragon NaturallySpeaking",
            "msedge"         => "Microsoft Edge (Read Aloud)",
            "winword"        => "Microsoft Word (Speak)",
            "powerpoint"     => "Microsoft PowerPoint (Speak)",
            "outlook"        => "Microsoft Outlook (Speak)",
            "vibesupertonic" => "VibeSuperTonic Control Panel",
            _                => imageName,
        };
    }

    public static bool IsWritable(string path)
    {
        if (!File.Exists(path)) return true;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // ---------------------------------------------------------- P/Invoke

    private const int CCH_RM_SESSION_KEY = 32;
    private const int ERROR_MORE_DATA = 234;
    private const uint RmRebootReasonNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private const int RM_INVALID_TS_SESSION = -1;
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}

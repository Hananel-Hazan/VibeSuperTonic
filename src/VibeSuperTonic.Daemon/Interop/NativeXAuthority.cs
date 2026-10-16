using System.Runtime.InteropServices;

namespace VibeSuperTonic.Daemon.Interop;

/// <summary>
/// Point libX11 at the current session's <c>XAUTHORITY</c> before the X11
/// selection source connects.
///
/// <para><b>Why.</b> The daemon is long-lived and its environment is fixed at
/// start. A daemon that outlived a logout (started by the Speech Dispatcher
/// module, whose systemd user service survives one) still names the old
/// session's cookie file, and on an X11 session every hotkey press then fails
/// with "could not open X display". <see cref="Core.Ipc.ClientDisplay"/> fixed
/// the same thing for the window on 2026-09-26 by setting the window's
/// environment; the capture runs inside this process, so it needs this
/// process's own environment changed.</para>
///
/// <para><b>Through libc, not .NET.</b> libXau reads <c>getenv("XAUTHORITY")</c>
/// on every <c>XOpenDisplay</c>, and <see cref="Environment.SetEnvironmentVariable(string, string?)"/>
/// changes only .NET's copy, which native code never sees. <c>setenv</c> is not
/// safe against a concurrent <c>getenv</c> on another thread; it runs only when
/// a new session has reported in, on the capture path, which is the only
/// native code here that reads this variable.</para>
/// </summary>
internal static class NativeXAuthority
{
    private const string Name = "XAUTHORITY";

    /// <summary>
    /// What to do, pure. <paramref name="session"/> is
    /// <see cref="Core.Ipc.ClientDisplay.ForWindow"/>'s answer: no
    /// <c>XAUTHORITY</c> key means "nothing reported, or the reported file is
    /// gone", and changes nothing; a null value means "the default,
    /// <c>~/.Xauthority</c>", which a stale value would hide.
    /// </summary>
    internal static (bool Change, string? Value) Decide(string? current, IReadOnlyDictionary<string, string?> session)
    {
        if (!session.TryGetValue(Name, out string? wanted)) return (false, null);
        return wanted == current ? (false, null) : (true, wanted);
    }

    /// <summary>Apply it. Returns the sentence to log when it changed something.</summary>
    internal static string? Apply(IReadOnlyDictionary<string, string?> session)
    {
        string? current = Marshal.PtrToStringUTF8(getenv(Name));
        var (change, value) = Decide(current, session);
        if (!change) return null;

        int rc = value is null ? unsetenv(Name) : setenv(Name, value, 1);
        return rc == 0
            ? $"selection: XAUTHORITY now {value ?? "unset (the default)"}, as the latest client reported (was {current ?? "unset"})"
            : $"selection: could not update XAUTHORITY (errno {Marshal.GetLastPInvokeError()})";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getenv(string name);

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", SetLastError = true)]
    private static extern int unsetenv(string name);
}

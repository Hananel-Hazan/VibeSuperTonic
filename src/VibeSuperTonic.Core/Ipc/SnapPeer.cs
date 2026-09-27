namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// Whether this process belongs to a snap install of this product, and the
/// control socket name that confinement allows it.
///
/// <para><b>Why an abstract socket.</b> Revision 1 on the store's edge channel
/// aborted its daemon 1.7 s after every hotkey press, "Permission denied" at
/// <c>Socket.Listen</c> in <c>DaemonServer.Bind</c>, found by the first person
/// to install it (2026-09-25). This type was written on the belief that
/// AppArmor refused <c>listen()</c> on the socket FILE. It did not: the kernel
/// logged a seccomp denial of syscall 50, <c>listen</c>, which snapd's default
/// filter leaves out and only the <c>network-bind</c> plug grants, whatever the
/// address. That plug is the fix (snapcraft.yaml.in). The abstract name stays
/// because the template allows <c>@snap.&lt;instance&gt;.*</c> to every app of
/// the snap and it depends on no directory existing.</para>
///
/// <para><b>An abstract socket has no file mode</b>, so the 0600 that protects
/// the socket file everywhere else does not exist here: any process on the
/// machine, any user's, can connect. The daemon therefore checks each peer's
/// uid (<c>SO_PEERCRED</c>) on an abstract socket and refuses anyone else. The
/// uid is in the name too, so two users of one machine each get their own.</para>
///
/// <para>Validated rather than trusted, for the reason <see cref="FlatpakPeer"/>
/// is: these variables are inherited by children, and a tarball daemon started
/// from a snap's terminal must not move its socket somewhere its clients will
/// not look.</para>
/// </summary>
/// <param name="Instance">The snap's instance name, <c>$SNAP_INSTANCE_NAME</c>.</param>
/// <param name="Uid">Whose socket this is, from <c>$SNAP_UID</c>.</param>
public sealed record SnapPeer(string Instance, string Uid)
{
    private static SnapPeer? _current;
    private static bool _resolved;

    /// <summary>This process, resolved once.</summary>
    public static SnapPeer? Current
    {
        get
        {
            if (!_resolved)
            {
                _current = Detect(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
                _resolved = true;
            }
            return _current;
        }
    }

    private static SnapPeer? _host;
    private static bool _hostResolved;

    /// <summary>
    /// This process runs from a snap's files WITHOUT its confinement: the
    /// hotkey client, bound as <c>/snap/&lt;name&gt;/current/vst-ctl</c>. Null
    /// inside the snap (see <see cref="Current"/>) and everywhere else.
    ///
    /// <para><b>Why the hotkey runs it that way.</b> Through <c>/snap/bin</c>,
    /// <c>snap run</c> sets up confinement on every press, and that alone
    /// measured 108 ms on Kubuntu (2026-09-27; <c>snap run --shell … true</c>
    /// costs the same), against a 100 ms budget for the whole press. Run
    /// directly it is 6 ms. An unconfined client of the same user may connect
    /// to the confined daemon's abstract socket (seen, no AppArmor denial), and
    /// the daemon checks the uid regardless. It is the Flatpak's arrangement:
    /// the client on the host, the daemon in the sandbox.</para>
    ///
    /// <para>Kept apart from <see cref="Current"/> because everything that asks
    /// <c>Current</c> means "inside": the store, the update watch, the Wayland
    /// link, the window's command chain. Only the socket name and how to start
    /// the daemon are shared.</para>
    /// </summary>
    public static SnapPeer? Host
    {
        get
        {
            if (!_hostResolved)
            {
                _host = Current is null ? DetectHost(Environment.GetEnvironmentVariable, AppContext.BaseDirectory, Posix.Uid) : null;
                _hostResolved = true;
            }
            return _host;
        }
    }

    /// <summary>
    /// The host-side rule, pure: no <c>$SNAP</c>, and this executable sits
    /// directly in <c>/snap/&lt;instance&gt;/&lt;revision or current&gt;/</c>.
    /// </summary>
    public static SnapPeer? DetectHost(Func<string, string?> env, string baseDir, Func<uint> uid)
    {
        if (!string.IsNullOrWhiteSpace(env("SNAP"))) return null;
        string[] parts = baseDir.TrimEnd('/').Split('/');
        // "", "snap", instance, revision
        if (parts.Length != 4 || parts[0] != "" || parts[1] != "snap") return null;
        if (!IsInstanceName(parts[2])) return null;
        if (parts[3] != "current" && !parts[3].All(char.IsAsciiDigit) && !(parts[3].StartsWith('x') && parts[3][1..].All(char.IsAsciiDigit)))
            return null;
        return new SnapPeer(parts[2], uid().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// What the host-side client starts when no daemon answers: the daemon app
    /// through <c>snap run</c>, so it runs confined, with the snap's store and
    /// plugs, and never as a plain child of an unconfined client.
    /// </summary>
    public string DaemonCommand => $"/snap/bin/{Instance}.daemon";

    /// <summary>
    /// The abstract socket name, written with the leading <c>@</c> that tools
    /// such as <c>ss -x</c> print. <see cref="Protocol.EndPoint"/> turns it into
    /// the leading NUL the kernel wants.
    /// </summary>
    public string SocketName => $"@snap.{Instance}.ctl-{Uid}";

    /// <summary>
    /// The rule, pure. Environment variables only, and the cheapest first: this
    /// runs on the hotkey path, and outside a snap it makes no system call.
    /// </summary>
    public static SnapPeer? Detect(Func<string, string?> env, string baseDir)
    {
        string? snap = env("SNAP");
        if (string.IsNullOrWhiteSpace(snap) || !snap.StartsWith('/')) return null;

        string? instance = env("SNAP_INSTANCE_NAME");
        if (string.IsNullOrWhiteSpace(instance)) instance = env("SNAP_NAME");
        if (string.IsNullOrWhiteSpace(instance) || !IsInstanceName(instance.Trim())) return null;

        // The executable really is inside the snap.
        string root = snap.TrimEnd('/');
        string dir = baseDir.TrimEnd('/');
        if (dir != root && !dir.StartsWith(root + "/", StringComparison.Ordinal)) return null;

        // snapd sets SNAP_UID for every app. Digits only, because it becomes
        // part of a socket name.
        string? uid = env("SNAP_UID")?.Trim();
        if (string.IsNullOrEmpty(uid) || !uid.All(char.IsAsciiDigit)) return null;

        return new SnapPeer(instance.Trim(), uid);
    }

    /// <summary>
    /// A snap name, optionally with a parallel-install key: lowercase letters,
    /// digits and hyphens, then <c>_key</c>.
    /// </summary>
    public static bool IsInstanceName(string value)
    {
        if (value.Length is 0 or > 60) return false;
        foreach (char c in value)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')) return false;
        return char.IsAsciiLetterOrDigit(value[0]);
    }
}

internal static class Posix
{
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    /// <summary>The real uid. Only asked on Linux, from a path under /snap.</summary>
    public static uint Uid() => GetUid();
}

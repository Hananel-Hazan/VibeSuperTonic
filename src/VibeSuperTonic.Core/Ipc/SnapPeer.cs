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

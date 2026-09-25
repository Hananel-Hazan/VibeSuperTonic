namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// Whether this process belongs to a Flatpak install of this product, and from
/// which side of the sandbox wall.
///
/// <para><b>Why the socket and the daemon launch need to know.</b> A Flatpak
/// gives every <c>flatpak run</c> its own private <c>/run/user/&lt;uid&gt;</c>, so
/// a daemon listening on <c>$XDG_RUNTIME_DIR/vibesupertonic/ctl.sock</c> is
/// listening where no other instance can see it: the window, the hotkey client
/// and the Speech Dispatcher module would each find no daemon, start their own,
/// and speak over each other. The one runtime directory every instance of an app
/// shares, and the host can see at the same path, is
/// <c>$XDG_RUNTIME_DIR/app/&lt;app-id&gt;</c>. So the socket goes there.</para>
///
/// <para>And a Flatpak runs each launch in its own PID namespace, torn down when
/// the launch's main process exits. A daemon started as a plain child of the
/// window dies when the window closes, and one started by a hotkey press dies
/// before it has said a word. It has to be started as an instance of its own.
/// See <see cref="DaemonLaunch"/>.</para>
///
/// <para><b>The host side exists for the hotkey.</b> <c>vst-ctl</c> is NativeAOT
/// because every press pays its startup, and <c>flatpak run</c> costs a sandbox
/// setup of its own on every press. The binary inside the deployment needs
/// nothing but libc, so the hotkey runs it directly from the host, and it finds
/// the daemon's socket by reading the app id from the deployment's own
/// <c>metadata</c> file. That file sits two levels above <c>files/</c> in every
/// Flatpak deployment.</para>
///
/// <para>In Core rather than the daemon because <c>vst-ctl</c> and the window
/// both need it, and neither references the daemon. It names no platform type:
/// it reads two environment variables and two files, as
/// <see cref="Protocol.SocketPath()"/> already reads one.</para>
/// </summary>
/// <param name="AppId">The Flatpak application id.</param>
/// <param name="Inside">True inside the sandbox, false for the host-side client.</param>
public sealed record FlatpakPeer(string AppId, bool Inside)
{
    /// <summary>
    /// Where the composed tree lands inside a Flatpak. The manifest installs it
    /// here, and the host side recognises a deployment by this suffix.
    /// </summary>
    public const string InstallSuffix = "/files/lib/vibesupertonic";

    /// <summary>The same directory as the sandbox sees it.</summary>
    public const string SandboxDir = "/app/lib/vibesupertonic";

    private static FlatpakPeer? _current;
    private static bool _resolved;

    /// <summary>This process, resolved once.</summary>
    public static FlatpakPeer? Current
    {
        get
        {
            if (!_resolved)
            {
                _current = Detect(Environment.GetEnvironmentVariable, AppContext.BaseDirectory,
                                  File.Exists, TryReadAllText);
                _resolved = true;
            }
            return _current;
        }
    }

    /// <summary>
    /// The rule, pure, so the tests do not need a Flatpak.
    ///
    /// <para><b>Cheapest question first</b>, because this runs on the hotkey
    /// path: an environment variable, then a string suffix, and only a file read
    /// when both already say "Flatpak". A tarball install makes no system call
    /// here at all.</para>
    /// </summary>
    public static FlatpakPeer? Detect(
        Func<string, string?> env,
        string baseDir,
        Func<string, bool> fileExists,
        Func<string, string?> readFile)
    {
        // Inside: the variable is set by flatpak run, and the file exists only in
        // a sandbox. Both, because a variable is inherited by anything a shell
        // started, and this one decides where the socket is.
        string? id = env("FLATPAK_ID");
        if (!string.IsNullOrWhiteSpace(id) && IsAppId(id.Trim()) && fileExists("/.flatpak-info"))
            return new FlatpakPeer(id.Trim(), Inside: true);

        // Host side: this binary lives in a deployment. The suffix check is the
        // cheap filter; the metadata file is the proof.
        string dir = baseDir.TrimEnd('/');
        if (!dir.EndsWith(InstallSuffix, StringComparison.Ordinal)) return null;

        string deploy = dir[..^InstallSuffix.Length];
        string? metadata = readFile(deploy + "/metadata");
        string? appId = metadata is null ? null : AppIdFromMetadata(metadata);
        return appId is null ? null : new FlatpakPeer(appId, Inside: false);
    }

    /// <summary>
    /// <c>name=</c> from the <c>[Application]</c> group of a deployment's
    /// <c>metadata</c>, a GKeyFile. A runtime's metadata has a <c>[Runtime]</c>
    /// group instead, and is not us.
    /// </summary>
    public static string? AppIdFromMetadata(string metadata)
    {
        bool inApplication = false;
        foreach (string raw in metadata.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inApplication = line == "[Application]";
                continue;
            }
            if (!inApplication || !line.StartsWith("name=", StringComparison.Ordinal)) continue;

            string value = line["name=".Length..].Trim();
            return IsAppId(value) ? value : null;
        }
        return null;
    }

    /// <summary>
    /// A Flatpak id is reverse-DNS: dot-separated elements of letters, digits,
    /// <c>_</c> and <c>-</c>, at least three of them. Checked because the value
    /// becomes a path component and a command-line argument.
    /// </summary>
    public static bool IsAppId(string value)
    {
        if (value.Length is 0 or > 255) return false;
        string[] parts = value.Split('.');
        if (parts.Length < 3) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0 || char.IsAsciiDigit(part[0])) return false;
            foreach (char c in part)
                if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') return false;
        }
        return true;
    }

    /// <summary>
    /// The runtime directory every instance of this app shares, given the
    /// session's <c>$XDG_RUNTIME_DIR</c>. The same path inside and outside.
    /// </summary>
    public string SharedRuntimeDir(string xdgRuntimeDir) =>
        // '/' rather than Path.Combine: a Flatpak path is a Linux path, and Core's
        // tests run on Windows too, where Path.Combine would write backslashes.
        xdgRuntimeDir.TrimEnd('/') + "/app/" + AppId;

    private static string? TryReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}

/// <summary>
/// How to start the daemon so that it outlives whoever started it.
///
/// <para>A tarball, an AppImage and a snap all start it as an ordinary child
/// process. The child outlives its parent in all three. A snap gives
/// every process of one snap the same confinement and the same
/// <c>$XDG_RUNTIME_DIR</c>, so a child of the window or of <c>vst-ctl</c> is in
/// the right place already.</para>
///
/// <para>A Flatpak does not. See <see cref="FlatpakPeer"/>. Inside, the daemon
/// is started through <c>flatpak-spawn</c>, which asks the Flatpak portal for a
/// new sandbox of the same app. It needs no permission, and without
/// <c>--watch-bus</c> the new instance is not tied to the caller's lifetime.
/// From the host side, it is started with <c>flatpak run</c>.</para>
/// </summary>
public static class DaemonLaunch
{
    /// <summary>
    /// The program and arguments that start <paramref name="daemon"/>, given
    /// the arguments it should receive.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) Plan(
        string daemon, IReadOnlyList<string> arguments, FlatpakPeer? flatpak)
    {
        if (flatpak is null) return (daemon, arguments);

        // Inside the sandbox the path is the sandbox's own; from the host it is
        // the deployment's, and the sandbox will not recognise it. Either way
        // the daemon's name is the same, under the directory the manifest uses.
        string inSandbox = flatpak.Inside
            ? daemon
            : $"{FlatpakPeer.SandboxDir}/{Path.GetFileName(daemon)}";

        var args = new List<string>();
        if (flatpak.Inside)
        {
            args.Add(inSandbox);
            args.AddRange(arguments);
            return ("flatpak-spawn", args);
        }

        args.Add("run");
        args.Add($"--command={inSandbox}");
        args.Add(flatpak.AppId);
        args.AddRange(arguments);
        return ("flatpak", args);
    }
}

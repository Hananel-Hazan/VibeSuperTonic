using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Where the control socket lives and how the daemon is started, for a Flatpak
/// install. Both fail silently when wrong: two instances that cannot see each
/// other's socket each start a daemon and speak over each other, and a daemon
/// started as a plain child of a hotkey press dies with the press.
/// </summary>
public sealed class FlatpakPeerTests
{
    private const string Id = "io.github.hananel_hazan.VibeSuperTonic";
    private const string Deploy = "/var/lib/flatpak/app/" + Id + "/x86_64/stable/active";
    private const string HostDir = Deploy + "/files/lib/vibesupertonic/";

    private const string Metadata = """
        [Application]
        name=io.github.hananel_hazan.VibeSuperTonic
        runtime=org.freedesktop.Platform/x86_64/25.08
        command=vibesupertonic

        [Context]
        sockets=wayland;fallback-x11;pulseaudio;
        """;

    private static FlatpakPeer? Detect(
        Dictionary<string, string>? env = null,
        string baseDir = "/opt/vibesupertonic/",
        bool flatpakInfo = false,
        Dictionary<string, string>? files = null)
    {
        env ??= [];
        files ??= [];
        return FlatpakPeer.Detect(
            name => env.TryGetValue(name, out var v) ? v : null,
            baseDir,
            path => path == "/.flatpak-info" ? flatpakInfo : files.ContainsKey(path),
            path => files.TryGetValue(path, out var v) ? v : null);
    }

    [Fact]
    public void A_tarball_install_is_not_a_flatpak()
    {
        Assert.Null(Detect());
    }

    [Fact]
    public void Inside_the_sandbox_needs_both_the_variable_and_the_info_file()
    {
        var env = new Dictionary<string, string> { ["FLATPAK_ID"] = Id };

        Assert.Equal(new FlatpakPeer(Id, Inside: true),
                     Detect(env, baseDir: "/app/lib/vibesupertonic/", flatpakInfo: true));

        // A variable left over in a shell that once ran a Flatpak must not move
        // the socket of a tarball daemon somewhere its clients will not look.
        Assert.Null(Detect(env, flatpakInfo: false));
    }

    [Fact]
    public void The_host_side_client_reads_the_app_id_from_the_deployment()
    {
        var files = new Dictionary<string, string> { [Deploy + "/metadata"] = Metadata };

        Assert.Equal(new FlatpakPeer(Id, Inside: false), Detect(baseDir: HostDir, files: files));
    }

    [Fact]
    public void A_deployment_shaped_path_without_metadata_is_not_a_flatpak()
    {
        // The suffix alone is only the filter. Somebody's own directory can end
        // in files/lib/vibesupertonic.
        Assert.Null(Detect(baseDir: HostDir));
    }

    [Fact]
    public void A_runtime_metadata_file_is_not_an_application()
    {
        const string runtime = """
            [Runtime]
            name=org.freedesktop.Platform
            """;

        Assert.Null(FlatpakPeer.AppIdFromMetadata(runtime));
        Assert.Equal(Id, FlatpakPeer.AppIdFromMetadata(Metadata));
    }

    [Theory]
    [InlineData("io.github.hananel_hazan.VibeSuperTonic", true)]
    [InlineData("org.example.App-Name", true)]
    [InlineData("two.parts", false)]
    [InlineData("io.github..App", false)]
    [InlineData("io.1github.App", false)]
    [InlineData("io.github.App/../../etc", false)]
    [InlineData("io.github.App name", false)]
    [InlineData("", false)]
    public void An_app_id_becomes_a_path_so_it_is_checked(string value, bool valid)
    {
        Assert.Equal(valid, FlatpakPeer.IsAppId(value));
    }

    [Fact]
    public void The_socket_moves_to_the_runtime_directory_every_instance_shares()
    {
        var peer = new FlatpakPeer(Id, Inside: true);

        // Path.Combine for the part SocketPath has always joined that way, so the
        // test holds on the Windows runner too. The Flatpak part is always '/'.
        Assert.Equal(Path.Combine($"/run/user/1000/app/{Id}", "vibesupertonic", "ctl.sock"),
                     Protocol.SocketPath("/run/user/1000", _ => true, peer));

        // And the host side names the same file, which is the whole point.
        Assert.Equal(Protocol.SocketPath("/run/user/1000", _ => true, peer),
                     Protocol.SocketPath("/run/user/1000", _ => true, peer with { Inside = false }));
    }

    [Fact]
    public void Without_a_flatpak_the_socket_is_where_it_always_was()
    {
        Assert.Equal(Path.Combine("/run/user/1000", "vibesupertonic", "ctl.sock"),
                     Protocol.SocketPath("/run/user/1000", _ => true, flatpak: null));
    }

    [Fact]
    public void Outside_a_flatpak_the_daemon_is_started_directly()
    {
        var (file, args) = DaemonLaunch.Plan("/opt/vst/vibesupertonicd", ["daemon"], flatpak: null);

        Assert.Equal("/opt/vst/vibesupertonicd", file);
        Assert.Equal(["daemon"], args);
    }

    [Fact]
    public void Inside_a_flatpak_the_daemon_gets_a_sandbox_of_its_own()
    {
        var (file, args) = DaemonLaunch.Plan(
            "/app/lib/vibesupertonic/vibesupertonicd", [], new FlatpakPeer(Id, Inside: true));

        Assert.Equal("flatpak-spawn", file);
        Assert.Equal(["/app/lib/vibesupertonic/vibesupertonicd"], args);
        // --watch-bus would tie the daemon to the caller, which is the bug.
        Assert.DoesNotContain("--watch-bus", args);
    }

    [Fact]
    public void From_the_host_the_daemon_is_started_with_flatpak_run_at_its_sandbox_path()
    {
        var (file, args) = DaemonLaunch.Plan(
            HostDir + "vibesupertonicd", [], new FlatpakPeer(Id, Inside: false));

        Assert.Equal("flatpak", file);
        // The deployment path means nothing inside the sandbox; /app does.
        Assert.Equal(["run", "--command=/app/lib/vibesupertonic/vibesupertonicd", Id], args);
    }
}

using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The control socket a snap install uses. Revision 1 on the store used a socket
/// file, which AppArmor let the daemon create and refused to listen on, so the
/// daemon aborted on every hotkey press. These pin the name confinement allows.
/// </summary>
public sealed class SnapPeerTests
{
    private static Dictionary<string, string> SnapEnv() => new()
    {
        ["SNAP"] = "/snap/vibesupertonic/2",
        ["SNAP_NAME"] = "vibesupertonic",
        ["SNAP_INSTANCE_NAME"] = "vibesupertonic",
        ["SNAP_UID"] = "1000",
    };

    private static SnapPeer? Detect(Dictionary<string, string> env, string baseDir = "/snap/vibesupertonic/2/") =>
        SnapPeer.Detect(name => env.TryGetValue(name, out var v) ? v : null, baseDir);

    [Fact]
    public void A_snap_listens_on_an_abstract_name_its_confinement_allows()
    {
        var peer = Detect(SnapEnv());

        Assert.Equal(new SnapPeer("vibesupertonic", "1000"), peer);
        // snapd's template allows bind and listen on "@snap.<instance>.**" and
        // nothing that is a file. The uid keeps two users of one machine apart.
        Assert.Equal("@snap.vibesupertonic.ctl-1000", peer!.SocketName);
        Assert.Equal(peer.SocketName, Protocol.SocketPath("/run/user/1000/snap.vibesupertonic", _ => true, null, peer));
    }

    [Fact]
    public void A_parallel_install_uses_its_instance_name()
    {
        var env = SnapEnv();
        env["SNAP"] = "/snap/vibesupertonic_beta/3";
        env["SNAP_INSTANCE_NAME"] = "vibesupertonic_beta";

        Assert.Equal("@snap.vibesupertonic_beta.ctl-1000",
                     Detect(env, "/snap/vibesupertonic_beta/3")!.SocketName);
    }

    [Fact]
    public void Snap_variables_inherited_outside_the_snap_are_ignored()
    {
        // A tarball daemon started from a terminal a snap opened keeps its file.
        Assert.Null(Detect(SnapEnv(), "/opt/VibeSuperTonic/"));
    }

    [Theory]
    [InlineData("SNAP_UID", "")]
    [InlineData("SNAP_UID", "10a0")]
    [InlineData("SNAP_INSTANCE_NAME", "Vibe/../x")]
    public void Values_that_become_part_of_a_socket_name_are_checked(string name, string value)
    {
        var env = SnapEnv();
        env[name] = value;
        if (name == "SNAP_INSTANCE_NAME") env.Remove("SNAP_NAME");

        Assert.Null(Detect(env));
    }

    [Fact]
    public void Outside_a_snap_the_socket_is_a_file_as_before()
    {
        Assert.False(Protocol.IsAbstract(Protocol.SocketPath("/run/user/1000", _ => true, flatpak: null)));
    }

    [Fact]
    public void An_abstract_name_reaches_the_kernel_with_a_leading_nul()
    {
        Assert.True(Protocol.IsAbstract("@snap.vibesupertonic.ctl-1000"));

        // Abstract unix sockets are a Linux feature, and Core's tests also run
        // on the Windows runner.
        if (!OperatingSystem.IsLinux()) return;

        var endpoint = Protocol.EndPoint("@snap.vibesupertonic.ctl-1000");
        var file = Protocol.EndPoint("/run/user/1000/vibesupertonic/ctl.sock");

        // The serialized address: sun_path begins with NUL for an abstract name.
        var abstractAddress = endpoint.Serialize();
        Assert.Equal(0, abstractAddress[2]);
        Assert.Equal((byte)'s', abstractAddress[3]);
        Assert.Equal((byte)'/', file.Serialize()[2]);
    }
}

using System.Net.Sockets;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The check that stands in for a socket file's 0600 mode on a snap's abstract
/// socket, which has no mode and which any user on the machine can connect to.
/// Driven over a real abstract socket, because the kernel is what fills in the
/// credentials and a fake would only test the fake.
/// </summary>
public sealed class PeerCredentialsTests
{
    [Fact]
    public void A_connection_from_this_user_over_an_abstract_socket_is_recognised()
    {
        string name = $"@vst-test.ctl-{Guid.NewGuid():N}";

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(Protocol.EndPoint(name));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(Protocol.EndPoint(name));
        using var accepted = listener.Accept();

        Assert.NotNull(PeerCredentials.PeerUid(accepted));
        Assert.True(PeerCredentials.IsSameUser(accepted));
    }

    [Fact]
    public void A_socket_with_no_peer_is_a_stranger()
    {
        using var lonely = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        Assert.False(PeerCredentials.IsSameUser(lonely));
    }
}

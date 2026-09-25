using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Who is on the other end of a unix socket, from the kernel rather than from
/// anything the peer says.
///
/// <para>Needed only for an abstract socket, which is what a snap listens on
/// (see <c>SnapPeer</c> in Core). A socket file is protected by its 0600 mode
/// and its 0700 directory; an abstract name has neither, so any process on the
/// machine, any user's, could connect and drive this user's speakers. The
/// kernel records the connecting process's credentials at <c>connect()</c>, and
/// <c>SO_PEERCRED</c> reads them back. The peer cannot forge them.</para>
/// </summary>
internal static class PeerCredentials
{
    private const int SolSocket = 1;    // SOL_SOCKET on Linux
    private const int SoPeerCred = 17;  // SO_PEERCRED on Linux

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <summary>
    /// The peer's uid, or null if the kernel would not say — which the caller
    /// treats as a stranger.
    /// </summary>
    public static uint? PeerUid(Socket socket)
    {
        try
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; } — three 32-bit fields.
            Span<byte> ucred = stackalloc byte[12];
            int length = socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred);
            return length >= 8 ? BitConverter.ToUInt32(ucred[4..8]) : null;
        }
        catch (SocketException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>True when the peer runs as the same user as this process.</summary>
    public static bool IsSameUser(Socket socket) =>
        PeerUid(socket) is uint peer && peer == geteuid();
}

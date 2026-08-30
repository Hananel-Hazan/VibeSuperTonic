using System.Net.Sockets;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Who else on this machine can talk to the daemon. The answer has to be
/// nobody, and until now nothing said so.
///
/// <para><b>The socket is a remote control for the user's speakers and a reader
/// of their selection.</b> Anything that can connect to it can make the machine
/// speak, ask what is being read, and see the text of whatever window had focus
/// — so the directory is 0700 and the socket 0600, set explicitly rather than
/// inherited. docs/TESTING-PLAN.md, "safe": this was the one surface on that
/// list with a defence and no test.</para>
///
/// <para><b>Explicitly, because the fallback path is world-writable.</b> Under
/// <c>$XDG_RUNTIME_DIR</c> the parent is already 0700 and the modes would come
/// out right by inheritance — which is exactly what makes this worth checking,
/// since the inherited version passes on every developer machine and fails only
/// in an <c>su</c>'d or container session, where the path falls back to
/// <c>/tmp</c>. The failure mode is a mode that widens under a refactor: silent,
/// invisible in any log, and reachable by any local process.</para>
///
/// <para>These tests move <c>$XDG_RUNTIME_DIR</c>, which is process-wide, so
/// they are one class and xunit runs a class's tests one at a time. The path
/// stays SHORT — AF_UNIX truncates at 108 bytes and a socket under a long
/// scratch path fails to bind with an error that reads like a permissions
/// problem.</para>
/// </summary>
public sealed class SocketModeTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDir = Private | UnixFileMode.UserExecute;

    private readonly string _root = Path.Combine("/tmp", $"vst-sock-{Guid.NewGuid():N}"[..24]);
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"vst-sock-data-{Guid.NewGuid():N}");

    private readonly string? _runtimeWas = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    private readonly string? _tmpWas = Environment.GetEnvironmentVariable("TMPDIR");

    /// <summary>
    /// Held open for the length of the test, because the socket file is the
    /// thing being measured and <see cref="DaemonServer.Dispose"/> unlinks it —
    /// which is correct, and cost the first version of these tests an hour of
    /// "nothing was bound".
    /// </summary>
    private DaemonServer? _server;

    public SocketModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _server?.Dispose();
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtimeWas);
        Environment.SetEnvironmentVariable("TMPDIR", _tmpWas);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A synthesizer that is never asked for anything: Bind touches none of this.</summary>
    private sealed class Silent : ISynthesizer
    {
        public int SampleRate => 22050;
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken token = default) => [];
        public Task PreloadAsync(CancellationToken token = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoDevice : IAudioSink
    {
        public int SampleRate => 22050;
        public long WrittenFrames => 0;
        public long LatencyUsec => 0;
        public void Write(ReadOnlySpan<short> pcm) { }
        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() { }
    }

    /// <summary>Bind a real server on the redirected path and hand back what it made.</summary>
    private (string Socket, string Dir) Bind()
    {
        Directory.CreateDirectory(_dataRoot);

        var synth = new Silent();
        var session = new SpeechSession(synth, new NoDevice());
        var config = new HostConfig(_dataRoot, _dataRoot);

        _server = new DaemonServer(new DaemonOptions(), config, synth, session);
        _server.Bind();

        string path = Protocol.SocketPath();
        return (path, Path.GetDirectoryName(path)!);
    }

    [Fact]
    public void The_socket_is_private_to_the_user_who_started_the_daemon()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);

        var (socket, dir) = Bind();

        Assert.True(File.Exists(socket), $"nothing was bound at {socket}");
        Assert.Equal(Private, File.GetUnixFileMode(socket));
        Assert.Equal(PrivateDir, File.GetUnixFileMode(dir));
    }

    /// <summary>
    /// THE CASE THE EXPLICIT CHMOD EXISTS FOR. With no runtime directory the
    /// path falls back under <c>/tmp</c>, which is world-writable and survives
    /// logout — so nothing about the modes can be inherited, and a version of
    /// this code that relied on inheritance would pass the test above and fail
    /// here.
    /// </summary>
    [Fact]
    public void The_tmp_fallback_is_locked_down_too_because_nothing_there_is_private_by_default()
    {
        // TMPDIR as well as XDG_RUNTIME_DIR, so the fallback lands in this
        // test's own directory rather than in the shared /tmp path a real
        // daemon on this machine may be using. Redirect every variable the
        // thing under test reads, and never the user's real one: the same rule
        // the speech-dispatcher harness is built on, and there it was somebody's
        // screen reader at stake.
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
        Environment.SetEnvironmentVariable("TMPDIR", _root);

        var (socket, dir) = Bind();

        Assert.StartsWith(_root, socket);
        Assert.Equal(Private, File.GetUnixFileMode(socket));
        Assert.Equal(PrivateDir, File.GetUnixFileMode(dir));
    }

    /// <summary>
    /// A directory left behind by something else — a previous run under a
    /// different umask, or a helpful <c>mkdir -p</c> — must be NARROWED rather
    /// than accepted. Creating it 0755 and finding it 0755 afterwards would be a
    /// daemon anyone logged in could listen to.
    /// </summary>
    [Fact]
    public void An_existing_directory_with_a_wide_mode_is_narrowed_not_accepted()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);

        string dir = Path.Combine(_root, Protocol.SocketDirName);
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, PrivateDir
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var (socket, bound) = Bind();

        Assert.Equal(dir, bound);
        Assert.Equal(PrivateDir, File.GetUnixFileMode(bound));
        Assert.Equal(Private, File.GetUnixFileMode(socket));
    }

    /// <summary>
    /// And the modes are the ones the kernel enforces, not a claim about them:
    /// the socket accepts a connection from this process, which is the only
    /// proof that 0600 was applied to a live listener rather than to a file that
    /// happens to sit there.
    /// </summary>
    [Fact]
    public void What_was_bound_is_a_listening_socket_and_not_a_leftover_file()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);
        Directory.CreateDirectory(_dataRoot);

        var synth = new Silent();
        var session = new SpeechSession(synth, new NoDevice());
        _server = new DaemonServer(new DaemonOptions(), new HostConfig(_dataRoot, _dataRoot), synth, session);
        _server.Bind();

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(Protocol.SocketPath()));

        Assert.True(client.Connected);
    }
}

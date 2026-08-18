using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The window's entire relationship with the daemon: one long-lived subscription
/// for what is happening, and one short connection per verb for making something
/// happen.
///
/// <para><b>R-1, enforced rather than intended.</b> Everything here goes over the
/// same socket, in the same JSON, as <c>vst-ctl</c>. There is no privileged path
/// and no shared object — the UI cannot read pipeline state even if a later
/// change would find it convenient, because the pipeline is in another
/// process.</para>
///
/// <para><b>It starts a daemon exactly once</b>, when the window opens, and never
/// again. Opening the window is a person asking for the product; a reconnect is
/// not. Without that distinction the tray's "stop the engine" item would be
/// undone a second later by this loop, and the one verb whose whole job is to
/// release 830 MB would do nothing while a window was open.</para>
/// </summary>
public sealed class DaemonClient
{
    /// <summary>How long to wait before looking for a daemon again.</summary>
    private const int RetryMs = 1000;

    /// <summary>
    /// Fired for every line of the event stream, on a background thread. The
    /// caller marshals — the daemon is not made to wait for a repaint.
    /// </summary>
    public event Action<SessionEvent>? Received;

    /// <summary>The first line of a subscription: state, text and offset in one.</summary>
    public event Action<StatusPayload>? Snapshotted;

    /// <summary>Connected or not. The window stays open either way, and says which.</summary>
    public event Action<bool>? ConnectedChanged;

    public async Task RunAsync(CancellationToken token)
    {
        bool mayStartDaemon = true;

        while (!token.IsCancellationRequested)
        {
            Socket? socket = Connect();

            if (socket is null && mayStartDaemon)
            {
                // The one place this happens. See the class remarks.
                if (TryStartDaemon(out string error))
                {
                    socket = await WaitForDaemonAsync(TimeSpan.FromSeconds(5), token);
                }
                else
                {
                    Console.Error.WriteLine($"could not start the daemon: {error}");
                }
            }

            mayStartDaemon = false;

            if (socket is null)
            {
                await Task.Delay(RetryMs, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await SubscribeAsync(socket, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // The daemon went away mid-stream. Not an error to show: the
                // window reports it as disconnected and comes back by itself.
            }
            finally
            {
                socket.Dispose();
                ConnectedChanged?.Invoke(false);
            }

            if (!token.IsCancellationRequested)
                await Task.Delay(RetryMs, token).ConfigureAwait(false);
        }
    }

    private async Task SubscribeAsync(Socket socket, CancellationToken token)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        // hello, carried on the subscribe request itself — see Request.ClientKind.
        await writer.WriteLineAsync(Protocol.Encode(new Request
        {
            Verb = RequestVerb.Subscribe,
            ClientKind = Request.ClientKindUi,
            ClientPid = Environment.ProcessId,
        })).ConfigureAwait(false);

        // The snapshot is the first line, not a second call. A client that
        // called status and then subscribed would miss every event in between,
        // which at three to four boundaries a second is a highlight that starts
        // out wrong.
        string? first = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (first is null) return;

        var response = Protocol.TryDecode<Response>(first);
        if (response?.Status is { } snapshot)
        {
            ConnectedChanged?.Invoke(true);
            Snapshotted?.Invoke(snapshot);
        }

        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) return;                       // daemon went away

            if (Protocol.TryDecode<SessionEvent>(line) is { } evt)
                Received?.Invoke(evt);
        }
    }

    /// <summary>
    /// Send one verb and read one reply, on a connection of its own — the same
    /// shape <c>vst-ctl</c> uses, for the same reason: a request is not a
    /// session, and multiplexing it onto the subscription would put a reply in
    /// the middle of the event stream.
    /// </summary>
    /// <returns>The daemon's reply, or null when there is no daemon to ask.</returns>
    public async Task<Response?> SendAsync(Request request, CancellationToken token = default)
    {
        using Socket? socket = Connect();
        if (socket is null) return null;

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(Protocol.Encode(request)).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);

        return line is null ? null : Protocol.TryDecode<Response>(line);
    }

    // ------------------------------------------------------------- connecting

    private static Socket? Connect()
    {
        string path = Protocol.SocketPath();
        if (!File.Exists(path)) return null;

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return socket;
        }
        catch (SocketException)
        {
            // Refused means the file outlived its daemon; anything else means
            // busy. Neither is worth a message here — the window reports the
            // state, and the retry loop is a second away.
            socket.Dispose();
            return null;
        }
    }

    private static async Task<Socket?> WaitForDaemonAsync(TimeSpan budget, CancellationToken token)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < budget && !token.IsCancellationRequested)
        {
            if (Connect() is { } socket) return socket;
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Start the daemon beside this executable. Same rule as <c>vst-ctl</c>'s
    /// auto-start, deliberately: the tarball ships both binaries in one
    /// directory, so any other guess describes a layout this product does not
    /// produce.
    /// </summary>
    private static bool TryStartDaemon(out string error)
    {
        string exe = Environment.GetEnvironmentVariable("VST_DAEMON")
            ?? Path.Combine(AppContext.BaseDirectory, "vibesupertonicd");

        if (!File.Exists(exe))
        {
            error = $"{exe} not found (set VST_DAEMON to override)";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                // The daemon outlives this window. Its output is discarded
                // rather than captured, because a reader that stopped reading
                // would block the daemon's stderr once the pipe filled — a hang
                // hours later with no apparent cause. It has its own log.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var proc = Process.Start(psi);
            if (proc is null) { error = "Process.Start returned null"; return false; }

            proc.OutputDataReceived += (_, _) => { };
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}

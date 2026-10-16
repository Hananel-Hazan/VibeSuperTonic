using System.Net.Sockets;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The event stream under load, and under a subscriber that stops reading.
///
/// <para><b>Session events are raised on the audio thread</b>, and the daemon
/// used to write each one straight to every subscriber's socket. A subscriber
/// that stops reading (a hung window, <c>vst-ctl subscribe | less</c> left
/// paused) fills its socket buffer after a few minutes of word boundaries, and
/// the next write blocks the audio thread for good: speech freezes mid-word, and
/// the tray's "show window" blocks behind it. Each subscriber now has a bounded
/// queue drained by its own writer, and one that falls a queue behind is dropped
/// instead of waited for.</para>
///
/// <para>Same collection as <see cref="SocketModeTests"/>, for the same reason:
/// these move <c>$XDG_RUNTIME_DIR</c>, which is process-wide.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SubscriberStressTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", $"vst-sub-{Guid.NewGuid():N}"[..24]);
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"vst-sub-data-{Guid.NewGuid():N}");
    private readonly string? _runtimeWas = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private readonly DaemonServer _server;
    private readonly Task _serving;

    public SubscriberStressTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_dataRoot);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);

        var synth = new Silent();
        _server = new DaemonServer(new DaemonOptions(), new HostConfig(_dataRoot, _dataRoot),
            synth, new SpeechSession(synth, new NoDevice()));
        _server.Bind();
        _serving = _server.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serving.Wait(TimeSpan.FromSeconds(10)); } catch { /* cancelled */ }
        _server.Dispose();
        _cts.Dispose();
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtimeWas);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }

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

    private sealed class Client : IDisposable
    {
        public Socket Socket { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        public StreamReader Reader { get; }
        private readonly NetworkStream _stream;

        public Client(int receiveBuffer = 0)
        {
            if (receiveBuffer > 0) Socket.ReceiveBufferSize = receiveBuffer;
            Socket.Connect(new UnixDomainSocketEndPoint(Protocol.SocketPath()));
            _stream = new NetworkStream(Socket, ownsSocket: false);
            Reader = new StreamReader(_stream);
        }

        public async Task SubscribeAsync(CancellationToken token)
        {
            await using var writer = new StreamWriter(_stream, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(Protocol.Encode(new Request { Verb = RequestVerb.Subscribe }));
            var snapshot = Protocol.TryDecode<Response>(await Reader.ReadLineAsync(token) ?? "");
            Assert.True(snapshot is { Ok: true, Status: not null }, "the subscribe reply carries the snapshot");
        }

        public void Dispose()
        {
            Reader.Dispose();
            _stream.Dispose();
            Socket.Dispose();
        }
    }

    /// <summary>
    /// THE FREEZE. A subscriber that never reads past its snapshot, and far more
    /// events than any socket buffer holds. Before the queue, the raising thread
    /// (the audio thread, in the daemon) blocked in the write and this timed out.
    /// </summary>
    [Fact]
    public async Task A_subscriber_that_stops_reading_never_blocks_the_thread_raising_events()
    {
        using var stalled = new Client(receiveBuffer: 4096);
        await stalled.SubscribeAsync(_cts.Token);

        // ~60 bytes a line: 50,000 is ~3 MB, an order of magnitude past what the
        // kernel buffers for one AF_UNIX connection.
        var raising = Task.Run(() =>
        {
            for (int i = 0; i < 50_000; i++) _server.RaiseWindow();
        });

        bool finished = await Task.WhenAny(raising, Task.Delay(TimeSpan.FromSeconds(15))) == raising;
        Assert.True(finished, "raising events blocked on a subscriber that is not reading");
        await raising;

        // And the stalled subscriber was let go rather than kept: after what it
        // had buffered, its stream ends.
        var drain = Task.Run(async () =>
        {
            while (await stalled.Reader.ReadLineAsync(_cts.Token) is not null) { }
        });
        Assert.True(await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(15))) == drain,
            "a subscriber that fell a whole queue behind was never disconnected");
    }

    /// <summary>
    /// Many subscribers coming and going, some hanging up mid-stream, while
    /// events are raised from two threads. A subscriber that keeps reading gets
    /// every event, in order, and nothing throws.
    /// </summary>
    [Fact]
    public async Task Subscribers_churning_under_load_do_not_cost_a_reading_subscriber_an_event()
    {
        const int Events = 4_000;
        using var loyal = new Client();
        await loyal.SubscribeAsync(_cts.Token);

        int received = 0;
        var reading = Task.Run(async () =>
        {
            while (received < Events && await loyal.Reader.ReadLineAsync(_cts.Token) is { } line)
            {
                var e = Protocol.TryDecode<SessionEvent>(line);
                Assert.True(e?.Kind == SessionEventKind.WindowRequested, $"event {received}: {line}");
                received++;
            }
        });

        using var stop = new CancellationTokenSource();
        var churn = Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
        {
            var rng = new Random(n);
            while (!stop.IsCancellationRequested)
            {
                using var c = new Client();
                await c.SubscribeAsync(_cts.Token);
                // Bounded, because once the raisers finish no more lines come.
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                patience.CancelAfter(TimeSpan.FromMilliseconds(200));
                try
                {
                    for (int k = rng.Next(0, 20); k > 0; k--)
                        if (await c.Reader.ReadLineAsync(patience.Token) is null) break;
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { }
                // Hang up without a word, mid-stream.
            }
        })).ToArray();

        var raisers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < Events / 2; i++)
            {
                _server.RaiseWindow();
                // A burst of 8 per millisecond per thread: hundreds of times the
                // real rate (a few word boundaries a second), and still a pace a
                // reading subscriber keeps up with. Unpaced, two threads raise
                // faster than any socket drains and the loyal reader falls a
                // whole queue behind, which is the drop working, not a bug.
                if (i % 8 == 0) Thread.Sleep(1);
            }
        })).ToArray();

        await Task.WhenAll(raisers).WaitAsync(TimeSpan.FromSeconds(30));
        await reading.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        await Task.WhenAll(churn).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Events, received);
    }
}

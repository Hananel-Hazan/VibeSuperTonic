using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// An audio device that does exactly what the test tells it to.
///
/// The point of <see cref="IAudioSink"/> existing at all: the session's hardest
/// behaviour is what happens when a stop arrives while the writer is blocked
/// inside the device, and on real hardware that is a race you can only provoke
/// by getting lucky. Here it is a parameter.
///
/// Latency is modelled as "the device is exactly <see cref="BufferFrames"/>
/// behind, once that much has been written", which is the steady state a
/// blocking write settles into and is deterministic to the frame.
/// </summary>
public sealed class FakeSink : IAudioSink
{
    private long _written;
    private volatile bool _flushRequested;

    public FakeSink(int sampleRate = 44100) => SampleRate = sampleRate;

    public int SampleRate { get; }
    public long WrittenFrames => _written;

    /// <summary>How far behind the device pretends to be.</summary>
    public long BufferFrames { get; set; } = 44100 / 10;   // 100 ms

    /// <summary>
    /// Once this many frames have been written, <see cref="Write"/> blocks until
    /// a flush is requested — the exact situation a stop has to interrupt.
    /// </summary>
    public long? BlockAfterFrames { get; set; }

    /// <summary>Set when <see cref="Write"/> has actually blocked.</summary>
    public ManualResetEventSlim Blocked { get; } = new(false);

    /// <summary>
    /// Milliseconds each write takes. Zero makes an utterance complete faster
    /// than a test can observe it, which is fine for end-state assertions and
    /// useless for anything about the middle — pause, or a press during speech.
    /// A few milliseconds per block stands in for a device consuming audio in
    /// real time.
    /// </summary>
    public int WriteDelayMs { get; set; }

    public int FlushCount { get; private set; }
    public int DrainCount { get; private set; }
    public int WriteCount { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>
    /// Pull the device out from under the writer, the way an audio-server restart
    /// does. Every subsequent <see cref="Write"/> and <see cref="Drain"/> throws
    /// <see cref="AudioDeviceLostException"/> and <see cref="IsAlive"/> goes
    /// false — which is precisely what pa_simple does once its connection is
    /// terminated.
    /// </summary>
    public void LoseDevice() => IsAlive = false;

    /// <inheritdoc/>
    public bool IsAlive { get; private set; } = true;

    /// <summary>
    /// Fail the next <see cref="Write"/> with something that is NOT device loss,
    /// so tests can check that ordinary failures do not trigger a reconnect.
    /// </summary>
    public Exception? ThrowOnWrite { get; set; }

    public long LatencyUsec =>
        _written == 0 ? 0 : Math.Min(_written, BufferFrames) * 1_000_000L / SampleRate;

    public void Write(ReadOnlySpan<short> pcm)
    {
        if (!IsAlive) throw new AudioDeviceLostException("fake write failed: Connection terminated");
        if (ThrowOnWrite is { } boom) throw boom;

        if (_flushRequested) { Flush(); throw new OperationCanceledException("flushed"); }

        if (BlockAfterFrames is long n && _written >= n)
        {
            Blocked.Set();
            while (!_flushRequested) Thread.Sleep(1);
            Flush();
            throw new OperationCanceledException("flushed");
        }

        if (WriteDelayMs > 0) Thread.Sleep(WriteDelayMs);

        _written += pcm.Length;
        WriteCount++;
    }

    /// <summary>
    /// Held closed by a test that needs <c>Stop()</c>'s flush request to arrive
    /// LATE — after the worker it was meant for has finished unwinding. That
    /// interleaving is a few instructions wide in production and is the whole
    /// mechanism behind an utterance that dies silently, so it is driven rather
    /// than waited for.
    /// </summary>
    public ManualResetEventSlim? DelayFlushRequest { get; set; }

    public void RequestFlush()
    {
        DelayFlushRequest?.Wait();
        _flushRequested = true;
    }

    public void Flush()
    {
        _flushRequested = false;
        _written = 0;
        FlushCount++;
    }

    public void Drain()
    {
        if (!IsAlive) throw new AudioDeviceLostException("fake drain failed: Connection terminated");
        DrainCount++;
    }

    public void Dispose()
    {
        Disposed = true;
        Blocked.Dispose();
    }
}

/// <summary>
/// A synthesizer that renders silence of a known length, instantly, and can be
/// made to stall or fail on demand.
///
/// Known length is what makes boundary assertions exact: with one second of
/// audio per chunk, a word 40% of the way through the characters is at frame
/// 17640 and nothing has to be approximate.
/// </summary>
public sealed class FakeSynthesizer : ISynthesizer
{
    private readonly object _gate = new();

    public FakeSynthesizer(int sampleRate = 44100) => SampleRate = sampleRate;

    public int SampleRate { get; }

    /// <summary>Audio produced per chunk. One second by default.</summary>
    public int FramesPerChunk { get; set; } = 44100;

    /// <summary>Every chunk this was asked to render, in order.</summary>
    public List<string> Rendered { get; } = new();

    /// <summary>Thrown from the first <see cref="Synthesize"/> call, if set.</summary>
    public Exception? ThrowOnSynthesize { get; set; }

    /// <summary>Blocks in <see cref="Synthesize"/> until cancelled, if set — a cold load.</summary>
    public bool StallUntilCancelled { get; set; }

    public int PreloadCount { get; private set; }
    public bool Disposed { get; private set; }

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) Rendered.Add(text);

        if (ThrowOnSynthesize is { } ex) throw ex;

        if (StallUntilCancelled)
        {
            // A real backend translates ORT's terminate-flag exception into
            // OperationCanceledException at the seam; this stands in for that.
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new short[FramesPerChunk];
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        PreloadCount++;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

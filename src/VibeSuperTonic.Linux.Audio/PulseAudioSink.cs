using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VibeSuperTonic.Core.Audio;

namespace VibeSuperTonic.Linux.Audio;

/// <summary>
/// PCM to the speakers, via PulseAudio (or pipewire-pulse, which presents the
/// same API and is what a current Mint desktop actually runs).
///
/// Opened as mono 16-bit at the model's native rate, so nothing in the chain
/// resamples: the vocoder's output goes to the device in the format it was
/// produced in.
///
/// <para><b>Threading.</b> <c>pa_simple</c> is not thread-safe, so every member
/// here except <see cref="RequestFlush"/> must be called from a single writer
/// thread. That constraint is the reason <see cref="RequestFlush"/> exists at
/// all: stop arrives on another thread, and calling <c>pa_simple_flush</c>
/// directly from it while the writer sits inside <c>pa_simple_write</c> is a
/// data race in native code — which presents as the daemon vanishing, not as an
/// exception. Instead the flag is set, and the writer performs the flush itself
/// between blocks.</para>
///
/// <para>That makes stop latency bounded by one block plus whatever
/// <c>pa_simple_write</c> is blocked on, which is why <see cref="Write"/> feeds
/// the device in small pieces rather than handing over a whole chunk: a
/// sentence of audio is seconds long, and a stop that waited for it would be no
/// stop at all.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PulseAudioSink : IAudioSink
{
    /// <summary>
    /// Target buffer fill. The stop-latency knob: audio already inside libpulse
    /// when a flush arrives is dropped, but audio the kernel has taken is gone
    /// regardless, so this bounds how much speech can outlive a stop. Small
    /// enough to keep stop tight, large enough that a scheduling hiccup on a
    /// loaded desktop does not underrun.
    /// </summary>
    public const int DefaultTargetBufferMs = 150;

    /// <summary>
    /// How much audio goes to the device per <c>pa_simple_write</c> call. Sets
    /// the granularity of <see cref="WrittenFrames"/>, and therefore how
    /// finely the playback clock can move; also bounds how long a stop waits.
    /// </summary>
    public const int WriteBlockMs = 20;

    private readonly int _blockFrames;
    private IntPtr _handle;
    private long _written;
    private volatile bool _flushRequested;

    /// <param name="sampleRate">Frames per second; 44100 for this model.</param>
    /// <param name="streamName">What shows up in a volume mixer next to the level slider.</param>
    /// <param name="targetBufferMs">Override for <see cref="DefaultTargetBufferMs"/>.</param>
    /// <exception cref="PlatformNotSupportedException">libpulse is not installed.</exception>
    /// <exception cref="InvalidOperationException">The stream could not be opened.</exception>
    public unsafe PulseAudioSink(
        int sampleRate = 44100,
        string streamName = "VibeSuperTonic",
        int targetBufferMs = DefaultTargetBufferMs)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (targetBufferMs <= 0) throw new ArgumentOutOfRangeException(nameof(targetBufferMs));

        // Probe before the first P/Invoke so a missing package produces one
        // actionable sentence instead of a DllNotFoundException naming a file
        // the user has never heard of.
        if (!NativeLibrary.TryLoad(PulseNative.LibSimple, out _))
        {
            throw new PlatformNotSupportedException(
                $"{PulseNative.LibSimple} could not be loaded. It ships in the 'libpulse0' " +
                "package, which is present by default on Mint and on any system running " +
                "pipewire-pulse. Install it with: sudo apt install libpulse0");
        }

        SampleRate = sampleRate;
        _blockFrames = Math.Max(1, sampleRate * WriteBlockMs / 1000);

        var spec = new PulseNative.SampleSpec
        {
            Format = PulseNative.PA_SAMPLE_S16LE,
            Rate = (uint)sampleRate,
            Channels = 1,
        };

        // Only TLength is set; the rest take libpulse's defaults, which are
        // derived from it. Setting PreBuf explicitly is tempting and wrong —
        // the default ("start when TLength is buffered") is what the playback
        // clock's priming window is sized against.
        var attr = new PulseNative.BufferAttr
        {
            MaxLength = PulseNative.Default,
            TLength = (uint)(sampleRate * targetBufferMs / 1000 * sizeof(short)),
            PreBuf = PulseNative.Default,
            MinReq = PulseNative.Default,
            FragSize = PulseNative.Default,
        };

        int error = 0;
        _handle = PulseNative.pa_simple_new(
            server: null,
            name: "VibeSuperTonic",
            dir: PulseNative.PA_STREAM_PLAYBACK,
            dev: null,
            streamName: streamName,
            ss: &spec,
            channelMap: IntPtr.Zero,
            attr: &attr,
            error: &error);

        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"could not open a PulseAudio playback stream: {PulseNative.Describe(error)}");
    }

    public int SampleRate { get; }

    public long WrittenFrames => _written;

    /// <inheritdoc/>
    public unsafe long LatencyUsec
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;

            int error = 0;
            ulong usec = PulseNative.pa_simple_get_latency(_handle, &error);

            // (pa_usec_t)-1 is the documented failure return. Reporting zero
            // rather than throwing is deliberate: a failed latency read is a
            // missing sample, and PlaybackClock is built to ignore those. A
            // throw here would take down a speak over a poll that a later poll
            // would have answered.
            if (error != 0 || usec == ulong.MaxValue) return 0;

            return usec > long.MaxValue ? 0 : (long)usec;
        }
    }

    /// <summary>
    /// Ask the writer thread to drop buffered audio at its next opportunity.
    /// The one member safe to call from another thread — see the class remarks
    /// for why the flush cannot simply happen here.
    /// </summary>
    public void RequestFlush() => _flushRequested = true;

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">
    /// A flush was requested part-way through. The samples not yet written are
    /// dropped along with everything already buffered, which is what stop means.
    /// </exception>
    public unsafe void Write(ReadOnlySpan<short> pcm)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

        int offset = 0;
        while (offset < pcm.Length)
        {
            if (_flushRequested)
            {
                Flush();
                throw new OperationCanceledException("playback flushed");
            }

            int count = Math.Min(_blockFrames, pcm.Length - offset);
            int error = 0;
            int rc;

            fixed (short* p = pcm.Slice(offset, count))
            {
                rc = PulseNative.pa_simple_write(_handle, p, (nuint)(count * sizeof(short)), &error);
            }

            if (rc < 0)
                throw new InvalidOperationException(
                    $"pa_simple_write failed: {PulseNative.Describe(error)}");

            // Counted only after the write is accepted. A partial or failed
            // write that still advanced this would make the clock believe the
            // user heard audio the device never received, and the highlight
            // would run permanently ahead of the speech.
            offset += count;
            _written += count;
        }
    }

    /// <inheritdoc/>
    public unsafe void Flush()
    {
        _flushRequested = false;
        if (_handle == IntPtr.Zero) return;

        int error = 0;
        if (PulseNative.pa_simple_flush(_handle, &error) < 0)
        {
            // Not fatal: the stream stays usable and the audio drains on its
            // own. Worth knowing about, but not worth failing a stop over —
            // stop's job is to be reliable above all else.
            FlushFailed = PulseNative.Describe(error);
        }

        _written = 0;
    }

    /// <summary>
    /// Why the last <see cref="Flush"/> did not take effect, or null if it did.
    /// Read by the daemon's log rather than thrown — see <see cref="Flush"/>.
    /// </summary>
    public string? FlushFailed { get; private set; }

    /// <inheritdoc/>
    public unsafe void Drain()
    {
        if (_handle == IntPtr.Zero) return;

        int error = 0;
        if (PulseNative.pa_simple_drain(_handle, &error) < 0)
            throw new InvalidOperationException(
                $"pa_simple_drain failed: {PulseNative.Describe(error)}");
    }

    public void Dispose()
    {
        IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) PulseNative.pa_simple_free(h);
    }
}

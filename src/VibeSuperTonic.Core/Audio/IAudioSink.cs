namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// Somewhere to put PCM. The one seam between Core and the platform's audio
/// stack, and the counterpart to <c>ISynthesizer</c> on the other end of the
/// pipeline.
///
/// It exists for the same reason <c>ISynthesizer</c> does — Core cannot
/// reference libpulse any more than it can reference ONNX Runtime — and for one
/// more: the playback clock is the hardest thing in Phase 2 to get right and the
/// easiest to get wrong invisibly, so it has to be drivable by a fake. A test
/// implementing this interface can report any latency curve it likes, including
/// the pathological ones a real sink only produces on someone else's machine.
///
/// Format is fixed at mono 16-bit at the model's native rate. Nothing in this
/// product resamples, and an interface that permitted formats would invite
/// something to start.
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>Frames per second the sink was opened with.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Frames accepted since the sink was opened or last flushed. This is the
    /// "written" half of the clock's subtraction; the sink owns it because a
    /// partial write must not be counted.
    /// </summary>
    long WrittenFrames { get; }

    /// <summary>
    /// Hand samples to the device, blocking until they are accepted (which is
    /// the backpressure that stops the renderer running arbitrarily far ahead).
    /// </summary>
    void Write(ReadOnlySpan<short> pcm);

    /// <summary>
    /// How much of what has been written is still buffered, in microseconds.
    /// Zero or negative means "no usable reading" — it is the normal answer
    /// before the stream primes, not an error. See <see cref="PlaybackClock"/>.
    /// </summary>
    long LatencyUsec { get; }

    /// <summary>
    /// Discard buffered audio immediately. One of the three things stop has to
    /// do, and the one that decides whether stop <em>sounds</em> instant:
    /// without it the user keeps hearing up to a full buffer of speech they have
    /// already asked to end. Resets <see cref="WrittenFrames"/>, because frames
    /// thrown away were never heard and must not be counted as played.
    ///
    /// <para>Call from the writer thread only — see <see cref="RequestFlush"/>.</para>
    /// </summary>
    void Flush();

    /// <summary>
    /// Ask the writer thread to flush at its next opportunity. <b>The only
    /// member safe to call from another thread</b>, and the reason it exists:
    /// stop arrives on the hotkey's thread while the writer is blocked inside
    /// the platform's write call, and most audio APIs — <c>pa_simple</c>
    /// included — are not thread-safe. Flushing from the stopping thread is a
    /// data race in native code, which presents as the daemon vanishing rather
    /// than as an exception.
    ///
    /// <para>The writer observes the request between blocks, performs the flush
    /// itself, and abandons the rest of the buffer by throwing
    /// <see cref="OperationCanceledException"/> out of <see cref="Write"/>. Stop
    /// latency is therefore bounded by one write block, which is why
    /// implementations must write in small pieces rather than whole
    /// utterances.</para>
    /// </summary>
    void RequestFlush();

    /// <summary>
    /// Block until everything written has actually played. The one moment the
    /// playback position is known exactly rather than inferred — see
    /// <see cref="PlaybackClock.Drained"/>.
    /// </summary>
    void Drain();
}

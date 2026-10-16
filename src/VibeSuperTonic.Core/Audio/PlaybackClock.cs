namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// Answers "how much of what I have written has the user actually <em>heard</em>?"
///
/// On Windows this question does not exist: the engine hands SAPI a stream
/// offset per event and SAPI's own audio object fires it at the right moment.
/// Off Windows nothing does that for us, so the Linux daemon owns the clock —
/// and the follow-along highlight is only ever as good as this class is.
///
/// The arithmetic is one line: everything written to the sink, minus everything
/// still sitting in the sink's buffer. Both are in frames (mono 16-bit, so frame
/// == sample) at <see cref="SampleRate"/>; the sink reports its buffer depth in
/// microseconds, which is the only unit conversion in the chain.
///
/// Two properties do the real work, and both exist because of specific ways this
/// goes wrong:
///
/// <list type="bullet">
/// <item><b>Priming suppression (R-7).</b> Reported latency is zero or
/// nonsensical until the stream fills, so a clock that answered immediately
/// would place the highlight at the wrong word for the first few hundred
/// milliseconds — the single most visible moment in the product. The clock
/// refuses to answer at all until it has both written enough audio and seen a
/// plausible reading, then jumps straight to the true position.</item>
/// <item><b>Monotonicity.</b> Latency readings jitter. A reading that jitters
/// upward implies the user un-heard something, which would drag the highlight
/// backwards over text they have already been shown. The clock never
/// decreases; it simply declines to move.</item>
/// </list>
///
/// Deliberately holds no wall-clock and starts no timer. It is driven entirely
/// by what the caller reports, which is what makes it testable to the frame on a
/// machine with no audio device — including Windows CI, where nothing else in
/// Phase 2 can run.
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>
    /// Audio that must be written before the clock will answer. Below roughly
    /// this much, PulseAudio has not begun playback and its latency reading
    /// describes an empty buffer rather than a playing stream.
    /// </summary>
    public const int DefaultPrimeMs = 200;

    /// <summary>
    /// Latency readings above this are rejected as sensor noise rather than
    /// believed. A sink genuinely four seconds behind is broken; a sink that
    /// reports it for one poll is common, and believing it once would freeze the
    /// highlight for four seconds (the monotonic guard means it cannot be undone
    /// by the next good reading).
    /// </summary>
    public const int MaxPlausibleLatencyMs = 4000;

    private readonly long _primeFrames;

    private long _written;
    private long _played;
    private bool _primed;

    /// <param name="sampleRate">Frames per second. 44100 everywhere in this product.</param>
    /// <param name="primeMs">Override for <see cref="DefaultPrimeMs"/>; tests use small values.</param>
    public PlaybackClock(int sampleRate, int primeMs = DefaultPrimeMs)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (primeMs < 0) throw new ArgumentOutOfRangeException(nameof(primeMs));

        SampleRate = sampleRate;
        _primeFrames = (long)sampleRate * primeMs / 1000L;
    }

    public int SampleRate { get; }

    /// <summary>Total frames handed to the sink since the last <see cref="Reset"/>.</summary>
    public long WrittenFrames => _written;

    /// <summary>
    /// Frames the user has heard. Zero and meaningless until
    /// <see cref="IsPrimed"/>; consumers should gate on the return of
    /// <see cref="Update"/> rather than read this speculatively.
    /// </summary>
    public long PlayedFrames => _played;

    /// <summary>
    /// True once a plausible latency reading has been seen on a sufficiently
    /// filled stream. Latches — a stream that primes and then reports nonsense
    /// has a bad reading, not an unprimed stream, and re-arming the suppression
    /// mid-utterance would stall the highlight rather than protect it.
    /// </summary>
    public bool IsPrimed => _primed;

    /// <summary>Report frames successfully handed to the sink.</summary>
    public void AddWritten(long frames)
    {
        if (frames < 0) throw new ArgumentOutOfRangeException(nameof(frames));
        _written += frames;
    }

    /// <summary>
    /// Feed a sink latency reading, in microseconds, and recompute the position.
    /// </summary>
    /// <returns>
    /// True when the clock is primed and <see cref="PlayedFrames"/> is meaningful
    /// — <em>not</em> whether it moved. False means "no answer yet"; the caller
    /// must not fire boundary events on a false.
    /// </returns>
    public bool Update(long latencyUsec)
    {
        // Non-positive is how both "not started" and "read failed" present. It is
        // also, briefly, what a fully drained stream reports — but a drained
        // stream is Drained()'s business, and treating a spurious zero as "all
        // played" would throw the highlight to the end of the paragraph with no
        // way back past the monotonic guard.
        if (latencyUsec <= 0) return _primed;

        long latencyFrames = latencyUsec * SampleRate / 1_000_000L;

        if (latencyUsec > (long)MaxPlausibleLatencyMs * 1000L) return _primed;

        if (!_primed)
        {
            if (_written < _primeFrames) return false;

            // More buffered than was ever written cannot be true of this stream.
            // It shows up when the reading is picked up before the stream is
            // properly connected.
            if (latencyFrames > _written) return false;

            _primed = true;
        }

        long candidate = _written - latencyFrames;
        if (candidate < 0) candidate = 0;
        if (candidate > _written) candidate = _written;

        // Never backwards: see the class doc. Declining to move is correct; the
        // next reading will carry the position forward.
        if (candidate > _played) _played = candidate;

        return true;
    }

    /// <summary>
    /// The sink has drained: everything written has been heard. Called after
    /// <c>pa_simple_drain</c> returns, which is the one moment the position is
    /// known exactly rather than inferred.
    /// </summary>
    public void Drained()
    {
        _primed = true;
        _played = _written;
    }

    /// <summary>
    /// Back to the start of a new utterance. Used on stop as well as on
    /// completion: a flush discards buffered audio, so frames that were written
    /// but never heard would otherwise be counted as played by the next
    /// utterance's arithmetic.
    /// </summary>
    public void Reset()
    {
        _written = 0;
        _played = 0;
        _primed = false;
    }
}

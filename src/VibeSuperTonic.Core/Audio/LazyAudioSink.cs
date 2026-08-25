namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// An <see cref="IAudioSink"/> that does not touch the audio device until
/// something actually needs to play.
///
/// <para><b>Why this exists.</b> The daemon opened its sink in <c>Main</c>, so a
/// machine whose audio server was unreachable got an unhandled exception, a core
/// dump and exit 134 — before the control socket existed. Every verb was then
/// unavailable, including the ones that would have explained the problem, and
/// <c>vst-ctl</c>'s autostart (R-5) turned that into a hotkey that silently does
/// nothing: the client starts a daemon, the daemon dies, and the client waits out
/// its full budget before printing a message to a terminal nobody pressing a key
/// is looking at.</para>
///
/// <para>That is the failure shape Phase 7 already fixed for a missing model set,
/// and the generalisation it drew from it: <em>anything the daemon refuses to
/// start for is a hotkey that silently does nothing</em>, and the set of such
/// conditions should stay at "the socket is already held by another daemon" —
/// the one case where continuing is worse. An absent audio device is not that
/// case. The daemon should start, answer <c>status</c>, <c>config</c> and
/// <c>subscribe</c>, and fail <c>speak</c> with a sentence naming the cause.</para>
///
/// <para><b>And it reopens a device that went away — added after the field found
/// the gap.</b> This class used to say that once the device opened it stayed
/// open for the life of the process, "exactly as before". That was inherited
/// from the Windows engine, where it is true and harmless: a SAPI engine lives
/// as long as its host, which is one utterance to a few minutes. This daemon
/// holds its stream for the whole login session, and on a PipeWire desktop the
/// audio server restarts routinely — so the assumption expires, quietly, and
/// takes the product with it.</para>
///
/// <para>Measured in the field 2026-08-16: an audio-server restart forty minutes
/// after the daemon started left every subsequent press doing exactly nothing for
/// five hours. <c>status</c> answered, <c>config</c> answered, <c>speak</c> was
/// accepted — and the write failed on a worker thread whose only report was an
/// <c>Error</c> event on a stream nothing subscribes to yet. This is the R-5
/// failure shape arriving through a door nobody had watched: not a daemon that
/// refuses to start, but one that starts, accepts, and silently cannot.</para>
///
/// <para><b>Recovery is between utterances, never inside one.</b> See
/// <see cref="Write"/>.</para>
/// </summary>
public sealed class LazyAudioSink : IAudioSink
{
    private readonly Func<int, IAudioSink> _open;
    private readonly object _gate = new();

    private IAudioSink? _inner;
    private bool _disposed;
    private int _reconnects;

    /// <param name="sampleRate">
    /// What the sink will first be opened with — see <see cref="Retune"/> for
    /// what changes it. Declared up front because
    /// <see cref="SpeechSession"/> builds its playback clock from
    /// <see cref="SampleRate"/> before the first write, and a clock that had to
    /// wait for the device would be a device that had to open before the clock —
    /// which is the ordering this class exists to break. Checked against the real
    /// sink on open.
    /// </param>
    /// <param name="open">
    /// Creates the real sink at the rate it is given. Called at most once per
    /// successful open, under a lock, and allowed to throw —
    /// <see cref="TryOpen"/> turns that into a message. It takes the rate rather
    /// than closing over one so that <see cref="Retune"/> has something to
    /// reopen with.
    /// </param>
    public LazyAudioSink(int sampleRate, Func<int, IAudioSink> open)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        SampleRate = sampleRate;
    }

    /// <inheritdoc/>
    public int SampleRate { get; private set; }

    /// <summary>
    /// Open at a different rate from now on, because the engine speaking changed.
    ///
    /// <para><b>Between utterances, never inside one.</b> The third case of the
    /// rule that already governs the device-loss reconnect and the provider
    /// switch, and it is the same rule for the same reason: a fresh stream has
    /// written nothing while <see cref="PlaybackClock"/> and every scheduled
    /// boundary are counted against the old one — and here the rate itself is
    /// what the clock converts frames with, so a change mid-utterance would
    /// desynchronise the highlight permanently AND play the remainder at the
    /// wrong speed. Callers enforce the idle window; this class cannot see it.
    /// </para>
    ///
    /// <para>Why a Piper voice needs it at all: those voices render at 22050 or
    /// 16000 and <b>nothing in this product resamples</b> — that is the
    /// native-rate decision, and it buys the CPU a resampler would cost on every
    /// utterance. So the device follows the model rather than the model
    /// following the device.</para>
    ///
    /// <para>The device is dropped rather than reopened here: the next
    /// <see cref="TryOpen"/> builds it, which is the one place a failure has
    /// somewhere to be reported.</para>
    /// </summary>
    /// <returns>True if the rate actually changed.</returns>
    public bool Retune(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sampleRate == SampleRate) return false;

            // Not counted as a reconnect and not recorded as a loss: nothing
            // went wrong, and a field report's "this daemon has already survived
            // something" reading of Reconnects has to stay true.
            if (_inner is not null)
            {
                try { _inner.Dispose(); } catch { /* replacing it either way */ }
                _inner = null;
            }

            SampleRate = sampleRate;
            return true;
        }
    }

    /// <summary>True once the device has actually been opened.</summary>
    public bool IsOpen { get { lock (_gate) return _inner is not null; } }

    /// <summary>
    /// How many times the device has been reopened after being lost. Zero on a
    /// machine whose audio server has not restarted, which is what makes it worth
    /// reporting: a non-zero count in a field report says the daemon has already
    /// survived something, and turns "it stopped working once" into a fact.
    /// </summary>
    public int Reconnects { get { lock (_gate) return _reconnects; } }

    /// <summary>
    /// Why the device was last lost, or null if it never has been. Kept after a
    /// successful reopen — the interesting question is what happened, not whether
    /// it is happening right now.
    /// </summary>
    public string? LastLoss { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers for the inner sink, and answers <c>true</c> before anything is
    /// open: nothing has been lost yet, and the caller's next step is
    /// <see cref="TryOpen"/> either way.
    /// </remarks>
    public bool IsAlive
    {
        get { lock (_gate) return _inner is null || _inner.IsAlive; }
    }

    /// <summary>
    /// Open the device if it is not open already, reporting why not rather than
    /// throwing.
    ///
    /// <para>Called from the request path, so the answer reaches the person who
    /// pressed the key instead of only the daemon's stderr. Checked per request
    /// rather than once, because an audio server that was down when the daemon
    /// started may well be up by the time anyone speaks — the same reasoning that
    /// makes the model-directory check per-request.</para>
    ///
    /// <para><b>A cached sink is verified, not assumed.</b> Returning true because
    /// something was opened once is what let a dead stream survive for five hours;
    /// the device is asked whether it is still there, and a sink that says no is
    /// dropped here and reopened before the caller ever writes to it. That is what
    /// makes the <em>first</em> press after an audio-server restart work rather
    /// than the second.</para>
    /// </summary>
    /// <returns>False if the device could not be opened; <paramref name="error"/> says why.</returns>
    public bool TryOpen(out string? error)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_inner is not null)
            {
                if (_inner.IsAlive) { error = null; return true; }
                DropLocked("the audio device went away");
            }

            try
            {
                var sink = _open(SampleRate);

                // A sink that opened at a rate other than the one the clock was
                // told about would desynchronise every boundary by the ratio,
                // silently -- and a wrong word-boundary offset sounds exactly
                // like a right one, so nothing downstream would catch it.
                if (sink.SampleRate != SampleRate)
                {
                    sink.Dispose();
                    error = $"audio device opened at {sink.SampleRate} Hz, expected {SampleRate} Hz";
                    return false;
                }

                _inner = sink;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Let go of a sink that is gone, so the next <see cref="TryOpen"/> builds a
    /// new one. Call under <see cref="_gate"/>.
    /// </summary>
    /// <remarks>
    /// Dispose is inside the try because it goes through the same dead handle
    /// that just failed. <c>pa_simple_free</c> on a terminated stream is fine,
    /// but "the cleanup of a failure throws and replaces the failure" is a
    /// mechanism worth denying outright rather than reasoning about per
    /// implementation.
    /// </remarks>
    private void DropLocked(string reason)
    {
        if (_inner is null) return;

        try { _inner.Dispose(); } catch { /* it is already gone; that is the point */ }
        _inner = null;
        _reconnects++;
        LastLoss = reason;
    }

    private IAudioSink Opened()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inner is not null) return _inner;
        }

        // Reached only if something writes without asking first. Not silently
        // opened here: the request path is where a failure can be reported to a
        // caller, and an open that happens on the audio thread instead would
        // surface as a mid-utterance Error event with no request to blame.
        if (!TryOpen(out string? error))
            throw new InvalidOperationException(error ?? "no audio device");

        lock (_gate) return _inner!;
    }

    /// <inheritdoc/>
    public long WrittenFrames { get { lock (_gate) return _inner?.WrittenFrames ?? 0; } }

    /// <inheritdoc/>
    public long LatencyUsec { get { lock (_gate) return _inner?.LatencyUsec ?? 0; } }

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>A device lost mid-utterance ends that utterance</b>, and the next
    /// one reconnects. Reconnecting here instead is tempting and wrong: a fresh
    /// stream starts at zero written frames with no buffered audio, while
    /// <see cref="PlaybackClock"/> and every boundary already scheduled are
    /// counted against the old one. The speech would resume and the highlight
    /// would be permanently wrong — which sounds perfect, and is the failure
    /// class every offset defect in this repository belongs to.</para>
    ///
    /// <para>So the exception propagates, the session reports <c>Error</c> and
    /// returns to Idle, and the sink is dropped on the way out. The user presses
    /// again and it works. One lost utterance at the moment the audio server
    /// restarted is a fair price and an honest one.</para>
    /// </remarks>
    public void Write(ReadOnlySpan<short> pcm)
    {
        var sink = Opened();
        try
        {
            sink.Write(pcm);
        }
        catch (AudioDeviceLostException ex)
        {
            lock (_gate) { if (ReferenceEquals(_inner, sink)) DropLocked(ex.Message); }
            throw;
        }
    }

    /// <inheritdoc/>
    public void Flush() { lock (_gate) _inner?.Flush(); }

    /// <inheritdoc/>
    public void RequestFlush() { lock (_gate) _inner?.RequestFlush(); }

    /// <inheritdoc/>
    public void Drain()
    {
        IAudioSink? sink;
        lock (_gate) sink = _inner;
        if (sink is null) return;

        try
        {
            sink.Drain();
        }
        catch (AudioDeviceLostException ex)
        {
            // Drain is the last thing an utterance does, so losing the device
            // here costs only the tail. It still has to be dropped, or the next
            // utterance opens the request path against a sink that is already
            // known to be gone.
            lock (_gate) { if (ReferenceEquals(_inner, sink)) DropLocked(ex.Message); }
            throw;
        }
    }

    public void Dispose()
    {
        IAudioSink? inner;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            inner = _inner;
            _inner = null;
        }

        // Guarded, because shutdown and the audio server going away are the same
        // event more often than not: a logout tears down pipewire and every
        // client at once, so closing a stream whose server has already gone is
        // the ORDINARY path out, not an exotic one. An exception here escapes
        // Main's using-declarations after everything else has been torn down,
        // which this codebase has already paid for twice — exit 134 and a core
        // file, read by journald as a crash and by a restart policy as a reason
        // to act.
        try { inner?.Dispose(); } catch { /* it is going away; that was the goal */ }
    }
}

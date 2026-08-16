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
/// <para>Deferring rather than retrying-forever is deliberate. Once the device
/// opens it stays open for the life of the process, exactly as before — this
/// changes <em>when</em> the sink is created and nothing about how it behaves
/// afterwards.</para>
/// </summary>
public sealed class LazyAudioSink : IAudioSink
{
    private readonly Func<IAudioSink> _open;
    private readonly object _gate = new();

    private IAudioSink? _inner;
    private bool _disposed;

    /// <param name="sampleRate">
    /// What the sink will be opened with. Declared up front because
    /// <see cref="SpeechSession"/> builds its playback clock from
    /// <see cref="SampleRate"/> before the first write, and a clock that had to
    /// wait for the device would be a device that had to open before the clock —
    /// which is the ordering this class exists to break. Checked against the real
    /// sink on open.
    /// </param>
    /// <param name="open">
    /// Creates the real sink. Called at most once per successful open, under a
    /// lock, and allowed to throw — <see cref="TryOpen"/> turns that into a
    /// message.
    /// </param>
    public LazyAudioSink(int sampleRate, Func<IAudioSink> open)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        SampleRate = sampleRate;
    }

    /// <inheritdoc/>
    public int SampleRate { get; }

    /// <summary>True once the device has actually been opened.</summary>
    public bool IsOpen { get { lock (_gate) return _inner is not null; } }

    /// <summary>
    /// Open the device if it is not open already, reporting why not rather than
    /// throwing.
    ///
    /// <para>Called from the request path, so the answer reaches the person who
    /// pressed the key instead of only the daemon's stderr. Checked per request
    /// rather than once, because an audio server that was down when the daemon
    /// started may well be up by the time anyone speaks — the same reasoning that
    /// makes the model-directory check per-request.</para>
    /// </summary>
    /// <returns>False if the device could not be opened; <paramref name="error"/> says why.</returns>
    public bool TryOpen(out string? error)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_inner is not null) { error = null; return true; }

            try
            {
                var sink = _open();

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
    public void Write(ReadOnlySpan<short> pcm) => Opened().Write(pcm);

    /// <inheritdoc/>
    public void Flush() { lock (_gate) _inner?.Flush(); }

    /// <inheritdoc/>
    public void RequestFlush() { lock (_gate) _inner?.RequestFlush(); }

    /// <inheritdoc/>
    public void Drain() { lock (_gate) _inner?.Drain(); }

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
        inner?.Dispose();
    }
}

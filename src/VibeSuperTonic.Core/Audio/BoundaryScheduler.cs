namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// Holds planned boundary events and releases each one when
/// <see cref="PlaybackClock"/> says its audio has been heard.
///
/// Events are queued as chunks render, which is well ahead of playback — the
/// pipeline renders forward so that audio never starves — so at any moment this
/// holds several seconds of future highlights. Playback then walks through them.
///
/// <para><b>Batches are the interesting case.</b> <see cref="Advance"/> returns
/// every event crossed since the last call, which is usually zero or one but is
/// deliberately allowed to be many. That happens twice, both legitimately: at
/// the priming fast-forward, where the clock's first answer already accounts for
/// a couple of hundred milliseconds of audio (R-7), and whenever a poll is late
/// — a busy desktop will skip a 20 ms tick.</para>
///
/// <para>A consumer painting a highlight should therefore use the <b>last</b>
/// element of a batch, not iterate it: replaying five words in one frame is a
/// visible flash that draws the eye to exactly the moment we were trying to make
/// invisible. A consumer logging or driving a caret history wants all of them.
/// Returning the batch rather than collapsing it here leaves that choice with
/// the caller, which is the one that knows.</para>
///
/// Out-of-order queueing throws rather than being sorted away. Frames arriving
/// backwards means a caller mixed up two coordinate spaces, and that class of
/// bug shipped in five consecutive releases precisely because every layer
/// quietly coped with it.
/// </summary>
public sealed class BoundaryScheduler
{
    private readonly List<BoundaryEvent> _queue = new();
    private readonly List<BoundaryEvent> _batch = new();
    private int _next;
    private long _lastQueuedFrame = -1;

    /// <summary>Events queued but not yet heard.</summary>
    public int PendingCount => _queue.Count - _next;

    /// <summary>
    /// The most recent event released by <see cref="Advance"/> — i.e. what the
    /// user is hearing now. Null before the first one.
    /// </summary>
    public BoundaryEvent? Current { get; private set; }

    /// <summary>Queue one event. Frames must not go backwards.</summary>
    public void Add(BoundaryEvent e)
    {
        if (e.Frame < _lastQueuedFrame)
            throw new ArgumentOutOfRangeException(nameof(e),
                $"boundary at frame {e.Frame} queued after frame {_lastQueuedFrame}; " +
                "events must be planned in stream order");

        _lastQueuedFrame = e.Frame;
        _queue.Add(e);
    }

    /// <summary>Queue a planned chunk's worth of events.</summary>
    public void AddRange(IEnumerable<BoundaryEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events) Add(e);
    }

    /// <summary>
    /// Release everything whose audio has now been heard.
    /// </summary>
    /// <param name="playedFrames">
    /// <see cref="PlaybackClock.PlayedFrames"/>, and only when
    /// <see cref="PlaybackClock.Update"/> returned true — calling this with an
    /// unprimed clock's zero is how the highlight lands on the first word before
    /// any audio has reached the speakers.
    /// </param>
    /// <returns>
    /// The crossed events in stream order. Valid until the next call: the list
    /// is reused, because this runs at ~50 Hz for the length of a reading
    /// session and returns nothing at all on most calls.
    /// </returns>
    public IReadOnlyList<BoundaryEvent> Advance(long playedFrames)
    {
        _batch.Clear();

        while (_next < _queue.Count && _queue[_next].Frame <= playedFrames)
        {
            var e = _queue[_next++];
            _batch.Add(e);
            Current = e;
        }

        return _batch;
    }

    /// <summary>
    /// Drop everything. Stop and end-of-utterance both land here: buffered audio
    /// is discarded on a flush, so its events must never fire, and the next
    /// utterance restarts stream frames at zero.
    /// </summary>
    public void Reset()
    {
        _queue.Clear();
        _batch.Clear();
        _next = 0;
        _lastQueuedFrame = -1;
        Current = null;
    }
}

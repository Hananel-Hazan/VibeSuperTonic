using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Coverage for the queue that turns "the user has heard N frames" into "the
/// user is hearing these characters".
///
/// The cases worth having are the ones where more than one event comes out at
/// once, because that is where a highlight either fast-forwards invisibly or
/// flashes through five words, and where a stop either drops pending events or
/// fires them into a stream that is no longer playing.
/// </summary>
public class BoundarySchedulerTests
{
    private static BoundaryEvent Word(long frame, int sourceOffset, int length = 4) =>
        new(frame, sourceOffset, length, BoundaryKind.Word);

    [Fact]
    public void Releases_nothing_until_its_audio_has_been_heard()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));
        s.Add(Word(2000, 5));

        Assert.Empty(s.Advance(999));
        Assert.Null(s.Current);
        Assert.Equal(2, s.PendingCount);
    }

    [Fact]
    public void Releases_each_event_once_in_stream_order()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));
        s.Add(Word(2000, 5));
        s.Add(Word(3000, 11));

        Assert.Equal(new[] { 0 }, s.Advance(1500).Select(e => e.SourceOffset));
        Assert.Equal(new[] { 5 }, s.Advance(2500).Select(e => e.SourceOffset));

        // Already-released events do not come back on a later poll.
        Assert.Empty(s.Advance(2600));
        Assert.Equal(new[] { 11 }, s.Advance(3000).Select(e => e.SourceOffset));
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public void Fires_exactly_on_the_frame_not_after_it()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));

        Assert.Empty(s.Advance(999));
        Assert.Single(s.Advance(1000));
    }

    /// <summary>
    /// The priming fast-forward (R-7). The clock's very first answer already
    /// accounts for a couple of hundred milliseconds, so several words are
    /// crossed at once. They must all be accounted for — a consumer painting a
    /// highlight uses the last, which is the whole reason the batch is returned
    /// rather than the events being delivered one per call.
    /// </summary>
    [Fact]
    public void Returns_the_whole_batch_when_the_clock_jumps()
    {
        var s = new BoundaryScheduler();
        for (int i = 0; i < 5; i++) s.Add(Word(i * 1000, i * 6));

        var batch = s.Advance(4000);

        Assert.Equal(5, batch.Count);
        Assert.Equal(24, batch[^1].SourceOffset);
        Assert.Equal(24, s.Current!.Value.SourceOffset);
    }

    [Fact]
    public void The_returned_batch_is_only_valid_until_the_next_call()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));
        s.Add(Word(2000, 5));

        var first = s.Advance(1000);
        Assert.Single(first);

        // Documented contract: the list is reused because this runs at ~50 Hz
        // for a whole reading session and is empty on almost every call. A
        // consumer that stashed the reference gets the next batch's contents,
        // so the test states it rather than leaving it to be discovered.
        s.Advance(2000);
        Assert.Equal(5, first[0].SourceOffset);
    }

    [Fact]
    public void Events_queued_after_playback_started_still_fire()
    {
        // Normal operation, not an edge case: chunks render ahead while earlier
        // audio plays, so the queue is appended to constantly.
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));
        Assert.Single(s.Advance(1000));

        s.Add(Word(2000, 5));
        Assert.Equal(new[] { 5 }, s.Advance(2000).Select(e => e.SourceOffset));
    }

    [Fact]
    public void Queueing_a_frame_that_goes_backwards_throws()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(2000, 5));

        // R-14's whole family is one coordinate space added to another. Every
        // layer quietly coping with the result is why it shipped five times, so
        // this layer does not cope.
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(Word(1000, 0)));
    }

    [Fact]
    public void Equal_frames_are_allowed()
    {
        // A chunk's sentence event and its first word event share a frame, and
        // a very short chunk can place two words on one frame.
        var s = new BoundaryScheduler();
        s.Add(new BoundaryEvent(500, 0, 20, BoundaryKind.Sentence));
        s.Add(Word(500, 0));

        Assert.Equal(2, s.Advance(500).Count);
    }

    /// <summary>
    /// The exact sequence the Windows TestHarness runs in step 11, pinned here
    /// so it can be checked on the machine that wrote it.
    ///
    /// This is not redundant with the tests above: the harness shares one
    /// scheduler across four consecutive calls, so each expected count depends
    /// on what the previous call consumed — which is its own thing to get wrong,
    /// and was, on the first run against a VM. The tests above each start clean
    /// and could not have caught it.
    /// </summary>
    [Fact]
    public void The_harness_step_11_sequence_holds()
    {
        var s = new BoundaryScheduler();
        for (int i = 1; i <= 5; i++) s.Add(Word(i * 1000, i * 6));

        Assert.Empty(s.Advance(999));
        Assert.Single(s.Advance(1000));
        Assert.Equal(3, s.Advance(4000).Count);     // 2000, 3000, 4000 together
        Assert.Empty(s.Advance(4000));
        Assert.Equal(1, s.PendingCount);            // 5000 is still ahead

        Assert.Throws<ArgumentOutOfRangeException>(() => s.Add(Word(0, 0)));

        s.Reset();
        Assert.Equal(0, s.PendingCount);
        Assert.Null(s.Current);
    }

    [Fact]
    public void Reset_drops_pending_events_so_a_stop_cannot_fire_them()
    {
        var s = new BoundaryScheduler();
        s.Add(Word(1000, 0));
        s.Add(Word(9000, 5));
        s.Advance(1000);

        s.Reset();

        Assert.Equal(0, s.PendingCount);
        Assert.Null(s.Current);

        // Flushed audio was never heard, so the next utterance restarts stream
        // frames at zero and the old queue's frames must not still be waiting.
        Assert.Empty(s.Advance(long.MaxValue));

        // And the order check restarts too — otherwise the next utterance's
        // frame 0 would look like a backwards jump from the old queue's 9000.
        s.Add(Word(0, 0));
        Assert.Single(s.Advance(0));
    }
}

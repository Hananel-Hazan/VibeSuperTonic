using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Coverage for the Phase 2 playback clock, and specifically for R-7 — the
/// highlight jumping to a wrong word at the start of every utterance because
/// PulseAudio's latency reading is meaningless until the stream primes.
///
/// Every case here is a latency curve a real sink produces. They are fed as
/// numbers rather than measured from a device on purpose: the clock is driven
/// entirely by what it is told, so the pathological readings that only show up
/// on someone else's machine — a zero mid-stream, a four-second spike, a
/// reading that jitters backwards — can be reproduced exactly, on any platform,
/// with no audio hardware at all. That includes the Windows CI job, where
/// nothing else in Phase 2 can run.
/// </summary>
public class PlaybackClockTests
{
    private const int Rate = 44100;

    /// <summary>Microseconds of audio in a given number of frames.</summary>
    private static long Usec(long frames) => frames * 1_000_000L / Rate;

    private static long Frames(int ms) => (long)Rate * ms / 1000L;

    // ------------------------------------------------------------ R-7: priming

    [Fact]
    public void Refuses_to_answer_before_enough_audio_is_written()
    {
        var clock = new PlaybackClock(Rate);

        // 100 ms written against a 200 ms prime window. The sink is perfectly
        // willing to report a latency here; it just isn't playing yet.
        clock.AddWritten(Frames(100));

        Assert.False(clock.Update(Usec(Frames(100))));
        Assert.False(clock.IsPrimed);
    }

    [Fact]
    public void Refuses_to_answer_while_latency_reads_zero()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(500));

        // Plenty written, but the stream has not connected. This is the exact
        // shape of R-7: without the gate, played = written - 0 = written, and
        // the highlight lands half a second into text nobody has heard.
        Assert.False(clock.Update(0));
        Assert.False(clock.IsPrimed);
        Assert.Equal(0, clock.PlayedFrames);
    }

    [Fact]
    public void Refuses_a_reading_that_claims_more_buffered_than_was_ever_written()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(300));

        Assert.False(clock.Update(Usec(Frames(900))));
        Assert.False(clock.IsPrimed);
    }

    [Fact]
    public void Primes_on_the_first_plausible_reading_and_fast_forwards()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(500));

        // 350 ms still buffered of 500 ms written: 150 ms has been heard. The
        // clock must land there directly rather than starting at zero and
        // walking up, which would replay the opening words.
        Assert.True(clock.Update(Usec(Frames(350))));
        Assert.True(clock.IsPrimed);
        Assert.Equal(Frames(150), clock.PlayedFrames);
    }

    [Fact]
    public void Priming_latches_so_a_later_bad_reading_does_not_stall_the_highlight()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(500));
        Assert.True(clock.Update(Usec(Frames(350))));

        // A dropped reading mid-stream is a missing sample, not a re-priming
        // stream. Re-arming the suppression here would freeze the highlight
        // for the rest of the utterance.
        Assert.True(clock.Update(0));
        Assert.True(clock.IsPrimed);
        Assert.Equal(Frames(150), clock.PlayedFrames);
    }

    // ------------------------------------------------------------ monotonicity

    [Fact]
    public void Never_moves_backwards_when_latency_jitters()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(1000));

        Assert.True(clock.Update(Usec(Frames(300))));   // played 700 ms
        Assert.Equal(Frames(700), clock.PlayedFrames);

        // Latency jitters up by 50 ms, which arithmetically un-hears 50 ms of
        // speech. Dragging the highlight back over words already shown is worse
        // than standing still, so the clock stands still.
        Assert.True(clock.Update(Usec(Frames(350))));
        Assert.Equal(Frames(700), clock.PlayedFrames);

        // And recovers on the next good reading rather than staying stuck.
        Assert.True(clock.Update(Usec(Frames(250))));
        Assert.Equal(Frames(750), clock.PlayedFrames);
    }

    [Fact]
    public void Ignores_an_implausible_latency_spike()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(10_000));
        Assert.True(clock.Update(Usec(Frames(200))));
        long before = clock.PlayedFrames;

        // Six seconds buffered is arithmetically possible on a 10 s stream but
        // is sensor noise in practice. Believing it once is unrecoverable: the
        // monotonic guard means the highlight cannot be pulled back, so it
        // would sit frozen until playback caught up to where it had jumped.
        Assert.True(clock.Update(6_000_000));
        Assert.Equal(before, clock.PlayedFrames);
    }

    [Fact]
    public void Never_reports_more_played_than_written()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(500));

        Assert.True(clock.Update(1));   // 1 µs buffered: essentially all played
        Assert.Equal(Frames(500), clock.PlayedFrames);
        Assert.True(clock.PlayedFrames <= clock.WrittenFrames);
    }

    // -------------------------------------------------------------- lifecycle

    [Fact]
    public void Draining_is_the_one_exact_answer()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(500));
        clock.Update(Usec(Frames(300)));

        clock.Drained();

        Assert.Equal(clock.WrittenFrames, clock.PlayedFrames);
        Assert.True(clock.IsPrimed);
    }

    [Fact]
    public void Reset_unprimes_so_a_flushed_utterance_does_not_credit_the_next_one()
    {
        var clock = new PlaybackClock(Rate);
        clock.AddWritten(Frames(1000));
        clock.Update(Usec(Frames(200)));

        clock.Reset();

        Assert.Equal(0, clock.WrittenFrames);
        Assert.Equal(0, clock.PlayedFrames);
        Assert.False(clock.IsPrimed);

        // The next utterance re-primes from scratch. Without this, its first
        // reading would be subtracted from the previous utterance's written
        // total and the highlight would open in the wrong paragraph.
        clock.AddWritten(Frames(100));
        Assert.False(clock.Update(Usec(Frames(50))));
    }

    // ------------------------------------------------------------- integration

    /// <summary>
    /// A whole utterance, driven the way the daemon will drive it: write a
    /// block, poll, repeat. Asserts the property that actually matters — the
    /// clock tracks real playback within one poll interval and never runs ahead
    /// of the speech, which is what "the highlight is on the right word" means.
    /// </summary>
    [Fact]
    public void Tracks_a_simulated_stream_within_one_poll_interval()
    {
        var clock = new PlaybackClock(Rate);

        long blockFrames = Frames(20);      // the sink's write block
        long bufferFrames = Frames(150);    // pa_buffer_attr.tlength
        long trulyPlayed = 0;

        for (int i = 0; i < 200; i++)       // 4 s of audio
        {
            clock.AddWritten(blockFrames);

            // The device consumes one block per iteration once the buffer has
            // filled — the steady state a blocking write settles into.
            if (clock.WrittenFrames > bufferFrames)
                trulyPlayed = clock.WrittenFrames - bufferFrames;

            long buffered = clock.WrittenFrames - trulyPlayed;
            bool primed = clock.Update(Usec(buffered));

            if (!primed) continue;

            Assert.True(clock.PlayedFrames <= trulyPlayed,
                $"clock ran ahead of playback at block {i}: " +
                $"{clock.PlayedFrames} > {trulyPlayed}");
            Assert.True(trulyPlayed - clock.PlayedFrames <= blockFrames,
                $"clock fell more than one block behind at block {i}");
        }

        Assert.True(clock.IsPrimed);
    }
}

using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The daemon must survive an audio device it cannot open.
///
/// <para>Found by running the daemon with an unreachable PulseAudio server: it
/// threw out of <c>Main</c>, core-dumped, and exited 134 — before the control
/// socket existed, so every verb that could have explained it was gone too. With
/// <c>vst-ctl</c>'s autostart (R-5) on top, that is a hotkey that silently does
/// nothing, which is the exact failure the plan's Phase 7 note says the daemon
/// must stop growing new causes for.</para>
/// </summary>
public class LazyAudioSinkTests
{
    private static readonly SupertonicOptions Voice = new("M1", "en");

    [Fact]
    public void Reports_its_sample_rate_without_opening_the_device()
    {
        // SpeechSession builds its playback clock from SampleRate before the
        // first write, so a rate that required an open device would put the
        // device back on the startup path this class exists to take it off.
        bool opened = false;
        var sink = new LazyAudioSink(44100, () => { opened = true; return new FakeSink(); });
        using var _s = sink;

        Assert.Equal(44100, sink.SampleRate);
        Assert.False(opened);
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public void A_device_that_will_not_open_is_reported_not_thrown()
    {
        var sink = new LazyAudioSink(44100,
            () => throw new InvalidOperationException(
                "could not open a PulseAudio playback stream: Connection refused"));
        using var _s = sink;

        Assert.False(sink.TryOpen(out string? error));
        Assert.Contains("Connection refused", error);
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public void Opening_is_retried_on_every_ask_because_the_server_may_come_back()
    {
        // Per request, not once: an audio server that was down when the daemon
        // started may be up by the time anyone presses the key, and a daemon that
        // had latched the failure would stay mute until restarted.
        int attempts = 0;
        var sink = new LazyAudioSink(44100, () =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("Connection refused");
            return new FakeSink();
        });
        using var _s = sink;

        Assert.False(sink.TryOpen(out _));
        Assert.False(sink.TryOpen(out _));
        Assert.True(sink.TryOpen(out string? error));
        Assert.Null(error);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void The_device_is_opened_once_and_then_reused()
    {
        int opens = 0;
        var sink = new LazyAudioSink(44100, () => { opens++; return new FakeSink(); });
        using var _s = sink;

        Assert.True(sink.TryOpen(out _));
        Assert.True(sink.TryOpen(out _));
        Assert.True(sink.IsOpen);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void A_device_that_opens_at_the_wrong_rate_is_refused()
    {
        // The clock was already told 44100. A sink running at another rate would
        // desynchronise every boundary by the ratio -- and a wrong word-boundary
        // offset sounds exactly like a right one, so nothing downstream catches it.
        var inner = new FakeSink(22050);
        var sink = new LazyAudioSink(44100, () => inner);
        using var _s = sink;

        Assert.False(sink.TryOpen(out string? error));
        Assert.Contains("22050", error);
        Assert.True(inner.Disposed);          // not leaked on the way out
    }

    [Fact]
    public void Stop_before_anything_opened_the_device_is_harmless()
    {
        // Stop() calls RequestFlush unconditionally, and the daemon reaches it
        // whenever a press is followed by a stop -- including with no device.
        var sink = new LazyAudioSink(44100, () => new FakeSink());
        using var _s = sink;

        sink.RequestFlush();
        sink.Flush();
        sink.Drain();

        Assert.Equal(0, sink.WrittenFrames);
        Assert.Equal(0, sink.LatencyUsec);
        Assert.False(sink.IsOpen);
    }

    [Fact]
    public void Disposing_before_opening_never_touches_the_device()
    {
        bool opened = false;
        var sink = new LazyAudioSink(44100, () => { opened = true; return new FakeSink(); });

        sink.Dispose();
        sink.Dispose();                       // idempotent

        Assert.False(opened);
    }

    [Fact]
    public void Disposing_after_opening_closes_the_real_device()
    {
        var inner = new FakeSink();
        var sink = new LazyAudioSink(44100, () => inner);

        Assert.True(sink.TryOpen(out _));
        sink.Dispose();

        Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task A_session_speaks_normally_through_a_sink_opened_late()
    {
        // The whole point: deferring changes WHEN the device is created and
        // nothing about what happens afterwards.
        var inner = new FakeSink();
        var sink = new LazyAudioSink(44100, () => inner);
        using var session = new SpeechSession(new FakeSynthesizer(), sink, new SpeechSessionOptions(PrimeMs: 10));

        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };

        Assert.True(sink.TryOpen(out _));
        session.Speak("One two three.", Voice);
        await session.Completion;

        Assert.Equal(SpeechState.Idle, session.State);
        lock (events)
        {
            Assert.Contains(events, e => e.Kind == SessionEventKind.Finished);
            Assert.DoesNotContain(events, e => e.Kind == SessionEventKind.Error);
            Assert.Contains(events, e => e.Kind == SessionEventKind.WordBoundary);
        }
        Assert.Equal(1, inner.DrainCount);
    }
}

using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Surviving an audio server that restarts underneath a long-lived daemon.
///
/// <para><b>The defect these were written for, from the field 2026-08-16.</b> The
/// daemon opened its PulseAudio stream once and assumed it would live as long as
/// the process. PipeWire restarted forty minutes later and every press for the
/// next five hours did nothing at all: <c>status</c> answered, <c>config</c>
/// answered, <c>speak</c> was accepted and acknowledged, and then
/// <c>pa_simple_write</c> failed with "Connection terminated" on a worker thread
/// whose only report was an <c>Error</c> event on a stream nothing subscribes
/// to. Nothing was logged. Nothing refused. The product looked healthy from every
/// angle except the speakers.</para>
///
/// <para>The assumption came from the Windows engine, where it is true — a SAPI
/// engine lives as long as its host, which is minutes. It expires the moment the
/// same code runs in something meant to be left running for a week.</para>
///
/// <para>What is pinned here is the whole recovery, in both directions: that
/// device loss is <em>detected</em> before an utterance rather than after it,
/// that it is recovered from, that recovery does not corrupt the clock, and that
/// an ordinary failure is <b>not</b> mistaken for device loss — which would turn
/// one broken write into an unbounded reconnect loop.</para>
/// </summary>
public class AudioDeviceLossTests
{
    private static readonly SynthesisOptions Voice = new("M1", "en");

    // ------------------------------------------------------- detect before use

    [Fact]
    public void A_cached_device_that_died_is_reopened_before_the_next_utterance()
    {
        // The heart of it. TryOpen used to return true because something had been
        // opened once; it now asks whether that something is still there. This is
        // the difference between the FIRST press after a restart working and the
        // second.
        var devices = new List<FakeSink>();
        var sink = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        Assert.Single(devices);

        devices[0].LoseDevice();

        Assert.True(sink.TryOpen(out string? error));
        Assert.Null(error);
        Assert.Equal(2, devices.Count);              // a second device was opened
        Assert.Equal(1, sink.Reconnects);
        Assert.True(devices[0].Disposed);            // and the dead one was let go
        Assert.False(devices[1].Disposed);
    }

    [Fact]
    public void A_live_device_is_not_reopened()
    {
        // The other half, and the one a careless fix breaks: reopening on every
        // request would drop the stream mid-session, cost a reconnect per press,
        // and lose the buffered audio each time.
        var devices = new List<FakeSink>();
        var sink = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = sink;

        for (int i = 0; i < 5; i++) Assert.True(sink.TryOpen(out _));

        Assert.Single(devices);
        Assert.Equal(0, sink.Reconnects);
    }

    [Fact]
    public void A_device_lost_while_the_server_is_still_down_reports_the_reason()
    {
        // Loss and "cannot reopen" are different answers and the caller needs
        // both: the first is transient, the second is what the user is told.
        var devices = new List<FakeSink>();
        bool serverUp = true;
        var sink = new LazyAudioSink(44100, () =>
        {
            if (!serverUp) throw new InvalidOperationException("Connection refused");
            var s = new FakeSink();
            devices.Add(s);
            return s;
        });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        devices[0].LoseDevice();
        serverUp = false;

        Assert.False(sink.TryOpen(out string? error));
        Assert.Equal("Connection refused", error);

        serverUp = true;
        Assert.True(sink.TryOpen(out _));            // and it comes back by itself
        Assert.Equal(2, devices.Count);
    }

    // -------------------------------------------------- loss during an utterance

    [Fact]
    public void A_device_lost_mid_write_ends_that_utterance_and_frees_the_sink()
    {
        var devices = new List<FakeSink>();
        var sink = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        devices[0].LoseDevice();

        // Deliberately propagates. Swallowing it here would let the session
        // believe it played audio the device never took, and every boundary after
        // that point would be timed against silence.
        Assert.Throws<AudioDeviceLostException>(() => sink.Write(new short[64]));

        Assert.False(sink.IsOpen);
        Assert.Equal(1, sink.Reconnects);
        Assert.Contains("Connection terminated", sink.LastLoss);
    }

    [Fact]
    public async Task The_session_reports_an_error_and_returns_to_idle()
    {
        var devices = new List<FakeSink>();
        var lazy = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = lazy;
        Assert.True(lazy.TryOpen(out _));

        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, lazy, new SpeechSessionOptions(PrimeMs: 10));
        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };

        devices[0].LoseDevice();
        Assert.True(session.Speak("The sea is everything.", Voice));
        await session.Completion;

        // Error, not a clean Finished. A daemon that reported success for an
        // utterance nobody heard is the bug, restated.
        SessionEvent[] snapshot;
        lock (events) snapshot = events.ToArray();

        var error = Assert.Single(snapshot, e => e.Kind == SessionEventKind.Error);
        Assert.Contains("Connection terminated", error.Message);
        Assert.DoesNotContain(snapshot, e => e.Kind == SessionEventKind.Finished);
        Assert.Equal(SpeechState.Idle, session.State);
    }

    [Fact]
    public async Task The_next_utterance_after_a_loss_succeeds()
    {
        // The whole point. One utterance is lost at the moment the audio server
        // restarts; the one after it works, with no restart of anything.
        var devices = new List<FakeSink>();
        var lazy = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = lazy;
        Assert.True(lazy.TryOpen(out _));

        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, lazy, new SpeechSessionOptions(PrimeMs: 10));

        devices[0].LoseDevice();
        session.Speak("This one dies.", Voice);
        await session.Completion;

        // What the daemon's request path does before every utterance.
        Assert.True(lazy.TryOpen(out string? error));
        Assert.Null(error);

        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };

        Assert.True(session.Speak("This one is heard.", Voice));
        await session.Completion;

        SessionEvent[] snapshot;
        lock (events) snapshot = events.ToArray();

        Assert.Single(snapshot, e => e.Kind == SessionEventKind.Finished);
        Assert.DoesNotContain(snapshot, e => e.Kind == SessionEventKind.Error);
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task A_reconnected_device_starts_the_clock_from_zero()
    {
        // The reason recovery is between utterances and never inside one: a fresh
        // stream has written nothing and buffers nothing, while the clock and
        // every scheduled boundary are counted against the old one. Boundaries on
        // the new utterance must be timed from 0, not carried over.
        var devices = new List<FakeSink>();
        var lazy = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = lazy;
        Assert.True(lazy.TryOpen(out _));

        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, lazy, new SpeechSessionOptions(PrimeMs: 10));

        session.Speak("First utterance, heard normally.", Voice);
        await session.Completion;

        devices[0].LoseDevice();
        session.Speak("Second one, lost.", Voice);
        await session.Completion;
        Assert.True(lazy.TryOpen(out _));

        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };
        session.Speak("Third one, after the reconnect.", Voice);
        await session.Completion;

        SessionEvent[] snapshot;
        lock (events) snapshot = events.ToArray();

        var boundaries = snapshot
            .Where(e => e.Kind is SessionEventKind.WordBoundary or SessionEventKind.SentenceBoundary)
            .Select(e => e.AudioSeconds!.Value)
            .ToArray();

        Assert.NotEmpty(boundaries);
        Assert.Equal(0.0, boundaries[0], precision: 6);
        Assert.All(boundaries, s => Assert.True(s < 5.0,
            $"boundary at {s}s carried time over from a previous stream"));
    }

    // ---------------------------------------- what must NOT count as device loss

    [Fact]
    public void An_ordinary_write_failure_does_not_reconnect()
    {
        // The guard against the cure being worse. If any failure were treated as
        // loss, a sink that rejects every write — a format mismatch, a caller
        // bug — would be reopened on every attempt, forever, and the log would
        // fill with reconnects that fix nothing.
        var devices = new List<FakeSink>();
        var sink = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        devices[0].ThrowOnWrite = new InvalidOperationException("some other problem");

        Assert.Throws<InvalidOperationException>(() => sink.Write(new short[64]));

        Assert.True(sink.IsOpen);
        Assert.Equal(0, sink.Reconnects);
        Assert.Single(devices);
    }

    [Fact]
    public void A_stop_mid_utterance_is_not_device_loss_either()
    {
        // Flush throws OperationCanceledException out of Write by design — that
        // is what stop IS. Treating it as loss would drop and reopen the device
        // on every single stop, which is the commonest thing the product does.
        var devices = new List<FakeSink>();
        var sink = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        sink.RequestFlush();

        Assert.Throws<OperationCanceledException>(() => sink.Write(new short[64]));

        Assert.True(sink.IsOpen);
        Assert.Equal(0, sink.Reconnects);
    }

    // ------------------------------------------------------------- under stress

    [Fact]
    public async Task Repeated_losses_are_each_recovered_and_counted()
    {
        // A machine whose audio server is genuinely unstable, or a laptop being
        // docked and undocked. Nothing here should degrade with repetition — the
        // count is the only thing that grows.
        var devices = new List<FakeSink>();
        var lazy = new LazyAudioSink(44100, () => { var s = new FakeSink(); devices.Add(s); return s; });
        using var closing = lazy;

        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, lazy, new SpeechSessionOptions(PrimeMs: 10));

        for (int round = 0; round < 20; round++)
        {
            Assert.True(lazy.TryOpen(out string? error), $"round {round}: {error}");
            devices[^1].LoseDevice();

            session.Speak($"Round {round}.", Voice);
            await session.Completion;
            Assert.Equal(SpeechState.Idle, session.State);
        }

        // Twenty devices opened and twenty lost — the loop drops the last one on
        // its way out, so nothing is live here. Getting this wrong the other way
        // round is what the assertion is for: a reconnect count that drifted from
        // the number of devices actually opened would mean one was leaked.
        Assert.Equal(20, lazy.Reconnects);
        Assert.Equal(20, devices.Count);
        Assert.False(lazy.IsOpen);

        // And it still speaks at the end of all that.
        Assert.True(lazy.TryOpen(out _));
        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };
        session.Speak("Still here.", Voice);
        await session.Completion;

        SessionEvent[] snapshot;
        lock (events) snapshot = events.ToArray();
        Assert.Single(snapshot, e => e.Kind == SessionEventKind.Finished);
    }

    [Fact]
    public void Concurrent_callers_racing_a_loss_open_exactly_one_replacement()
    {
        // The daemon serves every client on its own task, so two presses can
        // reach TryOpen at the same moment — and the moment they are most likely
        // to is right after a resume, which is exactly when the device has just
        // been lost. Two replacements would leave one stream orphaned and
        // playing.
        var devices = new List<FakeSink>();
        var opens = 0;
        var sink = new LazyAudioSink(44100, () =>
        {
            Interlocked.Increment(ref opens);
            Thread.Sleep(5);                          // widen the window
            var s = new FakeSink();
            lock (devices) devices.Add(s);
            return s;
        });
        using var closing = sink;

        Assert.True(sink.TryOpen(out _));
        lock (devices) devices[0].LoseDevice();

        var barrier = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            barrier.Wait();
            sink.TryOpen(out _);
        })).ToList();

        foreach (var t in threads) t.Start();
        barrier.Set();
        foreach (var t in threads) t.Join();

        // One replacement, not eight: the check and the open are under one lock.
        Assert.Equal(2, opens);
        Assert.Equal(1, sink.Reconnects);
        Assert.True(sink.IsOpen);
    }

    [Fact]
    public void Losing_the_device_during_disposal_does_not_throw()
    {
        // Shutdown runs while the audio server may already be going away — a
        // logout tears both down at once. Dispose that throws on the way out is
        // the exit-134-and-a-core-file failure this codebase has paid for twice.
        var sink = new LazyAudioSink(44100, () => new ThrowingOnDispose());
        Assert.True(sink.TryOpen(out _));

        sink.Dispose();
        sink.Dispose();                               // and idempotent
    }

    private sealed class ThrowingOnDispose : IAudioSink
    {
        public int SampleRate => 44100;
        public long WrittenFrames => 0;
        public long LatencyUsec => 0;
        public bool IsAlive => false;
        public void Write(ReadOnlySpan<short> pcm) => throw new AudioDeviceLostException("gone");
        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() => throw new InvalidOperationException("the server already went away");
    }
}

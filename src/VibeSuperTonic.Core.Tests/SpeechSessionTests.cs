using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The Phase 3 pipeline: text in, speech out, and a stop that actually stops.
///
/// Every test here runs against <see cref="FakeSink"/> and
/// <see cref="FakeSynthesizer"/> in a few milliseconds with no audio device and
/// no model, which is the entire reason the session was written against two
/// interfaces rather than against libpulse and ONNX. The behaviour that decides
/// whether the product feels broken — a stop arriving while the writer is blocked
/// inside the device, a press during a cold load — is a race on real hardware and
/// a parameter here.
/// </summary>
public class SpeechSessionTests
{
    private static readonly SynthesisOptions Voice = new("M1", "en");

    /// <summary>Prime fast: these tests are not measuring the clock, they assume it.</summary>
    private static SpeechSessionOptions Options() => new(PrimeMs: 10);

    private sealed class Recorder
    {
        private readonly List<SessionEvent> _events = new();
        public IReadOnlyList<SessionEvent> Events { get { lock (_events) return _events.ToList(); } }
        public void Add(SessionEvent e) { lock (_events) _events.Add(e); }

        public IEnumerable<SpeechState> States =>
            Events.Where(e => e.Kind == SessionEventKind.StateChanged).Select(e => e.State!.Value);

        public IEnumerable<SessionEvent> OfKind(SessionEventKind k) =>
            Events.Where(e => e.Kind == k);
    }

    private static (SpeechSession Session, FakeSynthesizer Synth, FakeSink Sink, Recorder Log) Build(
        SpeechSessionOptions? options = null)
    {
        var synth = new FakeSynthesizer();
        var sink = new FakeSink();
        var session = new SpeechSession(synth, sink, options ?? Options());
        var log = new Recorder();
        session.Emitted += log.Add;
        return (session, synth, sink, log);
    }

    // -------------------------------------------------------------- happy path

    [Fact]
    public async Task Speaks_an_utterance_and_returns_to_idle()
    {
        var (session, synth, sink, log) = Build();
        using var _ = session;

        Assert.True(session.Speak("Hello world. Second sentence here.", Voice));
        await session.Completion;

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Equal(
            new[] { SpeechState.Preparing, SpeechState.Speaking, SpeechState.Idle },
            log.States);
        Assert.Single(log.OfKind(SessionEventKind.Started));
        Assert.Single(log.OfKind(SessionEventKind.Finished));
        Assert.Empty(log.OfKind(SessionEventKind.Error));
        Assert.Empty(log.OfKind(SessionEventKind.Stopped));

        // Drained rather than abandoned: the last words must be heard, not cut.
        Assert.Equal(1, sink.DrainCount);
        Assert.NotEmpty(synth.Rendered);
    }

    [Fact]
    public async Task Reports_every_word_against_the_text_the_caller_passed_in()
    {
        const string Text = "First one.  Second one.\n\nThird one here.";
        var (session, _, _, log) = Build();
        using var _s = session;

        session.Speak(Text, Voice);
        await session.Completion;

        var words = log.OfKind(SessionEventKind.WordBoundary)
            .Select(e => Text.Substring(e.SourceOffset!.Value, e.SourceLength!.Value))
            .ToList();

        // The double space and the paragraph break are the point — the chunker
        // collapses both, so an offset carried in rewritten coordinates drifts
        // one character per separator. That was R-14, and it survived R-2's fix.
        Assert.Equal(
            new[] { "First", "one", "Second", "one", "Third", "one", "here" },
            words);
    }

    [Fact]
    public async Task Boundaries_arrive_in_audio_order()
    {
        var (session, _, _, log) = Build();
        using var _s = session;

        session.Speak("One two three. Four five six. Seven eight nine.", Voice);
        await session.Completion;

        var times = log.Events
            .Where(e => e.Kind is SessionEventKind.WordBoundary or SessionEventKind.SentenceBoundary)
            .Select(e => e.AudioSeconds!.Value)
            .ToList();

        Assert.NotEmpty(times);
        for (int i = 1; i < times.Count; i++)
            Assert.True(times[i] >= times[i - 1], $"audio position went backwards at event {i}");
    }

    [Fact]
    public async Task Empty_text_finishes_without_touching_the_device()
    {
        var (session, synth, sink, log) = Build();
        using var _ = session;

        session.Speak("   ", Voice);
        await session.Completion;

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Single(log.OfKind(SessionEventKind.Finished));
        Assert.Equal(0, sink.WriteCount);
        Assert.Empty(synth.Rendered);
    }

    // --------------------------------------------------------------------- stop

    [Fact]
    public async Task Stop_interrupts_a_writer_blocked_inside_the_device()
    {
        var (session, synth, sink, log) = Build();
        using var _ = session;

        // Four chunks of a second each, and a device that stops accepting audio
        // half a second in. This is the situation stop exists for: the writer is
        // inside the sink, the renderer is working on a chunk nobody will hear,
        // and the user has just pressed the key again.
        synth.FramesPerChunk = 44100;
        sink.BlockAfterFrames = 22050;

        session.Speak(
            "One two three four. Five six seven eight. Nine ten eleven twelve. Thirteen fourteen.",
            Voice);

        Assert.True(sink.Blocked.Wait(TimeSpan.FromSeconds(5)), "the sink never blocked");
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Single(log.OfKind(SessionEventKind.Stopped));
        Assert.Empty(log.OfKind(SessionEventKind.Finished));
        Assert.Contains(SpeechState.Stopping, log.States);

        // Flushed, not drained: buffered audio is dropped, which is what makes a
        // stop sound instant rather than trailing a buffer of speech the user
        // has already asked to end.
        Assert.True(sink.FlushCount > 0);
        Assert.Equal(0, sink.DrainCount);
    }

    [Fact]
    public async Task Stop_during_a_cold_load_cancels_it()
    {
        var (session, synth, _, log) = Build();
        using var _ = session;

        // Preparing counts as active. A cold load is 2–5 s and is exactly when a
        // user who has heard nothing presses again; a stop that only worked once
        // audio started would leave the key feeling dead in that window.
        synth.StallUntilCancelled = true;

        session.Speak("Anything at all.", Voice);
        Assert.True(SpinUntil(() => session.State == SpeechState.Preparing));

        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Single(log.OfKind(SessionEventKind.Stopped));
        Assert.Empty(log.OfKind(SessionEventKind.Started));
    }

    [Fact]
    public async Task Stop_discards_audio_that_was_rendered_but_never_played()
    {
        var (session, synth, sink, _) = Build();
        using var _s = session;

        synth.FramesPerChunk = 44100;
        sink.BlockAfterFrames = 4410;   // block almost immediately

        session.Speak("One two. Three four. Five six. Seven eight. Nine ten.", Voice);
        Assert.True(sink.Blocked.Wait(TimeSpan.FromSeconds(5)));
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // The third part of stop. Rendering runs ahead of playback, so chunks
        // are already sitting in the queue; leaving them there would make the
        // next utterance start by speaking the previous one.
        sink.BlockAfterFrames = null;
        Assert.True(session.Speak("A fresh start.", Voice));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // Exactly the new text, and only once — no leftovers from the utterance
        // that was abandoned.
        Assert.Equal("A fresh start.", synth.Rendered[^1]);
    }

    [Fact]
    public void Stop_when_idle_is_a_no_op()
    {
        var (session, _, sink, log) = Build();
        using var _ = session;

        session.Stop();
        session.Stop();

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Empty(log.Events);
        Assert.Equal(0, sink.FlushCount);
    }

    // ------------------------------------------------------------- concurrency

    [Fact]
    public async Task A_second_speak_while_busy_is_refused_rather_than_queued()
    {
        var (session, synth, _, _) = Build();
        using var _s = session;
        synth.StallUntilCancelled = true;

        Assert.True(session.Speak("First.", Voice));
        Assert.True(SpinUntil(() => session.State == SpeechState.Preparing));

        // ToggleGate turns a press during speech into a stop, so this is a race
        // between two clients rather than a normal path — but silently queueing
        // it would mean a second utterance starting minutes later with no
        // apparent cause.
        Assert.False(session.Speak("Second.", Voice));

        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_take_the_session_with_it()
    {
        var (session, _, _, log) = Build();
        using var _s = session;

        // In the daemon a subscriber is a socket write, and a client that
        // disconnects mid-utterance is routine.
        session.Emitted += _ => throw new InvalidOperationException("subscriber went away");

        session.Speak("Hello world.", Voice);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Single(log.OfKind(SessionEventKind.Finished));
    }

    // ------------------------------------------- what a late subscriber can see

    [Fact]
    public async Task While_speaking_the_session_can_say_what_and_where()
    {
        // Phase 6 opens a window mid-read and expects the highlight to land on
        // the right word. The event stream only carries what happens after you
        // connect, so a late subscriber has a position and nothing to index it
        // into unless the session can be asked directly.
        const string Text = "One two three. Four five six. Seven eight nine.";
        var (session, synth, sink, _) = Build();
        using var _s = session;
        synth.FramesPerChunk = 44100;
        sink.WriteDelayMs = 2;

        Assert.Null(session.CurrentText);

        session.Speak(Text, Voice);
        Assert.True(SpinUntil(() => session.LastBoundary is not null));

        Assert.Equal(Text, session.CurrentText);
        var at = session.LastBoundary!.Value;
        Assert.InRange(at.SourceOffset, 0, Text.Length - 1);
        Assert.NotEqual(0, at.SourceLength);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // Cleared on the way out, or a window opened after a read would show the
        // last utterance as though it were still playing.
        Assert.Null(session.CurrentText);
        Assert.Null(session.LastBoundary);
    }

    [Fact]
    public async Task A_stopped_utterance_leaves_nothing_behind_either()
    {
        var (session, synth, sink, _) = Build();
        using var _s = session;
        synth.FramesPerChunk = 44100;
        sink.BlockAfterFrames = 22050;

        session.Speak("One two three. Four five six. Seven eight nine.", Voice);
        Assert.True(sink.Blocked.Wait(TimeSpan.FromSeconds(5)));
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(session.CurrentText);
        Assert.Null(session.LastBoundary);
    }

    // ------------------------------------------------------------------ failure

    [Fact]
    public async Task A_synthesizer_failure_is_reported_and_the_session_recovers()
    {
        var (session, synth, _, log) = Build();
        using var _s = session;
        synth.ThrowOnSynthesize = new InvalidOperationException("model file is corrupt");

        session.Speak("Hello world.", Voice);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var error = Assert.Single(log.OfKind(SessionEventKind.Error));
        Assert.Contains("model file is corrupt", error.Message);

        // Back to Idle, not stuck: a daemon that wedges on one bad utterance
        // needs restarting, and the user's only symptom is a key that stopped
        // working.
        Assert.Equal(SpeechState.Idle, session.State);

        synth.ThrowOnSynthesize = null;
        Assert.True(session.Speak("And again.", Voice));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(log.OfKind(SessionEventKind.Finished));
    }

    // -------------------------------------------------------------------- pause

    [Fact]
    public async Task Pause_and_resume_carry_the_utterance_to_completion()
    {
        var (session, synth, sink, log) = Build();
        using var _s = session;
        synth.FramesPerChunk = 44100;
        sink.WriteDelayMs = 2;      // otherwise it finishes before it can be paused

        session.Speak("One two three. Four five six.", Voice);
        Assert.True(SpinUntil(() => session.State == SpeechState.Speaking));

        session.Pause();
        Assert.True(session.IsPaused);
        session.Resume();

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(log.OfKind(SessionEventKind.Finished));
        Assert.False(session.IsPaused);
    }

    [Fact]
    public async Task Stop_releases_a_paused_writer()
    {
        var (session, synth, sink, log) = Build();
        using var _s = session;
        synth.FramesPerChunk = 44100 * 2;
        sink.WriteDelayMs = 2;

        session.Speak("One two three four five six seven eight.", Voice);
        Assert.True(SpinUntil(() => session.State == SpeechState.Speaking));

        session.Pause();
        Thread.Sleep(50);           // long enough for the writer to park

        // A paused writer is parked on a wait handle. Stop has to wake it — if
        // it does not, this test does not fail an assertion, it hangs until the
        // timeout, which is exactly how the bug would present in the product:
        // the key stops working and nothing is logged.
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Single(log.OfKind(SessionEventKind.Stopped));
    }

    // ------------------------------------------------------------------ helpers

    private static bool SpinUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            Thread.Sleep(1);
        }
        return condition();
    }
}

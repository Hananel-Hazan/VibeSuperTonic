using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Stop, Pause and the next utterance racing each other.
///
/// <para>Every defect here is a few instructions wide and lives between a state
/// check made under the session's lock and an action taken after it — a flush,
/// an event, a reset. On a desktop the window is a press landing as a reading
/// ends, which is exactly when people press. So each one is first DRIVEN: the
/// subscriber callback (which runs on the stopping thread, between the lock and
/// the action) or the Pause seam holds the window open while another utterance
/// is pushed through it. Those tests failed on the unfixed session every run;
/// waiting for the scheduler to produce the same interleaving would have been a
/// test that passes for a dozen releases and then fails under load.</para>
///
/// <para>The hammering test at the end is a smoke check on top, not the
/// evidence: it asserts the invariants hold under arbitrary interleavings, and
/// it is the thing that would notice a new window opening somewhere else.</para>
/// </summary>
public class SpeechSessionStressTests
{
    private static readonly SupertonicOptions Voice = new("M1", "en");

    /// <summary>
    /// A synthesizer whose every call can be held: the test decides when an
    /// utterance is allowed to have audio, which is what makes "the utterance
    /// finished while Stop was still emitting" a sequence instead of a hope.
    /// </summary>
    private sealed class HookSynth : ISynthesizer
    {
        public int SampleRate => 44100;
        public int FramesPerChunk { get; set; } = 2205;     // 50 ms
        public Action<string, CancellationToken>? OnSynthesize { get; set; }

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnSynthesize?.Invoke(text, cancellationToken);
            return new short[FramesPerChunk];
        }

        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class Recorder
    {
        private readonly List<SessionEvent> _events = new();
        public IReadOnlyList<SessionEvent> Events { get { lock (_events) return _events.ToList(); } }
        public void Add(SessionEvent e) { lock (_events) _events.Add(e); }
        public IEnumerable<SessionEvent> OfKind(SessionEventKind k) => Events.Where(e => e.Kind == k);
        public string[] Errors => OfKind(SessionEventKind.Error).Select(e => e.Message ?? "").ToArray();
    }

    /// <summary>
    /// The order state events reached subscribers must be one a single utterance
    /// can actually go through. In particular a Stopping after the Idle that
    /// ended its utterance tells the tray and the toggle gate that something is
    /// being stopped when nothing is running — and the gate acts on it.
    /// </summary>
    private static void AssertStateOrderIsCoherent(Recorder log)
    {
        var states = log.OfKind(SessionEventKind.StateChanged).Select(e => e.State!.Value).ToList();
        var current = SpeechState.Idle;
        for (int i = 0; i < states.Count; i++)
        {
            var next = states[i];
            bool legal = (current, next) switch
            {
                (SpeechState.Idle, SpeechState.Preparing) => true,
                (SpeechState.Preparing, SpeechState.Speaking or SpeechState.Stopping or SpeechState.Idle) => true,
                (SpeechState.Speaking, SpeechState.Stopping or SpeechState.Idle) => true,
                (SpeechState.Stopping, SpeechState.Idle) => true,
                _ => false,
            };
            Assert.True(legal,
                $"state event {i}: {current} -> {next} is not a transition one utterance can make " +
                $"(sequence: {string.Join(", ", states)})");
            current = next;
        }
    }

    // ------------------------------------------------------------ driven races

    [Fact]
    public async Task A_stop_overtaken_by_the_end_of_its_utterance_does_not_flush_the_next_one()
    {
        // The interleaving, in order:
        //   1. Stop() finds utterance A running, sets Stopping and releases the
        //      lock — then emits Stopping, OUTSIDE it.
        //   2. While it is emitting, A finishes on its own: nothing has been
        //      cancelled yet. The session goes Idle.
        //   3. B starts, and its writer does the initial Flush that is meant to
        //      guarantee no utterance begins under a flush request.
        //   4. Only now does the stale Stop call RequestFlush — for A, which no
        //      longer exists — and B's first Write throws. B is silent, and the
        //      session reports "the stale-flush defect, and it should not be
        //      reachable".
        // The subscriber below runs on the stopping thread at step 1, so it is
        // the window. It gives step 2 a second to happen: with the fix, A cannot
        // reach Idle while its own Stopping is being announced, and the wait
        // simply times out.
        var synth = new HookSynth();
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));
        var log = new Recorder();
        session.Emitted += log.Add;

        using var stoppingSeen = new ManualResetEventSlim(false);
        using var idleSeen = new ManualResetEventSlim(false);
        using var bRendering = new ManualResetEventSlim(false);
        using var releaseB = new ManualResetEventSlim(false);

        synth.OnSynthesize = (text, _) =>
        {
            if (text.StartsWith("Alpha")) stoppingSeen.Wait(TimeSpan.FromSeconds(5));
            else { bRendering.Set(); releaseB.Wait(TimeSpan.FromSeconds(5)); }
        };

        int stopThread = -1;
        bool bStarted = false;
        session.Emitted += e =>
        {
            if (e.Kind != SessionEventKind.StateChanged) return;
            if (e.State == SpeechState.Idle) idleSeen.Set();
            if (e.State != SpeechState.Stopping || Environment.CurrentManagedThreadId != stopThread) return;

            stoppingSeen.Set();                                    // let A finish on its own...
            if (!idleSeen.Wait(TimeSpan.FromSeconds(1))) return;   // ...which the fix forbids
            bStarted = session.Speak("Bravo must be heard.", Voice);
            if (bStarted) bRendering.Wait(TimeSpan.FromSeconds(5));   // B is past its initial Flush
        };

        Assert.True(session.Speak("Alpha.", Voice));
        await Task.Run(() => { stopThread = Environment.CurrentManagedThreadId; session.Stop(); });

        if (!bStarted)
        {
            await session.Completion;
            Assert.True(session.Speak("Bravo must be heard.", Voice));
        }
        long writesBeforeB = sink.WriteCount;
        releaseB.Set();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(log.Errors.Length == 0, "errors: " + string.Join(" | ", log.Errors.Distinct()));
        Assert.True(sink.WriteCount > writesBeforeB, "B produced no audio: the stale flush landed on it");
        Assert.Equal(SessionEventKind.Finished, log.Events.Last(e =>
            e.Kind is SessionEventKind.Finished or SessionEventKind.Stopped).Kind);
        Assert.Equal(SpeechState.Idle, session.State);
        AssertStateOrderIsCoherent(log);
    }

    [Fact]
    public async Task A_stop_during_preparing_is_not_overwritten_by_the_first_write()
    {
        // The same window, one step earlier. Stop() lands while A is Preparing
        // and sets Stopping — and before it cancels, A's first write is accepted
        // and promotes the state to Speaking, overwriting the Stopping. The
        // cancel then arrives at an utterance whose state says nobody asked for
        // it, and the session reports the "should not be reachable" error for a
        // perfectly ordinary stop.
        var synth = new HookSynth { FramesPerChunk = 44100 };     // long enough to be mid-write
        var sink = new FakeSink { WriteDelayMs = 1 };
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));
        var log = new Recorder();
        session.Emitted += log.Add;

        using var stoppingSeen = new ManualResetEventSlim(false);
        using var startedSeen = new ManualResetEventSlim(false);
        synth.OnSynthesize = (_, _) => stoppingSeen.Wait(TimeSpan.FromSeconds(5));

        int stopThread = -1;
        session.Emitted += e =>
        {
            if (e.Kind == SessionEventKind.Started) startedSeen.Set();
            if (e.Kind != SessionEventKind.StateChanged || e.State != SpeechState.Stopping
                || Environment.CurrentManagedThreadId != stopThread) return;
            stoppingSeen.Set();
            startedSeen.Wait(TimeSpan.FromSeconds(1));   // the fix: no Started under a stop
        };

        Assert.True(session.Speak("Alpha.", Voice));
        await Task.Run(() => { stopThread = Environment.CurrentManagedThreadId; session.Stop(); });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(log.Errors.Length == 0, "errors: " + string.Join(" | ", log.Errors.Distinct()));
        Assert.Single(log.OfKind(SessionEventKind.Stopped));
        Assert.Equal(SpeechState.Idle, session.State);
        AssertStateOrderIsCoherent(log);
    }

    [Fact]
    public async Task A_late_pause_cannot_freeze_the_next_utterance()
    {
        // Pause() checked for Speaking under the lock and reset the event after
        // it. A pause that found A speaking, then lost the CPU while A was
        // stopped and B started, reset the event B's Speak had just set — and B
        // sat in Preparing, paused before it had made a sound, with nothing on
        // any screen saying why. The seam is that lost CPU.
        var synth = new HookSynth { FramesPerChunk = 44100 * 30 };   // A outlasts the test
        var sink = new FakeSink { WriteDelayMs = 1 };
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));
        var log = new Recorder();
        session.Emitted += log.Add;

        Assert.True(session.Speak("Alpha is long.", Voice));
        using var speaking = new ManualResetEventSlim(false);
        session.Emitted += e => { if (e.Kind == SessionEventKind.Started) speaking.Set(); };
        if (session.State != SpeechState.Speaking) Assert.True(speaking.Wait(TimeSpan.FromSeconds(5)));
        synth.FramesPerChunk = 2205;                                 // B is short

        session.AfterPauseCheck = () =>
        {
            // A different thread: the fixed Pause holds the lock here, and the
            // restart has to be able to wait for it rather than deadlock.
            var swap = Task.Run(() =>
                session.Restart("Bravo.", Voice) || session.Restart("Bravo.", Voice));
            swap.Wait(TimeSpan.FromSeconds(1));
        };
        await Task.Run(session.Pause);
        session.AfterPauseCheck = null;

        // Whichever order the fix produced, B must now be either running or
        // about to; nobody pauses it, so it has to finish on its own.
        await WaitForAsync(() => log.Events.Any(e => e.Kind == SessionEventKind.StateChanged
            && e.State == SpeechState.Preparing && e.Text == "Bravo."), 5000);
        bool finished = await WaitForAsync(() => log.Events.Any(e =>
            e.Kind == SessionEventKind.Finished), 5000);

        Assert.False(session.IsPaused && session.State == SpeechState.Preparing,
            "B is paused in Preparing: a stale Pause reset the event its Speak had set");
        Assert.True(finished, $"B never finished; state {session.State}, paused {session.IsPaused}");
        Assert.True(log.Errors.Length == 0, "errors: " + string.Join(" | ", log.Errors.Distinct()));
    }

    [Fact]
    public async Task A_long_inter_chunk_silence_is_not_truncated_by_overflow()
    {
        // SampleRate * InterChunkSilenceMs was computed in int: 50 000 ms at
        // 44.1 kHz is 2.2e9, which wraps negative and clamps to a ONE-SAMPLE
        // gap. A setting meant as "a long pause" produced no pause at all.
        var synth = new HookSynth { FramesPerChunk = 441 };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(
            MaxChunkChars: 24, MinChunkChars: 1, LeadChunkChars: 24, PrimeMs: 10,
            InterChunkSilenceMs: 50_000));
        var log = new Recorder();
        session.Emitted += log.Add;

        Assert.True(session.Speak("First one. Second one. Third one.", Voice));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        double seconds = log.OfKind(SessionEventKind.Finished).Single().AudioSeconds!.Value;
        Assert.True(seconds >= 50.0, $"stream was {seconds:F3} s; the 50 s gap was lost");
    }

    // ------------------------------------------------------------- hammering

    [Fact]
    public async Task Hammering_every_verb_from_several_threads_leaves_a_coherent_idle_session()
    {
        var synth = new HookSynth { FramesPerChunk = 882 };          // 20 ms per chunk
        var sink = new FakeSink { WriteDelayMs = 1 };
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(
            MaxChunkChars: 24, MinChunkChars: 1, LeadChunkChars: 24, PrimeMs: 10,
            InterChunkSilenceMs: 5));
        var log = new Recorder();
        session.Emitted += log.Add;

        const string Text = "One here. Two here. Three here. Four here.";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var token = cts.Token;

        // Dedicated threads, not Task.Run: seven pool threads that sleep and
        // block in Restart starve the pool the session's own worker and renderer
        // run on, and the first version of this test spent its three seconds
        // starting four utterances — which tests the thread pool's injection
        // rate, not the session.
        Task Loop(int seed, Action<Random> step)
        {
            var done = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                try
                {
                    var rng = new Random(seed);
                    while (!token.IsCancellationRequested)
                    {
                        step(rng);
                        if (rng.Next(4) == 0) Thread.Yield();
                        else Thread.Sleep(rng.Next(0, 3));
                    }
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.Start();
            return done.Task;
        }

        var workers = new[]
        {
            Loop(1, _ => session.Speak(Text, Voice)),
            Loop(2, _ => session.Restart(Text, Voice, timeoutMs: 2000)),
            Loop(3, r => session.Restart(Text, Voice, timeoutMs: 2000, startOffset: r.Next(0, Text.Length))),
            Loop(4, _ => session.Stop()),
            Loop(5, _ => session.Stop()),
            Loop(6, r => { if (r.Next(2) == 0) session.Pause(); else session.Resume(); }),
            Loop(7, r => { if (r.Next(3) == 0) session.Pause(); else session.Resume(); }),
        };
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        session.Resume();
        session.Stop();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SpeechState.Idle, session.State);

        // And the session still works: one clean utterance, start to finish.
        Assert.True(session.Speak("Last one.", Voice));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SpeechState.Idle, session.State);
        Assert.False(session.IsPaused);

        Assert.True(log.Errors.Length == 0, "errors: " + string.Join(" | ", log.Errors.Distinct()));
        Assert.Equal(SessionEventKind.Finished, log.Events.Last(e =>
            e.Kind is SessionEventKind.Finished or SessionEventKind.Stopped).Kind);
        AssertStateOrderIsCoherent(log);
        Assert.True(log.OfKind(SessionEventKind.Stopped).Count() > 10,
            $"the hammering barely stopped anything — it tested nothing ({log.OfKind(SessionEventKind.Stopped).Count()} stopped, " +
            $"{log.OfKind(SessionEventKind.Finished).Count()} finished)");
    }

    // ------------------------------------------------------- pool starvation

    [Fact]
    public void An_utterance_does_not_need_a_free_thread_pool_thread()
    {
        // CI saw one utterance of "A short sentence." against a no-op sink take
        // 971 ms (PipelineLatencyTests, 2026-10-04) while a bare Task.Run round
        // trip took 0.02 ms. The session ran its worker AND its renderer on the
        // pool, and both block for the whole utterance: the renderer was queued
        // from the worker — onto that thread's LOCAL queue — and the worker
        // then blocked waiting for it. Unless another pool thread happened to be
        // idle to steal it, it waited for the pool's starvation injection, which
        // is about a second. On a desktop it is the same second, between a
        // keypress and the first word, whenever the daemon is busy elsewhere.
        //
        // Driven here by occupying every pool thread, so the outcome does not
        // depend on what else the machine is doing. Synchronous on purpose: an
        // await would need the pool this test is starving.
        // Never disposed: blockers still queued when this test ends run later,
        // and must find it set rather than disposed.
        var hold = new ManualResetEventSlim(false);
        int running = 0;
        for (int i = 0; i < 256; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => { Interlocked.Increment(ref running); hold.Wait(); }, null);

        try
        {
            // Every existing pool thread is now parked on `hold`, with the rest
            // of the 256 queued behind them.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref running) < ThreadPool.ThreadCount && settle.ElapsedMilliseconds < 2000)
                Thread.Sleep(5);

            var synth = new FakeSynthesizer { FramesPerChunk = 4410 };
            var sink = new FakeSink();
            using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));
            var log = new Recorder();
            session.Emitted += log.Add;

            var t = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(session.Speak("A short sentence.", Voice));
            bool done = session.Completion.Wait(TimeSpan.FromSeconds(3));
            double ms = t.Elapsed.TotalMilliseconds;

            Assert.True(done && ms < 500,
                $"with the pool busy the utterance took {ms:F0} ms{(done ? "" : " and never finished")}; " +
                $"it waited for a pool thread it should not need");
            Assert.Single(log.OfKind(SessionEventKind.Finished));
        }
        finally
        {
            hold.Set();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }
}

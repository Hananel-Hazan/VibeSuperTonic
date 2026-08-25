using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Core.Session;

/// <summary>Knobs a session needs that are not per-utterance.</summary>
/// <param name="Pronunciations">Rules to apply, or null for none.</param>
/// <param name="CompiledPronunciations">Cached regexes matching the rules by index.</param>
/// <param name="MaxChunkChars">Chunker upper target.</param>
/// <param name="MinChunkChars">Chunker merge threshold.</param>
/// <param name="WriteBlockMs">
/// Audio handed to the sink per write. Bounds stop latency and sets how finely
/// the clock can move; 20 ms is what Phase 2 measured against.
/// </param>
/// <param name="PrimeMs">Playback-clock priming window — see <see cref="PlaybackClock"/>.</param>
/// <param name="StretchFactor">
/// Pitch-preserving time-stretch applied to each rendered chunk before it is
/// queued: greater than 1 plays faster. This is the half of the requested rate
/// the model could not supply — see <see cref="SpeechRate.Compute"/>. 1.0
/// disables the stage entirely.
/// </param>
/// <param name="VolumeScale">
/// Linear gain applied with the stretch. 1.0 disables it. From
/// <see cref="SpeechRate.VolumeScale"/>, never from dB directly.
/// </param>
/// <param name="LeadChunkChars">
/// Cap on the first chunk, which is what press-to-speech latency actually is:
/// nothing is heard until the first chunk has rendered, and the merge pass will
/// happily glue three short opening sentences into one. 64 characters is about
/// one sentence of prose. See <see cref="SentenceChunker.Chunk"/>.
/// </param>
/// <param name="InterChunkSilenceMs">
/// Silence written between one chunk and the next, so a run of sentences is
/// heard as speech rather than as one unbroken stream. Windows parity: the SAPI
/// engine writes exactly this gap through its site, from the same
/// <c>settings.json</c> key, and Linux ignored it until now.
///
/// <para><b>Zero here, 200 in the product.</b> This record's defaults are the
/// neutral values — <c>StretchFactor</c> 1.0 and <c>VolumeScale</c> 1.0 disable
/// their stages the same way — and the shipped number comes from settings, which
/// is what makes a direct <c>new SpeechSessionOptions()</c> mean "no processing"
/// rather than "whatever the product happens to default to this month".</para>
///
/// <para>It is not cosmetic: the gap is real audio in the stream, so it moves
/// every boundary after it. See <see cref="SpeechSession.Run"/> for why it is
/// counted into the stream position before the next chunk is planned.</para>
/// </param>
public sealed record SpeechSessionOptions(
    PronunciationsConfig? Pronunciations = null,
    IReadOnlyList<Regex?>? CompiledPronunciations = null,
    int MaxChunkChars = SentenceChunker.MaxChunkChars,
    int MinChunkChars = SentenceChunker.MinChunkChars,
    int WriteBlockMs = 20,
    int PrimeMs = PlaybackClock.DefaultPrimeMs,
    double StretchFactor = 1.0,
    float VolumeScale = 1.0f,
    int LeadChunkChars = 64,
    int InterChunkSilenceMs = 0);

/// <summary>
/// One utterance at a time, from text to speakers, with a running answer to
/// "which word is the user hearing right now?"
///
/// <para>Everything platform-specific is behind the two interfaces it is handed:
/// <see cref="ISynthesizer"/> and <see cref="IAudioSink"/>. So this class — the
/// state machine, the render-ahead pipeline, the stop sequence, the coordinate
/// bookkeeping — is portable, and is tested against fakes that can be made to
/// fail in ways real hardware only manages on someone else's machine.</para>
///
/// <para><b>Stop is three operations, not one</b> [R-8]. The pipeline renders
/// ahead, so at any moment there is typically an inference in flight that can
/// take seconds. Cancelling only the audio feed leaves the session busy and
/// queues the next request behind a dead utterance — and with one key doing both,
/// stop-then-speak is the *normal* two-press pattern, not a rare one. So stop
/// cancels the in-flight inference, flushes the sink, and discards what is
/// queued.</para>
///
/// <para><b>The write loop is the clock's poll loop.</b> <see cref="IAudioSink.Write"/>
/// blocks until the device has room, so it is already paced by playback at
/// exactly the rate the clock wants sampling. A separate timer would add jitter,
/// a thread and a race, and measure nothing better.</para>
/// </summary>
public sealed class SpeechSession : IDisposable
{
    private readonly ISynthesizer _synth;
    private readonly IAudioSink _sink;
    private readonly object _gate = new();

    // Swappable, because config can change under a running daemon: Phase 4b's
    // `reload` re-reads settings.json and pronunciations.json and has to reach
    // the session without restarting it. Reference assignment, so a reader
    // never sees a torn object — but see Options and the snapshot in Run for
    // the part that actually matters.
    private volatile SpeechSessionOptions _options;

    private CancellationTokenSource? _cts;
    private Task _worker = Task.CompletedTask;
    private SpeechState _state = SpeechState.Idle;
    private bool _disposed;

    // Pause is a modifier on Speaking rather than a state of its own: the hotkey
    // contract has four states and adding a fifth would change what the key
    // means, which is a product decision and not this class's to make. The
    // clock needs no special handling — with writing stopped the sink drains,
    // latency falls to zero and played converges on written, which is the
    // truth: everything written really has been heard.
    private readonly ManualResetEventSlim _unpaused = new(true);

    public SpeechSession(ISynthesizer synth, IAudioSink sink, SpeechSessionOptions? options = null)
    {
        _synth = synth ?? throw new ArgumentNullException(nameof(synth));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new SpeechSessionOptions();
    }

    /// <summary>
    /// The knobs that are not per-utterance — pronunciation rules, chunk sizes,
    /// the clock's priming window.
    ///
    /// <para>Settable so a running daemon can pick up an edited
    /// <c>pronunciations.json</c> or <c>settings.json</c> without restarting.
    /// A change takes effect on the <b>next</b> utterance, never the one in
    /// flight: <see cref="Run"/> snapshots this once and uses the snapshot
    /// throughout. That is not a limitation to work around — re-reading it
    /// mid-utterance would let the chunker and the offset maps disagree about
    /// the same text, which is the R-2/R-14 failure with a new cause.</para>
    /// </summary>
    public SpeechSessionOptions Options
    {
        get => _options;
        set => _options = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Every event the session produces. Raised on the worker thread, so a
    /// handler that blocks stalls playback — subscribers that do real work
    /// should hand off.
    /// </summary>
    public event Action<SessionEvent>? Emitted;

    public SpeechState State { get { lock (_gate) return _state; } }

    /// <summary>True while paused. Only meaningful in <see cref="SpeechState.Speaking"/>.</summary>
    public bool IsPaused => !_unpaused.IsSet;

    /// <summary>
    /// The text currently being spoken, or null when idle.
    ///
    /// <para>Exists for subscribers that arrive mid-utterance. The event stream
    /// only carries what happens after you connect, so a UI opened while speech
    /// is already running would have a position and no idea what it indexes
    /// into. That is Phase 6's "open the window mid-read and the highlight snaps
    /// to the correct word", and without this the answer is that it cannot.</para>
    /// </summary>
    public string? CurrentText { get; private set; }

    /// <summary>
    /// The most recent utterance, <em>retained after it ends</em> — unlike
    /// <see cref="CurrentText"/>, which is cleared on the terminal event.
    ///
    /// <para>Exists so a client can seek into a reading that has already
    /// finished, which is the ordinary Reader-tab gesture: the text is still on
    /// screen, the user clicks a word, and expects to hear it. Kept here rather
    /// than in the daemon because the session is what owns pipeline state — a
    /// second copy anywhere else is a thing that can disagree.</para>
    /// </summary>
    public string? LastText { get; private set; }

    /// <summary>
    /// The last boundary released, or null if none yet. With
    /// <see cref="CurrentText"/> this is a complete answer to "where are we",
    /// which is what a late subscriber needs and what no stream of future events
    /// can supply.
    /// </summary>
    public BoundaryEvent? LastBoundary { get; private set; }

    /// <summary>
    /// Start speaking <paramref name="text"/>, optionally from part way in.
    /// </summary>
    /// <param name="startOffset">
    /// Where in <paramref name="text"/> to begin. Everything before it is not
    /// rendered, but it remains part of <see cref="CurrentText"/> and every
    /// reported offset stays in <em>whole-text</em> coordinates — see
    /// <see cref="Run"/>. Use <see cref="BoundaryPlanner.SnapToWordStart"/> to
    /// turn a click into one of these.
    /// </param>
    /// <param name="notice">
    /// Something the user should know about this utterance — a truncated
    /// selection, a selection that never changed. Carried out on the
    /// <see cref="SpeechState.Preparing"/> event so subscribers see it; see
    /// <see cref="SessionEvent.Notice"/>. Null when there is nothing to say.
    /// </param>
    /// <returns>
    /// False if the session was not idle. The caller decides what that means —
    /// <see cref="ToggleGate"/> turns a press during speech into a stop, so this
    /// returning false is a race (two clients, or a press that beat a state
    /// update), not a normal path.
    /// </returns>
    public bool Speak(string text, SynthesisOptions synthesisOptions, int startOffset = 0,
        string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(synthesisOptions);
        if (startOffset < 0 || startOffset > text.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset));

        CancellationTokenSource cts;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != SpeechState.Idle) return false;

            _cts = cts = new CancellationTokenSource();
            _state = SpeechState.Preparing;
        }

        // Set BEFORE the event, not after. A subscriber's natural reaction to
        // Preparing is to ask what is being spoken, and with the assignment
        // after the emit that question was answered with the PREVIOUS
        // utterance's text — or null on the first one. The event now carries the
        // text itself, which is the real fix, but the snapshot has to agree with
        // it or a client that uses both sees two different answers for one
        // utterance.
        CurrentText = text;
        LastText = text;
        LastBoundary = null;

        Emit(SessionEvent.Preparing(text, startOffset, notice));

        _unpaused.Set();
        _worker = Task.Run(() => Run(text, startOffset, synthesisOptions, cts));
        return true;
    }

    /// <summary>
    /// Stop, from any state. Returns immediately; the worker completes the
    /// teardown and the session reaches <see cref="SpeechState.Idle"/> shortly
    /// after. Safe to call from any thread, and safe to call when already idle.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_state is SpeechState.Idle or SpeechState.Stopping) return;
            cts = _cts;
            _state = SpeechState.Stopping;
        }
        Emit(SessionEvent.Of(SpeechState.Stopping));

        // Order matters. Cancel first so the renderer stops producing work that
        // is about to be thrown away, then release the writer — which may be
        // blocked inside the sink and will only notice between blocks.
        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* raced with completion */ }
        _unpaused.Set();                 // a paused writer must wake to see the flush
        try { _sink.RequestFlush(); } catch { /* the worker's teardown covers it */ }
    }

    /// <summary>Stop feeding the device. Buffered audio plays out, then silence.</summary>
    public void Pause()
    {
        lock (_gate) { if (_state != SpeechState.Speaking) return; }
        _unpaused.Reset();
    }

    public void Resume() => _unpaused.Set();

    /// <summary>
    /// Stop whatever is playing and immediately speak <paramref name="text"/>
    /// instead — one operation, so a caller cannot be left having stopped the
    /// old utterance and failed to start the new one.
    ///
    /// <para>This is what a "read the selection" key needs and what
    /// <see cref="Speak"/> cannot give it: <c>Speak</c> refuses unless the
    /// session is Idle, so a caller would have to stop, poll for Idle, and then
    /// start — with a window in the middle where another client can take the
    /// session. Here the wait is bounded and the start happens under the same
    /// lock discipline as any other <c>Speak</c>.</para>
    ///
    /// <para>Blocking, and deliberately so: it is the caller's request that must
    /// not be acknowledged before the swap has happened. A stop is ~40 ms, well
    /// inside the acknowledgement budget, and the daemon serves each client on
    /// its own task so nothing else is held up.</para>
    /// </summary>
    /// <returns>False if the previous utterance did not release in time.</returns>
    public bool Restart(string text, SynthesisOptions synthesisOptions, int timeoutMs = 3000,
        int startOffset = 0, string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(synthesisOptions);

        Task worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _worker;
        }

        Stop();                                   // returns immediately if already Idle
        try { worker.Wait(timeoutMs); } catch { /* faulted or cancelled: either way it is done */ }

        return Speak(text, synthesisOptions, startOffset, notice);
    }

    /// <summary>Wait for the current utterance to finish or stop. For tests and shutdown.</summary>
    public Task Completion => _worker;

    // ------------------------------------------------------------------ worker

    /// <param name="startOffset">
    /// Where in <paramref name="text"/> to start rendering. The text before it is
    /// never handed to the synthesizer, but it is still counted: the offset is
    /// passed to <see cref="BoundaryPlanner.PlanChunk"/> as <c>sourceBase</c>, so
    /// every boundary is reported against the whole text rather than against the
    /// fragment being spoken.
    ///
    /// <para>That is the entire trick, and it is why seeking is a few lines
    /// rather than a coordinate-space rewrite. Speaking the substring and letting
    /// clients add the offset back would put a second coordinate space on the
    /// wire — which is R-2 and R-14, both of which were exactly this: an index
    /// from one space added to an offset from another. One space, resolved where
    /// the map already is, and a highlight cannot drift.</para>
    ///
    /// <para>Pronunciation rules see only the fragment, so a rule that would have
    /// matched across the cut does not fire. Inherent to starting mid-text, and
    /// the alternative — rewriting the whole text and then discarding the front —
    /// would make the offsets depend on text nobody is going to hear.</para>
    /// </param>
    private void Run(string text, int startOffset, SynthesisOptions synthesisOptions,
        CancellationTokenSource cts)
    {
        var token = cts.Token;

        // Snapshot once. Options is swappable (reload), and every read below
        // has to see the same generation of config: the pipeline map, the chunk
        // boundaries and the offset maps are all computed from it and are only
        // consistent with each other.
        var options = _options;

        var scheduler = new BoundaryScheduler();
        var clock = new PlaybackClock(_sink.SampleRate, options.PrimeMs);
        bool started = false;
        bool cancelled = false;

        // AN UTTERANCE NEVER BEGINS UNDER A FLUSH REQUEST. Reported from daily
        // use twice: press the key, the text appears in the window, the highlight
        // never moves, no sound — and the press after it works.
        //
        // Stop() sets the sink's flush flag from the stopping thread, and only
        // the writer consumes it. The teardown below clears one the writer never
        // saw, which covers a stop that lands during Preparing. It does not cover
        // a stop that lands as the utterance ENDS: Stop() reads the state, finds
        // Speaking, cancels — and sets the flag afterwards, by which time a
        // worker in its last moments has already run that teardown and gone. The
        // flag then belongs to nobody, the next utterance's first write trips it,
        // flushes and throws, and that utterance dies silently. Tripping it is
        // also what clears it, which is why the press after always works.
        //
        // On a desktop that window is not exotic — it is a press landing as the
        // reading ends, which is exactly when people press: to cut off the tail,
        // or to read something new.
        //
        // Clearing it here, on the writer thread, at the start of the one
        // operation that must not inherit it, makes the invariant independent of
        // how any two threads happened to interleave. Flush is the right verb
        // rather than a new one: dropping whatever the device still holds from a
        // previous utterance is also correct, and after a stop it is required.
        try { _sink.Flush(); } catch { /* not open yet, or going away; either way there is nothing held */ }

        // Bounded at one so the renderer stays exactly one chunk ahead. Deeper
        // buys nothing — the device is the bottleneck, and every extra rendered
        // chunk is work to throw away on stop.
        var rendered = new BlockingCollection<(int Index, short[] Pcm)>(1);
        Task? renderTask = null;

        try
        {
            // The composition the Windows engine uses, in the one order that is
            // correct: prepare once, chunk the result, then chain each chunk's
            // map onto the pipeline's. Reversing it is R-2; skipping the chunk
            // map is R-14.
            string spoken = SynthTextPipeline.Prepare(
                startOffset == 0 ? text : text[startOffset..],
                options.Pronunciations, options.CompiledPronunciations, out var pipelineMap);

            // Whitespace-only chunks are dropped rather than rendered. A model
            // asked to speak "" still produces audio — a breath, or a click —
            // and an empty selection is meant to be a no-op, not a noise. The
            // chunker can also emit one mid-text from a run of separators.
            var chunks = SentenceChunker
                .ChunkWithOffsets(spoken, options.MaxChunkChars, options.MinChunkChars,
                    options.LeadChunkChars)
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .ToList();

            if (chunks.Count == 0)
            {
                Finish(SessionEventKind.Finished, 0);
                return;
            }

            // The renderer's failure has to reach the consumer, which is on
            // another thread and blocked waiting for audio that will never
            // arrive. Stashing it and rethrowing below is the only path: an
            // exception thrown here dies with the task, and the session would
            // report a clean Finished for an utterance nobody heard.
            Exception? renderError = null;

            renderTask = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        short[] pcm = _synth.Synthesize(chunks[i].Text, synthesisOptions, token);

                        // DSP here, in the renderer, and NOT on the write path.
                        // Two reasons, both load-bearing:
                        //
                        //  - It is CPU work, and the consumer loop is paced by
                        //    the device; doing it there would stall writes and
                        //    show up as an underrun rather than as slowness.
                        //  - The stretch CHANGES THE FRAME COUNT, and the
                        //    consumer plans word boundaries from pcm.Length.
                        //    Stretching before the queue means boundaries are
                        //    planned against the audio that will actually be
                        //    heard, so nothing needs retiming and the playback
                        //    clock stays the only source of position. Applying
                        //    it after planning would desynchronise every
                        //    highlight by the stretch ratio.
                        // The rate the SYNTHESIZER rendered at, which is what
                        // this buffer is — not the sink's, even though the two
                        // agree by the time anything is written. They are
                        // separately owned: the sink re-tunes to follow the
                        // engine between utterances, and reading it here would
                        // make the DSP depend on that having already happened.
                        pcm = TimeStretch.Stretch(pcm, options.StretchFactor, _synth.SampleRate);
                        SpeechRate.ApplyGain(pcm, options.VolumeScale);

                        rendered.Add((i, pcm), token);
                    }
                }
                catch (OperationCanceledException) { /* stop */ }
                catch (InvalidOperationException) when (rendered.IsAddingCompleted)
                {
                    // Add raced the consumer's teardown. Narrowly guarded: an
                    // unguarded catch here swallowed real synthesizer failures,
                    // which arrive as InvalidOperationException as often as not.
                }
                catch (Exception ex) { renderError = ex; }
                finally { rendered.CompleteAdding(); }
            }, token);

            long streamFrame = 0;
            int blockFrames = Math.Max(1, _sink.SampleRate * options.WriteBlockMs / 1000);
            var planned = new List<BoundaryEvent>();

            // Allocated once and never written to. Reused for every gap: it is
            // read-only to the sink, and a fresh array per chunk boundary would
            // be ~18 KB of garbage per sentence at the shipped 200 ms for no
            // reason at all.
            short[]? gap = options.InterChunkSilenceMs > 0
                ? new short[Math.Max(1, _sink.SampleRate * options.InterChunkSilenceMs / 1000)]
                : null;

            // Chunk audio and inter-chunk silence both go through here, which is
            // the point: the gap is then paced by the device, interrupted by a
            // stop, held by a pause and counted by the playback clock exactly as
            // speech is. Silence written any other way would be audio the clock
            // never saw, and every boundary after it would fire early.
            void WriteFrames(short[] buffer)
            {
                for (int offset = 0; offset < buffer.Length; offset += blockFrames)
                {
                    token.ThrowIfCancellationRequested();
                    _unpaused.Wait(token);

                    int count = Math.Min(blockFrames, buffer.Length - offset);
                    long before = _sink.WrittenFrames;
                    _sink.Write(buffer.AsSpan(offset, count));
                    clock.AddWritten(_sink.WrittenFrames - before);

                    if (!started)
                    {
                        // First audio accepted by the device. Not "first audio
                        // heard" — that is the clock's business — but it is the
                        // moment the session stops being able to fail silently,
                        // and the moment the tray should stop blinking.
                        started = true;
                        SetStateLocked(SpeechState.Speaking);
                        Emit(SessionEvent.Of(SessionEventKind.Started));
                    }

                    if (clock.Update(_sink.LatencyUsec))
                        Release(scheduler, clock);
                }
            }

            foreach (var (index, pcm) in rendered.GetConsumingEnumerable(token))
            {
                planned.Clear();
                BoundaryPlanner.PlanChunk(
                    planned, chunks[index].Text,
                    TextOffsetMap.Chain(chunks[index].Map, pipelineMap),
                    sourceBase: startOffset, chunkFrames: pcm.Length, streamStartFrame: streamFrame);
                scheduler.AddRange(planned);
                streamFrame += pcm.Length;

                WriteFrames(pcm);

                // The gap the Windows engine writes between chunks, from the same
                // settings key, so one settings.json paces both platforms alike.
                //
                // Counted into streamFrame as well as written, and that is the
                // half that is easy to omit: the NEXT chunk is planned against
                // streamFrame, so a gap the planner did not know about would
                // schedule every subsequent boundary early — by 200 ms after the
                // first sentence and by a growing multiple of that after the
                // rest. It would sound perfect and highlight the wrong word,
                // which is trap 12 exactly.
                //
                // Not after the last chunk: that is trailing silence before the
                // drain, which delays Finished to no end. Windows guards the same
                // way — it writes the gap only when another chunk follows.
                if (gap is not null && index + 1 < chunks.Count)
                {
                    streamFrame += gap.Length;
                    WriteFrames(gap);
                }
            }

            // Before drain, not after: draining blocks until the device has
            // played everything, and there is nothing to play if rendering died.
            if (renderError is not null) throw renderError;

            _sink.Drain();
            clock.Drained();
            Release(scheduler, clock);
            Finish(SessionEventKind.Finished, (double)clock.WrittenFrames / _sink.SampleRate);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;

            // A cancellation with no stop behind it is the defect above coming
            // back: Stop() is the only thing that cancels this token or requests
            // a flush, and it sets Stopping before doing either. Anything else
            // that ends an utterance early is a silent hotkey, so it says so on
            // the event stream — which the daemon logs — rather than presenting
            // as a stop nobody asked for.
            if (State != SpeechState.Stopping)
                Emit(SessionEvent.Error(
                    "the utterance was interrupted without a stop request — " +
                    "this is the stale-flush defect, and it should not be reachable"));
        }
        catch (Exception ex)
        {
            Emit(SessionEvent.Error($"{ex.GetType().Name}: {ex.Message}"));
            cancelled = true;
        }
        finally
        {
            // Drain the renderer before releasing anything it might still be
            // writing into. Disposing the collection with a producer mid-Add
            // throws inside the producer and loses the real reason we stopped.
            rendered.CompleteAdding();
            try { renderTask?.Wait(TimeSpan.FromSeconds(10)); } catch { /* cancelled */ }
            while (rendered.TryTake(out _)) { }   // the third part of stop: discard queued PCM
            rendered.Dispose();

            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
            }
            cts.Dispose();

            if (cancelled)
            {
                // Clear a stop request the writer never got to see.
                //
                // Stop() sets the sink's flush flag from the stopping thread,
                // and only the writer consumes it — between blocks, inside
                // Write. But a stop arriving during Preparing unwinds through
                // the renderer and the cancellation token WITHOUT EVER ENTERING
                // Write, so nothing consumes the flag and it survives this
                // utterance. The next utterance's very first write then trips
                // it, flushes, and throws — so that utterance dies silently,
                // and the press after it works, because tripping the flag is
                // also what clears it.
                //
                // The symptom is a hotkey that needs "a couple of presses" to
                // restart after every stop, which reads as the key being
                // unreliable rather than as a bug with a cause. Reported from
                // real use; no test could have found it, because the fakes are
                // only ever driven through the path where Write does the
                // clearing for you.
                //
                // Safe here: this is the writer thread and the write loop has
                // finished, which is the whole reason RequestFlush exists as a
                // separate member (see PulseAudioSink's threading remarks).
                try { _sink.Flush(); } catch { /* the device is going away */ }

                Finish(SessionEventKind.Stopped, null);
            }
        }
    }

    private void Release(BoundaryScheduler scheduler, PlaybackClock clock)
    {
        foreach (var e in scheduler.Advance(clock.PlayedFrames))
        {
            LastBoundary = e;
            Emit(new SessionEvent
            {
                Kind = e.Kind == BoundaryKind.Word
                    ? SessionEventKind.WordBoundary
                    : SessionEventKind.SentenceBoundary,
                SourceOffset = e.SourceOffset,
                SourceLength = e.SourceLength,
                AudioSeconds = (double)e.Frame / clock.SampleRate,
            });
        }
    }

    private void Finish(SessionEventKind kind, double? audioSeconds)
    {
        Emit(new SessionEvent { Kind = kind, AudioSeconds = audioSeconds });

        // Cleared after the terminal event, not before: a subscriber reacting to
        // Finished may want to know what finished.
        CurrentText = null;
        LastBoundary = null;

        SetStateLocked(SpeechState.Idle);
    }

    /// <summary>
    /// Transition and announce it, with the announcement made <em>outside</em>
    /// the lock. Subscribers run arbitrary code — in the daemon, a socket write
    /// to a client that may have wandered off — and holding the state lock
    /// across that would let one slow subscriber block every other thread that
    /// merely wanted to read <see cref="State"/>.
    /// </summary>
    private void SetStateLocked(SpeechState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        Emit(SessionEvent.Of(state));
    }

    private void Emit(SessionEvent e)
    {
        // A subscriber that throws must not take the audio thread with it. The
        // daemon's subscriber list is a socket write, and a client that
        // disconnects mid-utterance is routine rather than exceptional.
        try { Emitted?.Invoke(e); } catch { /* swallow */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
        try { _worker.Wait(TimeSpan.FromSeconds(10)); } catch { /* stopping */ }
        _unpaused.Dispose();
    }
}

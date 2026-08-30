using System.Diagnostics;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// How long the pipeline takes when the model takes no time at all.
///
/// <para><b>The insight that makes this testable is separating pipeline latency
/// from model latency</b> (docs/TESTING-PLAN.md, "fast"). The model's cost needs
/// hardware, models on disk and an idle machine, so it belongs in a benchmark a
/// human runs — <c>vst-ctl benchmark</c> is that. What creeps is everything
/// <em>around</em> it: a chunker that went quadratic, a render that stopped
/// streaming, a per-utterance cost that arrived with a feature. Handed a
/// synthesizer that returns instantly, "time to the first sample" is a pure-code
/// number, and a regression in it is one the model would otherwise hide.</para>
///
/// <para><b>Two of these do not look at the clock at all</b>, and they are the
/// sensitive ones. "How many chunks were rendered before the first sample" is a
/// property of the pipeline's shape rather than of the machine it runs on, so it
/// cannot flake and cannot be papered over by a fast box. The timed checks are
/// the catastrophe net behind them, with budgets wide enough that a loaded CI
/// runner passes: measured 2026-08-30, a 27 KB document reaches its first sample
/// in <b>1 ms</b> against a 250 ms ceiling.</para>
///
/// <para>The number this is protecting, for context: press-to-speech is 150 ms
/// in the port plan and ~750 ms in practice, of which ~600 is one inference. The
/// pipeline's share of that is what is measured here, and it should stay
/// invisible.</para>
/// </summary>
public class PipelineLatencyTests
{
    private static readonly SupertonicOptions Voice = new("M1", "en");

    /// <summary>
    /// The catastrophe net, not a target. Nothing here has ever measured above
    /// 1 ms; this is set to catch a pipeline that has started doing seconds of
    /// work, on a machine that may be doing five other things.
    /// </summary>
    private const int CeilingMs = 250;

    /// <summary>
    /// How much slower the first sample may be for a 27 KB document than for one
    /// sentence. THIS is the check that catches work proportional to the
    /// document: a uniformly slow machine inflates both measurements and the
    /// difference between them survives.
    ///
    /// <para>25 rather than the 50 it was written with, and the sabotage run is
    /// why. A deliberate quarter-millisecond per chunk — 46 ms across a 184-chunk
    /// document, invisible on one sentence — passed at 50 and is caught at 25.
    /// The observed difference is 1 ms, so the headroom is still 25x; a budget
    /// set for comfort rather than for what it must catch is decoration.</para>
    /// </summary>
    private const int GrowthBudgetMs = 25;

    private static string Document(int sentences) =>
        string.Join(" ", Enumerable.Range(0, sentences)
            .Select(i => $"This is sentence number {i} of a document that goes on for a while."));

    /// <summary>An audio device that records when the first sample arrived, and nothing else.</summary>
    private sealed class StampingSink : IAudioSink
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _written;

        public Func<int>? RenderedSoFar { get; init; }

        /// <summary>Milliseconds from construction to the first sample, or -1 if none came.</summary>
        public long FirstWriteMs { get; private set; } = -1;

        /// <summary>How many chunks the synthesizer had been asked for by then.</summary>
        public int RenderedAtFirstWrite { get; private set; } = -1;

        public int SampleRate => 44100;
        public long WrittenFrames => Interlocked.Read(ref _written);
        public long LatencyUsec => 0;

        public void Write(ReadOnlySpan<short> pcm)
        {
            if (FirstWriteMs < 0)
            {
                FirstWriteMs = _clock.ElapsedMilliseconds;
                RenderedAtFirstWrite = RenderedSoFar?.Invoke() ?? -1;
            }
            Interlocked.Add(ref _written, pcm.Length);
        }

        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() { }
    }

    private sealed record Run(long FirstSampleMs, int RenderedAtFirstSample, int ChunksInAll, string FirstChunk);

    private static Run Speak(string text)
    {
        // FramesPerChunk is a tenth of a second rather than a whole one: this is
        // measuring the pipeline, and a synthesizer that returns instantly is
        // the entire point.
        var synth = new FakeSynthesizer { FramesPerChunk = 4410 };
        var sink = new StampingSink { RenderedSoFar = () => synth.Rendered.Count };

        // The SHIPPED options, PrimeMs included — a test that tuned them would be
        // measuring a configuration nobody runs.
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions());

        Assert.True(session.Speak(text, Voice));
        session.Completion.Wait();

        Assert.True(sink.WrittenFrames > 0, "the utterance produced no audio at all");
        return new Run(sink.FirstWriteMs, sink.RenderedAtFirstWrite, synth.Rendered.Count, synth.Rendered[0]);
    }

    /// <summary>Median of five, after a warm-up run that pays for the JIT.</summary>
    private static long MedianFirstSampleMs(string text)
    {
        Speak(text);
        return Enumerable.Range(0, 5).Select(_ => Speak(text).FirstSampleMs).Order().ElementAt(2);
    }

    // ------------------------------------------------- the shape, not the clock

    /// <summary>
    /// A long document must not be rendered before it starts speaking. Render-ahead
    /// is bounded, so the first sample arrives after two or three chunks whether
    /// the document is one sentence or four hundred — measured 2026-08-30: 1 of 1,
    /// 3 of 4, 3 of 34, 2 of 184.
    /// </summary>
    [Fact]
    public void The_first_sample_does_not_wait_for_the_document()
    {
        var run = Speak(Document(400));

        Assert.True(run.ChunksInAll > 100, $"the document should be many chunks, was {run.ChunksInAll}");
        Assert.InRange(run.RenderedAtFirstSample, 1, 4);
    }

    /// <summary>
    /// And the chunk it waited for is the LEAD chunk — the opening SENTENCE,
    /// alone, rather than the merged ~200-character chunk the chunker would
    /// otherwise build for even pacing. That merge is what put 1.45 s between
    /// the key press and the first word on the Mint box, against a 150 ms
    /// budget. LeadChunkTests proves the chunker can do it; this proves the
    /// session asks for it, which is a different claim and the one a user hears.
    ///
    /// <para>The cap frees a sentence, it does not cut one — a 65-character
    /// opening sentence stays whole under a 64-character cap, which is why this
    /// asserts the sentence rather than the number.</para>
    /// </summary>
    [Fact]
    public void The_first_chunk_is_the_opening_sentence_however_long_the_document_is()
    {
        var run = Speak(Document(400));

        Assert.Equal("This is sentence number 0 of a document that goes on for a while.", run.FirstChunk);
        Assert.True(run.FirstChunk.Length < new SpeechSessionOptions().MaxChunkChars,
            "the first chunk is a merged one, which is the 1.45 s regression");
    }

    // ------------------------------------------------------------- the clock

    /// <summary>
    /// The check a quadratic chunker fails. A machine that is uniformly slow
    /// inflates both numbers and the difference between them survives.
    /// </summary>
    [Fact]
    public void Time_to_the_first_sample_stays_flat_as_the_document_grows()
    {
        long one = MedianFirstSampleMs(Document(1));
        long many = MedianFirstSampleMs(Document(400));

        Assert.True(many <= one + GrowthBudgetMs,
            $"one sentence reached its first sample in {one} ms and a 27 KB document in {many} ms. "
            + $"The budget for that growth is {GrowthBudgetMs} ms; work proportional to the whole "
            + "document is happening before the first word.");
    }

    /// <summary>The catastrophe net: absolute, wide, and it has never been near.</summary>
    [Fact]
    public void The_pipeline_itself_costs_nothing_worth_measuring()
    {
        Assert.InRange(MedianFirstSampleMs(Document(400)), 0, CeilingMs);
    }

    /// <summary>
    /// Twenty utterances back to back, because per-utterance cost is the other
    /// shape this creeps in — a settings file re-read, a device reopened, a
    /// pronunciation table recompiled for every sentence. Measured 2026-08-30:
    /// twenty complete utterances end to end in under a millisecond.
    /// </summary>
    [Fact]
    public void An_utterance_does_not_cost_anything_to_start()
    {
        Speak("A short sentence.");

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) Speak("A short sentence.");

        Assert.InRange(clock.ElapsedMilliseconds, 0, CeilingMs);
    }
}

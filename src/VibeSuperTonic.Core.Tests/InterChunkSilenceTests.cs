using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The gap between chunks — Windows parity, and the last <c>settings.json</c> key
/// the Linux daemon ignored.
///
/// <para><b>Why this has tests at all, for what is arguably 200 ms of nothing.</b>
/// The gap is real audio written into the stream, so it moves the position of
/// every boundary after it. Get the write right and the accounting wrong and the
/// speech is perfect while the highlight drifts a fifth of a second further
/// behind after every sentence. That is trap 12 of the port plan stated as a
/// rule: a wrong offset sounds exactly like a right one, and every offset defect
/// in this repository was found by reading numbers rather than by
/// listening.</para>
///
/// <para>So the assertions below are about frames and scheduled seconds, never
/// about audio.</para>
/// </summary>
public class InterChunkSilenceTests
{
    private static readonly SynthesisOptions Voice = new("M1", "en");

    private const int Rate = 44100;
    private const int FramesPerChunk = Rate;                 // one second per chunk

    /// <summary>
    /// Chunk small and merge nothing, so a few short sentences really do become a
    /// few chunks. With the shipped thresholds (200/100) this text is one chunk
    /// and there is no gap to measure.
    /// </summary>
    private static SpeechSessionOptions Options(int silenceMs) => new(
        MaxChunkChars: 24, MinChunkChars: 1, LeadChunkChars: 24,
        PrimeMs: 10, InterChunkSilenceMs: silenceMs);

    private const string ThreeSentences = "First one. Second one. Third one.";

    private sealed class Recorder
    {
        private readonly List<SessionEvent> _events = new();
        public IReadOnlyList<SessionEvent> Events { get { lock (_events) return _events.ToList(); } }
        public void Add(SessionEvent e) { lock (_events) _events.Add(e); }

        /// <summary>Scheduled position of every boundary, in seconds.</summary>
        public double[] BoundarySeconds => Events
            .Where(e => e.Kind is SessionEventKind.WordBoundary or SessionEventKind.SentenceBoundary)
            .Select(e => e.AudioSeconds!.Value)
            .ToArray();
    }

    private static async Task<(FakeSynthesizer Synth, FakeSink Sink, Recorder Log)> SpeakAsync(int silenceMs)
    {
        var synth = new FakeSynthesizer(Rate) { FramesPerChunk = FramesPerChunk };
        var sink = new FakeSink(Rate);
        using var session = new SpeechSession(synth, sink, Options(silenceMs));
        var log = new Recorder();
        session.Emitted += log.Add;

        Assert.True(session.Speak(ThreeSentences, Voice));
        await session.Completion;

        return (synth, sink, log);
    }

    [Fact]
    public async Task The_gap_is_written_between_chunks_and_not_after_the_last()
    {
        var (synth, sink, _) = await SpeakAsync(silenceMs: 500);

        int chunks = synth.Rendered.Count;
        Assert.True(chunks >= 2, $"the fixture needs more than one chunk; got {chunks}");

        // n chunks means n-1 gaps. A trailing gap would be silence held against
        // the drain, delaying Finished for nothing — Windows guards the same way,
        // writing the gap only when another chunk follows.
        long expected = (long)chunks * FramesPerChunk + (chunks - 1) * (Rate / 2);
        Assert.Equal(expected, sink.WrittenFrames);
    }

    [Fact]
    public async Task No_gap_is_written_when_the_setting_is_zero()
    {
        var (synth, sink, _) = await SpeakAsync(silenceMs: 0);

        Assert.Equal((long)synth.Rendered.Count * FramesPerChunk, sink.WrittenFrames);
    }

    [Fact]
    public async Task Every_boundary_after_a_gap_is_scheduled_later_by_exactly_the_gap()
    {
        // The assertion that matters. Same text, same chunking, same audio per
        // chunk — the only difference is the gap, so any boundary in chunk k must
        // move by exactly k gaps and no other number.
        var (synthA, _, without) = await SpeakAsync(silenceMs: 0);
        var (synthB, _, with) = await SpeakAsync(silenceMs: 500);

        Assert.Equal(synthA.Rendered, synthB.Rendered);          // identical chunking

        double[] a = without.BoundarySeconds;
        double[] b = with.BoundarySeconds;
        Assert.Equal(a.Length, b.Length);
        Assert.NotEmpty(a);

        for (int i = 0; i < a.Length; i++)
        {
            // Which chunk this boundary is in, from its ungapped position: chunk
            // k occupies [k, k+1) seconds when every chunk is one second long.
            int chunk = (int)Math.Floor(a[i] / (FramesPerChunk / (double)Rate) + 1e-9);
            double shift = chunk * 0.5;

            Assert.Equal(a[i] + shift, b[i], precision: 6);
        }
    }

    [Fact]
    public async Task The_first_chunks_boundaries_do_not_move()
    {
        // Stated separately because it is the half a wrong implementation gets
        // right by accident: putting the gap BEFORE each chunk rather than after
        // it would shift chunk 0 too, and delay first audio by the gap on every
        // single utterance.
        var (_, _, without) = await SpeakAsync(silenceMs: 0);
        var (_, _, with) = await SpeakAsync(silenceMs: 500);

        double first = without.BoundarySeconds[0];
        Assert.Equal(first, with.BoundarySeconds[0], precision: 6);
        Assert.Equal(0.0, first, precision: 6);
    }

    [Fact]
    public async Task The_source_offsets_are_untouched_by_the_gap()
    {
        // The gap is a timing change and must not be a coordinate change. Offsets
        // are in the caller's text, which has no silence in it.
        var (_, _, without) = await SpeakAsync(silenceMs: 0);
        var (_, _, with) = await SpeakAsync(silenceMs: 500);

        int[] Offsets(Recorder r) => r.Events
            .Where(e => e.Kind == SessionEventKind.WordBoundary)
            .Select(e => e.SourceOffset!.Value)
            .ToArray();

        Assert.Equal(Offsets(without), Offsets(with));
    }

    [Fact]
    public async Task A_single_chunk_utterance_writes_no_gap_at_all()
    {
        var synth = new FakeSynthesizer(Rate) { FramesPerChunk = FramesPerChunk };
        var sink = new FakeSink(Rate);
        using var session = new SpeechSession(synth, sink, Options(silenceMs: 500));

        Assert.True(session.Speak("Alone.", Voice));
        await session.Completion;

        Assert.Single(synth.Rendered);
        Assert.Equal(FramesPerChunk, sink.WrittenFrames);
    }
}

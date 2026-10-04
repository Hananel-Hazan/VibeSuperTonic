using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Telemetry;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The daemon's account of its own speech — the Linux counterpart of the Windows
/// Monitor tab.
///
/// <para>Each of these pins a rule whose breakage is silent: an RTF of zero that
/// reads as "infinitely fast", a latency measured from the wrong moment, a
/// snippet that leaks the document into a bug report, an underrun counter that
/// never moves. The tracker takes its clock, so none of them waits for real time.</para>
/// </summary>
public sealed class DiagnosticsTests
{
    private sealed class Clock
    {
        public long Ms;
        public DateTime Utc = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (DiagnosticsTracker Tracker, Clock Clock) Build()
    {
        var clock = new Clock();
        return (new DiagnosticsTracker(() => clock.Ms, () => clock.Utc), clock);
    }

    // ------------------------------------------------------------------ RTF

    [Fact]
    public void A_render_is_wall_time_over_audio_time()
    {
        var (t, _) = Build();
        t.RecordRender(wallSeconds: 0.3, audioSeconds: 1.0);

        var s = t.Snapshot(false);
        Assert.Equal(0.3, s.LastRtf, 6);
        Assert.Equal(0.3, s.RollingRtf, 6);
    }

    /// <summary>
    /// A zero in the window would read as infinitely fast, and NaN would poison
    /// every average after it. These are not measurements, so they are not kept.
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(-0.1, 1.0)]
    [InlineData(-1.0, -2.0)]   // two negatives make a plausible positive ratio; neither is a time
    [InlineData(0.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void A_render_that_is_not_a_measurement_changes_nothing(double wall, double audio)
    {
        var (t, _) = Build();
        t.RecordRender(0.7, 1.0);

        t.RecordRender(wall, audio);

        var s = t.Snapshot(false);
        Assert.Equal(0.7, s.LastRtf, 6);
        Assert.Equal(0.7, s.RollingRtf, 6);
    }

    [Fact]
    public void Nothing_rendered_is_reported_as_zero_and_not_as_fast()
    {
        var (t, _) = Build();
        var s = t.Snapshot(false);

        Assert.Equal(0, s.LastRtf);
        Assert.Equal(0, s.RollingRtf);
        Assert.Contains("nothing rendered yet",
            DiagnosticsFormat.Rows(s, DateTime.UtcNow).Single(r => r.Label == "Last render").Value);
    }

    [Fact]
    public void The_rolling_figure_forgets_renders_older_than_the_window()
    {
        var (t, _) = Build();

        // One slow cold render, then a full window of fast ones: the slow one
        // must fall out, or "rolling" is "since start".
        t.RecordRender(5.0, 1.0);
        for (int i = 0; i < DiagnosticsTracker.RollingWindow; i++) t.RecordRender(0.2, 1.0);

        var s = t.Snapshot(false);
        Assert.Equal(0.2, s.RollingRtf, 6);
        Assert.Equal(0.2, s.LastRtf, 6);
    }

    // -------------------------------------------------------------- latency

    [Fact]
    public void Pipeline_latency_runs_from_the_request_to_the_first_audio()
    {
        var (t, clock) = Build();

        clock.Ms = 1_000;
        t.UtteranceStarting("Hello there.");
        clock.Ms = 1_412;
        t.FirstAudio();

        Assert.Equal(412, t.Snapshot(false).FirstByteLatencyMs, 6);
    }

    /// <summary>
    /// Only the first audio of an utterance is the latency. A second event must not
    /// restate it as the length of the whole reading.
    /// </summary>
    [Fact]
    public void Only_the_first_audio_counts()
    {
        var (t, clock) = Build();

        clock.Ms = 0; t.UtteranceStarting("x");
        clock.Ms = 100; t.FirstAudio();
        clock.Ms = 9_000; t.FirstAudio();

        Assert.Equal(100, t.Snapshot(false).FirstByteLatencyMs, 6);
    }

    [Fact]
    public void A_new_utterance_starts_the_latency_again_and_does_not_report_the_last_ones()
    {
        var (t, clock) = Build();

        clock.Ms = 0; t.UtteranceStarting("one");
        clock.Ms = 100; t.FirstAudio();
        t.UtteranceEnded();

        clock.Ms = 5_000; t.UtteranceStarting("two");

        // Preparing, nothing heard yet: not 100 ms, and not 5000.
        Assert.Equal(0, t.Snapshot(false).FirstByteLatencyMs);
        Assert.Equal(2, t.Snapshot(false).UtteranceCount);
    }

    [Fact]
    public void First_audio_with_no_utterance_started_is_ignored()
    {
        var (t, clock) = Build();
        clock.Ms = 777;
        t.FirstAudio();
        Assert.Equal(0, t.Snapshot(false).FirstByteLatencyMs);
    }

    [Fact]
    public void The_active_flag_follows_the_utterance()
    {
        var (t, _) = Build();
        Assert.False(t.Snapshot(false).IsActive);
        t.UtteranceStarting("x");
        Assert.True(t.Snapshot(false).IsActive);
        t.UtteranceEnded();
        Assert.False(t.Snapshot(false).IsActive);
    }

    // ----------------------------------------------------------------- text

    [Fact]
    public void The_length_is_always_reported_and_the_words_only_when_asked_for()
    {
        var (t, _) = Build();
        t.UtteranceStarting("A private sentence about nobody's business.");

        var plain = t.Snapshot(includeSnippet: false);
        Assert.Equal(43, plain.TextLength);
        Assert.Equal("", plain.CurrentText);

        var withText = t.Snapshot(includeSnippet: true);
        Assert.Equal(43, withText.TextLength);
        Assert.Equal("A private sentence about nobody's business.", withText.CurrentText);
    }

    [Fact]
    public void The_length_survives_the_end_of_the_utterance()
    {
        var (t, _) = Build();
        t.UtteranceStarting("twelve chars");
        t.UtteranceEnded();
        Assert.Equal(12, t.Snapshot(false).TextLength);
    }

    [Fact]
    public void A_long_text_is_cut_to_the_snippet_limit_and_says_so()
    {
        string snippet = DiagnosticsTracker.Snippet(new string('a', 500));

        Assert.True(snippet.Length <= DiagnosticsTracker.SnippetChars + 1);
        Assert.EndsWith("…", snippet);
        Assert.StartsWith("aaaa", snippet);
    }

    [Fact]
    public void Whitespace_is_collapsed_so_a_snippet_is_one_line()
    {
        Assert.Equal("one two three", DiagnosticsTracker.Snippet("  one\n\n two\t three  "));
        Assert.Equal("", DiagnosticsTracker.Snippet("   \n\t"));
        Assert.Equal("", DiagnosticsTracker.Snippet(null));
    }

    /// <summary>A cut between the halves of a surrogate pair leaves U+FFFD in the window.</summary>
    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // 79 ASCII characters, then an emoji (two UTF-16 units): the 80-character
        // limit lands between them.
        string text = new string('a', DiagnosticsTracker.SnippetChars - 1) + "\U0001F600 and more";

        string snippet = DiagnosticsTracker.Snippet(text);

        Assert.DoesNotContain(snippet, char.IsSurrogate);   // the whole pair went, not half
        Assert.Equal(new string('a', DiagnosticsTracker.SnippetChars - 1) + "…", snippet);
    }

    // --------------------------------------------------------------- errors

    [Fact]
    public void The_last_error_is_kept_with_its_time_and_replaced_by_the_next()
    {
        var (t, clock) = Build();

        t.RecordError("first");
        clock.Utc = clock.Utc.AddMinutes(5);
        t.RecordError("second");

        var s = t.Snapshot(false);
        Assert.Equal("second", s.LastError);
        Assert.Equal(clock.Utc, s.LastErrorUtc);
    }

    [Fact]
    public void An_error_is_reduced_to_its_first_line_and_bounded()
    {
        var (t, _) = Build();

        // ORT reports a CUDA fault as a long single line with frames after it.
        t.RecordError("CUDA failure 100: no device\n   at Frame1\n   at Frame2");
        Assert.Equal("CUDA failure 100: no device", t.Snapshot(false).LastError);

        t.RecordError(new string('x', 5_000));
        Assert.True(t.Snapshot(false).LastError.Length <= DiagnosticsTracker.ErrorChars + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void A_blank_error_does_not_replace_a_real_one(string? blank)
    {
        var (t, _) = Build();
        t.RecordError("real");
        t.RecordError(blank);
        Assert.Equal("real", t.Snapshot(false).LastError);
    }

    [Fact]
    public void No_error_has_no_time()
    {
        var (t, _) = Build();
        var s = t.Snapshot(false);
        Assert.Equal("", s.LastError);
        Assert.Null(s.LastErrorUtc);
    }

    // --------------------------------------------------------------- format

    [Fact]
    public void The_table_words_the_numbers_the_same_way_for_the_window_and_the_terminal()
    {
        var now = new DateTime(2026, 10, 3, 12, 10, 0, DateTimeKind.Utc);
        var snap = new SessionSnapshot
        {
            Pid = 4242,
            UptimeSec = 7_500,
            IsActive = true,
            VoiceId = "piper:en_US-lessac-medium",
            Provider = "cpu",
            OnnxThreads = 4,
            OnnxInterOpThreads = 1,
            LastRtf = 0.31,
            RollingRtf = 0.29,
            FirstByteLatencyMs = 412.4,
            UnderrunCount = 3,
            EngineRssMb = 812.6,
            TextLength = 1234,
            LastError = "boom",
            LastErrorUtc = now.AddSeconds(-90),
        };

        string text = DiagnosticsFormat.Text(snap, now);

        Assert.Contains("pid 4242, up 2 h 5 min", text);
        Assert.Contains("speaking", text);
        Assert.Contains("cpu, 4 intra-op threads, 1 inter-op", text);
        Assert.Contains("RTF 0.31", text);
        Assert.Contains("412 ms", text);
        Assert.Contains("3 since the daemon started", text);
        Assert.Contains("813 MB", text);
        Assert.Contains("1,234 characters (being read)", text);
        Assert.Contains("boom (1 min ago)", text);
    }

    [Fact]
    public void Zero_latency_is_not_printed_as_a_measurement()
    {
        var rows = DiagnosticsFormat.Rows(new SessionSnapshot(), DateTime.UtcNow);
        Assert.Equal("not measured yet", rows.Single(r => r.Label == "Pipeline latency").Value);
        Assert.Equal("none since the daemon started", rows.Single(r => r.Label == "Underruns").Value);
        Assert.Equal("none", rows.Single(r => r.Label == "Last error").Value);
    }

    // ------------------------------------------------------------- the wire

    /// <summary>
    /// The trap <see cref="ProtocolJson"/> documents: a type missing from the
    /// source-generated context works in every JIT build and aborts only in the
    /// NativeAOT <c>vst-ctl</c> that a hotkey runs. Going through the context
    /// itself is what catches it.
    /// </summary>
    [Fact]
    public void The_diagnostics_reply_round_trips_through_the_source_generated_context()
    {
        var reply = new Response
        {
            Ok = true,
            Diagnostics = new SessionSnapshot
            {
                Pid = 1, Provider = "cuda", OnnxInterOpThreads = 2, LastRtf = 0.5,
                LastError = "x", LastErrorUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                TextLength = 9,
            },
        };

        string line = Protocol.Encode(reply);
        var back = Protocol.TryDecode<Response>(line)!;

        Assert.Equal("cuda", back.Diagnostics!.Provider);
        Assert.Equal(2, back.Diagnostics.OnnxInterOpThreads);
        Assert.Equal(reply.Diagnostics.LastErrorUtc, back.Diagnostics.LastErrorUtc);
        Assert.NotNull(ProtocolJson.Default.GetTypeInfo(typeof(SessionSnapshot)));
    }

    [Fact]
    public void The_diagnostics_verb_round_trips_by_name()
    {
        string line = Protocol.Encode(new Request { Verb = RequestVerb.Diagnostics, Text = Protocol.DiagnosticsSnippet });
        Assert.Contains("Diagnostics", line, StringComparison.OrdinalIgnoreCase);

        var back = Protocol.TryDecode<Request>(line)!;
        Assert.Equal(RequestVerb.Diagnostics, back.Verb);
        Assert.Equal(Protocol.DiagnosticsSnippet, back.Text);
    }

    // ------------------------------------------------------------ underruns

    [Theory]
    [InlineData(300, 100, true)]    // waited 300 ms with 100 ms queued: 200 ms of silence
    [InlineData(120, 100, false)]   // 20 ms over is jitter, not a gap
    [InlineData(121, 100, true)]
    [InlineData(5, 0, false)]       // a sink that hides its latency, and a fast renderer
    [InlineData(50, 0, true)]       // ...and a slow one
    [InlineData(500, -10, true)]    // a negative latency is nonsense, treated as none
    [InlineData(double.NaN, 100, false)]
    public void An_underrun_is_a_wait_longer_than_what_the_device_still_held(
        double waitedMs, double bufferedMs, bool expected) =>
        Assert.Equal(expected, UnderrunPolicy.IsUnderrun(waitedMs, bufferedMs));

    /// <summary>A renderer that takes a fixed time per chunk, for a session to wait on.</summary>
    private sealed class SlowSynth(int delayMs) : ISynthesizer
    {
        public int SampleRate => 44100;
        public List<string> Rendered { get; } = [];

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken ct = default)
        {
            lock (Rendered) Rendered.Add(text);
            if (delayMs > 0) Thread.Sleep(delayMs);
            return new short[44100];   // one second of audio per chunk
        }

        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private const string ThreeChunks =
        "Alpha beta gamma delta epsilon. Zeta eta theta iota kappa. Lambda mu nu xi omicron.";

    private static SpeechSessionOptions SmallChunks() =>
        new(PrimeMs: 10, MaxChunkChars: 40, MinChunkChars: 10, LeadChunkChars: 10);

    /// <summary>
    /// THE COUNTER MOVES. Three chunks, each taking 300 ms to render against a
    /// device holding 100 ms: the first wait is start-up latency and exempt, the
    /// other two are gaps the listener hears.
    /// </summary>
    [Fact]
    public async Task A_renderer_slower_than_playback_is_counted_as_underruns()
    {
        var synth = new SlowSynth(delayMs: 300);
        using var session = new SpeechSession(synth, new FakeSink(), SmallChunks());

        Assert.True(session.Speak(ThreeChunks, new SupertonicOptions("M1", "en")));
        await session.Completion;

        Assert.True(synth.Rendered.Count >= 3, $"the text must chunk into at least three, got {synth.Rendered.Count}");
        Assert.Equal(synth.Rendered.Count - 1, session.UnderrunCount);
    }

    /// <summary>...and it does not move when nothing is wrong, or the counter would be noise.</summary>
    [Fact]
    public async Task A_renderer_that_keeps_ahead_is_not_counted()
    {
        var synth = new SlowSynth(delayMs: 0);
        using var session = new SpeechSession(synth, new FakeSink(), SmallChunks());

        Assert.True(session.Speak(ThreeChunks, new SupertonicOptions("M1", "en")));
        await session.Completion;

        Assert.True(synth.Rendered.Count >= 3);
        Assert.Equal(0, session.UnderrunCount);
    }

    /// <summary>
    /// The device holding a lot of audio hides a slow render: waiting 300 ms is
    /// fine when two seconds are queued. The buffer is what the policy subtracts.
    /// </summary>
    [Fact]
    public async Task A_deep_device_buffer_absorbs_a_slow_render()
    {
        var synth = new SlowSynth(delayMs: 300);
        var sink = new FakeSink { BufferFrames = 44100 * 2 };
        using var session = new SpeechSession(synth, sink, SmallChunks());

        Assert.True(session.Speak(ThreeChunks, new SupertonicOptions("M1", "en")));
        await session.Completion;

        Assert.Equal(0, session.UnderrunCount);
    }
}

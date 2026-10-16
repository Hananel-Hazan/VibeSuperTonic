using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Telemetry;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Two things the Tune and Status tabs depend on the daemon for: that
/// <c>OnnxInterOpThreads</c> in settings.json actually reaches a rebuilt ONNX
/// session, and that the <c>diagnostics</c> verb reports what really happened.
///
/// <para>Both are silent when wrong. A setting that saves, reads back and is never
/// applied looks like a working knob; a diagnostics panel that stays at zero looks
/// like a healthy daemon.</para>
/// </summary>
public sealed class DiagnosticsAndInterOpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-diag-{Guid.NewGuid():N}");
    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public DiagnosticsAndInterOpTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Settings(string json) => File.WriteAllText(Path.Combine(Data, "settings.json"), json);

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    // ------------------------------------------------------------- the setting

    [Fact]
    public void Inter_op_threads_default_to_what_the_session_was_always_built_with()
    {
        Assert.Equal(1, Loaded().Settings.OnnxInterOpThreads);
    }

    [Fact]
    public void Inter_op_threads_are_read_from_the_file()
    {
        Settings("""{ "OnnxInterOpThreads": 3 }""");
        Assert.Equal(3, Loaded().Settings.OnnxInterOpThreads);
    }

    // ------------------------------------------------------------ the rebuild

    private sealed class Counting : ISynthesizer
    {
        public int SampleRate => 24000;
        public bool Disposed { get; private set; }
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken ct = default) => [1];
        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private (ProviderSwitchingSynthesizer Switch, HostConfig Config, List<(int Threads, string Provider)> Builds)
        BuildSwitch()
    {
        var config = Loaded();
        var builds = new List<(int, string)>();

        // The decision the daemon would have arrived at, so the first idle check
        // finds nothing to change and every rebuild after it is attributable to
        // the setting under test.
        var machine = MachineFacts.Current(Models, config.Settings.TotalStep, "M1", config.Settings.Language);
        var initial = ExecutionDecision.Decide(
            null, machine, config.Settings.MaxCpuPercent, Environment.ProcessorCount,
            ProviderPreference.Auto, "no gpu here", machine.PowerState, config.Settings.GpuOnBattery);

        var sut = new ProviderSwitchingSynthesizer(
            config, initial, new Counting(),
            (threads, provider) => { builds.Add((threads, provider)); return new Counting(); },
            gpuUnavailable: "no gpu here",
            sessionIdle: () => SpeechStateProbe.Idle,
            log: _ => { });

        return (sut, config, builds);
    }

    /// <summary>
    /// THE WIRING. Without it the knob is decoration: the daemon would go on
    /// building every session with the same inter-op count until a restart, and
    /// nothing anywhere would say so.
    /// </summary>
    [Fact]
    public void Changing_inter_op_threads_rebuilds_the_session_between_utterances()
    {
        var (sut, config, builds) = BuildSwitch();
        Assert.False(sut.ReevaluateWhenIdle());
        Assert.Empty(builds);
        Assert.Equal(1, sut.InterOpThreads);

        Settings("""{ "OnnxInterOpThreads": 4 }""");
        config.Reload(force: true);

        Assert.True(sut.ReevaluateWhenIdle());
        Assert.Single(builds);
        Assert.Equal(4, sut.InterOpThreads);

        // And once it has caught up it does not rebuild again on every press.
        Assert.False(sut.ReevaluateWhenIdle());
        Assert.Single(builds);
    }

    [Fact]
    public void A_nonsense_inter_op_value_is_held_to_the_ceiling_not_passed_to_ort()
    {
        var (sut, config, _) = BuildSwitch();

        Settings("""{ "OnnxInterOpThreads": 4000 }""");
        config.Reload(force: true);
        sut.ReevaluateWhenIdle();

        Assert.Equal(CpuBudget.MaxInterOp, sut.InterOpThreads);
    }

    [Fact]
    public void Editing_an_unrelated_setting_does_not_cost_a_second_of_rebuild()
    {
        var (sut, config, builds) = BuildSwitch();

        Settings("""{ "TotalStep": 6, "OnnxInterOpThreads": 1 }""");
        config.Reload(force: true);

        Assert.False(sut.ReevaluateWhenIdle());
        Assert.Empty(builds);
    }

    [Theory]
    [InlineData("cpu", 2, 1, "cpu", 2, 1, false)]
    [InlineData("cpu", 2, 1, "cpu", 2, 4, true)]    // inter-op alone
    [InlineData("cpu", 2, 1, "cpu", 6, 1, true)]    // threads alone, as before
    [InlineData("cpu", 2, 1, "cuda", 2, 1, true)]   // provider alone, as before
    public void A_rebuild_is_needed_when_any_of_the_three_moves(
        string p0, int t0, int i0, string p1, int t1, int i1, bool expected)
    {
        var current = new ExecutionDecision(t0, p0, "r", false);
        var next = new ExecutionDecision(t1, p1, "r", false);

        Assert.Equal(expected, ProviderSwitchingSynthesizer.NeedsRebuild(current, next, i0, i1));
    }

    // ------------------------------------------------------------ diagnostics

    private sealed class Sink : IAudioSink
    {
        public int SampleRate => 24000;
        public long WrittenFrames { get; private set; }
        public long LatencyUsec => 0;
        public void Write(ReadOnlySpan<short> pcm) => WrittenFrames += pcm.Length;
        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() { }
    }

    private sealed class Rendering(Func<short[]> render, int delayMs = 0) : ISynthesizer
    {
        public int SampleRate => 24000;
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken ct = default)
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
            return render();
        }
        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private (DaemonServer Server, SpeechSession Session) BuildServer(ISynthesizer inner)
    {
        var tracker = new DiagnosticsTracker();
        var wrapped = new DiagnosticSynthesizer(inner, tracker);
        var session = new SpeechSession(wrapped, new Sink(), new SpeechSessionOptions(PrimeMs: 10));
        var server = new DaemonServer(
            new DaemonOptions(Version: "test"), Loaded(), wrapped, session, diagnostics: tracker);
        return (server, session);
    }

    private static SessionSnapshot Ask(DaemonServer server, bool snippet = false)
    {
        var reply = server.Invoke(new Request
        {
            Verb = RequestVerb.Diagnostics,
            Text = snippet ? Protocol.DiagnosticsSnippet : null,
        });
        Assert.True(reply.Ok, reply.Error);
        return reply.Diagnostics!;
    }

    [Fact]
    public async Task A_fresh_daemon_reports_itself_and_nothing_it_has_not_done()
    {
        var (server, session) = BuildServer(new Rendering(() => [1]));
        using var _s = server; using var _t = session;
        await Task.CompletedTask;

        var snap = Ask(server);

        Assert.Equal(Environment.ProcessId, snap.Pid);
        Assert.True(snap.EngineRssMb > 0, "a running process has a resident set");
        Assert.Equal(0, snap.LastRtf);
        Assert.Equal(0, snap.FirstByteLatencyMs);
        Assert.Equal(0, snap.UnderrunCount);
        Assert.Equal(0, snap.TextLength);
        Assert.Equal("", snap.LastError);
        Assert.False(snap.IsActive);
    }

    /// <summary>
    /// The whole chain: the session speaks, the wrapper times the render, the
    /// event hooks time the first audio, and the verb returns all of it.
    /// </summary>
    [Fact]
    public async Task Speaking_fills_in_the_rtf_the_latency_and_the_text_length()
    {
        var (server, session) = BuildServer(new Rendering(() => new short[24000], delayMs: 50));
        using var _s = server; using var _t = session;

        const string Text = "Reading something private aloud.";
        Assert.True(session.Speak(Text, new SupertonicOptions("M1", "en")));
        await session.Completion;

        var snap = Ask(server);
        Assert.Equal(Text.Length, snap.TextLength);
        Assert.Equal(1, snap.UtteranceCount);
        Assert.True(snap.LastRtf > 0.04 && snap.LastRtf < 5, $"RTF {snap.LastRtf}");
        Assert.True(snap.FirstByteLatencyMs >= 50, $"latency {snap.FirstByteLatencyMs}");
        Assert.False(snap.IsActive);

        // Privacy: the words are not in the default reply.
        Assert.Equal("", snap.CurrentText);
    }

    /// <summary>
    /// The session owns the counter and the server reports it: a renderer slower
    /// than playback (300 ms a chunk against a device that holds nothing) must
    /// reach the verb, or the panel shows a healthy daemon over a stuttering one.
    /// </summary>
    [Fact]
    public async Task Underruns_reach_the_verb()
    {
        var (server, session) = BuildServer(new Rendering(() => new short[24000], delayMs: 300));
        using var _s = server; using var _t = session;
        session.Options = new SpeechSessionOptions(
            PrimeMs: 10, MaxChunkChars: 40, MinChunkChars: 10, LeadChunkChars: 10);

        Assert.True(session.Speak(
            "Alpha beta gamma delta epsilon. Zeta eta theta iota kappa. Lambda mu nu xi omicron.",
            new SupertonicOptions("M1", "en")));
        await session.Completion;

        var snap = Ask(server);
        Assert.True(snap.UnderrunCount >= 2, $"underruns {snap.UnderrunCount}");
        Assert.Equal(session.UnderrunCount, snap.UnderrunCount);
    }

    [Fact]
    public async Task The_words_come_back_only_when_the_request_asks_for_the_snippet()
    {
        var (server, session) = BuildServer(new Rendering(() => new short[2400]));
        using var _s = server; using var _t = session;

        Assert.True(session.Speak("Short enough to show.", new SupertonicOptions("M1", "en")));
        await session.Completion;

        Assert.Equal("", Ask(server).CurrentText);
        Assert.Equal("Short enough to show.", Ask(server, snippet: true).CurrentText);
    }

    [Fact]
    public async Task A_failed_utterance_becomes_the_last_error_with_a_time()
    {
        var (server, session) = BuildServer(new Rendering(
            () => throw new InvalidOperationException("the model fell over")));
        using var _s = server; using var _t = session;

        var before = DateTime.UtcNow;
        Assert.True(session.Speak("This will not work.", new SupertonicOptions("M1", "en")));
        await session.Completion;

        var snap = Ask(server);
        Assert.Contains("the model fell over", snap.LastError);
        Assert.NotNull(snap.LastErrorUtc);
        Assert.True(snap.LastErrorUtc >= before.AddSeconds(-1));

        // A failed render measured nothing about throughput.
        Assert.Equal(0, snap.LastRtf);
    }

    [Fact]
    public async Task A_cancelled_render_is_not_an_rtf_sample()
    {
        using var cts = new CancellationTokenSource();
        var tracker = new DiagnosticsTracker();
        var wrapped = new DiagnosticSynthesizer(
            new Rendering(() => throw new OperationCanceledException()), tracker);

        await Task.CompletedTask;
        Assert.Throws<OperationCanceledException>(
            () => wrapped.Synthesize("x", new SupertonicOptions("M1", "en"), cts.Token));

        Assert.Equal(0, tracker.Snapshot(false).LastRtf);
    }

    [Fact]
    public void The_wrapper_leaves_audio_and_disposal_alone()
    {
        var inner = new Counting();
        var wrapped = new DiagnosticSynthesizer(inner, new DiagnosticsTracker());

        Assert.Equal([1], wrapped.Synthesize("x", new SupertonicOptions("M1", "en")));
        Assert.Equal(24000, wrapped.SampleRate);

        // Program owns the inner synthesizer with its own `using`.
        wrapped.Dispose();
        Assert.False(inner.Disposed);
    }
}

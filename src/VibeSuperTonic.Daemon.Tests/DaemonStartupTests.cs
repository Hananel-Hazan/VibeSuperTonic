using System.Net.Sockets;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What the daemon does between being started and answering its first press:
/// which voice it points itself at, whether that costs a model load, and
/// whether a failure of accept() or of the socket directory ends it.
///
/// <para>From docs/CODE-REVIEW-2026-11-09.md items 5, 6, 7 and 23. Each test
/// here was run against the code before its fix and seen failing.</para>
///
/// <para>In <see cref="ProcessEnvironmentCollection"/> because two of them move
/// <c>$XDG_RUNTIME_DIR</c> and <c>$TMPDIR</c>, which are process-wide. The
/// socket root is a short path under <c>/tmp</c>: AF_UNIX stops at 108 bytes,
/// and a scratch directory is long enough to break it.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DaemonStartupTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", $"vst-start-{Guid.NewGuid():N}"[..24]);
    private readonly string? _runtimeWas = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    private readonly string? _tmpWas = Environment.GetEnvironmentVariable("TMPDIR");
    private DaemonServer? _server;

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public DaemonStartupTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        _server?.Dispose();
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtimeWas);
        Environment.SetEnvironmentVariable("TMPDIR", _tmpWas);
        // A test leaves a directory at 0500; put it back so it can be deleted.
        foreach (string dir in Directory.Exists(_root) ? Directory.GetDirectories(_root) : [])
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The Supertonic engine as the daemon holds it: asking its rate is what
    /// loads the model (<c>OrtSynthesizer.SampleRate => EnsureLoaded()...</c>).
    /// Throws, so any read is visible both in the count and in the selection.
    /// </summary>
    private sealed class LoadOnAsk : ISynthesizer
    {
        public int RateReads;
        public int Preloads;

        public int SampleRate
        {
            get
            {
                Interlocked.Increment(ref RateReads);
                throw new InvalidOperationException("the model was loaded to answer SampleRate");
            }
        }

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken token = default) =>
            throw new InvalidOperationException("nothing speaks in these tests");

        public Task PreloadAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref Preloads);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakePiper(int sampleRate) : ISynthesizer
    {
        public int SampleRate { get; } = sampleRate;
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken token = default) => [];
        public Task PreloadAsync(CancellationToken token = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoDevice : IAudioSink
    {
        public int SampleRate => 22050;
        public long WrittenFrames => 0;
        public long LatencyUsec => 0;
        public void Write(ReadOnlySpan<short> pcm) { }
        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() { }
    }

    private void Settings(string json) => File.WriteAllText(Path.Combine(Data, "settings.json"), json);

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    private void InstallPiperVoice(string id)
    {
        string dir = Path.Combine(Models, PiperVoiceStore.FolderName, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".onnx"), "not a graph");
        File.WriteAllText(Path.Combine(dir, id + ".onnx.json"), """
            {
              "audio": { "sample_rate": 22050, "quality": "medium" },
              "espeak": { "voice": "en-us" },
              "inference": { "noise_scale": 0.667, "length_scale": 1, "noise_w": 0.8 },
              "phoneme_id_map": { "_": [0], "^": [1], "$": [2] },
              "num_speakers": 1
            }
            """);
        // No calibration file: the router measures only a real PiperSynthesizer,
        // and these tests build fakes.
    }

    // ------------------------------------------------------------------ item 5

    /// <summary>
    /// ITEM 5. Pointing the daemon at a Supertonic voice when it starts must not
    /// ask the Supertonic engine anything: the engine answers its rate by loading
    /// 380 MB, and that load used to happen before the socket was bound, on
    /// every start, with or without <c>--preload</c>.
    /// </summary>
    [Fact]
    public void Startup_selection_of_a_supertonic_voice_does_not_touch_the_engine()
    {
        var engine = new LoadOnAsk();
        var log = new List<string>();
        using var router = new EngineRoutingSynthesizer(engine, new PiperVoiceStore(Models),
            _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle, log.Add);

        var selection = router.SelectAtStartup("M1");

        Assert.Equal(0, engine.RateReads);
        Assert.Null(selection.Error);
        Assert.Equal("supertonic", router.Engine);
    }

    /// <summary>
    /// The same, for a Piper voice that cannot be built at startup: the refusal
    /// is logged, and getting there must not load the other engine either.
    /// </summary>
    [Fact]
    public void A_piper_voice_that_fails_at_startup_does_not_load_supertonic_on_the_way()
    {
        InstallPiperVoice("en_US-test-medium");
        var engine = new LoadOnAsk();
        using var router = new EngineRoutingSynthesizer(engine, new PiperVoiceStore(Models),
            _ => throw new FileNotFoundException("espeak-ng data missing"),
            () => SpeechStateProbe.Idle, _ => { });

        var selection = router.SelectAtStartup("piper:en_US-test-medium");

        Assert.NotNull(selection.Error);
        Assert.Equal(0, engine.RateReads);
    }

    /// <summary>
    /// ITEM 5, as the daemon runs it: bind, make the startup selection, answer
    /// status. Without <c>--preload</c> nothing is loaded and status says so;
    /// with it, the load happens.
    /// </summary>
    [Fact]
    public async Task Without_preload_the_model_is_not_loaded_after_start_and_preload_still_loads_it()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);
        Settings("""{ "DefaultVoice": "M1" }""");
        var config = Loaded();
        var engine = new LoadOnAsk();
        using var router = new EngineRoutingSynthesizer(engine, config.PiperVoices,
            _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle, _ => { });
        var session = new SpeechSession(router, new NoDevice());
        _server = new DaemonServer(new DaemonOptions(), config, router, session, engines: router);

        _server.Bind();
        router.SelectAtStartup(EngineRoutingSynthesizer.StartupVoice(null, config));

        var status = _server.Invoke(new Request { Verb = RequestVerb.Status }).Status;
        Assert.NotNull(status);
        Assert.False(status.ModelLoaded);
        Assert.Equal(0, engine.RateReads);
        Assert.Equal(0, engine.Preloads);

        await _server.PreloadAsync(CancellationToken.None);

        Assert.Equal(1, engine.Preloads);
        Assert.True(_server.Invoke(new Request { Verb = RequestVerb.Status }).Status!.ModelLoaded);
    }

    // ------------------------------------------------------------------ item 6

    /// <summary>
    /// ITEM 6. The voice the daemon starts on is the one the user chose, which
    /// is <c>VoiceId</c> when the file has one. <c>DefaultVoice</c> is only ever
    /// the last Supertonic style, so a Piper user got Supertonic selected at
    /// every start and paid the Piper load on the first press.
    /// </summary>
    [Fact]
    public void Startup_points_at_the_configured_voice_not_the_last_supertonic_style()
    {
        InstallPiperVoice("de_DE-test-medium");
        Settings("""{ "DefaultVoice": "M1", "VoiceId": "piper:de_DE-test-medium" }""");
        var config = Loaded();
        int built = 0;
        using var router = new EngineRoutingSynthesizer(new LoadOnAsk(), config.PiperVoices,
            _ => { built++; return new FakePiper(22050); },
            () => SpeechStateProbe.Idle, _ => { });

        string? voice = EngineRoutingSynthesizer.StartupVoice(null, config);
        router.SelectAtStartup(voice);

        Assert.Equal("piper:de_DE-test-medium", voice);
        Assert.Equal("piper", router.Engine);
        Assert.Equal("de_DE-test-medium", router.PiperVoice);
        Assert.Equal(1, built);
        // And --voice still outranks the file.
        Assert.Equal("M4", EngineRoutingSynthesizer.StartupVoice("M4", config));
    }

    // ------------------------------------------------------------------ item 7

    /// <summary>
    /// ITEM 7. accept() failing — EMFILE when the process is out of descriptors,
    /// ECONNABORTED when a client gives up in the queue — is a SocketException,
    /// which is neither an OperationCanceledException nor an IOException, so it
    /// ended the daemon with nothing in its log. The loop must log it, pause and
    /// keep serving.
    /// </summary>
    [Fact]
    public async Task The_accept_loop_survives_a_failed_accept_and_keeps_serving()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _root);
        var config = Loaded();
        var synth = new FakePiper(22050);
        var session = new SpeechSession(synth, new NoDevice());
        _server = new DaemonServer(new DaemonOptions(), config, synth, session);

        int failures = 0;
        _server.Accept = (listener, token) =>
            Interlocked.Increment(ref failures) <= 2
                ? ValueTask.FromException<Socket>(new SocketException(
                    failures == 1 ? (int)SocketError.TooManyOpenSockets : (int)SocketError.ConnectionAborted))
                : listener.AcceptAsync(token);

        _server.Bind();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving = _server.RunAsync(cts.Token);

        // Give a loop that is going to die the time to do it.
        await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.False(serving.IsCompleted, $"the accept loop ended: {serving.Exception?.InnerException}");

        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(Protocol.SocketPath()));
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var reader = new StreamReader(stream);
        await writer.WriteLineAsync(Protocol.Encode(new Request { Verb = RequestVerb.Status }));
        Assert.NotNull(await reader.ReadLineAsync(cts.Token));
        Assert.True(failures >= 3);

        cts.Cancel();
        try { await serving; } catch (OperationCanceledException) { }
    }

    // ----------------------------------------------------------------- item 23

    /// <summary>
    /// ITEM 23. With no <c>$XDG_RUNTIME_DIR</c> the socket falls back under
    /// <c>/tmp</c>, where the directory can belong to somebody else.
    /// <c>Directory.CreateDirectory</c> and <c>SetUnixFileMode</c> then throw
    /// UnauthorizedAccessException, which Program did not catch: exit 134, a core
    /// file, no log line. Bind must report it the way it reports every other
    /// reason it cannot bind — as an IOException that names the directory.
    /// A directory this user cannot write stands in for one somebody else owns.
    /// </summary>
    [Fact]
    public void A_socket_directory_this_user_may_not_use_is_a_logged_refusal_not_a_crash()
    {
        string locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
        Environment.SetEnvironmentVariable("TMPDIR", locked);

        var config = Loaded();
        var synth = new FakePiper(22050);
        _server = new DaemonServer(new DaemonOptions(), config, synth, new SpeechSession(synth, new NoDevice()));

        var ex = Assert.Throws<IOException>(() => _server.Bind());
        Assert.Contains(locked, ex.Message);
    }
}

/// <summary>
/// ITEM 19. The provider decision made before every utterance must compare the
/// stored benchmark against the step count the benchmark voice runs at, as the
/// startup decision does. With a per-engine step of 6 under a global 12, it
/// compared against 12, called a fresh profile stale and dropped its pick on
/// the first press.
/// </summary>
public sealed class PerUtteranceDecisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-decide-{Guid.NewGuid():N}");
    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public PerUtteranceDecisionTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Silent : ISynthesizer
    {
        public int SampleRate => 44100;
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken token = default) => [];
        public Task PreloadAsync(CancellationToken token = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void A_profile_measured_at_the_scoped_step_count_still_applies_on_the_first_press()
    {
        File.WriteAllText(Path.Combine(Data, "settings.json"), """
            {
              "TotalStep": 12,
              "VoiceId": "supertonic:M1",
              "Provider": "cpu",
              "PerEngine": { "supertonic": { "TotalStep": 6 } }
            }
            """);
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);

        // Measured as a sweep of this install would measure it: the benchmark
        // voice, at the steps that voice runs.
        string benchVoice = config.BenchmarkVoiceFor(null);
        var measured = MachineFacts.Current(Models, config.TotalStepFor(benchVoice), benchVoice, "en");
        Assert.Equal(6, measured.TotalStep);
        var profile = new BenchmarkProfile(
            Threads: 3, Provider: ExecutionProviders.Cpu, MeasuredUtc: "2026-11-09T00:00:00Z",
            Machine: measured,
            Table: [new BenchmarkRow("cpu-3", 3, ExecutionProviders.Cpu, 100, 0.1, 3, 0.3)],
            NotVaried: [], SampleSeconds: 1);
        Assert.True(BenchmarkStore.TrySave(LinuxDataPaths.BenchmarkFile(Data), profile, out string? error), error);

        // The startup decision, exactly as Program makes it.
        var startup = ExecutionDecision.Decide(
            BenchmarkStore.Load(LinuxDataPaths.BenchmarkFile(Data)), measured,
            config.Settings.MaxCpuPercent, Environment.ProcessorCount, config.Settings.Provider);
        Assert.True(startup.FromProfile, startup.Reason);

        var log = new List<string>();
        int builds = 0;
        using var sut = new ProviderSwitchingSynthesizer(
            config, startup, new Silent(), (_, _) => { builds++; return new Silent(); },
            gpuUnavailable: "no GPU in tests", sessionIdle: () => SpeechStateProbe.Idle, log: log.Add);

        sut.ReevaluateWhenIdle();

        Assert.True(sut.Decision.FromProfile, sut.Decision.Reason);
        Assert.Equal(0, builds);
    }
}

using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Which engine speaks, and when it is allowed to change.
///
/// <para>Tested here rather than in Core.Tests because the rule lives in the
/// daemon: Core holds one <see cref="ISynthesizer"/> and is deliberately unaware
/// that there are two engines behind it.</para>
/// </summary>
public class EngineRoutingTests : IDisposable
{
    private readonly string _models = Path.Combine(
        Path.GetTempPath(), $"vst-engines-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_models, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A voice directory with the two files the store requires. The .onnx is a
    /// placeholder: nothing here loads a graph, and the store's rule is about
    /// which files are present.
    /// </summary>
    private string InstallVoice(string id, int sampleRate = 22050)
    {
        string dir = Path.Combine(_models, PiperVoiceStore.FolderName, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".onnx"), "not a graph");
        File.WriteAllText(Path.Combine(dir, id + ".onnx.json"), $$"""
            {
              "audio": { "sample_rate": {{sampleRate}}, "quality": "medium" },
              "espeak": { "voice": "en-us" },
              "inference": { "noise_scale": 0.667, "length_scale": 1, "noise_w": 0.8 },
              "phoneme_id_map": { "_": [0], "^": [1], "$": [2] },
              "num_speakers": 1
            }
            """);
        return dir;
    }

    private sealed class FakeSynth(int sampleRate) : ISynthesizer
    {
        public int SampleRate { get; } = sampleRate;
        public int Renders { get; private set; }

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
        {
            Renders++;
            return new short[16];
        }

        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// A synthesizer that cannot answer its own rate — which is what the
    /// Supertonic engine IS on a machine with no models, because it answers
    /// <c>SampleRate</c> by loading one.
    /// </summary>
    private sealed class UnloadableSynth : ISynthesizer
    {
        public int SampleRate =>
            throw new DirectoryNotFoundException(
                "ONNX model directory not found: /nope/models/onnx. Models download on first run.");

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default) =>
            throw new DirectoryNotFoundException("ONNX model directory not found");

        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void A_fresh_install_with_no_models_can_still_start_its_daemon()
    {
        // THE 0.2.11 REGRESSION, and the reason build/smoke-test.sh exists.
        //
        // The daemon calls Select at STARTUP to point itself at the default
        // voice. Select's fast path reads _current.SampleRate — described in its
        // own comment as "cheap, one directory stat and a dictionary lookup" —
        // and the Supertonic engine answers that question by loading the model.
        // With models/onnx absent it throws, and at startup there is no handler
        // between Select and Main: the daemon logged "starting anyway; speech
        // will fail until the models are downloaded" and then died.
        //
        // A fresh install could not start the daemon it needs in order to stop
        // being a fresh install. Every machine that had ever run this had models
        // on it, so nothing saw it until an empty directory did.
        //
        // A routing decision may fail. It may not throw.
        var log = new List<string>();
        using var router = Router(new UnloadableSynth(),
            _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle, log);

        var selection = router.Select("M1");

        Assert.NotNull(selection.Error);
        Assert.Contains("not ready", selection.Error);
        // The reason survives to the message, so a user is told what is missing
        // rather than that something went wrong.
        Assert.Contains("Models download on first run", selection.Error);
        Assert.Contains(log, m => m.Contains("could not select"));
    }

    [Fact]
    public void The_default_voice_at_startup_is_asked_for_by_a_null_id()
    {
        // Program.cs passes `voice ?? config.Settings.DefaultVoice`, which is
        // null when neither is set — the exact shape of a fresh install. It must
        // reach the same refusal rather than a NullReferenceException on the way.
        using var router = Router(new UnloadableSynth(),
            _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle);

        var selection = router.Select(null);

        Assert.NotNull(selection.Error);
        Assert.Contains("(default)", selection.Error);
    }

    private EngineRoutingSynthesizer Router(
        ISynthesizer supertonic,
        Func<string, ISynthesizer> buildPiper,
        Func<SpeechStateProbe> idle,
        List<string>? log = null)
        => new(supertonic, new PiperVoiceStore(_models), buildPiper, idle, m => log?.Add(m));

    [Fact]
    public void A_voice_that_is_not_installed_as_piper_stays_on_supertonic()
    {
        // "M1" is a Supertonic style, and so is any id the store has never heard
        // of. The store is the whole rule — there is no Engine setting that
        // could disagree with the voice.
        var supertonic = new FakeSynth(44100);
        using var router = Router(supertonic, _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle);

        var selection = router.Select("M1");

        Assert.False(selection.Changed);
        Assert.Equal(44100, selection.SampleRate);
        Assert.Equal("supertonic", router.Engine);
    }

    [Fact]
    public void An_installed_piper_voice_routes_to_piper_and_reports_its_rate()
    {
        InstallVoice("en_US-lessac-medium", sampleRate: 22050);
        var supertonic = new FakeSynth(44100);
        using var router = Router(supertonic, _ => new FakeSynth(22050), () => SpeechStateProbe.Idle);

        var selection = router.Select("en_US-lessac-medium");

        Assert.True(selection.Changed);
        Assert.Equal(22050, selection.SampleRate);
        Assert.Equal("piper", router.Engine);
        Assert.Equal("en_US-lessac-medium", router.PiperVoice);
        Assert.Equal(22050, router.SampleRate);
    }

    [Fact]
    public void A_half_finished_download_is_not_a_voice()
    {
        // Weights with no config, which is what an interrupted fetch leaves. It
        // must not shadow a Supertonic voice of the same name or claim to be
        // speakable — the failure would otherwise land at render time.
        string dir = Path.Combine(_models, PiperVoiceStore.FolderName, "en_US-half-medium");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "en_US-half-medium.onnx"), "partial");

        using var router = Router(new FakeSynth(44100),
            _ => throw new Xunit.Sdk.XunitException("must not build Piper"),
            () => SpeechStateProbe.Idle);

        Assert.False(router.Select("en_US-half-medium").Changed);
        Assert.Equal("supertonic", router.Engine);
    }

    [Fact]
    public void The_engine_does_not_change_while_something_is_being_read()
    {
        // The same rule as the audio-device reconnect and the provider switch,
        // and sharper here: the engines do not share a sample rate, so a change
        // inside an utterance would play the remainder at the wrong speed as
        // well as putting every boundary permanently wrong.
        InstallVoice("en_US-lessac-medium");
        var state = SpeechStateProbe.Busy;
        using var router = Router(new FakeSynth(44100), _ => new FakeSynth(22050), () => state);

        var busy = router.Select("en_US-lessac-medium");

        Assert.True(busy.NeedsIdle);
        Assert.Null(busy.Error);            // not a failure — the caller may stop and ask again
        Assert.Equal("supertonic", router.Engine);
        Assert.Equal(44100, busy.SampleRate);

        state = SpeechStateProbe.Idle;
        Assert.True(router.Select("en_US-lessac-medium").Changed);
    }

    [Fact]
    public void A_voice_that_will_not_load_is_refused_rather_than_replaced()
    {
        // Speaking in another voice would be a hotkey that works and lies, which
        // is worse than one that says why it cannot.
        InstallVoice("en_US-lessac-medium");
        var log = new List<string>();
        using var router = Router(new FakeSynth(44100),
            _ => throw new FileNotFoundException("libespeak-ng.so is missing"),
            () => SpeechStateProbe.Idle, log);

        var selection = router.Select("en_US-lessac-medium");

        Assert.NotNull(selection.Error);
        Assert.Contains("en_US-lessac-medium", selection.Error);
        Assert.Contains("libespeak-ng", selection.Error);
        Assert.Equal("supertonic", router.Engine);
        Assert.Contains(log, m => m.Contains("could not load"));
    }

    [Fact]
    public void Renders_go_to_the_selected_engine()
    {
        InstallVoice("en_US-lessac-medium");
        var supertonic = new FakeSynth(44100);
        var piper = new FakeSynth(22050);
        using var router = Router(supertonic, _ => piper, () => SpeechStateProbe.Idle);

        router.Synthesize("before", new SupertonicOptions("M1", "en"));
        router.Select("en_US-lessac-medium");
        router.Synthesize("after", new PiperOptions("en_US-lessac-medium"));

        Assert.Equal(1, supertonic.Renders);
        Assert.Equal(1, piper.Renders);
    }

    [Fact]
    public void Going_back_to_supertonic_releases_the_piper_voice_and_says_so()
    {
        InstallVoice("en_US-lessac-medium");
        var log = new List<string>();
        using var router = Router(new FakeSynth(44100), _ => new FakeSynth(22050),
            () => SpeechStateProbe.Idle, log);

        router.Select("en_US-lessac-medium");
        var back = router.Select("M1");

        Assert.True(back.Changed);
        Assert.Equal(44100, back.SampleRate);
        Assert.Null(router.PiperVoice);
        Assert.Contains(log, m => m.Contains("piper -> supertonic"));
    }

    [Fact]
    public void Disposing_the_router_does_not_dispose_supertonic()
    {
        // It belongs to Program's `using`, and disposing it twice would tear the
        // default engine down on the ordinary shutdown path.
        InstallVoice("en_US-lessac-medium");
        var supertonic = new TrackingSynth(44100);
        var piper = new TrackingSynth(22050);
        var router = Router(supertonic, _ => piper, () => SpeechStateProbe.Idle);

        router.Select("en_US-lessac-medium");
        router.Dispose();

        Assert.True(piper.Disposed);
        Assert.False(supertonic.Disposed);
    }

    private sealed class TrackingSynth(int sampleRate) : ISynthesizer
    {
        public int SampleRate { get; } = sampleRate;
        public bool Disposed { get; private set; }
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default) => [];
        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}

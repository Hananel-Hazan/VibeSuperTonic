using System.Collections.Concurrent;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// A <c>render</c> keeps the engine it started with, whatever the press path or
/// another render selects while it runs.
///
/// <para><b>Why this exists.</b> Reported 2026-10-04: <c>Select</c> treated "the
/// session is idle" as "safe to swap", but a render — the Speech Dispatcher
/// module's verb — is not speech and never touches the session. So Orca reading
/// through a Piper voice, interrupted by a hotkey asking for Supertonic, had its
/// remaining chunks rendered by Supertonic with <see cref="PiperOptions"/>:
/// <c>Require</c> threw <see cref="ArgumentException"/> and the sentence was cut
/// off, while the Piper session it had been using was disposed underneath
/// it.</para>
///
/// <para>The render loop is reachable only through a socket and a fully built
/// <see cref="DaemonServer"/>, so <see cref="Render"/> below reproduces its use
/// of the router — one routing decision, then one synthesis call per chunk —
/// rather than the loop itself.</para>
/// </summary>
public class RenderEnginePinTests : IDisposable
{
    private readonly string _models = Path.Combine(
        Path.GetTempPath(), $"vst-pin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_models, recursive: true); } catch { /* best effort */ }
    }

    private void InstallVoice(string id, int sampleRate = 22050)
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
    }

    /// <summary>
    /// An engine that behaves like the real ones in the two ways that matter:
    /// it refuses the other engine's options exactly as the backends do
    /// (<see cref="SynthesisOptions.Require{T}"/>), and it refuses to render
    /// once disposed. Every sample it returns is its own tag, so a chunk says
    /// which instance rendered it.
    /// </summary>
    private sealed class TaggedEngine(short tag, int sampleRate, bool piper) : ISynthesizer
    {
        private volatile bool _disposed;
        public short Tag { get; } = tag;
        public int SampleRate { get; } = sampleRate;
        public bool Disposed => _disposed;

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (piper) options.Require<PiperOptions>("Piper");
            else options.Require<SupertonicOptions>("Supertonic");

            // A little real work, so renders and selects genuinely overlap.
            Thread.SpinWait(200);
            ObjectDisposedException.ThrowIf(_disposed, this);
            return [Tag, Tag, Tag, Tag];
        }

        public Task PreloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => _disposed = true;
    }

    private sealed class Fixture
    {
        public readonly TaggedEngine Supertonic = new(1, 44100, piper: false);
        public readonly ConcurrentQueue<TaggedEngine> Built = new();   // in build order
        private int _nextTag = 100;

        public ISynthesizer BuildPiper(string modelPath)
        {
            var engine = new TaggedEngine((short)Interlocked.Increment(ref _nextTag), 22050, piper: true);
            Built.Enqueue(engine);
            return engine;
        }
    }

    private EngineRoutingSynthesizer Router(Fixture f) =>
        new(f.Supertonic, new PiperVoiceStore(_models), f.BuildPiper, () => SpeechStateProbe.Idle, _ => { });

    private static SynthesisOptions OptionsFor(string voice) =>
        voice.StartsWith("en_", StringComparison.Ordinal)
            ? new PiperOptions(voice)
            : new SupertonicOptions(voice, "en");

    /// <summary>
    /// What <c>RenderAsync</c> does with the router: route once, read the rate
    /// for the format reply, then synthesise chunk by chunk.
    /// <paramref name="between"/> runs after each chunk, which is where a
    /// competing press lands.
    /// </summary>
    private static (int Rate, List<short> Tags) Render(
        EngineRoutingSynthesizer router, string voice, int chunks, Action<int>? between = null)
    {
        var selection = router.Select(voice, out var lease);
        using var engine = lease;
        Assert.Null(selection.Error);
        Assert.False(selection.NeedsIdle);
        Assert.NotNull(engine);
        int rate = engine.SampleRate;

        var tags = new List<short>();
        var options = OptionsFor(voice);
        for (int i = 0; i < chunks; i++)
        {
            short[] pcm = engine.Synthesize($"chunk {i}", options);
            Assert.NotEmpty(pcm);
            tags.Add(pcm[0]);
            between?.Invoke(i);
        }
        return (rate, tags);
    }

    [Fact]
    public void A_press_for_supertonic_mid_render_does_not_cut_a_piper_render_off()
    {
        // The reviewer's scenario, exactly and without threads: Orca is two
        // chunks into a sentence on a Piper voice when a hotkey asks for
        // Supertonic. The session is idle — renders do not use it — so the
        // switch is allowed. The render must still finish, on the voice it began
        // with, at the rate it announced.
        InstallVoice("en_US-lessac-medium");
        var f = new Fixture();
        using var router = Router(f);

        var (rate, tags) = Render(router, "en_US-lessac-medium", chunks: 5, between: i =>
        {
            if (i == 1) Assert.True(router.Select("M1").Changed);
        });

        Assert.Equal(22050, rate);
        Assert.Equal(5, tags.Count);
        Assert.All(tags, t => Assert.Equal(tags[0], t));
        Assert.NotEqual(f.Supertonic.Tag, tags[0]);
    }

    [Fact]
    public void A_render_for_another_piper_voice_does_not_dispose_the_one_in_use()
    {
        // Two renders, two Piper voices: the second swaps the router's only
        // Piper slot. The first's session must survive until the first is done,
        // and only then be released.
        InstallVoice("en_US-lessac-medium");
        InstallVoice("en_GB-alan-medium");
        var f = new Fixture();
        using var router = Router(f);

        List<short> other = [];
        var (_, tags) = Render(router, "en_US-lessac-medium", chunks: 4, between: i =>
        {
            if (i != 0) return;
            other = Render(router, "en_GB-alan-medium", chunks: 2).Tags;

            // The release is a background task, so a chunk racing it usually
            // wins and a dispose-while-pinned bug would pass by luck. Give the
            // dispose that must not happen time to happen.
            var inUse = f.Built.First();
            Assert.False(SpinWait.SpinUntil(() => inUse.Disposed, TimeSpan.FromMilliseconds(250)),
                "the Piper voice a render is using was disposed under it");
        });

        Assert.All(tags, t => Assert.Equal(tags[0], t));
        Assert.All(other, t => Assert.Equal(other[0], t));
        Assert.NotEqual(tags[0], other[0]);

        // Released once nobody holds it — not leaked for the daemon's lifetime.
        var first = f.Built.Single(e => e.Tag == tags[0]);
        Assert.True(SpinWait.SpinUntil(() => first.Disposed, TimeSpan.FromSeconds(5)),
            "the swapped-out Piper voice was never released");
    }

    [Fact]
    public void Voice_remove_can_see_a_render_the_session_cannot()
    {
        // `voice remove` refused only while the SESSION was speaking through the
        // voice, and a render never is. A Piper session reads its model on first
        // use, so deleting it under a render is a render that fails — and the
        // voice need not even be the one in force any more.
        InstallVoice("en_US-lessac-medium");
        var f = new Fixture();
        using var router = Router(f);

        router.Select("en_US-lessac-medium", out var lease);
        Assert.True(router.IsRendering("en_US-lessac-medium"));

        router.Select("M1");    // a press moves on; the render has not
        Assert.Null(router.PiperVoice);
        Assert.True(router.IsRendering("EN_us-lessac-medium"));

        lease!.Dispose();
        Assert.False(router.IsRendering("en_US-lessac-medium"));
        lease.Dispose();        // twice is harmless — a using and a finally can both reach it
        Assert.False(router.IsRendering("en_US-lessac-medium"));
    }

    [Fact]
    public void Many_concurrent_renders_and_presses_each_keep_their_own_engine()
    {
        // The stress version: eight render streams alternating two Piper voices
        // and Supertonic, while a ninth thread plays the hotkey and switches the
        // engine as fast as it can. Seeded, so the schedule of voices
        // reproduces; bounded, so it is a second or two rather than a soak.
        //
        // Every render must finish all its chunks, on one engine instance, at the
        // rate it announced — and no chunk may land in a disposed session.
        InstallVoice("en_US-lessac-medium");
        InstallVoice("en_GB-alan-medium");
        var f = new Fixture();
        var router = Router(f);
        string[] voices = ["en_US-lessac-medium", "M1", "en_GB-alan-medium", "F2"];

        var failures = new ConcurrentQueue<string>();
        int completed = 0;
        using var stop = new CancellationTokenSource();

        var presses = Task.Run(() =>
        {
            var rng = new Random(7);
            while (!stop.IsCancellationRequested)
            {
                if (router.Select(voices[rng.Next(voices.Length)]).Error is { } e)
                    failures.Enqueue($"press: {e}");
                Thread.Yield();
            }
        });

        var renders = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var rng = new Random(1000 + worker);
            for (int n = 0; n < 60; n++)
            {
                string voice = voices[(worker + n) % voices.Length];
                bool piper = voice.StartsWith("en_", StringComparison.Ordinal);
                try
                {
                    var (rate, tags) = Render(router, voice, chunks: 6, between: _ =>
                    {
                        if (rng.Next(4) == 0) Thread.Yield();
                    });

                    if (tags.Count != 6) failures.Enqueue($"{voice}: {tags.Count} of 6 chunks");
                    else if (tags.Distinct().Count() != 1) failures.Enqueue($"{voice}: chunks from {string.Join(",", tags.Distinct())}");
                    else if (piper == (tags[0] == f.Supertonic.Tag)) failures.Enqueue($"{voice}: rendered by the wrong engine");
                    else if (rate != (piper ? 22050 : 44100)) failures.Enqueue($"{voice}: announced {rate} Hz");
                    else Interlocked.Increment(ref completed);
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"{voice}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        })).ToArray();

        bool finished = Task.WaitAll(renders, TimeSpan.FromSeconds(30));
        stop.Cancel();
        presses.Wait(TimeSpan.FromSeconds(5));

        Assert.True(finished, "the renders did not finish");
        Assert.True(failures.IsEmpty,
            $"{failures.Count} failure(s), {completed} clean of 480; first: " +
            string.Join(" | ", failures.Take(5)));
        Assert.Equal(480, completed);

        // Nothing leaked: once every render is done and the router is gone, every
        // Piper session it ever built has been released.
        router.Dispose();
        Assert.True(SpinWait.SpinUntil(() => f.Built.All(e => e.Disposed), TimeSpan.FromSeconds(5)),
            $"{f.Built.Count(e => !e.Disposed)} Piper session(s) never released");
    }
}

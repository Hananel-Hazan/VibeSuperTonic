using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What happens when the GPU stops working while a session is already built on
/// it.
///
/// <para><b>The defect these pin.</b> 0.2.10 handled a GPU session that failed to
/// BUILD and had nothing at all for a GPU session that built and then failed to
/// RUN. On 2026-08-26 an RTX A2000 entered "GPU requires reset"; the provider
/// library still loaded, the session still built, <c>status</c> still said
/// "CUDA, 2 threads" — and every hotkey press died on "CUDA failure 100: no
/// CUDA-capable device is detected" with no sound and no change. The product was
/// silently, permanently dead until someone read a log file.</para>
///
/// <para>No startup probe can close that: the device can leave after the probe
/// answers. So the rule is that the first render failure on a GPU falls back to
/// the CPU, mid-utterance, and the utterance still produces audio.</para>
/// </summary>
public class GpuFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"vst-gpufallback-{Guid.NewGuid():N}");

    private string DataDir => Path.Combine(_root, "data");
    private string ModelsRoot => Path.Combine(_root, "models");

    public GpuFallbackTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly SupertonicOptions Options = new("M4", "en");

    /// <summary>
    /// Ask for the GPU explicitly. Without this the decision has no profile to
    /// read, so it wants the CPU anyway and its reason never mentions the GPU at
    /// all — which would make a test of the GPU reason string pass on a sentence
    /// that was never about the GPU.
    /// </summary>
    private void RequestGpu() =>
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), """{"Provider":"gpu"}""");

    /// <summary>
    /// A synthesizer that either works or throws — and can throw from the LOAD
    /// rather than the render, which is the distinction that matters here.
    /// <c>OrtSynthesizer</c> loads its ONNX session lazily, so a dead GPU can
    /// surface from <c>SampleRate</c> or <c>PreloadAsync</c> just as easily as
    /// from <c>Synthesize</c>.
    /// </summary>
    private sealed class Fake : ISynthesizer
    {
        private readonly Func<short[]>? _render;
        private readonly Func<Exception>? _failLoad;

        private Fake(Func<short[]>? render, Func<Exception>? failLoad)
        {
            _render = render;
            _failLoad = failLoad;
        }

        /// <summary>Renders fine; only <see cref="Synthesize"/> can be made to throw.</summary>
        public static Fake Rendering(Func<short[]> render) => new(render, null);

        /// <summary>Throws from every entry point, the way an unloadable session does.</summary>
        public static Fake FailingToLoad(Func<Exception> failure) => new(null, failure);

        public int Calls { get; private set; }
        public int LoadCalls { get; private set; }
        public bool Disposed { get; private set; }

        public int SampleRate
        {
            get
            {
                LoadCalls++;
                if (_failLoad is not null) throw _failLoad();
                return 24000;
            }
        }

        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken ct = default)
        {
            Calls++;
            if (_failLoad is not null) throw _failLoad();
            return _render!();
        }

        public Task PreloadAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            // Task.Run, not a synchronous throw: OrtSynthesizer loads inside one,
            // so the exception arrives on the await and a wrapper that only
            // guards the synchronous call would miss it.
            return Task.Run(() =>
            {
                if (_failLoad is not null) throw _failLoad();
            }, ct);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A switcher whose current session is CUDA and whose rebuilds are recorded.
    /// </summary>
    private ProviderSwitchingSynthesizer Build(
        ISynthesizer first,
        Func<int, string, ISynthesizer> build,
        List<string> log,
        string provider = ExecutionProviders.Cuda)
    {
        var config = new HostConfig(DataDir, ModelsRoot);
        // The constructor only sets defaults; Reload is what reads settings.json,
        // exactly as Program does at startup.
        config.Reload(force: true);
        var decision = new ExecutionDecision(2, provider, "test", FromProfile: false);

        return new ProviderSwitchingSynthesizer(
            config,
            decision,
            first,
            build,
            gpuUnavailable: null,
            // Busy: this is the mid-utterance path, and it must work without the
            // idle-only ReevaluateWhenIdle door being open.
            sessionIdle: () => SpeechStateProbe.Busy,
            log: log.Add);
    }

    [Fact]
    public void GpuFailingMidRenderFallsBackToCpuAndStillProducesAudio()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException(
            "[ErrorCode:Fail] CUDA failure 100: no CUDA-capable device is detected"));
        var cpu = Fake.Rendering(() => [1, 2, 3]);

        var requested = new List<string>();
        var sut = Build(gpu, (threads, provider) =>
        {
            requested.Add(provider);
            return cpu;
        }, log);

        short[] pcm = sut.Synthesize("hello", Options);

        // The utterance survived. This is the whole point: before the fix this
        // threw, and the user heard nothing.
        Assert.Equal([1, 2, 3], pcm);
        Assert.Equal(ExecutionProviders.Cpu, sut.Decision.Provider);
        Assert.Equal([ExecutionProviders.Cpu], requested);
        Assert.Contains(log, l => l.Contains("failed in use"));
    }

    [Fact]
    public void TheFallbackIsLatchedSoTheSecondUtteranceDoesNotRetryTheGpu()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException("CUDA failure 100"));
        var cpu = Fake.Rendering(() => [7]);

        int builds = 0;
        var sut = Build(gpu, (_, _) => { builds++; return cpu; }, log);

        sut.Synthesize("one", Options);
        sut.Synthesize("two", Options);
        sut.Synthesize("three", Options);

        // One rebuild, not one per utterance. Paying a failed GPU render before
        // every press is the cost the latch exists to avoid.
        Assert.Equal(1, builds);
        Assert.Equal(1, gpu.Calls);
        Assert.Equal(3, cpu.Calls);

        // And the GPU is no longer offered to a benchmark sweep, because a sweep
        // that measured it would write a profile the daemon has to ignore.
        Assert.Empty(sut.SweepableGpuProviders);
    }

    [Fact]
    public void ACancelledRenderIsNotMistakenForADeadGpu()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var gpu = Fake.Rendering(() => throw new OperationCanceledException());
        var sut = Build(gpu, (_, _) => throw new InvalidOperationException("must not rebuild"), log);

        // Stop during a render is routine. Turning the GPU off for the life of
        // the process every time the user presses stop would be a slow bleed
        // that nothing reports.
        Assert.ThrowsAny<OperationCanceledException>(
            () => sut.Synthesize("hello", Options, cts.Token));

        Assert.Equal(ExecutionProviders.Cuda, sut.Decision.Provider);
        Assert.DoesNotContain(log, l => l.Contains("failed in use"));
    }

    [Fact]
    public void ACpuRenderThatThrowsIsLeftAlone()
    {
        var log = new List<string>();
        var cpu = Fake.Rendering(() => throw new InvalidOperationException("bad model"));
        var sut = Build(cpu, (_, _) => throw new InvalidOperationException("must not rebuild"),
            log, provider: ExecutionProviders.Cpu);

        // There is nothing below the CPU to fall back to, so the exception is the
        // honest answer rather than a rebuild loop.
        Assert.Throws<InvalidOperationException>(() => sut.Synthesize("hello", Options));
    }

    [Fact]
    public void WhenTheCpuSessionCannotBeBuiltEitherTheOriginalFailureSurfaces()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException("CUDA failure 100"));
        var sut = Build(gpu, (_, _) => throw new FileNotFoundException("no models"), log);

        // A broken install must not be reported as a GPU problem, and must not
        // hang: the render throws, the daemon logs it, the user gets an error.
        Assert.Throws<InvalidOperationException>(() => sut.Synthesize("hello", Options));
        Assert.Contains(log, l => l.Contains("CPU session failed to build too"));
    }

    /// <summary>
    /// The regression that mattered most, because its symptom was worse than the
    /// one being fixed. <c>SampleRate</c> is asked once on the startup path; with
    /// the model loaded lazily behind it, a dead GPU threw from there, nothing
    /// caught it, and the daemon died before it could listen on its socket. The
    /// hotkey restarts a dead daemon, so the desktop filled with crash
    /// notifications. Observed 2026-08-26.
    /// </summary>
    [Fact]
    public void AGpuThatFailsDuringTheLazyModelLoadDoesNotKillTheDaemon()
    {
        var log = new List<string>();
        var gpu = Fake.FailingToLoad(() => new InvalidOperationException(
            "[ErrorCode:Fail] CUDA failure 100: no CUDA-capable device is detected"));
        var cpu = Fake.Rendering(() => [5]);

        var sut = Build(gpu, (_, _) => cpu, log);

        Assert.Equal(24000, sut.SampleRate);
        Assert.Equal(ExecutionProviders.Cpu, sut.Decision.Provider);
        Assert.Contains(log, l => l.Contains("failed in use"));
    }

    /// <summary>
    /// The same load, reached the other way. Preload runs inside a Task.Run in
    /// the real backend, so the failure arrives on the await — a guard around
    /// only the synchronous call would let it through and leave the daemon on a
    /// GPU it has already proved it cannot use.
    /// </summary>
    [Fact]
    public async Task AGpuThatFailsDuringPreloadFallsBackToCpu()
    {
        var log = new List<string>();
        var gpu = Fake.FailingToLoad(() => new InvalidOperationException("CUDA failure 100"));
        var cpu = Fake.Rendering(() => [9]);

        var sut = Build(gpu, (_, _) => cpu, log);

        await sut.PreloadAsync();

        Assert.Equal(ExecutionProviders.Cpu, sut.Decision.Provider);
        Assert.Equal([9], sut.Synthesize("after", Options));
    }

    /// <summary>
    /// The reason string is read by people, in <c>vst-ctl status</c>, in
    /// <c>vst-ctl config</c> and in the tray tooltip. ORT's CUDA fault is one
    /// ~900-character line with the useful sentence in the middle of it, so
    /// pasting it in whole is what turns "CPU, 2 threads (…)" into a wall.
    /// </summary>
    [Fact]
    public void TheReasonSaysWhatFailedWithoutQuotingNineHundredCharactersOfOrt()
    {
        RequestGpu();
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException(
            "[ErrorCode:Fail] /onnxruntime_src/onnxruntime/core/providers/cuda/cuda_call.cc:129 " +
            "std::conditional_t<THRW, void, onnxruntime::common::Status> onnxruntime::CudaCall(" +
            "ERRTYPE, const char*, const char*, SUCCTYPE, const char*, const char*, int) " +
            "CUDA failure 100: no CUDA-capable device is detected ; GPU=-1 ; hostname=HHQ ; " +
            "file=/onnxruntime_src/.../cuda_execution_provider.cc ; line=282 ; " +
            "expr=cudaSetDevice(info_.device_id);"));

        var sut = Build(gpu, (_, _) => Fake.Rendering(() => [1]), log);
        sut.Synthesize("hello", Options);

        string reason = sut.Decision.Reason;
        Assert.Contains("CUDA failure 100: no CUDA-capable device is detected", reason);
        Assert.DoesNotContain("onnxruntime_src", reason);
        Assert.DoesNotContain("conditional_t", reason);

        // The log still carries the whole thing; that is where it is diagnosed.
        Assert.Contains(log, l => l.Contains("onnxruntime_src"));
    }

    /// <summary>A non-ORT failure has no useful fragment to find, so it is capped.</summary>
    [Fact]
    public void AnUnrecognisedFailureIsTruncatedRatherThanDropped()
    {
        RequestGpu();
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException(new string('x', 500)));

        var sut = Build(gpu, (_, _) => Fake.Rendering(() => [1]), log);
        sut.Synthesize("hello", Options);

        string reason = sut.Decision.Reason;
        Assert.Contains("InvalidOperationException", reason);

        // The type name survives and the 500-character body does not. Asserting
        // on the cap rather than on the finished sentence's length, because the
        // sentence also carries the veto phrase and the thread-count clause and
        // neither is this test's business.
        Assert.DoesNotContain(new string('x', 200), reason);

        // THE PROCESSOR COUNT IS COMPUTED, NOT WRITTEN DOWN. This read
        // "20% of 20 logical processors" — the count of the machine the test was
        // written on. It therefore passed on that one laptop and failed on every
        // other machine in the world, including every CI runner, which is how it
        // sat red from 2026-08-25 to 2026-08-27 while the suite was green locally.
        //
        // The clause comes from ExecutionDecision's
        // "{percent}% of {processorCount} logical processors, never benchmarked",
        // and processorCount is Environment.ProcessorCount. What this test is
        // actually asserting is that the ellipsis lands immediately before that
        // clause — that the message was truncated and the sentence still finishes
        // — so the clause is built the same way the product builds it.
        Assert.EndsWith(
            $"…, 20% of {Environment.ProcessorCount} logical processors, never benchmarked",
            reason);
    }

    // ------------------------- failures that are not the GPU's fault (2026-09-01)

    /// <summary>
    /// REPORTED FROM A REAL LOG, 2026-09-01. A voice that was not on disk turned
    /// the GPU off for the rest of the session:
    ///
    /// <code>
    /// inference: CUDA failed in use (FileNotFoundException: Voice style not found
    ///   for 'en_US-hfc_female-medium'); falling back to the CPU and not retrying
    ///   until restart
    /// inference: CUDA, 2 threads -> CPU, 2 threads (no GPU available —
    ///   FileNotFoundException: Voice style not found for 'en_US-hfc_female-medium')
    /// </code>
    ///
    /// <para>"No GPU available — Voice style not found" is not a sentence about a
    /// GPU. The catch here was a bare <c>catch (Exception)</c>, so every fault
    /// during a render read as the provider failing — and the user then ran on the
    /// CPU, slower, with <c>vst-ctl config</c> reporting a nonsense reason, until
    /// they next restarted the daemon.</para>
    /// </summary>
    [Fact]
    public void A_missing_file_is_not_the_gpu_failing()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new FileNotFoundException(
            "Voice style not found for 'en_US-hfc_female-medium'"));
        int rebuilds = 0;

        var switcher = Build(gpu, (_, _) => { rebuilds++; return Fake.Rendering(() => new short[10]); }, log);

        Assert.Throws<FileNotFoundException>(
            () => switcher.Synthesize("hello", Options));

        // It never pretended this was the GPU: no rebuild, no latch, and nothing
        // in the log claiming the provider failed.
        Assert.Equal(0, rebuilds);
        Assert.DoesNotContain(log, l => l.Contains("failed in use"));
        Assert.Equal(ExecutionProviders.Cuda, switcher.Decision.Provider);
    }

    /// <summary>A bad argument is the caller's fault too, and must travel intact.</summary>
    [Fact]
    public void An_invalid_argument_is_not_the_gpu_failing()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new ArgumentException("speaker 900 is out of range"));
        int rebuilds = 0;

        var switcher = Build(gpu, (_, _) => { rebuilds++; return Fake.Rendering(() => new short[10]); }, log);

        Assert.Throws<ArgumentException>(
            () => switcher.Synthesize("hello", Options));
        Assert.Equal(0, rebuilds);
        Assert.Equal(ExecutionProviders.Cuda, switcher.Decision.Provider);
    }

    /// <summary>
    /// THE SAFETY NET, for the faults no deny-list can name. If the CPU fails the
    /// same way the GPU did, the GPU was demonstrably not the problem — so the
    /// latch comes off rather than costing the user their GPU on false evidence.
    /// </summary>
    [Fact]
    public void A_cpu_that_fails_the_same_way_proves_the_gpu_was_innocent()
    {
        var log = new List<string>();
        static short[] Boom() => throw new InvalidDataException("the model file is corrupt");
        var gpu = Fake.Rendering(Boom);

        var switcher = Build(gpu, (_, _) => Fake.Rendering(Boom), log);

        Assert.Throws<InvalidDataException>(
            () => switcher.Synthesize("hello", Options));

        Assert.Contains(log, l => l.Contains("was not the problem"));
    }

    /// <summary>
    /// And a real GPU fault still latches, which is the behaviour 0.2.11 added and
    /// none of the above may weaken.
    /// </summary>
    [Fact]
    public void A_genuine_provider_fault_still_falls_back_and_stays_fallen_back()
    {
        var log = new List<string>();
        var gpu = Fake.Rendering(() => throw new InvalidOperationException(
            "[ErrorCode:Fail] CUDA failure 100: no CUDA-capable device is detected"));

        var switcher = Build(gpu, (_, _) => Fake.Rendering(() => new short[10]), log);

        Assert.NotEmpty(switcher.Synthesize("hello", Options));
        Assert.Equal(ExecutionProviders.Cpu, switcher.Decision.Provider);
        Assert.Contains(log, l => l.Contains("failed in use"));
    }
}

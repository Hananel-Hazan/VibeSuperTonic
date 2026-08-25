using Microsoft.ML.OnnxRuntime;
using Supertonic;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Onnx.Ort;

/// <summary>
/// <see cref="ISynthesizer"/> over one ONNX Runtime execution provider, chosen
/// when the instance is built.
///
/// Deliberately a fraction of the size of the Windows adapter, and that is the
/// point. That class is ~600 lines, most of it DirectML device-loss recovery:
/// TDR detection, a process-local latch that disables the GPU after repeated
/// losses, a reset channel from the launcher, and a CPU-retry path. None of it
/// has any meaning here, and dragging it across was exactly what R-12 warned
/// about. What is left when you remove it is this: load a session, render,
/// cancel.
///
/// <para><b>One instance, one provider, for the life of the instance.</b> ORT
/// binds the provider when the session is created, so switching means building
/// another of these and dropping the first — a rebuild of about a second, which
/// is precisely why Phase 8b's battery rule switches BETWEEN utterances and
/// never inside one.</para>
///
/// <para>The session is shared across voices because it is the expensive part
/// (~380 MB, 0.43 s warm on the development box); only the small per-voice Style
/// differs.</para>
/// </summary>
public sealed class OrtSynthesizer : ISynthesizer
{
    private readonly string _onnxDir;
    private readonly string _voiceStylesDir;
    private readonly string _provider;
    private readonly int _intraOpThreads;
    private readonly int _interOpThreads;

    private readonly object _gate = new();
    private readonly Dictionary<string, Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private TextToSpeech? _tts;
    private bool _disposed;

    // Renders currently inside TextToSpeech.Call, and an event that is set while
    // there are none. Dispose waits on it — see the Dispose remarks.
    private int _inFlight;
    private readonly ManualResetEventSlim _drained = new(initialState: true);

    /// <summary>
    /// How long <see cref="Dispose"/> waits for in-flight renders. Generous: one
    /// chunk is 1–3 s on the Mint box, and a caller that cancels first (which it
    /// should) drains in milliseconds. Bounded rather than infinite so a wedged
    /// native call cannot hang process shutdown forever.
    /// </summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(30);

    /// <param name="modelsRoot">Directory containing <c>onnx/</c> and <c>voice_styles/</c>.</param>
    /// <param name="provider">
    /// <see cref="ExecutionProviders.Cpu"/> or <see cref="ExecutionProviders.Cuda"/>.
    /// Anything else is refused here rather than at session build, because "the
    /// provider name in settings.json was a typo" and "this machine has no GPU"
    /// deserve different sentences.
    /// </param>
    /// <param name="intraOpThreads">
    /// 0 means "let ORT decide". Phase 0 measured every manual setting on Linux at
    /// roughly 2x worse than auto — and then Phase 8a measured the curve properly
    /// and found 4 beating auto on the development box, which is why the daemon
    /// passes a benchmarked number rather than 0. Still applies on the CUDA path:
    /// ORT leaves shape-related and unassigned nodes on the CPU, and the spike
    /// counted 46 to 80 Memcpy nodes added per graph.
    /// </param>
    public OrtSynthesizer(
        string modelsRoot,
        string provider = ExecutionProviders.Cpu,
        int intraOpThreads = 0,
        int interOpThreads = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (!ExecutionProviders.IsKnown(provider))
            throw new ArgumentException(
                $"unknown execution provider '{provider}'; expected " +
                $"'{ExecutionProviders.Cpu}' or '{ExecutionProviders.Cuda}'", nameof(provider));

        _onnxDir = Path.Combine(modelsRoot, "onnx");
        _voiceStylesDir = Path.Combine(modelsRoot, "voice_styles");
        _provider = provider;
        _intraOpThreads = intraOpThreads;
        _interOpThreads = interOpThreads;
    }

    /// <summary>Which provider this instance renders on.</summary>
    public string Provider => _provider;

    /// <summary>
    /// Whether the CUDA provider can be initialised on this machine — null when
    /// it can, and the reason it cannot otherwise.
    ///
    /// <para><b>Asked by appending the provider to a throwaway
    /// <c>SessionOptions</c>, not by looking for files or running
    /// <c>nvidia-smi</c>.</b> Every failure mode this has to detect lives inside
    /// that call: the 330 MB provider library absent (the default install), its
    /// CUDA and cuDNN dependencies unreachable, a driver too old, no device.
    /// Checking for the file only answers the first, and answering three of four
    /// questions confidently is how a machine ends up reporting "GPU available"
    /// and then falling back on every utterance.</para>
    ///
    /// <para>Costs a library load — tens of milliseconds with the pack absent,
    /// under a second with it present — and no CUDA context, which is created
    /// with the session. Call it once at startup and remember the answer: a GPU
    /// does not appear halfway through a login session.</para>
    /// </summary>
    public static string? ProbeCuda()
    {
        try
        {
            using var probe = new SessionOptions();
            probe.AppendExecutionProvider_CUDA(0);
            return null;
        }
        catch (Exception ex)
        {
            // EntryPointNotFoundException when the runtime was built without CUDA
            // at all, OnnxRuntimeException when the provider library or its
            // dependencies cannot be loaded. Both are measured cases — see the
            // csproj — and both are reported the same way: one sentence.
            return $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
        }
    }

    public int SampleRate => EnsureLoaded().SampleRate;

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Require<SupertonicOptions>("Supertonic");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text)) return [];

        // Both the session and the in-flight count are taken under one lock, so
        // Dispose cannot observe a zero count while a caller is on its way in.
        // Capturing the session outside the gate and using it after is the TOCTOU
        // the Windows adapter documents having walked into once: another thread
        // disposes it between the read and the Run, and ORT either throws
        // ObjectDisposedException or access-violates in native code once the
        // handle has been reclaimed.
        TextToSpeech tts;
        Style style;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            tts = EnsureLoadedLocked();
            style = EnsureStyleLoadedLocked(opts.VoiceId);
            if (_inFlight++ == 0) _drained.Reset();
        }

        // Two layers of cancellation, because they cover different windows. The
        // token alone is only observed between chunks inside Call; Terminate
        // aborts the ONNX Run that is already executing, which is where the
        // seconds actually are. Stop has to be able to interrupt that.
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() =>
        {
            try { runOptions.Terminate = true; } catch { /* already disposed; nothing to stop */ }
        });

        try
        {
            var (wav, _) = tts.Call(
                text, opts.Language, style,
                opts.TotalStep, opts.Speed, opts.SilenceSeconds,
                cancellationToken, runOptions);

            // Cancellation between chunks leaves Call returning normally with a
            // short buffer, so without this a cancelled utterance would surface as
            // truncated audio rather than as a cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            return AudioBuffer.FloatToPcm16(wav);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            // ORT reports a terminated Run as a generic failure —
            // "[ErrorCode:Fail] Exiting due to terminate flag being set to true" —
            // not as a cancellation. Letting that escape would make every stop
            // look like an engine fault in the daemon's logs, and R-8's stop path
            // is the most common code path there is once one key does both.
            throw new OperationCanceledException(
                "Synthesis cancelled: ONNX Run terminated.", cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                if (--_inFlight == 0) _drained.Set();
            }
        }
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLoaded();
        }, cancellationToken);

    private TextToSpeech EnsureLoaded()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EnsureLoadedLocked();
        }
    }

    /// <summary>
    /// Caller holds <c>_gate</c>. Model load happens under the lock — it is
    /// seconds cold, but doing it outside would let two callers each build a
    /// ~380 MB session set, and it is exactly the window Dispose must not slip
    /// into.
    /// </summary>
    private TextToSpeech EnsureLoadedLocked()
    {
        if (_tts is not null) return _tts;

        if (!Directory.Exists(_onnxDir))
            throw new DirectoryNotFoundException(
                $"ONNX model directory not found: {_onnxDir}. Models download on first run.");

        // gpuActive is discarded: whether CUDA was actually appended is a
        // question the daemon answers with its own probe, and a failure here
        // throws rather than quietly landing on the CPU.
        //
        // useGpu is the SDK's one provider switch, and on this build it means
        // CUDA — see the #else branch in SupertonicSdk.LoadTextToSpeech. It also
        // bypasses the optimised-graph cache, which is correct and not incidental:
        // those graphs are optimised for the CPU provider and mean nothing to a
        // GPU one.
        //
        // A CUDA failure throws out of here rather than falling back silently. The
        // daemon catches it, rebuilds on the CPU provider and logs the reason,
        // because only the daemon has somewhere to say it.
        _tts = Helper.LoadTextToSpeech(
            _onnxDir, out _, useGpu: _provider == ExecutionProviders.Cuda,
            intraOpThreads: _intraOpThreads, interOpThreads: _interOpThreads);
        return _tts;
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private Style EnsureStyleLoadedLocked(string voiceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        if (_styles.TryGetValue(voiceId, out var cached)) return cached;

        string path = Path.Combine(_voiceStylesDir, voiceId + ".json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Voice style not found for '{voiceId}'", path);

        var style = Helper.LoadVoiceStyle([path]);
        _styles[voiceId] = style;
        return style;
    }

    /// <summary>
    /// Closes the door, then waits for renders already through it before
    /// releasing the native sessions.
    ///
    /// Disposing an <c>InferenceSession</c> that a <c>Run</c> is still executing
    /// on reclaims native handles underneath it, which surfaces as an access
    /// violation rather than an exception — unrecoverable, and it takes the
    /// daemon down with it. This is not hypothetical for Phase 3: releasing the
    /// session on an idle timeout is an open decision in the port plan, and stop
    /// is the most-travelled path in the product.
    ///
    /// Cancel your renders before disposing and this returns immediately. The
    /// wait is the backstop, not the mechanism.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Outside the lock: an in-flight render needs _gate to decrement.
        bool drained = _drained.Wait(DisposeDrainTimeout);

        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
            _styles.Clear();
        }

        // Only safe to release once nothing can still call Set() on it. On the
        // timeout path a straggler may yet touch it, so it is deliberately
        // leaked — one event object beats a use-after-free.
        if (drained) _drained.Dispose();
    }
}

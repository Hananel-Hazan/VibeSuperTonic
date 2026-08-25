using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;

namespace VibeSuperTonic.Onnx.Ort;

/// <summary>
/// The second <see cref="ISynthesizer"/> — a Piper voice, on the ONNX Runtime
/// this repository already ships, with espeak-ng in front of it.
///
/// <para><b>What P0 and P1 bought.</b> The graph runs here byte-identically to
/// <c>python -m piper</c> (P0) and the ids we feed it are the ids piper feeds it
/// (P1, 327 sentences, 8 languages, 0 divergences). So this class is assembly
/// rather than invention, and the places it could still go wrong are the ones it
/// documents: the id interleave, the absent <c>sid</c> input on a single-speaker
/// voice, and the peak normalisation.</para>
///
/// <para><b>One render is one sentence.</b> espeak-ng returns clauses grouped
/// into sentences and a VITS graph is trained on one sentence at a time, so a
/// chunk containing three sentences is three <c>Run</c> calls with
/// <see cref="SynthesisOptions.SilenceSeconds"/> between them. This is the
/// counterpart of what the Supertonic SDK does internally by length, and it is
/// why <c>SilenceSeconds</c> is on the shared half of the options.</para>
///
/// <para><b>Cheap where Supertonic is expensive.</b> A render is one
/// <c>Run</c> of tens of milliseconds rather than a multi-second diffusion loop,
/// the session is ~140-190 MB rather than ~830 MB, and
/// <see cref="SampleRate"/> is answered from the voice's JSON without loading
/// the model at all. Measured in P2.</para>
///
/// <para><b>Where this file lives, and why.</b> Beside the Supertonic backend
/// because it needs ONNX Runtime and nothing else here does. Everything that
/// does NOT need ORT is deliberately elsewhere — the voice config and the id
/// assembly in Core, the phonemiser in VibeSuperTonic.Piper — so that a Windows
/// host can compile this one file against its own ORT flavour the way
/// <c>Onnx.Ort</c> compiles <c>SupertonicSdk.cs</c> today, rather than copying an
/// engine.</para>
/// </summary>
public sealed class PiperSynthesizer : ISynthesizer
{
    private readonly string _modelPath;
    private readonly PiperVoiceConfig _config;
    private readonly IPhonemizer _phonemizer;
    private readonly string _provider;
    private readonly int _intraOpThreads;
    private readonly int _interOpThreads;

    private readonly object _gate = new();
    private InferenceSession? _session;
    private string[]? _outputNames;
    private bool _disposed;

    private int _inFlight;
    private readonly ManualResetEventSlim _drained = new(initialState: true);

    /// <summary>Matches <see cref="OrtSynthesizer"/>'s, and for the same reason.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(30);

    /// <param name="modelPath">The voice's <c>.onnx</c>. Its config is <c>.onnx.json</c> beside it.</param>
    /// <param name="phonemizer">
    /// Owned by the caller, not by this class. One espeak-ng is initialised per
    /// process and it survives an engine switch — see
    /// <c>EspeakPhonemizer.Dispose</c> — so a synthesizer that disposed it would
    /// break the next one built.
    /// </param>
    public PiperSynthesizer(
        string modelPath,
        IPhonemizer phonemizer,
        string provider = ExecutionProviders.Cpu,
        int intraOpThreads = 0,
        int interOpThreads = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(phonemizer);
        if (!ExecutionProviders.IsKnown(provider))
            throw new ArgumentException(
                $"unknown execution provider '{provider}'; expected " +
                $"'{ExecutionProviders.Cpu}' or '{ExecutionProviders.Cuda}'", nameof(provider));

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Piper voice model not found: {modelPath}", modelPath);

        _modelPath = modelPath;
        // Loaded eagerly, and it is the whole reason SampleRate is free: the
        // sink has to be re-tuned before the first render, so an engine that
        // could only answer its rate by loading 63 MB of weights would put a
        // model load inside the press-to-speech budget.
        _config = PiperVoiceConfig.Load(modelPath + ".json");
        _phonemizer = phonemizer;
        _provider = provider;
        _intraOpThreads = intraOpThreads;
        _interOpThreads = interOpThreads;
    }

    /// <summary>Which provider this instance renders on.</summary>
    public string Provider => _provider;

    /// <summary>The voice, for anything that wants its defaults or its tier.</summary>
    public PiperVoiceConfig Voice => _config;

    /// <inheritdoc/>
    /// <remarks>From the voice's JSON — 22050 or 16000, and never resampled.</remarks>
    public int SampleRate => _config.SampleRate;

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Require<PiperOptions>("Piper");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Phonemised before the session is touched: it is the cheap half, it can
        // legitimately produce nothing (punctuation-only text), and there is no
        // reason to build a 140 MB session to render silence.
        var sentences = _phonemizer.Phonemize(_config.EspeakVoice, text);
        if (sentences.Count == 0) return [];

        InferenceSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = EnsureLoadedLocked();
            if (_inFlight++ == 0) _drained.Reset();
        }

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() =>
        {
            try { runOptions.Terminate = true; } catch { /* already disposed; nothing to stop */ }
        });

        try
        {
            var silence = new float[(int)Math.Max(0, opts.SilenceSeconds * _config.SampleRate)];
            var audio = new List<float>();

            foreach (var sentence in sentences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long[] ids = PiperPhonemes.ToIds(sentence, _config.PhonemeIdMap);
                // Two ids means BOS and EOS with nothing between them, which is
                // what a clause of pure whitespace produces. The graph accepts it
                // and returns a fragment of noise.
                if (ids.Length <= 2) continue;

                if (audio.Count > 0) audio.AddRange(silence);
                audio.AddRange(RunOne(session, ids, opts, runOptions));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Normalize(audio);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            // Same translation OrtSynthesizer documents: ORT reports a terminated
            // Run as a generic failure, and letting that escape would make every
            // stop look like an engine fault in the daemon's log.
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

    private float[] RunOne(InferenceSession session, long[] ids, PiperOptions opts, RunOptions runOptions)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input",
                new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("input_lengths",
                new DenseTensor<long>(new long[] { ids.Length }, [1])),
            // ORDER IS THE CONTRACT: noise, length, noise_w. The graph takes one
            // unnamed float[3] and mixing them up produces speech that is merely
            // wrong rather than a failure.
            NamedOnnxValue.CreateFromTensor("scales",
                new DenseTensor<float>(new[] { opts.NoiseScale, opts.LengthScale, opts.NoiseW }, new[] { 3 })),
        };

        // The fourth input exists only on a multi-speaker voice. Sending it to a
        // single-speaker graph is an error, not a no-op.
        if (_config.NumSpeakers > 1)
            inputs.Add(NamedOnnxValue.CreateFromTensor("sid",
                new DenseTensor<long>(new long[] { Math.Clamp(opts.SpeakerId, 0, _config.NumSpeakers - 1) }, [1])));

        using var results = session.Run(inputs, _outputNames ??= session.OutputMetadata.Keys.ToArray(), runOptions);
        return results.First().AsEnumerable<float>().ToArray();
    }

    /// <summary>
    /// Peak-normalise, then clip — what upstream does, reproduced rather than
    /// improved on.
    ///
    /// <para><b>Over the whole call, not per sentence.</b> Upstream normalises
    /// each sentence independently, which is audible as level pumping when a
    /// paragraph is read; our chunker usually hands over one sentence at a time,
    /// so for the common case this IS upstream's behaviour, and for the case
    /// where it is not, one gain across the chunk is the better of the two. The
    /// product's own volume trim is applied after this, in
    /// <c>SpeechSession</c>.</para>
    /// </summary>
    private static short[] Normalize(List<float> audio)
    {
        if (audio.Count == 0) return [];

        float peak = 0f;
        foreach (float s in audio) peak = Math.Max(peak, Math.Abs(s));
        float gain = peak < 1e-8f ? 0f : 1f / peak;

        var pcm = new short[audio.Count];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)Math.Clamp(audio[i] * gain * 32767f, -32767f, 32767f);
        return pcm;
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureLoadedLocked();
            }
        }, cancellationToken);

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private InferenceSession EnsureLoadedLocked()
    {
        if (_session is not null) return _session;

        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = _intraOpThreads,
            InterOpNumThreads = _interOpThreads,
        };

        try
        {
            if (_provider == ExecutionProviders.Cuda)
                sessionOptions.AppendExecutionProvider_CUDA(0);

            _session = new InferenceSession(_modelPath, sessionOptions);
        }
        catch
        {
            sessionOptions.Dispose();
            throw;
        }

        return _session;
    }

    /// <summary>
    /// Closes the door, then waits for renders already through it — the same
    /// shape as <see cref="OrtSynthesizer.Dispose"/> and for the same reason:
    /// disposing a session a <c>Run</c> is still executing on reclaims native
    /// handles underneath it, which is an access violation rather than an
    /// exception and takes the daemon with it.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        bool drained = _drained.Wait(DisposeDrainTimeout);

        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }

        // The phonemiser is NOT disposed: it is the caller's, one per process,
        // and it outlives every engine switch.
        if (drained) _drained.Dispose();
    }
}

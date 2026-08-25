using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Which engine speaks, decided per utterance from the voice and changed only
/// between them.
///
/// <para><b>The third layer of the same rule.</b> The audio device reconnects
/// between utterances (<c>LazyAudioSink</c>), the execution provider changes
/// between utterances (<see cref="ProviderSwitchingSynthesizer"/>), and now the
/// engine does too — all for the identical reason: a fresh anything has rendered
/// or played nothing while the clock and every scheduled boundary are counted
/// against the old one. Here it is sharper still, because the engines do not
/// share a sample rate: a switch inside an utterance would play the remainder at
/// the wrong speed as well as putting the highlight permanently wrong.</para>
///
/// <para><b>Both engines stay warm.</b> P2 measured a Piper session at 138-194 MB
/// against Supertonic's ~830 MB, so loading one does not have to evict the other
/// and a user who alternates does not pay a model load per press. One Piper
/// voice is held at a time; changing voice disposes the previous one after the
/// swap, off the gate, exactly as the provider switch does.</para>
///
/// <para><b>A refusal is not a fallback.</b> If the voice names a Piper voice
/// and that engine cannot be built — espeak-ng missing, a truncated download —
/// the utterance fails with a sentence naming the cause. Quietly speaking in
/// another voice would be a hotkey that works and lies, which is worse than one
/// that says why it cannot.</para>
/// </summary>
public sealed class EngineRoutingSynthesizer : ISynthesizer
{
    private readonly ISynthesizer _supertonic;
    private readonly PiperVoiceStore _store;
    private readonly Func<string, ISynthesizer> _buildPiper;
    private readonly Func<SpeechStateProbe> _sessionIdle;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private ISynthesizer _current;
    private string? _currentPiperVoice;
    private ISynthesizer? _piper;

    /// <summary>Voices whose calibration has been attempted, so a failure is not retried per press.</summary>
    private readonly HashSet<string> _calibrationAttempted = new(StringComparer.OrdinalIgnoreCase);
    private Task _calibration = Task.CompletedTask;

    /// <param name="supertonic">
    /// The provider-switching Supertonic synthesizer. Owned by the caller's
    /// <c>using</c>, not by this class — it is the default engine and outlives
    /// every switch.
    /// </param>
    /// <param name="buildPiper">Builds a Piper synthesizer for a model path.</param>
    /// <param name="sessionIdle">Whether it is safe to swap right now.</param>
    public EngineRoutingSynthesizer(
        ISynthesizer supertonic,
        PiperVoiceStore store,
        Func<string, ISynthesizer> buildPiper,
        Func<SpeechStateProbe> sessionIdle,
        Action<string> log)
    {
        _supertonic = supertonic ?? throw new ArgumentNullException(nameof(supertonic));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _buildPiper = buildPiper ?? throw new ArgumentNullException(nameof(buildPiper));
        _sessionIdle = sessionIdle ?? throw new ArgumentNullException(nameof(sessionIdle));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _current = supertonic;
    }

    /// <summary>Which engine is in force, for <c>status</c> and the log.</summary>
    public string Engine
    {
        get { lock (_gate) return _currentPiperVoice is null ? "supertonic" : "piper"; }
    }

    /// <summary>The Piper voice in force, or null while Supertonic is speaking.</summary>
    public string? PiperVoice
    {
        get { lock (_gate) return _currentPiperVoice; }
    }

    /// <summary>What <see cref="Select"/> decided.</summary>
    /// <param name="Changed">The engine or the voice actually moved.</param>
    /// <param name="SampleRate">What the sink must be tuned to before this utterance.</param>
    /// <param name="Error">Why the requested voice cannot speak, or null.</param>
    /// <param name="NeedsIdle">
    /// The engine would have changed, and the session is busy. <b>Not an error on
    /// its own</b>: a caller that is about to interrupt — which is what the read
    /// key does on every press — stops the session, waits, and asks again. A
    /// caller that is not gets a refusal, which is the same refusal
    /// <c>Speak</c> would have given it.
    /// </param>
    public readonly record struct Selection(
        bool Changed, int SampleRate, string? Error, bool NeedsIdle = false);

    /// <summary>
    /// Point this at the engine <paramref name="voiceId"/> belongs to.
    ///
    /// <para>Called from the daemon's speak funnel, in the same idle window as
    /// the provider switch and before the session starts scheduling anything. A
    /// swap while the session is busy is refused rather than deferred: the
    /// utterance in flight is using the current engine, and the next press asks
    /// again.</para>
    /// </summary>
    public Selection Select(string? voiceId)
    {
        string? modelPath = _store.ModelPath(voiceId);

        lock (_gate)
        {
            // Already there. Cheap and by far the common case — one directory
            // stat and a dictionary lookup.
            if (modelPath is null && _currentPiperVoice is null)
                return new Selection(false, _current.SampleRate, null);
            if (modelPath is not null && string.Equals(_currentPiperVoice, voiceId, StringComparison.OrdinalIgnoreCase))
                return new Selection(false, _current.SampleRate, null);

            if (_sessionIdle() != SpeechStateProbe.Idle)
                return new Selection(false, _current.SampleRate, null, NeedsIdle: true);

            if (modelPath is null)
            {
                var previous = _piper;
                _current = _supertonic;
                _currentPiperVoice = null;
                _piper = null;
                DisposeOffGate(previous, "the previous Piper voice");
                _log($"engine: piper -> supertonic, {_current.SampleRate} Hz");
                return new Selection(true, _current.SampleRate, null);
            }

            ISynthesizer built;
            try
            {
                built = _buildPiper(modelPath);
            }
            catch (Exception ex)
            {
                // Not a fallback to Supertonic: the user asked for this voice.
                string why = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
                _log($"engine: could not load the Piper voice '{voiceId}' ({why})");
                return new Selection(false, _current.SampleRate,
                    $"the voice '{voiceId}' could not be loaded — {why}");
            }

            var old = _piper;
            _piper = built;
            _current = built;
            _currentPiperVoice = voiceId;
            DisposeOffGate(old, "the previous Piper voice");

            _log($"engine: {(old is null ? "supertonic" : "piper")} -> piper '{voiceId}', " +
                 $"{built.SampleRate} Hz");

            StartCalibrationIfNeededLocked(voiceId!, built);
            return new Selection(true, built.SampleRate, null);
        }
    }

    /// <summary>
    /// Measure this voice's <c>length_scale</c> curve, once, in the background.
    ///
    /// <para><b>Why it is not on the press path.</b> It is 50-odd renders — 7 to
    /// 9 seconds for a <c>medium</c> voice and about 47 for a <c>high</c> one —
    /// and the first press with a new voice must not wait for it. Until the curve
    /// exists the daemon plans rates with
    /// <see cref="PiperRateCalibration.Reciprocal"/>, which is what upstream does
    /// and is up to 20% out; the log says which was used, and the next utterance
    /// picks up the file.</para>
    ///
    /// <para>Attempted once per voice per process. A voice whose calibration
    /// failed to write — a read-only store — must not re-measure on every press,
    /// which would be a 7-second CPU burst per utterance for the life of the
    /// daemon.</para>
    /// </summary>
    private void StartCalibrationIfNeededLocked(string voiceId, ISynthesizer synth)
    {
        if (!_calibrationAttempted.Add(voiceId)) return;
        if (_store.Calibration(voiceId) is not null) return;
        if (synth is not Onnx.Ort.PiperSynthesizer piper) return;

        string path = _store.CalibrationPath(voiceId);
        _calibration = Task.Run(() =>
        {
            try
            {
                _log($"calibrate: measuring '{voiceId}' — until it finishes, a requested rate " +
                     "is served by the uncalibrated reciprocal and can be up to 20% out");

                var voice = piper.Voice;
                var curve = PiperCalibrator.Measure(
                    scale => AverageSeconds(piper, voiceId, voice, scale),
                    DateTimeOffset.UtcNow);

                if (curve.TrySave(path, out string? error))
                    _log($"calibrate: '{voiceId}' delivers up to {curve.MaxRate:F2}x, " +
                         $"{curve.Points.Count} points, saved to {path}");
                else
                    _log($"calibrate: measured '{voiceId}' but could not save it to {path}: {error}. " +
                         "It will be measured again on the next daemon start.");
            }
            catch (ObjectDisposedException)
            {
                // The daemon is shutting down, or the voice changed under it.
                // Neither is a fault, and a stack-shaped line in the log for an
                // ordinary exit is how a clean shutdown gets read as a crash.
                _log($"calibrate: '{voiceId}' abandoned — the voice is no longer loaded");
            }
            catch (Exception ex)
            {
                _log($"calibrate: could not measure '{voiceId}': {ex.GetType().Name}: " +
                     $"{ex.Message.Split('\n')[0].Trim()}");
            }
        });
    }

    /// <summary>
    /// One rung: render the probe <see cref="PiperCalibrator.Repeats"/> times
    /// with the voice's OWN noise settings and average the durations. Both of
    /// those were measured decisions — see <see cref="PiperCalibrator.Repeats"/>.
    /// </summary>
    private static double AverageSeconds(
        Onnx.Ort.PiperSynthesizer piper, string voiceId, PiperVoiceConfig voice, float lengthScale)
    {
        double total = 0;
        for (int i = 0; i < PiperCalibrator.Repeats; i++)
        {
            var options = new PiperOptions(voiceId,
                LengthScale: lengthScale,
                NoiseScale: voice.NoiseScale,
                NoiseW: voice.NoiseW,
                SilenceSeconds: 0f);
            total += piper.Synthesize(PiperCalibrator.ProbeText, options).Length / (double)piper.SampleRate;
        }
        return total / PiperCalibrator.Repeats;
    }

    private void DisposeOffGate(ISynthesizer? synth, string what)
    {
        if (synth is null) return;

        // The session is idle and every render goes through _current, so nothing
        // is left inside it; the dispose is seconds of native teardown that the
        // gate should not be held for.
        Task.Run(() =>
        {
            try { synth.Dispose(); }
            catch (Exception ex) { _log($"engine: releasing {what} failed: {ex.Message}"); }
        });
    }

    // ------------------------------------------------------- ISynthesizer, delegated

    public int SampleRate
    {
        get { lock (_gate) return _current.SampleRate; }
    }

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        ISynthesizer current;
        lock (_gate) current = _current;
        return current.Synthesize(text, options, cancellationToken);
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        ISynthesizer current;
        lock (_gate) current = _current;
        return current.PreloadAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the Piper voice only. The Supertonic synthesizer belongs to the
    /// caller's <c>using</c> — disposing it here would dispose it twice on the
    /// ordinary shutdown path.
    /// </summary>
    public void Dispose()
    {
        ISynthesizer? piper;
        lock (_gate)
        {
            piper = _piper;
            _piper = null;
            _current = _supertonic;
            _currentPiperVoice = null;
        }

        try { piper?.Dispose(); } catch { /* going away either way */ }
    }
}

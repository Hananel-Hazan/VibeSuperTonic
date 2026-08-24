using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The synthesizer the session speaks through, which can change what it is
/// between utterances and never during one.
///
/// <para><b>Why a wrapper rather than a smarter backend.</b> ORT binds the
/// execution provider and sizes the intra-op thread pool when the session is
/// created, so "use the GPU now" means building another
/// <see cref="Core.Synthesis.ISynthesizer"/> and dropping the first — about a
/// second, and ~380 MB released and re-acquired. Putting that behind
/// <see cref="ISynthesizer"/> means <see cref="Core.Session.SpeechSession"/>,
/// the boundary planner and every test above it are unchanged: they hold one
/// synthesizer for the life of the daemon, exactly as before.</para>
///
/// <para><b>Between utterances, never inside one</b> — the same rule, and for the
/// same reason, as the audio-device reconnect in <c>LazyAudioSink</c>: a fresh
/// session has rendered nothing while the clock and every scheduled boundary are
/// counted against the old one. <see cref="ReevaluateWhenIdle"/> is called from
/// the daemon's one speak funnel and does nothing at all unless the session is
/// idle, so a switch cannot land underneath a render.</para>
///
/// <para><b>What it re-reads, and how often.</b> The power state on every check —
/// one file read, and always current, which is the whole reason the battery rule
/// does not need a D-Bus subscription. The stored profile only when
/// <c>benchmark.json</c> has changed, which is what lets <c>vst-ctl benchmark</c>
/// apply on the next utterance instead of the next daemon start.</para>
/// </summary>
public sealed class ProviderSwitchingSynthesizer : ISynthesizer
{
    private readonly HostConfig _config;
    private readonly Func<int, string, ISynthesizer> _build;
    private readonly Func<SpeechStateProbe> _sessionIdle;
    private readonly Action<string> _log;
    private readonly string? _voiceOverride;
    private readonly string? _languageOverride;

    private readonly object _gate = new();
    private ISynthesizer _current;
    private ExecutionDecision _decision;

    private BenchmarkProfile? _profile;
    private long _profileMtime = long.MinValue;

    /// <summary>
    /// Why CUDA cannot be used, or null while it can.
    ///
    /// <para><b>Latched, deliberately.</b> The startup probe answers "is the
    /// provider loadable"; a session build can still fail afterwards — out of
    /// VRAM, a driver reset between the two. Retrying that on every utterance
    /// would pay a failed session build, seconds long, before every single press.
    /// So the first build failure turns the GPU off for the life of the process
    /// and says so; restarting the daemon is what re-asks the question. This is
    /// the small, comprehensible half of the DirectML latch that
    /// <see cref="Onnx.Ort.OrtSynthesizer"/>'s header describes not carrying
    /// across.</para>
    /// </summary>
    private string? _gpuUnavailable;

    /// <param name="config">Settings and paths; re-read on its own mtime rules.</param>
    /// <param name="initial">The decision <c>Program</c> already made and built the first session from.</param>
    /// <param name="first">That session.</param>
    /// <param name="build">Builds a synthesizer for (threads, provider).</param>
    /// <param name="gpuUnavailable">The startup CUDA probe's answer: null when the GPU is usable.</param>
    /// <param name="sessionIdle">Whether it is safe to swap right now.</param>
    /// <param name="log">The daemon log. A switch nobody can see is a switch nobody can debug.</param>
    public ProviderSwitchingSynthesizer(
        HostConfig config,
        ExecutionDecision initial,
        ISynthesizer first,
        Func<int, string, ISynthesizer> build,
        string? gpuUnavailable,
        Func<SpeechStateProbe> sessionIdle,
        Action<string> log,
        string? voiceOverride = null,
        string? languageOverride = null)
    {
        _config = config;
        _decision = initial;
        _current = first;
        _build = build;
        _gpuUnavailable = gpuUnavailable;
        _sessionIdle = sessionIdle;
        _log = log;
        _voiceOverride = voiceOverride;
        _languageOverride = languageOverride;
    }

    /// <summary>What is in force right now, for <c>status</c> and <c>config</c>.</summary>
    public ExecutionDecision Decision
    {
        get { lock (_gate) return _decision; }
    }

    /// <summary>
    /// The providers a sweep should include beyond the CPU. Empty on the ordinary
    /// install, where the provider pack is absent — and a sweep that measured a
    /// provider this daemon cannot use would write a profile it then has to
    /// ignore, which is worse than not measuring it.
    /// </summary>
    public IReadOnlyList<string> SweepableGpuProviders
    {
        get { lock (_gate) return _gpuUnavailable is null ? [ExecutionProviders.Cuda] : []; }
    }

    /// <summary>
    /// Decide again, and rebuild if the answer changed. Called from the speak
    /// path before the session is asked to speak.
    /// </summary>
    /// <returns>True when the provider or thread count actually changed.</returns>
    public bool ReevaluateWhenIdle()
    {
        if (_sessionIdle() != SpeechStateProbe.Idle) return false;

        lock (_gate)
        {
            var next = Decide();
            if (next.Provider == _decision.Provider && next.Threads == _decision.Threads)
            {
                // The reason can move while the answer does not — "on battery"
                // becoming "on battery, benchmark 2026-08-24" after a sweep. Keep
                // the newer sentence; there is nothing to rebuild for it.
                _decision = next;
                return false;
            }

            var previous = _decision;
            ISynthesizer built;
            try
            {
                built = _build(next.Threads, next.Provider);
            }
            catch (Exception ex) when (next.Provider != ExecutionProviders.Cpu)
            {
                _gpuUnavailable = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
                _log($"inference: {ExecutionProviders.Display(next.Provider)} session failed to build " +
                     $"({_gpuUnavailable}); staying on {previous.Describe()} and not retrying until restart");

                // Ask again with the GPU now known-unavailable, so the decision
                // and the sentence it carries agree with what actually happened.
                var fallback = Decide();
                if (fallback.Provider == _decision.Provider && fallback.Threads == _decision.Threads)
                {
                    _decision = fallback;
                    return false;
                }

                try
                {
                    built = _build(fallback.Threads, fallback.Provider);
                    next = fallback;
                }
                catch (Exception second)
                {
                    // The CPU provider failing is not a provider problem, it is a
                    // broken install. Keep the working session rather than
                    // replacing it with nothing.
                    _log($"inference: CPU session failed to build too ({second.GetType().Name}: " +
                         $"{second.Message.Split('\n')[0].Trim()}); keeping {previous.Describe()}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log($"inference: could not rebuild as {next.Describe()} " +
                     $"({ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}); keeping {previous.Describe()}");
                return false;
            }

            var old = _current;
            _current = built;
            _decision = next;

            // After the swap: the old session has no callers left — the session is
            // idle and every render goes through _current — and disposing it is
            // seconds of native teardown we should not hold the gate for.
            Task.Run(() =>
            {
                try { old.Dispose(); }
                catch (Exception ex) { _log($"inference: releasing the previous session failed: {ex.Message}"); }
            });

            _log($"inference: {previous.Describe()} -> {next.Describe()}");
            return true;
        }
    }

    /// <summary>
    /// The decision, from what is true at this moment: the settings file, the
    /// stored profile, the probe, and the power lead.
    /// </summary>
    private ExecutionDecision Decide()
    {
        var settings = _config.Settings;

        string preference = settings.Provider?.Trim().ToLowerInvariant() ?? ProviderPreference.Auto;
        if (!ProviderPreference.IsKnown(preference))
        {
            // A typo in settings.json must not silently mean "auto" — that is a
            // setting the user believes they have set. Say it once per change and
            // carry on with the default.
            _log($"inference: settings.json Provider is '{settings.Provider}', which is not " +
                 $"auto, cpu or gpu — using auto");
            preference = ProviderPreference.Auto;
        }

        var machine = MachineFacts.Current(
            _config.ModelsRoot, settings.TotalStep,
            _voiceOverride ?? settings.DefaultVoice,
            _languageOverride ?? settings.Language);

        return ExecutionDecision.Decide(
            LoadProfileIfChanged(),
            machine,
            settings.MaxCpuPercent,
            Environment.ProcessorCount,
            preference,
            _gpuUnavailable,
            machine.PowerState,
            settings.GpuOnBattery);
    }

    /// <summary>
    /// The stored profile, re-parsed only when the file has actually changed.
    ///
    /// <para>Costs one <c>stat</c> per utterance, which is the same bargain
    /// <see cref="HostConfig"/> already makes for settings.json — and it is what
    /// makes <c>vst-ctl benchmark</c> take effect on the next press rather than
    /// the next daemon start, which is what everybody expects it to do.</para>
    /// </summary>
    private BenchmarkProfile? LoadProfileIfChanged()
    {
        try
        {
            string path = LinuxDataPaths.BenchmarkFile(_config.DataDir);
            long mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : long.MinValue;
            if (mtime == _profileMtime) return _profile;

            _profileMtime = mtime;
            _profile = BenchmarkStore.Load(_config.DataDir);
            return _profile;
        }
        catch
        {
            // BenchmarkStore.Load already swallows a malformed file; this catch is
            // for the stat itself, on a data directory that has gone away under a
            // running daemon. The last profile read is a better answer than none.
            return _profile;
        }
    }

    // ------------------------------------------------------- ISynthesizer, delegated

    public int SampleRate
    {
        get { lock (_gate) return _current.SampleRate; }
    }

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        // Captured under the gate and used outside it. A swap can only happen
        // while the session is idle, so nothing can be swapped out from under a
        // render in progress — and the dispose that follows a swap waits for
        // in-flight renders anyway (OrtSynthesizer.Dispose).
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

    public void Dispose()
    {
        ISynthesizer current;
        lock (_gate) current = _current;
        current.Dispose();
    }
}

/// <summary>
/// Whether the session can be interrupted for a provider switch. Deliberately
/// two states rather than the session's full state enum: the only question here
/// is "is anything in flight", and giving the switcher the whole enum invites it
/// to develop opinions about pausing.
/// </summary>
public enum SpeechStateProbe
{
    Idle,
    Busy,
}

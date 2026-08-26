using System.Diagnostics;
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
/// <para><b>With exactly one exception</b>, added in 0.2.11: a GPU that fails
/// during a render is swapped out mid-utterance by <see cref="FallBackToCpu"/>,
/// because the choice there is not between a good clock and a bad one but
/// between audio and silence.</para>
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
    /// Where render timings go. Created lazily on the first render so a daemon
    /// that never speaks does not create the file.
    /// </summary>
    private UsageRecorder? _usage;

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
    ///
    /// <para><b>Set from two places, and the second is the one that matters.</b>
    /// A failed session BUILD is the easy case and <see cref="ReevaluateWhenIdle"/>
    /// has always handled it. A session that builds and then fails on its first
    /// Run is the case that reached a user: see <see cref="FallBackToCpu"/> for
    /// what that looked like and why no probe can catch it.</para>
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
                _gpuUnavailable = Summarize(ex);
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
            // The PATH, not the directory. BenchmarkStore moved into Core on
            // 2026-08-19 and took the file path instead of the data directory
            // with it; this call auto-merged through that change because both
            // are strings, and a directory simply reads back as no profile —
            // so the GPU, the battery rule and every benchmark stopped applying
            // after the first utterance, with a green build behind it.
            _profile = BenchmarkStore.Load(path);
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

    /// <summary>
    /// Asked once at startup by <c>EngineRoutingSynthesizer.Select</c>, and that
    /// is what makes it a GPU failure site: <c>OrtSynthesizer</c> loads the model
    /// lazily, so this innocuous-looking property is where the ONNX session is
    /// actually built and where <c>cudaSetDevice</c> is actually called.
    ///
    /// <para>Measured 2026-08-26 on the wedged A2000: unwrapped, the CUDA failure
    /// came out of here as an unhandled exception on the startup path and killed
    /// the daemon outright — the hotkey then started a daemon that died before it
    /// could listen, over and over, which the desktop reports as a stream of
    /// crash notifications. Strictly worse than the silent failure this change
    /// set out to fix, and it lives one property away from it.</para>
    /// </summary>
    public int SampleRate => WithGpuFallback(static s => s.SampleRate);

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        bool fellBack = false;
        int sampleRate = 0;

        short[] pcm = WithGpuFallback(
            s =>
            {
                short[] rendered = s.Synthesize(text, options, cancellationToken);

                // Asked of the synthesizer that just rendered, not of this class:
                // going through the property would re-enter WithGpuFallback, and
                // asking the FALLEN-BACK-TO session for a rate to attribute to
                // the one that failed is how a sample ends up on the wrong
                // provider. The model is loaded by now, so this is a field read.
                sampleRate = s.SampleRate;
                return rendered;
            },
            cancellationToken,
            onFallback: () => fellBack = true);

        // A render that had to rebuild the session mid-flight measures the
        // rebuild, not the provider: seconds of session construction attributed
        // to the CPU would make the CPU look catastrophically slow in exactly the
        // window where the routing script is trying to judge it. Dropped.
        if (!fellBack && sampleRate > 0 && pcm.Length > 0)
            Record(Stopwatch.GetElapsedTime(started), pcm.Length, sampleRate);

        return pcm;
    }

    /// <summary>
    /// File one render against whichever provider produced it, as wall time over
    /// the duration of the audio it made — the same quantity the benchmark calls
    /// <c>Rtf</c>, so the two files can be compared directly.
    /// </summary>
    private void Record(TimeSpan wall, int samples, int sampleRate)
    {
        try
        {
            string provider;
            int threads;
            lock (_gate)
            {
                provider = _decision.Provider;
                threads = _decision.Threads;
                _usage ??= new UsageRecorder(
                    LinuxDataPaths.UsageStatsFile(_config.DataDir), _log);
            }

            double audioSeconds = samples / (double)sampleRate;
            if (audioSeconds <= 0) return;

            _usage.Add(provider, threads, wall.TotalSeconds / audioSeconds);
        }
        catch
        {
            // Bookkeeping must never be able to fail an utterance that has
            // already produced its audio.
        }
    }

    /// <summary>Write any buffered timings. Called when the daemon stops cleanly.</summary>
    public void FlushUsage()
    {
        UsageRecorder? usage;
        lock (_gate) usage = _usage;
        usage?.Flush();
    }

    /// <summary>
    /// Run one call against the current session and, if the GPU is what failed,
    /// rebuild on the CPU and run it again.
    ///
    /// <para><b>Why every entry point goes through here.</b> A GPU that has gone
    /// away fails at whichever call first touches the device, and which call that
    /// is depends on when it went away and on ORT's laziness — the session build
    /// (<see cref="ReevaluateWhenIdle"/>), the model load behind
    /// <see cref="SampleRate"/> or <see cref="PreloadAsync"/>, or the first
    /// <c>Run</c> inside <see cref="Synthesize"/>. 0.2.10 handled exactly the
    /// first of those. Both of the others were observed on one afternoon on one
    /// broken card, with two different and equally bad symptoms, so the rule is
    /// the surface and not the site.</para>
    ///
    /// <para><b>What went wrong on 2026-08-26.</b> An RTX A2000 entered "GPU
    /// requires reset" (nvidia-smi: ERR! across the board, Channel Repair
    /// Pending). The provider library still loaded, so
    /// <c>OrtSynthesizer.ProbeCuda</c> answered "available" — it asks whether the
    /// library loads, which is a different question from whether the device
    /// answers, and the device can leave at any time after it is asked anyway.
    /// The daemon reported <c>ModelLoaded: true</c> and "CUDA, 2 threads", and
    /// every hotkey press died on "CUDA failure 100: no CUDA-capable device is
    /// detected". No sound, no change, and the next press did the same thing.
    /// The user's report was that the hotkeys had stopped working.</para>
    ///
    /// <para><b>Latching on any non-cancellation exception is deliberate</b> and
    /// slightly over-broad. A fault that is really the text or the model will
    /// fail on the CPU too and propagate from the retry, with the GPU switched
    /// off until restart as the only side effect — a performance cost paid to
    /// keep this path simple. A recovery path with its own bugs is worse than a
    /// blunt one that works.</para>
    /// </summary>
    private T WithGpuFallback<T>(
        Func<ISynthesizer, T> call,
        CancellationToken cancellationToken = default,
        Action? onFallback = null)
    {
        // Captured under the gate and used outside it. An idle-time swap cannot
        // land under a render — the dispose that follows one waits for in-flight
        // renders (OrtSynthesizer.Dispose) — and the mid-render swap below
        // replaces _current only after the call it is recovering from has thrown.
        ISynthesizer current;
        string provider;
        lock (_gate)
        {
            current = _current;
            provider = _decision.Provider;
        }

        try
        {
            return call(current);
        }
        catch (Exception ex) when (
            provider != ExecutionProviders.Cpu &&
            ex is not OperationCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            var cpu = FallBackToCpu(ex, provider);
            if (cpu is null) throw;
            onFallback?.Invoke();
            return call(cpu);
        }
    }

    /// <summary>
    /// One short phrase naming why the GPU is out, for the reason string that
    /// <c>status</c>, <c>config</c> and the tray tooltip all render.
    ///
    /// <para><b>Why this is not just the message.</b> ORT reports a CUDA fault as
    /// a single ~900-character line: two full C++ signatures, a source path, a
    /// line number and the expression that failed, with the one sentence a person
    /// needs — "CUDA failure 100: no CUDA-capable device is detected" — buried in
    /// the middle. <see cref="ExecutionDecision.Describe"/> puts this inside
    /// brackets after "CPU, 2 threads", so pasting the raw message there makes
    /// every status line unreadable and the tooltip useless.</para>
    ///
    /// <para>The full text still reaches the daemon log, which is where a fault
    /// is actually diagnosed. This is the version for the sentence.</para>
    /// </summary>
    private static string Summarize(Exception ex)
    {
        string first = ex.Message.Split('\n')[0].Trim();

        // The informative fragment of an ORT CUDA failure, when there is one.
        int at = first.IndexOf("CUDA failure", StringComparison.Ordinal);
        if (at >= 0)
        {
            string tail = first[at..];
            int end = tail.IndexOf(';');
            if (end > 0) tail = tail[..end];
            return $"{ex.GetType().Name}: {tail.Trim()}";
        }

        const int Max = 160;
        return first.Length <= Max
            ? $"{ex.GetType().Name}: {first}"
            : $"{ex.GetType().Name}: {first[..Max].TrimEnd()}…";
    }

    /// <summary>
    /// Turn the GPU off for the life of the process and rebuild on the CPU, from
    /// inside a render rather than between them.
    /// </summary>
    /// <remarks>
    /// The class rule is "between utterances, never inside one", and this is the
    /// one deliberate exception. That rule protects the audio clock and the
    /// scheduled word boundaries, which are counted against a session that has
    /// already rendered; here the alternative is not a slightly wrong clock but
    /// no audio at all, and every chunk is an independent text-to-PCM call
    /// against the same model, so a chunk rendered on the CPU sounds like the
    /// chunk before it rendered on the GPU.
    /// </remarks>
    /// <returns>The CPU synthesizer to retry on, or null when even that failed.</returns>
    private ISynthesizer? FallBackToCpu(Exception cause, string failedProvider)
    {
        lock (_gate)
        {
            // A multi-chunk utterance can arrive here more than once — one render
            // per chunk was already in flight when the first one failed. The first
            // caller through does the work; the rest take the session it left.
            if (_decision.Provider == ExecutionProviders.Cpu) return _current;

            _gpuUnavailable = Summarize(cause);

            // The log gets the whole thing — it is the only place the C++ frames
            // and the failing expression are of any use, and a field report is
            // written from here.
            _log($"inference: {ExecutionProviders.Display(failedProvider)} failed in use " +
                 $"({cause.GetType().Name}: {cause.Message.Split('\n')[0].Trim()}); " +
                 $"falling back to the CPU and not retrying until restart");

            var previous = _decision;

            // Decide() has just been told the GPU is unavailable, so it returns a
            // CPU answer with the right thread count and an honest reason.
            var next = Decide();

            ISynthesizer built;
            try
            {
                built = _build(next.Threads, next.Provider);
            }
            catch (Exception ex)
            {
                // The CPU provider failing is a broken install, not a provider
                // problem. There is nothing left to speak with.
                _log($"inference: the CPU session failed to build too ({ex.GetType().Name}: " +
                     $"{ex.Message.Split('\n')[0].Trim()}); there is nothing left to speak with");
                return null;
            }

            var old = _current;
            _current = built;
            _decision = next;

            // Off the gate: tearing down a session on a wedged GPU can block, and
            // this one is by definition on a GPU that has just failed.
            Task.Run(() =>
            {
                try { old.Dispose(); }
                catch (Exception ex) { _log($"inference: releasing the previous session failed: {ex.Message}"); }
            });

            _log($"inference: {previous.Describe()} -> {next.Describe()}");
            return built;
        }
    }

    /// <summary>
    /// The other lazy-load site, and the one the daemon calls deliberately so the
    /// model is warm before the first press. <c>await</c> rather than returning
    /// the task, because <c>OrtSynthesizer</c> loads inside a <c>Task.Run</c> and
    /// a failure there surfaces on the await — returning the task unwrapped would
    /// hand the exception to a caller that is not the one holding the fallback.
    /// </summary>
    public async Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        ISynthesizer current;
        string provider;
        lock (_gate)
        {
            current = _current;
            provider = _decision.Provider;
        }

        try
        {
            await current.PreloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            provider != ExecutionProviders.Cpu &&
            ex is not OperationCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            var cpu = FallBackToCpu(ex, provider);
            if (cpu is null) throw;
            await cpu.PreloadAsync(cancellationToken).ConfigureAwait(false);
        }
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

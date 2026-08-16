using Microsoft.ML.OnnxRuntime;
using Microsoft.Win32;
using Supertonic;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Engine.Settings;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// One adapter per voice id. The heavy <see cref="TextToSpeech"/> ONNX session set
/// is shared statically across all voice ids — only the small <see cref="Style"/>
/// (per-voice embedding JSON) is per-instance. Synthesis is serialized via a static
/// gate because (a) ONNX <c>InferenceSession.Run</c> shouldn't be called concurrently
/// against the same DirectML provider, and (b) the device-loss recovery path needs
/// exclusive access to swap the shared session.
///
/// DirectML device-loss recovery: when the GPU is reset (TDR, sleep/resume, driver
/// crash) every <c>InferenceSession</c> in this process becomes permanently dead.
/// We catch the device-loss exception, dispose+null the shared session, and retry
/// ONCE on CPU — never on GPU. The earlier "retry on GPU" path could stall 10–30 s
/// inside <c>LoadTextToSpeech</c> on a still-recovering driver (Intel/AMD post-TDR
/// is the common offender) while holding the gate, which the SAPI host sees as
/// a freeze. CPU retry takes a known few seconds and always completes.
///
/// A SECOND loss within 60 s latches DML off for the rest of the process so the
/// NEXT utterance also skips GPU. The launcher's "Reset selected" clears the latch
/// so the user can re-try DirectML without restarting the host app.
///
/// Concurrency invariant: the shared session reference is read ONLY under
/// <c>_ttsGate</c>. Capturing it outside the gate and then using it inside is
/// the TOCTOU race the predecessor walked into — another thread could dispose
/// the session between read and use, leading to <c>ObjectDisposedException</c>
/// or, worse, native AV in ORT after the handle was reclaimed.
/// </summary>
internal sealed class SupertonicAdapter
{
    // Supertonic's natural pace; range 0.9-1.5 per their docs.
    public const float DefaultSpeed = 1.05f;

    private static readonly object _ttsGate = new();
    private static TextToSpeech? _sharedTts;
    private static string? _ttsOnnxDir;

    // Process-local DML health latch. Set true after repeated device-loss; once set,
    // GetSharedTts ignores the registry's UseDirectML and loads on CPU. Never
    // persisted — a driver glitch shouldn't silently downgrade the user's settings.
    private static volatile bool _useDmlLatchedOff;
    // Sliding window of recent device-loss timestamps for the latch heuristic.
    // Threshold = 2: the first loss might be a one-off (sleep/resume, transient
    // driver hiccup) and the NEXT utterance is allowed to retry GPU. Within the
    // CURRENT utterance, the retry always goes to CPU (forceCpu parameter on
    // GetSharedTtsLocked) regardless of the latch — that's how we avoid the
    // doomed second GPU load that stalls the host.
    private static readonly object _lossLogGate = new();
    private static readonly List<long> _lossTimestampsMs = new();
    private const int LossLatchThreshold = 2;
    private const int LossLatchWindowMs = 60_000;

    // Per-call watchdog. ONNX Run is not interruptible from the calling thread
    // without RunOptions.Terminate. A wedged DirectML driver can hang Run
    // indefinitely. If the call doesn't finish within this budget we (a) call
    // Terminate on the RunOptions to abort the in-flight Run, and (b) treat
    // it as a synthetic device-loss — the existing catch path disposes the
    // session, sets the latch as appropriate, and retries on CPU on attempt 1.
    // 30 s is roomy for legitimate CPU synth of the longest legal chunk
    // (~200 chars) on slow hardware.
    private const int SynthCallWatchdogMs = 30_000;
    // Dispose backpressure: if more than this many native disposes are
    // already pending, a chronically sick DML driver is stuck in cleanup.
    // Queue no further dispose tasks — leak the handle instead.
    private const int MaxPendingDisposes = 4;

    // External reset request — set by SessionRegistry when the launcher writes a
    // <pid>.reset marker. Consumed at the top of every Synthesize call via
    // Interlocked.Exchange so two threads can't both observe true and both
    // dispose (which would nuke a freshly-rebuilt session and force a second
    // unnecessary rebuild).
    private static int _resetRequestedFlag;
    // Counter exposed to the launcher as part of session telemetry.
    private static int _deviceLossCount;
    // Pending native disposes — incremented when DisposeSharedTtsLocked spawns
    // its fire-and-forget Task and decremented when that Task finishes. Stays
    // nonzero while a sick DML driver is stuck inside D3D12 cleanup. Surfaced
    // via LastDeviceEvent if any dispose has been pending for more than 30 s
    // so the user gets some indication their driver is unhappy at the
    // cleanup-layer (even when synthesis itself keeps working on CPU).
    private static int _pendingDisposeCount;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _disposeStartTicksMs = new();
    private static int _disposeTaskSeq;
    // Short human-readable note about the most recent DML event (loss caught,
    // retry succeeded, latch tripped). TelemetryWriter surfaces this as
    // LastError when SapiEngine isn't reporting a synthesis exception — that's
    // the only signal the user has that a silent GPU→CPU fallback happened.
    private static volatile string? _lastDeviceEvent;
    public static int DeviceLossCount => _deviceLossCount;
    public static bool DmlLatchedOff => _useDmlLatchedOff;
    public static string LastDeviceEvent
    {
        get
        {
            // Augment the cached message with a stuck-dispose warning if any
            // background dispose has been pending too long. The check is cheap
            // (one dictionary scan) and only matters when something's wrong.
            if (Volatile.Read(ref _pendingDisposeCount) > 0)
            {
                long now = Environment.TickCount64;
                foreach (var kv in _disposeStartTicksMs)
                {
                    if (now - kv.Value > 30_000)
                    {
                        return (_lastDeviceEvent ?? "")
                            + $" [warn: native dispose has been pending {((now - kv.Value) / 1000)}s — DML driver may be stuck in cleanup]";
                    }
                }
            }
            return _lastDeviceEvent ?? "";
        }
    }

    // ==== Test injection (internal) ====
    // Set to nonzero from the test harness to make the NEXT Synthesize call
    // throw a synthetic device-loss exception (matching IsDeviceLoss's substring
    // checks) instead of running ONNX. Drives the catch/recover/retry path
    // deterministically without needing to artificially kill the GPU. Decremented
    // once consumed. Zero in production.
    internal static int _testInjectDeviceLossOnNextCall;

    // Test hook: set to nonzero to make the next Synthesize call block
    // inside tts.Call (on _testHangRelease.Wait) until either the watchdog
    // fires or the test releases the event. Used by Stress12.
    internal static int _testInjectHangOnNextCall;
    internal static readonly ManualResetEventSlim _testHangRelease = new(false);

    /// <summary>
    /// Requests a session drop AND clears the DML health latch so the next
    /// rebuild gets to retry DirectML. The latch is process-local, so without
    /// this clear the only way to re-enable GPU was to restart the host app.
    /// </summary>
    public static void RequestReset()
    {
        Interlocked.Exchange(ref _resetRequestedFlag, 1);
        _useDmlLatchedOff = false;
        lock (_lossLogGate) _lossTimestampsMs.Clear();
        _lastDeviceEvent = "Reset requested — DirectML re-enabled. Next Speak will retry GPU.";
    }

    private readonly object _styleGate = new();
    private readonly string _voiceId;
    private Style? _style;

    public SupertonicAdapter(string voiceId) => _voiceId = voiceId;

    private static string GetBaseDirOrThrow()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
        var baseDir = k?.GetValue("BaseDir") as string;
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
            throw new InvalidOperationException($"VibeSuperTonic BaseDir not configured or missing: '{baseDir}'");
        return baseDir!;
    }

    /// <summary>
    /// Returns the cached shared <see cref="TextToSpeech"/>, building it on first use.
    /// Caller MUST hold <see cref="_ttsGate"/>. <paramref name="forceCpu"/> overrides
    /// the configured EP for THIS load only — used by the device-loss retry path so
    /// a still-recovering DML driver doesn't get a second doomed GPU load attempt
    /// (the failure mode that stalls the SAPI host while the gate is held).
    /// </summary>
    private static TextToSpeech GetSharedTtsLocked(out string baseDir, bool forceCpu = false)
    {
        baseDir = GetBaseDirOrThrow();
        if (_sharedTts is not null) return _sharedTts;

        string onnxDir = Path.Combine(baseDir, "models", "onnx");
        if (!Directory.Exists(onnxDir))
            throw new FileNotFoundException($"Supertonic ONNX directory missing: {onnxDir}");
        // Pull the user's tweaks from the registry-backed settings (cached by
        // Settings\Version). The Control Panel surfaces all of these on the Advanced
        // tab so users can experiment with their CPU/GPU.
        int intraOp = 0, interOp = 1, dmlDevice = 0;
        bool useDml = false;
        try
        {
            var es = EngineSettingsCache.Resolve();
            intraOp = es.OnnxThreads;
            interOp = es.OnnxInterOpThreads;
            useDml = es.UseDirectML;
            dmlDevice = es.DirectMLDeviceId;
        }
        catch { /* defaults */ }
        // Honor the in-process latch: repeated GPU device-loss in this process
        // means the DML path is unhealthy here; force CPU until process exit.
        if (_useDmlLatchedOff || forceCpu) useDml = false;
        try
        {
            _sharedTts = Helper.LoadTextToSpeech(onnxDir, useGpu: useDml,
                intraOpThreads: intraOp, interOpThreads: interOp, directMLDevice: dmlDevice);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw new InvalidOperationException(NativeLoadDiagnostic(), ex);
        }
        _ttsOnnxDir = onnxDir;
        return _sharedTts;
    }

    /// <summary>
    /// Is this ONNX Runtime's native DLL failing to load, in any of the shapes
    /// that failure arrives in? The first touch of ORT runs a static constructor,
    /// so the real cause is usually buried under a
    /// <see cref="TypeInitializationException"/>.
    /// </summary>
    private static bool IsNativeLoadFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is DllNotFoundException or BadImageFormatException) return true;
        return false;
    }

    /// <summary>
    /// Replace Windows' least helpful error message with the actual cause.
    ///
    /// A missing Visual C++ redistributable makes <c>onnxruntime.dll</c> fail to
    /// load with "Unable to load DLL '…\onnxruntime.dll' or one of its
    /// dependencies: The specified module could not be found. (0x8007007E)" —
    /// which names the file that IS present and says nothing about the one that
    /// is not. Every reader checks onnxruntime.dll, finds it, and starts hunting
    /// a corrupt download.
    ///
    /// This message reaches the user twice: engine.log, and the Monitor tab's
    /// LastError column via the Speak catch. Both are places someone lands while
    /// wondering why a voice they can see makes no sound.
    /// </summary>
    private static string NativeLoadDiagnostic()
    {
        string bitness = IntPtr.Size == 8 ? "x64" : "x86";
        string redist = IntPtr.Size == 8 ? "Microsoft.VCRedist.2015+.x64" : "Microsoft.VCRedist.2015+.x86";
        return
            $"ONNX Runtime's native library could not be loaded in this {bitness} host. " +
            "The file itself ships with the engine, so the missing piece is one of its " +
            $"dependencies — almost always the {bitness} Visual C++ runtime, which ONNX " +
            "Runtime links against and which Windows does not name in its error. " +
            $"Fix: winget install {redist}   " +
            "(The Control Panel's Status tab checks this and can install it for you.)";
    }

    /// <summary>
    /// External entry point — used by preload helpers that don't have Synthesize
    /// on the stack. Locks <c>_ttsGate</c> internally. Inside Synthesize, prefer
    /// <see cref="GetSharedTtsLocked"/> so the gate stays held across the get
    /// and the subsequent <c>Run</c> (no TOCTOU window).
    /// </summary>
    private static TextToSpeech GetSharedTts(out string baseDir)
    {
        lock (_ttsGate) return GetSharedTtsLocked(out baseDir);
    }

    /// <summary>
    /// Drops the shared session from the cache. Caller MUST hold <c>_ttsGate</c>.
    /// The native <c>Dispose</c> runs on a background task because on a sick DML
    /// driver post-device-loss it can stall for seconds inside D3D12 cleanup, and
    /// we don't want that holding the gate (the visible symptom would be a hung
    /// SAPI host). The Task root keeps the object alive until cleanup finishes;
    /// if it never does, the handles leak until process exit — better than a
    /// deadlock that the user can only resolve by killing the host.
    ///
    /// We track pending disposes so that, if one stays stuck beyond 30 s, the
    /// next telemetry write surfaces a warning. That's the user's signal that
    /// their DML driver isn't just losing the device — it's also failing to
    /// clean it up. (Without the warning the only symptom would be slowly
    /// growing native memory, which the launcher's monitor doesn't show.)
    /// </summary>
    private static void DisposeSharedTtsLocked()
    {
        var dead = _sharedTts;
        _sharedTts = null;
        _ttsOnnxDir = null;
        if (dead is null) return;

        // Backpressure: if multiple disposes are already pending, the DML
        // driver is stuck in cleanup. Queuing yet another Task that will
        // also stall just grows the threadpool and leaks native handles
        // faster. Leak this handle deliberately — GC may eventually
        // finalize the SafeHandle on process exit. LastDeviceEvent's
        // existing 30 s warning surface tells the user their driver is
        // sick; the user can restart the host to clear.
        if (Volatile.Read(ref _pendingDisposeCount) > MaxPendingDisposes)
        {
            _lastDeviceEvent = (_lastDeviceEvent ?? "")
                + " [warn: skipping native dispose — too many already pending]";
            return;
        }

        int id = Interlocked.Increment(ref _disposeTaskSeq);
        Interlocked.Increment(ref _pendingDisposeCount);
        _disposeStartTicksMs[id] = Environment.TickCount64;
        Task.Run(() =>
        {
            try { dead.Dispose(); }
            catch { /* best-effort */ }
            finally
            {
                _disposeStartTicksMs.TryRemove(id, out _);
                Interlocked.Decrement(ref _pendingDisposeCount);
            }
        });
    }

    /// <summary>
    /// Returns true if the exception (or any wrapping <see cref="AggregateException"/>
    /// or InnerException in its tree) is a DirectML device-loss / device-removed
    /// failure. ORT wraps the DXGI HRESULT into an <c>OnnxRuntimeException</c>
    /// whose message contains the HRESULT and a description we can match on.
    /// <para>
    /// We walk both <c>InnerException</c> and (for AggregateException)
    /// <c>InnerExceptions</c>. The predecessor's linear walk missed the
    /// Aggregate branch — under ORT versions that raise device-loss inside a
    /// parallel scheduler, the device-loss could be siblings in an aggregate
    /// and the walk would only check the first. A depth cap prevents pathological
    /// cyclic exception chains from looping (rare but observed in some interop
    /// scenarios).
    /// </para>
    /// </summary>
    private static bool IsDeviceLoss(Exception ex) => IsDeviceLossInternal(ex, depth: 0);

    private static bool IsDeviceLossInternal(Exception? ex, int depth)
    {
        if (ex is null || depth > 16) return false;
        string msg = ex.Message ?? "";
        if (msg.Contains("887A0005", StringComparison.Ordinal)) return true;
        if (msg.Contains("887A0020", StringComparison.Ordinal)) return true; // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        if (msg.Contains("device has been suspended", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("device removed", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("GetDeviceRemovedReason", StringComparison.Ordinal)) return true;
        if (msg.Contains("DXGI_ERROR", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("VIBESUPERTONIC_TEST_DEVICE_LOSS", StringComparison.Ordinal)) return true;

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                if (IsDeviceLossInternal(inner, depth + 1)) return true;
        }
        return IsDeviceLossInternal(ex.InnerException, depth + 1);
    }

    /// <summary>
    /// Records a device-loss event and returns true if the latch should engage.
    /// Window-based: <see cref="LossLatchThreshold"/> losses within
    /// <see cref="LossLatchWindowMs"/> trips the latch.
    /// </summary>
    private static bool RecordLossAndShouldLatch()
    {
        Interlocked.Increment(ref _deviceLossCount);
        long now = Environment.TickCount64;
        lock (_lossLogGate)
        {
            _lossTimestampsMs.Add(now);
            _lossTimestampsMs.RemoveAll(t => now - t > LossLatchWindowMs);
            return _lossTimestampsMs.Count >= LossLatchThreshold;
        }
    }

    private void EnsureStyleLoaded(string baseDir)
    {
        if (_style is not null) return;
        lock (_styleGate)
        {
            if (_style is not null) return;
            string voiceFile = Path.Combine(baseDir, "models", "voice_styles", $"{_voiceId}.json");
            if (!File.Exists(voiceFile))
                throw new FileNotFoundException($"Voice style file missing: {voiceFile}");
            _style = Helper.LoadVoiceStyle(new List<string> { voiceFile }, verbose: false);
        }
    }

    public short[] Synthesize(string text, int totalStep = 8, float speed = DefaultSpeed,
        CancellationToken cancellationToken = default, string? lang = null)
    {
        // Normalize here rather than trusting the caller: `lang` originates in
        // settings.json (user-editable) or a SAPI client's xml:lang (arbitrary
        // LCID). The SDK THROWS on an unknown code, and a thrown Speak is a
        // silent voice — degrade to English instead.
        string langCode = SupertonicLanguages.Normalize(lang);

        // Honor cancellation before we do anything heavy. If the SAPI client
        // aborted the parent Speak before this prefetch task ran, we don't
        // even want to take the gate — bail immediately so the next Speak
        // doesn't queue behind us.
        cancellationToken.ThrowIfCancellationRequested();

        // Honor an external reset request (launcher → SessionRegistry → here) before
        // we hand any new work to the (possibly-dead) shared session. Interlocked
        // consume so two threads can't both observe the flag and both dispose — that
        // would nuke a session the first thread just rebuilt and force a redundant
        // second rebuild before the user hears anything.
        if (Interlocked.Exchange(ref _resetRequestedFlag, 0) == 1)
        {
            lock (_ttsGate) DisposeSharedTtsLocked();
        }

        EnsureStyleLoaded(GetBaseDirOrThrow());

        // Up to two attempts. Attempt 0 honors the configured EP. Attempt 1 (only
        // reached on a device-loss) forces CPU regardless of the persistent latch.
        // The persistent latch reflects multi-loss policy ("next utterance might
        // still try GPU"); forceCpu is the WITHIN-CALL recovery guarantee that the
        // retry doesn't roll the dice on the same still-recovering driver and
        // stall LoadTextToSpeech for 10–30 s while the gate is held.
        Exception? firstLoss = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_ttsGate)
            {
                // Snapshot captured INSIDE the gate. Critical correctness point:
                // if we read _sharedTts outside the lock and then locked, another
                // thread could dispose between read and use — leaving `tts`
                // pointing at a disposed InferenceSession. The native handles
                // could already have been freed and reused, turning the next
                // Run into an AV that takes down the SAPI host.
                var tts = GetSharedTtsLocked(out _, forceCpu: attempt > 0);
                try
                {
                    // Test-only injection: simulate a device-loss on this call.
                    // The marker string is matched by IsDeviceLoss so it routes
                    // through the same catch+recover path real device-losses use.
                    if (Interlocked.Exchange(ref _testInjectDeviceLossOnNextCall, 0) > 0)
                    {
                        throw new InvalidOperationException(
                            "[ErrorCode:Fail] VIBESUPERTONIC_TEST_DEVICE_LOSS — synthetic device removed (887A0005) for harness coverage");
                    }

                    bool injectHang = Interlocked.Exchange(ref _testInjectHangOnNextCall, 0) > 0;

                    // RunOptions lets the watchdog call Terminate() on a wedged
                    // Run from another thread. The Run() the wedged thread is
                    // inside observes the termination flag at its next internal
                    // boundary and unwinds. Plumbed through tts.Call → _Infer.
                    using var runOptions = new RunOptions();

                    var ct = cancellationToken;
                    var styleLocal = _style!;
                    var ttsLocal = tts;
                    var textLocal = text;
                    var totalStepLocal = totalStep;
                    var speedLocal = speed;
                    var langLocal = langCode;
                    Task<(float[] wav, float[] duration)> callTask = Task.Run(() =>
                    {
                        if (injectHang)
                        {
                            // Block until the test releases us or the token
                            // cancels. The watchdog should fire well before
                            // the 5-minute ceiling — that's just a backstop
                            // so a buggy test doesn't pin a thread forever.
                            _testHangRelease.Wait(TimeSpan.FromMinutes(5), ct);
                        }
                        return ttsLocal.Call(textLocal, langLocal, styleLocal, totalStepLocal, speedLocal,
                            cancellationToken: ct, runOptions: runOptions);
                    }, cancellationToken);

                    if (!callTask.Wait(SynthCallWatchdogMs))
                    {
                        // Watchdog fired. Try to terminate the in-flight Run
                        // so the orphan unwinds quickly (no-op if the wedge
                        // is below ORT's level — that's the driver's problem).
                        try { runOptions.Terminate = true; } catch { /* best-effort */ }

                        // Throw a synthetic device-loss-shaped exception. The
                        // catch handler below disposes the session and the outer
                        // attempt loop falls through to attempt 1 (forceCpu=true).
                        throw new InvalidOperationException(
                            $"[ErrorCode:Fail] VIBESUPERTONIC_TEST_DEVICE_LOSS — synth watchdog timeout after {SynthCallWatchdogMs} ms (treating as device-loss for recovery)");
                    }

                    var (wav, _) = callTask.Result;
                    return FloatToInt16(wav);
                }
                catch (Exception ex) when (IsDeviceLoss(ex))
                {
                    firstLoss ??= ex;
                    bool latchNow = RecordLossAndShouldLatch();
                    bool wasAlreadyLatched = _useDmlLatchedOff;

                    // Latch is a process-global health signal. Set it whenever
                    // the window says we should — independent of who did the
                    // dispose. The predecessor scoped this to a ReferenceEquals
                    // branch, which silently dropped the decision under any
                    // race; the latch then never tripped on subsequent utterances
                    // and the user would loop through repeated device-losses
                    // wondering why GPU never disabled itself.
                    if (latchNow) _useDmlLatchedOff = true;

                    // We hold the gate, so _sharedTts is still the session we
                    // just used (no other thread can have swapped it). Drop it.
                    // The dispose is fire-and-forget — see DisposeSharedTtsLocked
                    // for why we don't wait on native cleanup.
                    DisposeSharedTtsLocked();

                    string shortMsg = ex.Message ?? "";
                    if (shortMsg.Length > 160) shortMsg = shortMsg.Substring(0, 157) + "…";
                    _lastDeviceEvent = latchNow
                        ? (wasAlreadyLatched
                            ? $"DirectML device-loss while latched off (#{_deviceLossCount}). Retrying on CPU. [{shortMsg}]"
                            : $"DirectML device-loss (×{_deviceLossCount} in {LossLatchWindowMs / 1000}s). GPU disabled for this process — falling back to CPU. Click \"Reset selected\" to re-enable GPU. [{shortMsg}]")
                        : $"DirectML device-loss caught (#{_deviceLossCount}). Rebuilding on CPU for this retry — next utterance may retry GPU. [{shortMsg}]";

                    // Fall through to the next loop iteration. The lock will be
                    // released as we exit this lock block, giving other threads
                    // (including a queued reset) a chance to make progress before
                    // we re-acquire and rebuild. forceCpu=true on the retry
                    // iteration forces CPU even when the persistent latch is off,
                    // so a transient one-off loss doesn't stall the host.
                }
            }
        }
        // Both attempts failed. firstLoss is the original device-loss; surface it
        // so the operator sees the underlying DXGI code in engine.log.
        throw new InvalidOperationException(
            "DirectML device-loss recovery failed after one CPU retry.", firstLoss);
    }

    // SanitizeForSynth and the three-pass chunker moved to VibeSuperTonic.Core.Text
    // (TextSanitizer, SentenceChunker). Both were pure text work in a class that is
    // otherwise DirectML device-loss machinery, and the Linux daemon needs both
    // while needing none of the rest — R-12. Callers use SentenceChunker.Chunk
    // directly; a forwarding shim here would just be a second name for it.
    //
    // SanitizeForSynth's old doc comment claimed the offset drift from stripping
    // was "not user-observable". That was true only because nothing highlighted
    // words accurately enough to notice. TextOffsetMap tracks it now; see R-2.

    /// <summary>
    /// Kick off background ONNX model + style loading so the first Speak doesn't pay the cost.
    /// Safe to call multiple times — load is idempotent.
    /// </summary>
    public Task PreloadAsync() => Task.Run(() =>
    {
        try
        {
            GetSharedTts(out string baseDir);
            EnsureStyleLoaded(baseDir);
        }
        catch { /* ignore — Speak will surface real errors */ }
    });

    /// <summary>
    /// Voice-agnostic pre-warm of the shared ONNX models. Safe to call from static init —
    /// no voice id required because the heavy 380 MB load is shared. Per-voice Style files
    /// are tiny and load on first use.
    /// </summary>
    public static Task PreloadSharedAsync() => Task.Run(() =>
    {
        try { GetSharedTts(out _); }
        catch { /* swallow — first real Speak will report */ }
    });

    // The local FloatToInt16 was a character-for-character duplicate of
    // AudioBuffer.FloatToPcm16, which the Linux backend already calls. Two copies
    // of the clamp-and-scale is exactly the drift Core exists to prevent: the two
    // platforms would render the same model to subtly different PCM the first
    // time one of them changed rounding or clipping.
    private static short[] FloatToInt16(float[] wav) => AudioBuffer.FloatToPcm16(wav);

    public int SampleRate
    {
        get
        {
            var tts = GetSharedTts(out _);
            return tts.SampleRate;
        }
    }
}

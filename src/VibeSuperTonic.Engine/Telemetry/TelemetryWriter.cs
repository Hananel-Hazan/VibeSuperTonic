using System.Diagnostics;
using System.Text.Json;
using VibeSuperTonic.Core.Telemetry;
using VibeSuperTonic.Engine.Settings;
using VibeSuperTonic.Engine.Synth;

namespace VibeSuperTonic.Engine.Telemetry;

/// <summary>
/// Writes a per-PID JSON snapshot of this engine instance into
/// <see cref="DataPaths.SessionsDir"/>. The Control Panel's Monitor tab
/// enumerates the directory at ~5 Hz so multiple SAPI clients (Lingoes +
/// Balabolka + Word + …) all show up at once with their own state.
///
/// File freshness — the engine ID is the filename (the host process's PID); a
/// stale file (mtime &gt; 5s) means the host crashed or the engine never updated.
/// On graceful process exit we delete our own file via <see cref="AppDomain.ProcessExit"/>.
///
/// Reset signal — the launcher writes <c>&lt;pid&gt;.reset</c> as a sentinel. We poll
/// for it on every <see cref="Update"/> tick; on detection we tell
/// <see cref="SupertonicAdapter"/> to drop its shared ONNX/DML session and delete
/// the marker. The next Speak rebuilds the session — Lingoes does NOT need to
/// restart.
///
/// Path resolution: we DO NOT cache the data directory at type load time. The
/// engine DLL is loaded inside arbitrary host processes (Lingoes, NVDA, …) at
/// unpredictable moments — sometimes before the launcher has populated
/// <c>HKCU\SOFTWARE\VibeSuperTonic\BaseDir</c>. Caching would freeze a bad path
/// for the rest of that host's lifetime. <see cref="DataPaths"/> reads the
/// registry on every call; the cost is sub-microsecond.
/// </summary>
internal static class TelemetryWriter
{
    // 3 as of the unpaced bench render: the snapshot gained Unpaced. Nothing
    // gates on this number — the Monitor tab reads fields it knows and ignores
    // the rest, which is why adding one is safe — but a number that stops
    // tracking the format it describes is worse than no number, and a field
    // report carrying two snapshots is exactly where someone will want to know.
    public const int SchemaVersion = 3;

    private static readonly object _gate = new();
    private static readonly int _pid = Environment.ProcessId;
    private static readonly string _processName = SafeProcessName();

    private static bool _exitHandlerRegistered;
    private static System.Threading.Timer? _resetPoll;

    // Diagnostic throttle: log the first failure of each kind to engine.log,
    // then suppress subsequent identical failures so we don't fill the disk.
    // Cleared if a write later succeeds — recoverable problems re-log when
    // they recur.
    private static string? _lastFailureSignature;

    private static long _engineCpuStartTicks;
    private static long _engineCpuStartWallMs;

    public static void Update(
        bool isActive,
        string voiceId,
        string textSnippet,
        int totalStep,
        float engineSpeed,
        float dspRate,
        double firstByteLatencyMs,
        double rollingRtf,
        int pipelineDepth,
        double interChunkGapMs,
        int onnxThreads,
        int underrunCount,
        string lastError)
    {
        // Resolve every call — see class-level note. If the caller hasn't
        // registered yet (BaseDir empty), we fall through to AppContext.BaseDir
        // which is the host process's folder; the directory create below
        // catches the writability problem and we log it.
        string sessionsDir, filePath, resetMarker;
        try
        {
            sessionsDir = DataPaths.SessionsDir;
            filePath = Path.Combine(sessionsDir, $"{_pid}.json");
            resetMarker = Path.Combine(sessionsDir, $"{_pid}.reset");
        }
        catch (Exception ex)
        {
            LogFailure("resolve-paths", ex);
            return;
        }

        try
        {
            EnsureInitialized(sessionsDir, resetMarker);

            var proc = Process.GetCurrentProcess();
            long nowCpuTicks = proc.TotalProcessorTime.Ticks;
            long nowWallMs = Environment.TickCount64;
            double cpuPct = 0;
            if (_engineCpuStartWallMs > 0)
            {
                long wallDelta = nowWallMs - _engineCpuStartWallMs;
                long cpuDelta = nowCpuTicks - _engineCpuStartTicks;
                if (wallDelta > 0)
                    cpuPct = TimeSpan.FromTicks(cpuDelta).TotalMilliseconds / (double)wallDelta * 100.0;
            }
            _engineCpuStartTicks = nowCpuTicks;
            _engineCpuStartWallMs = nowWallMs;
            double rssMb = proc.WorkingSet64 / (1024.0 * 1024.0);

            // Pick up any pending reset marker from the launcher BEFORE we publish the
            // snapshot, so the snapshot reflects post-reset state on the next tick.
            CheckResetMarker(resetMarker);

            // If the caller has no synthesis exception to report, surface the
            // adapter's most recent DML event instead — that's how silent
            // GPU→CPU fallbacks become visible to the user. A real synth error
            // always wins because it's the more actionable signal.
            string errToReport = lastError ?? "";
            if (errToReport.Length == 0) errToReport = SupertonicAdapter.LastDeviceEvent;

            // Latch what this utterance achieved, so MarkIdle can publish it
            // rather than erase it. Only while active: MarkIdle calls back into
            // here, and latching its own zeroes would defeat the whole point.
            if (isActive)
            {
                if (double.IsFinite(rollingRtf) && rollingRtf > 0) _lastRollingRtf = rollingRtf;
                if (double.IsFinite(firstByteLatencyMs) && firstByteLatencyMs > 0) _lastFirstByteMs = firstByteLatencyMs;
                if (onnxThreads > 0) _lastOnnxThreads = onnxThreads;
            }

            var snap = new SessionSnapshot
            {
                SchemaVersion = SchemaVersion,
                Pid = _pid,
                ProcessName = _processName,
                IsActive = isActive,
                VoiceId = voiceId ?? "",
                CurrentText = textSnippet ?? "",
                LastError = errToReport,
                TotalStep = totalStep,
                EngineSpeed = engineSpeed,
                DspRate = dspRate,
                // Sanitize: NaN/±Infinity are legitimate in-flight states
                // (rollingRtf starts NaN; cpuPct can divide oddly), but
                // System.Text.Json throws on them by default — which was
                // failing EVERY telemetry tick and spamming engine.log. The
                // launcher's reader treats 0 as "not yet measured", so collapse
                // non-finite values to 0 here rather than change the wire format.
                FirstByteLatencyMs = Finite(firstByteLatencyMs),
                RollingRtf = Finite(rollingRtf),
                PipelineDepth = pipelineDepth,
                InterChunkGapMs = Finite(interChunkGapMs),
                EngineCpuPct = Finite(cpuPct),
                EngineRssMb = Finite(rssMb),
                OnnxThreads = onnxThreads,
                UnderrunCount = underrunCount,
                DeviceLossCount = SupertonicAdapter.DeviceLossCount,
                DmlLatchedOff = SupertonicAdapter.DmlLatchedOff,
                // Read off the engine rather than passed in, same as the two
                // above: it is a property of the process, and threading it
                // through a thirteen-parameter call at every site would be noise
                // for a field only the benchmark reads.
                Unpaced = SapiEngine.UnpacedActive,
                ProfileApplied = SupertonicAdapter.ProfileApplied,
                SampleTimeUtc = DateTime.UtcNow,
            };

            lock (_gate)
            {
                WriteAtomic(filePath, snap);
            }

            // Successful write — clear the failure signature so a future
            // recurrence will log again.
            _lastFailureSignature = null;
        }
        catch (Exception ex)
        {
            // Telemetry must never break TTS. We just log the first occurrence
            // and try again on the next tick — no permanent latch.
            LogFailure($"update@{sessionsDir}", ex);
        }
    }

    /// <summary>
    /// What the utterance that just finished achieved. Latched so that going
    /// idle does not destroy it — see <see cref="MarkIdle"/>.
    /// </summary>
    private static double _lastRollingRtf;
    private static double _lastFirstByteMs;
    private static int _lastOnnxThreads;

    /// <summary>
    /// Start of a new utterance: forget the previous one's figures.
    ///
    /// <para>Without this the latch below would mean "the last rate this process
    /// ever measured", and a Speak that produced no audio at all would report
    /// the previous one's number as though it were its own. For the benchmark
    /// that is the worst failure available — three runs agreeing perfectly
    /// because they are all the same reading.</para>
    /// </summary>
    public static void BeginUtterance()
    {
        _lastRollingRtf = 0;
        _lastFirstByteMs = 0;
        _lastOnnxThreads = 0;
    }

    /// <summary>
    /// Mark the engine idle, keeping the figures the finished utterance earned.
    ///
    /// <para><b>Why it no longer zeroes the rate.</b> The engine publishes a
    /// chunk's rate when the chunk completes, and this runs in Speak's
    /// <c>finally</c>. Measured 2026-08-20: with the write pacing off, those two
    /// moments are <b>7 ms apart</b> — the chunk rate landed at 11:49:24.974 and
    /// Speak returned at .981 — so the only snapshot that ever carried the
    /// measurement was overwritten with zeros before any reader could poll it.
    /// The benchmark read zero from eight configurations in a row and reported
    /// "telemetry unreadable" for every one of them.</para>
    ///
    /// <para>Paced, the same code appeared to work only because ~5 s of
    /// real-time writing sat between the publish and this call, which gave a
    /// polling reader twenty-odd chances to catch it. That is a measurement
    /// depending on the slowness of the thing it measures, and it stopped being
    /// true the moment the slowness was removed.</para>
    ///
    /// <para>Idle means "not speaking", which is why <c>IsActive</c> and
    /// <c>CurrentText</c> are still cleared — a Monitor row stranded on the last
    /// chunk forever is the bug this method was written for. The rate, the
    /// first-byte latency and the thread count are facts about an utterance that
    /// happened; they do not stop being true because it finished.</para>
    /// </summary>
    public static void MarkIdle()
    {
        Update(
            isActive: false, voiceId: "", textSnippet: "",
            totalStep: 0, engineSpeed: 0, dspRate: 0,
            firstByteLatencyMs: _lastFirstByteMs,
            rollingRtf: _lastRollingRtf > 0 ? _lastRollingRtf : double.NaN,
            pipelineDepth: 0, interChunkGapMs: 0,
            onnxThreads: _lastOnnxThreads, underrunCount: 0, lastError: "");
    }

    private static void EnsureInitialized(string sessionsDir, string resetMarker)
    {
        // Always make sure the directory exists — paths can change between
        // calls if the user updated the DataDir override, and CreateDirectory
        // is a no-op when the directory's already there.
        Directory.CreateDirectory(sessionsDir);

        if (_exitHandlerRegistered) return;
        lock (_gate)
        {
            if (_exitHandlerRegistered) return;
            // Sweep stale ".tmp" files left by a prior crashed instance with our
            // PID (rare but real — Windows reuses PIDs). Unfiltered .tmp files
            // accumulate forever otherwise.
            try
            {
                foreach (var tmp in Directory.EnumerateFiles(sessionsDir, $"{_pid}.json.tmp"))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch { /* best-effort */ }

            // Clean up our snapshot on graceful process exit. COM in-proc DLLs may
            // skip this on host crash — readers also use mtime staleness as a
            // safety net so a leftover file isn't displayed as a live session.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                // Stop the heartbeat timer first so it can't fire after we've
                // started tearing down. Then delete our session+marker files.
                try { _resetPoll?.Dispose(); } catch { }
                _resetPoll = null;
                RemoveOwnedFiles();
            };
            // Background tick: poll the reset marker AND refresh our snapshot's
            // mtime so we don't get marked stale by the launcher between Speak
            // calls. The launcher's freshness window is 5 s — a 1 Hz tick keeps
            // us comfortably alive without thrashing the disk. Reset responds
            // within 1 s even if the engine is idle or stuck mid-chunk.
            _resetPoll = new System.Threading.Timer(_ =>
            {
                try { CheckResetMarker(Path.Combine(DataPaths.SessionsDir, $"{_pid}.reset")); } catch { }
                try { Heartbeat(Path.Combine(DataPaths.SessionsDir, $"{_pid}.json")); } catch { }
            }, null, dueTime: 1000, period: 1000);
            _exitHandlerRegistered = true;
        }
    }

    private static void CheckResetMarker(string resetMarker)
    {
        try
        {
            if (!File.Exists(resetMarker)) return;
            // Tell the adapter to drop its shared session at the next Synthesize.
            // We delete the marker first so a slow filesystem can't double-trigger.
            try { File.Delete(resetMarker); } catch { /* will retry next tick */ }
            SupertonicAdapter.RequestReset();
        }
        catch { /* never let a marker check break telemetry */ }
    }

    /// <summary>Collapse NaN/±Infinity to 0 so JSON serialization can't throw.</summary>
    private static double Finite(double d) => double.IsFinite(d) ? d : 0.0;

    private static void WriteAtomic(string filePath, SessionSnapshot snap)
    {
        // Write to a sibling temp file then rename — readers either see the old
        // contents or the new contents, never a half-written file.
        string tmp = filePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snap, SnapshotJsonContext.Default.SessionSnapshot);
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, filePath, overwrite: true); }
        catch
        {
            // File.Move with overwrite can fail under AV scans; fall back to delete+move.
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            File.Move(tmp, filePath);
        }
    }

    /// <summary>
    /// Touch the snapshot file to refresh its mtime — the launcher uses mtime as
    /// a heartbeat. If the file doesn't exist yet (no Speak has happened), do
    /// nothing; <see cref="Update"/> will create it on first call.
    /// </summary>
    private static void Heartbeat(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try { File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow); } catch { }
    }

    private static void RemoveOwnedFiles()
    {
        try
        {
            string dir = DataPaths.SessionsDir;
            string filePath = Path.Combine(dir, $"{_pid}.json");
            string resetMarker = Path.Combine(dir, $"{_pid}.reset");
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            try { if (File.Exists(resetMarker)) File.Delete(resetMarker); } catch { }
        }
        catch { /* shutdown best-effort */ }
    }

    /// <summary>
    /// Append a one-line failure record to engine.log. Throttled by signature
    /// so identical failures on every tick don't fill the disk. The launcher
    /// can read engine.log to surface "telemetry not writing because of X".
    /// </summary>
    private static void LogFailure(string where, Exception ex)
    {
        string sig = where + "|" + ex.GetType().Name + "|" + ex.Message;
        if (sig == _lastFailureSignature) return;
        _lastFailureSignature = sig;
        try
        {
            string logDir = DataPaths.LogsDir;
            // If LogsDir itself isn't writable, the catch below swallows it —
            // we tried our best to surface the problem.
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "engine.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] TelemetryWriter.{where} EXCEPTION: " +
                $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch
        {
            // Last-ditch: try the user's TEMP. If even that fails, give up.
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "VibeSuperTonic-engine.log");
                File.AppendAllText(fallback,
                    $"[{DateTime.Now:HH:mm:ss.fff}] TelemetryWriter.{where} EXCEPTION: " +
                    $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    private static string SafeProcessName()
    {
        try { return Process.GetCurrentProcess().ProcessName; }
        catch { return "(unknown)"; }
    }
}

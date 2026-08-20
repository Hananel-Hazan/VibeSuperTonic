using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VibeSuperTonic.Launcher.Export; // ExportFormat + MfAudioEncoder (linked sources)

namespace VibeSuperTonic.RenderHost;

/// <summary>
/// Out-of-process SAPI host for everything the Control Panel needs the neural
/// engine to do: render text to a file (Export), speak to the default audio
/// device (Tune's "Test voice"), and time a synthesis run (Benchmark).
///
/// Why this exists: the Control Panel (VibeSuperTonic.exe) is a self-contained
/// single-file app, and running either the neural COM engine (ONNX Runtime) or
/// the MF encoder in-process there fails — ONNX access-violates (0xc0000005 in
/// InferenceSession.RunImpl), MF returns MF_E_INVALIDMEDIATYPE (0xC00D36B4).
/// Ordinary SAPI hosts work fine, so this framework-dependent helper mirrors
/// that shape and does the whole job.
///
/// Speaks ASYNC so it can report progress ("PROGRESS &lt;pct&gt;" on stdout) and
/// honor pause/resume/stop — the parent writes "pause"/"resume"/"stop" into the
/// --control file, which we poll and apply via ISpVoice, so the user can give
/// the machine a breather. Cancellation is the parent killing this process.
///
/// Args: --mode &lt;render|speak|bench&gt; --voice &lt;id&gt; --text &lt;utf8 file&gt;
///       bench:  [--telemetry &lt;sessions dir&gt;] [--runs N] [--warmups N]
///       render: --out &lt;path&gt; --format &lt;wav|mp3|aac&gt; [--bitrate &lt;bps&gt;]
///       any:    [--control &lt;file&gt;]
/// --mode defaults to "render" so pre-0.3 callers keep working unchanged.
/// Exit: 0 ok, 2 bad args, 1 failure (reason on stderr).
/// </summary>
internal static class Program
{
    private const int SpeechAudioFormatType44kHz16BitMono = 35;
    private const int SSFMCreateForWrite = 3;
    private const int SVSFlagsAsync = 1;
    private const int SVSFPurgeBeforeSpeak = 2;

    private static int Main(string[] args)
    {
        // Match the encoding the parent decodes with. Left at the console code
        // page, every non-ASCII character in our own progress lines is silently
        // dropped on the way through the pipe.
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console attached */ }

        string mode = (GetArg(args, "--mode") ?? "render").ToLowerInvariant();
        try
        {
            return mode switch
            {
                "render" => RunRender(args),
                "speak" => RunSpeak(args),
                "bench" => RunBench(args),
                _ => Usage($"unknown --mode '{mode}'"),
            };
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder($"{ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                sb.Append($" | inner: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(sb.ToString());
            return 1;
        }
    }

    // ------------------------------------------------------------------ modes

    private static int RunRender(string[] args)
    {
        string? voiceId = GetArg(args, "--voice");
        string? outPath = GetArg(args, "--out");
        string? textPath = GetArg(args, "--text");
        string format = (GetArg(args, "--format") ?? "wav").ToLowerInvariant();
        string? controlPath = GetArg(args, "--control");
        int bitrate = int.TryParse(GetArg(args, "--bitrate"), out int b) ? b : 192_000;

        if (string.IsNullOrWhiteSpace(voiceId) || string.IsNullOrWhiteSpace(outPath) || string.IsNullOrWhiteSpace(textPath))
            return Usage("render needs --voice, --out and --text");
        if (!TryReadText(textPath!, out string text, out int code)) return code;

        // For wav we render straight to the final path; otherwise to a temp WAV
        // that we then encode to the requested format.
        bool encode = format is "mp3" or "aac";
        string wavPath = encode
            ? Path.Combine(Path.GetTempPath(), $"vst_renderhost_{Guid.NewGuid():N}.wav")
            : outPath!;

        try
        {
            Synthesize(voiceId!, text, wavPath, controlPath);

            if (encode)
            {
                Console.Out.WriteLine($"RenderHost: encoding {format.ToUpperInvariant()} @ {bitrate / 1000} kbps…");
                var fmt = format == "aac" ? ExportFormat.Aac : ExportFormat.Mp3;
                var log = new Progress<string>(Console.Out.WriteLine);
                MfAudioEncoder.TranscodeWavToOutput(wavPath, outPath!, fmt, bitrate, log, CancellationToken.None);
            }

            Console.Out.WriteLine("PROGRESS 100");
            Console.Out.WriteLine("RenderHost: done.");
            return 0;
        }
        finally
        {
            if (encode) { try { File.Delete(wavPath); } catch { } }
        }
    }

    private static int RunSpeak(string[] args)
    {
        string? voiceId = GetArg(args, "--voice");
        string? textPath = GetArg(args, "--text");
        if (string.IsNullOrWhiteSpace(voiceId) || string.IsNullOrWhiteSpace(textPath))
            return Usage("speak needs --voice and --text");
        if (!TryReadText(textPath!, out string text, out int code)) return code;

        var t = Synthesize(voiceId!, text, wavPath: null, GetArg(args, "--control"));
        Console.Out.WriteLine($"RenderHost: spoke in {t.SpeakSeconds:F2}s (startup {t.StartupSeconds:F2}s).");
        return 0;
    }

    private static int RunBench(string[] args)
    {
        string? voiceId = GetArg(args, "--voice");
        string? textPath = GetArg(args, "--text");
        if (string.IsNullOrWhiteSpace(voiceId) || string.IsNullOrWhiteSpace(textPath))
            return Usage("bench needs --voice and --text");
        if (!TryReadText(textPath!, out string text, out int code)) return code;

        // How many TIMED runs after the warm-up. One is the historical behaviour
        // and what the preset sweep still asks for; the thread sweep asks for
        // three and takes the median, because 8a measured 15-18% run-to-run
        // variation on identical configurations and a single run therefore ranks
        // configurations on luck. Every run emits its own BENCH lines and the
        // parent decides what to do with the set.
        int runs = int.TryParse(GetArg(args, "--runs"), out int r) ? Math.Clamp(r, 1, 15) : 1;

        // Untimed renders of the REAL text before timing starts, on top of the
        // short warm-up phrase below.
        //
        // Measured 2026-08-19 on an i7-12800H, seven consecutive timed runs of the
        // same 4.9 s sample in one process after the existing warm-up:
        //
        //     0.287  0.261  0.250  0.248  0.233  0.248  0.232   (engine RTF)
        //
        // The first two are still warming — the short phrase pays COM activation
        // and the model load, but not the arena growth and clock ramp that a
        // full-length utterance provokes. Steady state starts at run 3 and holds
        // to ~7%. A median of three taken from run 1 would therefore be measuring
        // the decay curve, identically in every row, which looks exactly like a
        // measurement and orders the rows by nothing.
        //
        // Default 0, so the preset benchmark is unchanged: its text can be 100 000
        // words, and warming with that would double a run that is already long.
        int warmups = int.TryParse(GetArg(args, "--warmups"), out int w) ? Math.Clamp(w, 0, 5) : 0;

        // Render to a throwaway WAV rather than to the speakers. A benchmark that
        // waits on an audio device measures the device — playback runs at 1.0x
        // real time by definition, so every preset would score RTF ≈ 1.00 and the
        // whole tab would be useless. It also means a 10 000-word sweep does not
        // have to be sat through.
        string warmPath = Path.Combine(Path.GetTempPath(), $"vst_warm_{Guid.NewGuid():N}.wav");
        string wavPath = Path.Combine(Path.GetTempPath(), $"vst_bench_{Guid.NewGuid():N}.wav");
        try
        {
            // The first utterance in a fresh process pays ONNX session creation and
            // model load — measured at ~9s on a mid-range box, dwarfing the actual
            // synthesis of a short sample. A SAPI client pays that once per session
            // and then streams, so charging it to the measured run reports "will not
            // keep up" for an engine that keeps up comfortably. Burn it here, report
            // it separately, and measure steady-state throughput below.
            Console.Out.WriteLine("RenderHost: warming up (COM activation + model load)…");
            var warm = Synthesize(voiceId!, WarmUpPhrase, warmPath, controlPath: null, reportProgress: false);
            double startupSeconds = warm.StartupSeconds + warm.SpeakSeconds;
            Console.Out.WriteLine($"RenderHost: warm after {startupSeconds:F2}s — measuring.");

            Emit("startupSeconds", startupSeconds);

            for (int i = 1; i <= warmups; i++)
            {
                Console.Out.WriteLine($"RenderHost: settling run {i} of {warmups} (untimed)…");
                Synthesize(voiceId!, text, wavPath, controlPath: null, reportProgress: false);
            }

            string? telemetryDir = GetArg(args, "--telemetry");
            for (int run = 1; run <= runs; run++)
            {
                if (runs > 1) Console.Out.WriteLine($"RenderHost: timed run {run} of {runs}…");

                // A fresh probe per run. The probe keeps the WORST RollingRtf it
                // saw, which is the right reduction inside one utterance and
                // exactly the wrong one across several — reusing it would make
                // every run after the first report run 1's peak, and a sweep whose
                // three runs are identical by construction has a spread of zero
                // and looks more trustworthy than it is.
                var probe = new TelemetryProbe(telemetryDir);
                var t = Synthesize(voiceId!, text, wavPath, GetArg(args, "--control"),
                    reportProgress: runs == 1, probe: probe);

                var proc = Process.GetCurrentProcess();
                proc.Refresh();

                Emit("synthSeconds", t.SpeakSeconds);
                // Measured off the rendered WAV's data chunk, not SAPI's
                // RealtimePosition (which this engine does not populate) and not the
                // old "14 chars/sec" guess — those made RTF a rough estimate of a
                // number the tab presents to three significant figures.
                Emit("audioSeconds", WavAudioSeconds(wavPath));
                Emit("peakRssMb", proc.PeakWorkingSet64 / (1024.0 * 1024.0));
                // Wall-clock CPU across a paced run: what the machine actually spends
                // while streaming, which is the "will this bog my box down" question
                // the tab asks. Not a throughput figure — that is engineRtf.
                Emit("cpuPercent", t.SpeakSeconds > 0 ? t.CpuSecondsDuringSpeak / t.SpeakSeconds * 100.0 : 0);
                Emit("cpuSeconds", t.CpuSecondsDuringSpeak);
                Emit("engineRtf", probe.Rtf);
                Emit("sustainRtf", probe.SustainRtf);
                Emit("firstByteMs", probe.FirstByteMs);
                Emit("underruns", probe.Underruns);
                // What the engine ACTUALLY built its session with, straight from
                // its own telemetry. The thread sweep asks for a thread count by
                // writing settings.json and spawning this process; without reading
                // back what the engine did, a sweep whose settings did not take
                // effect produces a full, plausible, entirely fictional table. See
                // the shared-session note in SupertonicAdapter.
                Emit("onnxThreads", probe.OnnxThreads);

                if (runs > 1) Console.Out.WriteLine($"PROGRESS {run * 100 / runs}");
            }

            Console.Out.WriteLine("PROGRESS 100");
            Console.Out.WriteLine("RenderHost: bench done.");
            return 0;
        }
        finally
        {
            try { File.Delete(warmPath); } catch { }
            try { File.Delete(wavPath); } catch { }
        }
    }

    /// <summary>
    /// Short enough not to waste the user's time, long enough that the model
    /// actually runs an inference rather than just loading.
    /// </summary>
    private const string WarmUpPhrase = "Warming up the engine.";

    // ------------------------------------------------------------- synthesis

    private readonly record struct SpeakTiming(
        double StartupSeconds,
        double SpeakSeconds,
        double CpuSecondsDuringSpeak);

    /// <summary>
    /// Binds the voice token and speaks. <paramref name="wavPath"/> null means
    /// "to the default audio device"; otherwise output is captured to that WAV.
    /// </summary>
    private static SpeakTiming Synthesize(string voiceId, string text, string? wavPath, string? controlPath,
        bool reportProgress = true, TelemetryProbe? probe = null)
    {
        long t0 = Stopwatch.GetTimestamp();

        Type? sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (sapiType is null)
            throw new InvalidOperationException("SAPI not registered (SAPI.SpVoice ProgID missing).");

        Console.Out.WriteLine($"RenderHost: creating COM instances (text len={text.Length}).");
        dynamic voice = Activator.CreateInstance(sapiType)!;
        dynamic? stream = null;

        try
        {
            if (wavPath is not null)
            {
                Type? streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
                if (streamType is null)
                    throw new InvalidOperationException("SAPI.SpFileStream ProgID missing — SAPI runtime is incomplete.");
                stream = Activator.CreateInstance(streamType)!;
                stream.Format.Type = SpeechAudioFormatType44kHz16BitMono;
                Console.Out.WriteLine($"RenderHost: opening file stream → {wavPath}");
                stream.Open(wavPath, SSFMCreateForWrite, false);
                voice.AudioOutputStream = stream;
            }

            BindVoice(voice, voiceId);

            long t1 = Stopwatch.GetTimestamp();
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            TimeSpan cpuAtStart = proc.TotalProcessorTime;

            Console.Out.WriteLine("RenderHost: calling Speak (async)…");
            voice.Speak(text, SVSFlagsAsync);

            int total = Math.Max(1, text.Length);
            int lastPct = -1;
            bool paused = false;
            while (true)
            {
                if (controlPath is not null)
                {
                    string cmd = ReadControl(controlPath);
                    if (cmd == "pause" && !paused) { try { voice.Pause(); paused = true; Console.Out.WriteLine("RenderHost: paused."); } catch { } }
                    else if (cmd == "resume" && paused) { try { voice.Resume(); paused = false; Console.Out.WriteLine("RenderHost: resumed."); } catch { } }
                    else if (cmd == "stop") { try { voice.Speak("", SVSFPurgeBeforeSpeak); } catch { } Console.Out.WriteLine("RenderHost: stopped."); break; }
                }

                if (reportProgress)
                {
                    try
                    {
                        int pos = (int)voice.Status.InputWordPosition;
                        // Reserve the top 2% for the encode phase so the bar doesn't
                        // sit at 100% during transcode.
                        int pct = Math.Clamp(pos * 98 / total, 0, 98);
                        if (pct != lastPct) { lastPct = pct; Console.Out.WriteLine($"PROGRESS {pct}"); }
                    }
                    catch { /* Status unavailable this tick */ }
                }

                // Sample BEFORE waiting: the engine zeroes its live figures when it
                // goes idle, so the last reading has to be taken while it is still
                // speaking.
                probe?.Sample();

                bool done;
                try { done = (bool)voice.WaitUntilDone(200); }
                catch { done = true; }
                if (done && !paused) break; // while paused, WaitUntilDone times out — keep polling for resume
            }

            long t2 = Stopwatch.GetTimestamp();
            proc.Refresh();
            double cpuSeconds = (proc.TotalProcessorTime - cpuAtStart).TotalSeconds;
            Console.Out.WriteLine("RenderHost: Speak finished.");
            return new SpeakTiming(Seconds(t0, t1), Seconds(t1, t2), cpuSeconds);
        }
        finally
        {
            if (stream is not null)
            {
                try { voice.AudioOutputStream = null; } catch { }
                try { stream.Close(); } catch { }
                try { Marshal.FinalReleaseComObject(stream); } catch { }
            }
            try { Marshal.FinalReleaseComObject(voice); } catch { }
        }
    }

    private static void BindVoice(dynamic voice, string voiceId)
    {
        string tokenSubstr = $"VibeSuperTonic_{voiceId}";
        try
        {
            dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
            int n = tokens.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic tok = tokens.Item(i);
                if (((string)tok.Id).IndexOf(tokenSubstr, StringComparison.OrdinalIgnoreCase) >= 0)
                { voice.Voice = tok; Console.Out.WriteLine($"RenderHost: bound voice token {voiceId}."); return; }
            }
            Console.Out.WriteLine($"RenderHost: voice token {voiceId} not found — using the SAPI default voice.");
        }
        catch (Exception ex) { Console.Out.WriteLine($"RenderHost: voice bind warning: {ex.Message}"); }
    }

    /// <summary>
    /// Reads the engine's own per-PID telemetry snapshot while it speaks.
    ///
    /// The engine is loaded into THIS process, so it writes
    /// <c>&lt;sessionsDir&gt;\&lt;our pid&gt;.json</c> — the same file the Control Panel's
    /// Monitor tab reads. That snapshot carries the numbers wall-clock cannot see
    /// through SAPI's real-time write pacing: true synthesis RTF, time to first
    /// audio, and underruns.
    ///
    /// The directory is passed in rather than resolved here (<c>--telemetry</c>):
    /// the Control Panel owns data-path resolution, and this helper lives one
    /// folder down in <c>render\</c>, so deriving it from our own base directory
    /// would land in the wrong place. No flag means no engine metrics, and the
    /// caller falls back to wall clock.
    /// </summary>
    private sealed class TelemetryProbe
    {
        private readonly string? _path;

        public TelemetryProbe(string? sessionsDir) =>
            _path = string.IsNullOrWhiteSpace(sessionsDir)
                ? null
                : Path.Combine(sessionsDir, $"{Environment.ProcessId}.json");

        /// <summary>
        /// Worst RollingRtf seen — the number the verdict is built on.
        ///
        /// The engine's per-chunk RTF is synth-WAIT over audio produced, and it
        /// pre-synthesizes the next chunk while the current one's paced write
        /// drains. So chunk 1 (nothing precomputed) reports true compute cost,
        /// and later chunks report ~0 whenever the pipeline stayed ahead — which
        /// pacing all but guarantees on adequate hardware. Taking the last value
        /// would score every preset at ~0.02 and make the tab as useless as
        /// wall-clock did; taking the peak keeps the honest cost, and on hardware
        /// that genuinely falls behind it captures the worst moment rather than
        /// averaging it away.
        /// </summary>
        public double Rtf { get; private set; }

        /// <summary>
        /// Last RollingRtf seen — how far the pipeline was stalling by the end.
        /// Near zero means synthesis comfortably outran playback.
        /// </summary>
        public double SustainRtf { get; private set; }

        public double FirstByteMs { get; private set; }
        public double Underruns { get; private set; }

        /// <summary>
        /// Intra-op thread count the engine reports having built its session with.
        ///
        /// <para>Read back rather than assumed: the sweep asks for a thread count
        /// by writing <c>settings.json</c>, and the engine's ONNX session is a
        /// process-wide singleton built once. Every mechanism by which that
        /// request could fail to take effect produces the same symptom — a
        /// complete table of numbers that all describe the same configuration —
        /// so the parent compares this against what it asked for and discards the
        /// row if they differ.</para>
        /// </summary>
        public double OnnxThreads { get; private set; }

        public void Sample()
        {
            if (_path is null || !File.Exists(_path)) return;
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;

                double rtf = Num(root, "RollingRtf");
                if (rtf > 0)
                {
                    Rtf = Math.Max(Rtf, rtf);
                    SustainRtf = rtf; // the engine publishes 0 once idle, so keep the last live one
                }

                double firstByte = Num(root, "FirstByteLatencyMs");
                if (firstByte > 0 && FirstByteMs == 0) FirstByteMs = firstByte;

                Underruns = Math.Max(Underruns, Num(root, "UnderrunCount"));

                // Max, not last: the engine publishes 0 for this field before a
                // session exists, and taking the last reading would report 0 for a
                // run that had just been measured at four threads.
                OnnxThreads = Math.Max(OnnxThreads, Num(root, "OnnxThreads"));
            }
            catch { /* snapshot mid-rename or malformed — try again next tick */ }
        }

        private static double Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }

    // ----------------------------------------------------------------- helpers

    private static double Seconds(long from, long to) => (to - from) / (double)Stopwatch.Frequency;

    private static void Emit(string key, double value) =>
        Console.Out.WriteLine($"BENCH {key}={value.ToString("R", CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Duration of a RIFF/WAVE file from its fmt and data chunks. Walks the
    /// chunk list rather than assuming the canonical 44-byte header, because
    /// SpFileStream is free to write extra chunks (and does, on some SAPI
    /// versions). Returns 0 if the file is not a WAV or holds no samples.
    /// </summary>
    private static double WavAudioSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 12) return 0;
            if (ReadFourCc(br) != "RIFF") return 0;
            br.ReadUInt32();
            if (ReadFourCc(br) != "WAVE") return 0;

            uint bytesPerSecond = 0, dataLength = 0;
            long dataStart = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                string id = ReadFourCc(br);
                uint len = br.ReadUInt32();
                long body = fs.Position;
                long next = body + len + (len % 2); // chunks are word-aligned

                if (id == "fmt " && len >= 16)
                {
                    br.ReadUInt16();                       // wFormatTag
                    br.ReadUInt16();                       // nChannels
                    br.ReadUInt32();                       // nSamplesPerSec
                    bytesPerSecond = br.ReadUInt32();      // nAvgBytesPerSec
                }
                else if (id == "data")
                {
                    dataLength = len;
                    dataStart = body;
                }

                if (next <= body || next > fs.Length) break;
                fs.Position = next;
            }

            if (dataLength == 0 || bytesPerSecond == 0) return 0;
            // A stream that was never closed cleanly leaves a stale (often huge)
            // length in the header; trust the file, not the claim.
            long actual = Math.Min(dataLength, Math.Max(0, fs.Length - dataStart));
            return actual / (double)bytesPerSecond;
        }
        catch { return 0; }
    }

    private static string ReadFourCc(BinaryReader br) => new(br.ReadChars(4));

    private static bool TryReadText(string path, out string text, out int exitCode)
    {
        try { text = File.ReadAllText(path); exitCode = 0; return true; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cannot read text file: {ex.Message}");
            text = string.Empty;
            exitCode = 1;
            return false;
        }
    }

    private static int Usage(string? problem = null)
    {
        if (problem is not null) Console.Error.WriteLine(problem);
        Console.Error.WriteLine(
            "usage: --mode <render|speak|bench> --voice <id> --text <utf8 file> [--control <file>]\n" +
            "       render: --out <path> --format <wav|mp3|aac> [--bitrate <bps>]\n" +
            "       speak:  plays through the default audio device\n" +
            "       bench:  renders to a temp WAV, metrics on stdout as 'BENCH key=value'\n" +
            "               [--telemetry <sessions dir>] adds the engine's own RTF\n" +
            "               [--runs N] emits one set of metrics per timed run (default 1)\n" +
            "               [--warmups N] untimed renders of the text before timing (default 0)");
        return 2;
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static string ReadControl(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : ""; }
        catch { return ""; } // file mid-write — retry next tick
    }
}

using System.Runtime.InteropServices;
using System.Text;
using VibeSuperTonic.Launcher.Export; // ExportFormat + MfAudioEncoder (linked sources)

namespace VibeSuperTonic.RenderHost;

/// <summary>
/// Out-of-process Export pipeline: renders text to a WAV via SAPI.SpVoice, then
/// (for mp3/aac) encodes it via Media Foundation — both in THIS process.
///
/// Why this exists: the Control Panel (VibeSuperTonic.exe) is a self-contained
/// single-file app, and running either the neural COM engine (ONNX Runtime) or
/// the MF encoder in-process there fails (ONNX AV 0xc0000005; MF
/// MF_E_INVALIDMEDIATYPE 0xC00D36B4). Ordinary SAPI hosts work fine, so this
/// framework-dependent helper mirrors that shape and does the whole job.
///
/// Speaks ASYNC so it can report progress ("PROGRESS &lt;pct&gt;" on stdout) and
/// honor pause/resume — the parent writes "pause"/"resume" into the --control
/// file, which we poll and apply via ISpVoice.Pause/Resume so the user can give
/// the machine a breather. Cancellation is the parent killing this process.
///
/// Args: --voice &lt;id&gt; --out &lt;final path&gt; --text &lt;utf8 file&gt;
///       --format &lt;wav|mp3|aac&gt; [--bitrate &lt;bps&gt;] [--control &lt;file&gt;]
/// Exit: 0 ok, 2 bad args, 1 failure (reason on stderr).
/// </summary>
internal static class Program
{
    private const int SpeechAudioFormatType44kHz16BitMono = 35;
    private const int SSFMCreateForWrite = 3;
    private const int SVSFlagsAsync = 1;

    private static int Main(string[] args)
    {
        string? voiceId = GetArg(args, "--voice");
        string? outPath = GetArg(args, "--out");
        string? textPath = GetArg(args, "--text");
        string format = (GetArg(args, "--format") ?? "wav").ToLowerInvariant();
        string? controlPath = GetArg(args, "--control");
        int bitrate = int.TryParse(GetArg(args, "--bitrate"), out int b) ? b : 192_000;

        if (string.IsNullOrWhiteSpace(voiceId) || string.IsNullOrWhiteSpace(outPath) || string.IsNullOrWhiteSpace(textPath))
        {
            Console.Error.WriteLine("usage: --voice <id> --out <path> --text <utf8 file> --format <wav|mp3|aac> [--bitrate <bps>] [--control <file>]");
            return 2;
        }

        string text;
        try { text = File.ReadAllText(textPath!); }
        catch (Exception ex) { Console.Error.WriteLine($"cannot read text file: {ex.Message}"); return 1; }

        // For wav we render straight to the final path; otherwise to a temp WAV
        // that we then encode to the requested format.
        bool encode = format is "mp3" or "aac";
        string wavPath = encode
            ? Path.Combine(Path.GetTempPath(), $"vst_renderhost_{Guid.NewGuid():N}.wav")
            : outPath!;

        try
        {
            Render(voiceId!, text, wavPath, controlPath);

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
        catch (Exception ex)
        {
            var sb = new StringBuilder($"{ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                sb.Append($" | inner: {inner.GetType().Name}: {inner.Message}");
            Console.Error.WriteLine(sb.ToString());
            return 1;
        }
        finally
        {
            if (encode) { try { File.Delete(wavPath); } catch { } }
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Render(string voiceId, string text, string wavPath, string? controlPath)
    {
        Type? sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (sapiType is null)
            throw new InvalidOperationException("SAPI not registered (SAPI.SpVoice ProgID missing).");
        Type? streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
        if (streamType is null)
            throw new InvalidOperationException("SAPI.SpFileStream ProgID missing — SAPI runtime is incomplete.");

        Console.Out.WriteLine($"RenderHost: creating COM instances (text len={text.Length}).");
        dynamic voice = Activator.CreateInstance(sapiType)!;
        dynamic stream = Activator.CreateInstance(streamType)!;

        try
        {
            stream.Format.Type = SpeechAudioFormatType44kHz16BitMono;
            Console.Out.WriteLine($"RenderHost: opening file stream → {wavPath}");
            stream.Open(wavPath, SSFMCreateForWrite, false);
            voice.AudioOutputStream = stream;

            string tokenSubstr = $"VibeSuperTonic_{voiceId}";
            try
            {
                dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
                int n = tokens.Count;
                for (int i = 0; i < n; i++)
                {
                    dynamic tok = tokens.Item(i);
                    if (((string)tok.Id).IndexOf(tokenSubstr, StringComparison.OrdinalIgnoreCase) >= 0)
                    { voice.Voice = tok; Console.Out.WriteLine($"RenderHost: bound voice token {voiceId}."); break; }
                }
            }
            catch (Exception ex) { Console.Out.WriteLine($"RenderHost: voice bind warning: {ex.Message}"); }

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
                }

                try
                {
                    int pos = (int)voice.Status.InputWordPosition;
                    // Reserve the top 2% for the encode phase so the bar doesn't
                    // sit at 100% during transcode.
                    int pct = Math.Clamp(pos * 98 / total, 0, 98);
                    if (pct != lastPct) { lastPct = pct; Console.Out.WriteLine($"PROGRESS {pct}"); }
                }
                catch { /* Status unavailable this tick */ }

                bool done;
                try { done = (bool)voice.WaitUntilDone(200); }
                catch { done = true; }
                if (done && !paused) break; // while paused, WaitUntilDone times out — keep polling for resume
            }
            Console.Out.WriteLine("RenderHost: Speak finished.");
        }
        finally
        {
            try { voice.AudioOutputStream = null; } catch { }
            try { stream.Close(); } catch { }
            try { Marshal.FinalReleaseComObject(stream); } catch { }
            try { Marshal.FinalReleaseComObject(voice); } catch { }
        }
    }

    private static string ReadControl(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : ""; }
        catch { return ""; } // file mid-write — retry next tick
    }
}

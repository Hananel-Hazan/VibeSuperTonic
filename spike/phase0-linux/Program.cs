using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Supertonic;

namespace VibeSuperTonic.Phase0;

/// <summary>
/// Phase 0 gate from docs/LINUX-PORT-PLAN.md.
///
/// Answers one question: does ONNX Runtime's CPU provider render Supertonic fast
/// enough on linux-x64 that the pipeline can stay ahead of playback? Exit criterion
/// in the plan is RTF &lt;= 0.5 at totalStep 8.
///
/// Everything else in the plan is downstream of this number, so the tool reports the
/// surrounding facts too — cold vs warm load, RTF across quality presets, peak RSS,
/// and the effect of the intra-op thread count.
/// </summary>
internal static class Program
{
    // ~200 chars: the engine's default MaxChunkChars, so this is one realistic
    // synthesis unit rather than a toy phrase.
    private const string SampleText =
        "The quick brown fox jumps over the lazy dog, and then it pauses for a moment " +
        "to consider whether the effort was worthwhile, before trotting away into the " +
        "long grass without any particular sense of urgency.";

    private const string Voice = "M1";
    private const string Lang = "en";
    private const float Speed = 1.05f;
    private const float SilenceSec = 0.3f;
    private const int Repeats = 3;

    private static async Task<int> Main(string[] args)
    {
        bool skipDownload = args.Contains("--no-download");
        bool threadSweep = !args.Contains("--no-thread-sweep");

        string baseDir = AppContext.BaseDirectory;
        string modelsDir = Path.Combine(baseDir, "models");
        string onnxDir = Path.Combine(modelsDir, "onnx");
        string stylesDir = Path.Combine(modelsDir, "voice_styles");
        string optimizedDir = Path.Combine(modelsDir, "onnx-optimized");

        Header();
        Environment();

        if (!skipDownload)
        {
            int rc = await EnsureModelsAsync(baseDir);
            if (rc != 0) return rc;
        }

        foreach (var required in new[] { onnxDir, stylesDir })
        {
            if (!Directory.Exists(required))
            {
                Console.WriteLine($"FAIL  missing directory: {required}");
                return 2;
            }
        }

        // The optimized-graph cache makes the first run of this binary
        // unrepresentative. Report which case we're measuring so the two runs
        // aren't confused for each other.
        bool cacheHit = Directory.Exists(optimizedDir)
            && new[] { "duration_predictor", "text_encoder", "vector_estimator", "vocoder" }
                .All(n => File.Exists(Path.Combine(optimizedDir, n + ".onnx")));

        Section("Model load");
        Console.WriteLine($"optimized-graph cache : {(cacheHit ? "HIT (warm run)" : "MISS (cold run — expect +1-3s per model)")}");

        var cfg = Helper.LoadCfgs(onnxDir);
        int sampleRate = cfg.AE.SampleRate;

        var sw = Stopwatch.StartNew();
        TextToSpeech tts;
        try
        {
            tts = Helper.LoadTextToSpeech(onnxDir, useGpu: false, intraOpThreads: 0, interOpThreads: 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  LoadTextToSpeech threw: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 3;
        }
        sw.Stop();
        double loadSec = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"load time             : {loadSec:F2} s");
        Console.WriteLine($"sample rate           : {sampleRate} Hz");

        var stylePath = Path.Combine(stylesDir, Voice + ".json");
        if (!File.Exists(stylePath))
        {
            Console.WriteLine($"FAIL  missing voice style: {stylePath}");
            return 2;
        }
        var style = Helper.LoadVoiceStyle(new List<string> { stylePath });

        Section($"Synthesis — voice {Voice}, {SampleText.Length} chars, median of {Repeats}");
        Console.WriteLine("totalStep   synth(s)   audio(s)      RTF   verdict");

        var results = new List<(int step, double rtf)>();
        float[]? lastWav = null;

        foreach (int totalStep in new[] { 4, 8, 12 })
        {
            var times = new List<double>();
            double audioSec = 0;
            for (int i = 0; i < Repeats; i++)
            {
                var t = Stopwatch.StartNew();
                var (wav, _) = tts.Call(SampleText, Lang, style, totalStep, Speed, SilenceSec);
                t.Stop();
                times.Add(t.Elapsed.TotalSeconds);
                audioSec = (double)wav.Length / sampleRate;
                lastWav = wav;
            }
            times.Sort();
            double median = times[times.Count / 2];
            double rtf = median / audioSec;
            results.Add((totalStep, rtf));

            string verdict = totalStep == 8
                ? (rtf <= 0.5 ? "PASS (<=0.50)" : rtf <= 1.0 ? "MARGINAL" : "FAIL (>1.0)")
                : "";
            Console.WriteLine($"{totalStep,9}   {median,8:F2}   {audioSec,8:F2}   {rtf,6:F3}   {verdict}");
        }

        if (lastWav is not null)
        {
            string wavPath = Path.Combine(baseDir, "phase0-out.wav");
            Helper.WriteWavFile(wavPath, lastWav, sampleRate);
            Console.WriteLine();
            Console.WriteLine($"wrote {wavPath}");
            Console.WriteLine($"play it with:  paplay \"{wavPath}\"");
        }

        tts.Dispose();

        if (threadSweep)
        {
            Section("Intra-op thread sweep — totalStep 8");
            Console.WriteLine("intraOp   synth(s)      RTF");
            int half = Math.Max(1, System.Environment.ProcessorCount / 2);
            foreach (int threads in new[] { 0, half, System.Environment.ProcessorCount }.Distinct())
            {
                using var t2 = Helper.LoadTextToSpeech(onnxDir, useGpu: false,
                    intraOpThreads: threads, interOpThreads: 1);
                var times = new List<double>();
                double audioSec = 0;
                for (int i = 0; i < Repeats; i++)
                {
                    var t = Stopwatch.StartNew();
                    var (wav, _) = t2.Call(SampleText, Lang, style, 8, Speed, SilenceSec);
                    t.Stop();
                    times.Add(t.Elapsed.TotalSeconds);
                    audioSec = (double)wav.Length / sampleRate;
                }
                times.Sort();
                double median = times[times.Count / 2];
                string label = threads == 0 ? "auto" : threads.ToString();
                Console.WriteLine($"{label,7}   {median,8:F2}   {median / audioSec,6:F3}");
            }
        }

        Section("Memory");
        Console.WriteLine($"peak RSS              : {PeakRssMb():F0} MB");

        Section("Verdict");
        var eight = results.FirstOrDefault(r => r.step == 8);
        if (eight.rtf <= 0.5)
            Console.WriteLine($"PASS — RTF {eight.rtf:F3} at totalStep 8 meets the <=0.5 gate. Plan proceeds as written.");
        else if (eight.rtf <= 1.0)
            Console.WriteLine($"MARGINAL — RTF {eight.rtf:F3} at totalStep 8. Plan contingency: default Linux to totalStep 4, raise MinChunkChars.");
        else
            Console.WriteLine($"FAIL — RTF {eight.rtf:F3} at totalStep 8. Do not start Phase 1. Escalate per plan (quantization / CUDA / reconsider).");

        Console.WriteLine();
        Console.WriteLine("Run this binary a SECOND time — the first run pays graph-optimization cost");
        Console.WriteLine("and writes models/onnx-optimized/. The second run is the representative one.");
        return 0;
    }

    private static void Header()
    {
        Console.WriteLine("VibeSuperTonic — Phase 0 gate (Linux CPU viability)");
        Console.WriteLine("===================================================");
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
    }

    private static void Environment()
    {
        Section("Environment");
        Console.WriteLine($"os                    : {System.Environment.OSVersion.VersionString}");
        Console.WriteLine($"rid                   : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"framework             : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"arch                  : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"logical cpus          : {System.Environment.ProcessorCount}");
    }

    /// <summary>
    /// Peak resident set. <c>Process.PeakWorkingSet64</c> is not implemented on
    /// Linux, so fall back to VmHWM out of /proc/self/status.
    /// </summary>
    private static double PeakRssMb()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadAllLines("/proc/self/status"))
                {
                    if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[1], out var kb)) return kb / 1024.0;
                }
            }
            return Process.GetCurrentProcess().PeakWorkingSet64 / 1024.0 / 1024.0;
        }
        catch { return double.NaN; }
    }

    // ========================================================================
    // Model acquisition
    //
    // Deliberately a self-contained reimplementation rather than a reference to
    // the launcher's ModelDownloader: that class pulls in LockProbe, which is
    // Restart Manager and therefore Windows-only. Proving the download path works
    // on Linux is a secondary output of this spike.
    // ========================================================================

    private sealed record ManifestFile(string Path, string Url, string? Sha256, long Bytes, string[] Mirrors);

    private static async Task<int> EnsureModelsAsync(string baseDir)
    {
        Section("Models");
        string manifestPath = Path.Combine(baseDir, "models-manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"FAIL  manifest not found: {manifestPath}");
            return 2;
        }

        var files = ParseManifest(manifestPath);
        Console.WriteLine($"manifest entries      : {files.Count}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeSuperTonic-Phase0/1.0");

        foreach (var f in files)
        {
            string full = Path.Combine(baseDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (Verify(full, f)) { Console.WriteLine($"ok    {f.Path}"); continue; }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            bool got = false;
            foreach (var url in new[] { f.Url }.Concat(f.Mirrors).Where(u => !string.IsNullOrWhiteSpace(u)))
            {
                try
                {
                    Console.Write($"get   {f.Path} ({f.Bytes / 1024.0 / 1024.0:F1} MB) ... ");
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await using (var fs = File.Create(full))
                        await resp.Content.CopyToAsync(fs);

                    if (Verify(full, f)) { Console.WriteLine("ok"); got = true; break; }
                    Console.WriteLine("checksum mismatch, trying next source");
                }
                catch (Exception ex) { Console.WriteLine($"failed ({ex.GetType().Name}), trying next source"); }
            }
            if (!got)
            {
                Console.WriteLine($"FAIL  could not obtain {f.Path} from any source");
                return 4;
            }
        }
        return 0;
    }

    private static bool Verify(string path, ManifestFile f)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            if (f.Bytes > 0 && fi.Length != f.Bytes) return false;
            // Small .json entries carry no LFS hash and are size-verified only —
            // matching the manifest's own documented contract.
            if (string.IsNullOrWhiteSpace(f.Sha256)) return true;
            using var s = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            return hash == f.Sha256!.ToLowerInvariant();
        }
        catch { return false; }
    }

    private static List<ManifestFile> ParseManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<ManifestFile>();
        foreach (var e in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            string p = e.GetProperty("path").GetString() ?? "";
            string url = e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            string? sha = e.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            long bytes = e.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
            var mirrors = e.TryGetProperty("mirrors", out var m)
                ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            list.Add(new ManifestFile(p, url, sha, bytes, mirrors));
        }
        return list;
    }
}

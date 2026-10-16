using System.Globalization;
using VibeSuperTonic.Launcher.Host;

namespace VibeSuperTonic.Launcher.Bench;

internal sealed record BenchmarkResult(
    QualityPreset Preset,
    int TotalStep,
    float EngineSpeed,
    int TextChars,
    int TextWords,
    double AudioSeconds,
    double SynthSeconds,
    double StartupSeconds,
    double FirstByteMs,
    double Rtf,                 // engine-reported synthesis RTF, NOT wall/audio
    double SustainRtf,          // how far the pipeline stalled by the end; ~0 = kept ahead
    double PeakProcessRssMb,
    double AverageCpuPct,
    int Underruns,
    string? Error);

/// <summary>
/// Times a synthesis run of the neural engine at a given quality preset.
///
/// The work runs in <c>render\VibeSuperTonic.RenderHost.exe</c>, not here. This
/// process is the self-contained single-file Control Panel, and driving the
/// engine in-process access-violates inside ONNX Runtime — see
/// <see cref="RenderHostProcess"/>. Until 0.3.0 this class activated
/// <c>SAPI.SpVoice</c> directly and the Benchmark tab reliably killed the app on
/// the first preset.
///
/// Moving out of process also fixed the measurements, which were quietly wrong:
/// RSS and CPU were sampled from the Control Panel (a WinForms UI that does no
/// synthesis), and audio length fell back to a flat 14 chars/sec guess because
/// this engine does not populate <c>ISpeechVoiceStatus.RealtimePosition</c>. The
/// helper now reports its own peak RSS and CPU, and derives audio length from
/// the rendered WAV.
///
/// The caller owns settings restoration — see
/// <see cref="EngineSettingsRegistry.BeginTemporaryChange"/>. A sweep applies
/// each preset in turn, so per-run restore inside here would snapshot an already
/// modified state and put back the wrong values.
/// </summary>
internal static class Benchmark
{
    public static async Task<BenchmarkResult> RunAsync(
        QualityPreset preset,
        string voiceId,
        string text,
        int wordCap,
        IProgress<string>? log,
        CancellationToken ct,
        IProgress<int>? progress = null)
    {
        // Apply the preset's settings; the helper's engine reads them on activation.
        var settings = EngineSettingsRegistry.Load();
        settings.ApplyPreset(preset);
        EngineSettingsRegistry.Save(settings);

        string trimmed = TrimToWords(text, wordCap);
        int words = CountWords(trimmed);
        log?.Report($"Preset {preset}: synthesizing {words} words…");

        var metrics = new Metrics();
        string? error = null;

        try
        {
            await Task.Run(() =>
            {
                var helperLog = new LogFilter(log);
                RenderHostProcess.RunWithText(
                    new[] { "--mode", "bench", "--voice", voiceId, "--telemetry", DataPaths.SessionsDir },
                    trimmed, helperLog, ct, progress, metrics.Absorb);
            }, ct);
        }
        catch (OperationCanceledException) { error = "cancelled"; }
        catch (Exception ex) { error = ex.Message; }

        double audioSeconds = metrics.Get("audioSeconds");
        double synthSeconds = metrics.Get("synthSeconds");

        // Only reached when the helper died before reporting — a completed run
        // always measures the WAV it just wrote.
        if (audioSeconds <= 0) audioSeconds = trimmed.Length / 14.0;

        // RTF comes from the ENGINE, not from wall clock. The engine paces its
        // writes to real time so live playback keeps its trailing words
        // (SapiEngine.StreamPcm), and it cannot tell a file render from an audio
        // device — so wall-clock RTF sits at ~1.0 for every preset and the tab
        // would report "real-time, tight" for a machine with 5x headroom. Wall
        // clock is still reported, as the honest answer to "how long does an
        // export take".
        double engineRtf = metrics.Get("engineRtf");
        double rtf = engineRtf > 0
            ? engineRtf
            : (audioSeconds > 0 && synthSeconds > 0 ? synthSeconds / audioSeconds : double.NaN);

        return new BenchmarkResult(
            preset,
            settings.TotalStep,
            settings.EngineSpeed,
            trimmed.Length,
            words,
            audioSeconds,
            synthSeconds,
            metrics.Get("startupSeconds"),
            metrics.Get("firstByteMs"),
            rtf,
            metrics.Get("sustainRtf"),
            metrics.Get("peakRssMb"),
            metrics.Get("cpuPercent"),
            (int)metrics.Get("underruns"),
            error);
    }

    /// <summary>
    /// Indents the helper's chatter and keeps its "BENCH key=value" data lines out
    /// of the log pane.
    ///
    /// Deliberately NOT a <see cref="Progress{T}"/>: constructed on a thread-pool
    /// thread it captures no synchronization context, so each report is posted
    /// independently and the helper's lines arrive shuffled — "creating COM
    /// instances" landing above the "warming up" line that caused it. Forwarding
    /// inline preserves the order the helper wrote them in and still lets the UI's
    /// own IProgress do the marshalling to the UI thread.
    /// </summary>
    private sealed class LogFilter : IProgress<string>
    {
        private readonly IProgress<string>? _inner;

        public LogFilter(IProgress<string>? inner) => _inner = inner;

        public void Report(string value)
        {
            if (value.StartsWith("BENCH ", StringComparison.Ordinal)) return;
            _inner?.Report("  " + value);
        }
    }

    /// <summary>
    /// Collects the helper's "BENCH key=value" stdout lines.
    ///
    /// <para>Every value is kept, not just the last. The helper emits one set per
    /// timed run (<c>--runs</c>), so a key arrives once for a preset measurement
    /// and three times for a thread-sweep row — and the median of three is the
    /// entire reason the thread sweep can tell two configurations apart. Callers
    /// that want a single number still get the last one from <see cref="Get"/>,
    /// which is what a single-run measurement always produced.</para>
    /// </summary>
    internal sealed class Metrics
    {
        private readonly Dictionary<string, List<double>> _values = new(StringComparer.OrdinalIgnoreCase);

        public void Absorb(string line)
        {
            if (!line.StartsWith("BENCH ", StringComparison.Ordinal)) return;
            int eq = line.IndexOf('=', 6);
            if (eq < 0) return;
            if (!double.TryParse(line.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return;

            string key = line[6..eq];
            if (!_values.TryGetValue(key, out var list)) _values[key] = list = new List<double>(3);
            list.Add(v);
        }

        /// <summary>The last value reported for <paramref name="key"/>, or 0.</summary>
        public double Get(string key) =>
            _values.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : 0;

        /// <summary>Every value reported for <paramref name="key"/>, in the order the runs happened.</summary>
        public IReadOnlyList<double> GetAll(string key) =>
            _values.TryGetValue(key, out var list) ? list : Array.Empty<double>();
    }

    public static string TrimToWords(string text, int maxWords)
    {
        if (maxWords <= 0) return text;
        int count = 0, i = 0;
        bool inWord = false;
        for (; i < text.Length && count < maxWords; i++)
        {
            bool ws = char.IsWhiteSpace(text[i]);
            if (!ws && !inWord) { count++; inWord = true; }
            else if (ws) inWord = false;
        }
        return i >= text.Length ? text : text[..i];
    }

    public static int CountWords(string text)
    {
        int count = 0;
        bool inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { count++; inWord = true; }
        }
        return count;
    }

    public static (string colour, string verdict) Verdict(BenchmarkResult r)
    {
        if (r.Error is not null) return ("#F44336", "Failed");
        if (double.IsNaN(r.Rtf)) return ("#9E9E9E", "No measurement");
        if (r.Rtf < 0.5)  return ("#4CAF50", "Plenty of headroom");
        if (r.Rtf < 1.0)  return ("#FFB300", "Real-time, tight");
        return ("#F44336", "Will not keep up");
    }
}

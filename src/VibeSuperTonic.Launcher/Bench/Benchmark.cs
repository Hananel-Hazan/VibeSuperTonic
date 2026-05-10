using System.Diagnostics;

namespace VibeSuperTonic.Launcher.Bench;

internal sealed record BenchmarkResult(
    QualityPreset Preset,
    int TotalStep,
    float EngineSpeed,
    int TextChars,
    int TextWords,
    double AudioSeconds,
    double SynthSeconds,
    double Rtf,                 // synthSec / audioSec
    double PeakProcessRssMb,
    double AverageCpuPct,
    int Underruns,
    string? Error);

/// <summary>
/// Drives synthesis via SAPI COM automation (SAPI.SpVoice ProgID), which is
/// available on every Windows install — no System.Speech NuGet dependency.
/// Writes audio to memory; measures wall time, RSS, CPU%.
/// </summary>
internal static class Benchmark
{
    public static async Task<BenchmarkResult> RunAsync(
        QualityPreset preset,
        string voiceTokenName,
        string text,
        int wordCap,
        IProgress<string>? log,
        CancellationToken ct)
    {
        // Apply the preset's settings so the engine (when Phase 2 lands) reads them.
        var settings = EngineSettingsRegistry.Load();
        settings.ApplyPreset(preset);
        EngineSettingsRegistry.Save(settings);

        string trimmed = TrimToWords(text, wordCap);
        int words = CountWords(trimmed);
        log?.Report($"Preset {preset}: synthesizing {words} words…");

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long startCpuTicks = proc.TotalProcessorTime.Ticks;
        long startWallMs = Environment.TickCount64;
        long peakRssBytes = proc.WorkingSet64;
        int underruns = 0;
        string? error = null;
        double audioSeconds = 0;

        try
        {
            await Task.Run(() =>
            {
                Type? sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (sapiType is null) throw new InvalidOperationException("SAPI not registered (SAPI.SpVoice ProgID missing).");
                dynamic voice = Activator.CreateInstance(sapiType)!;

                // Bind to the requested voice token if found.
                try
                {
                    dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
                    int n = tokens.Count;
                    for (int i = 0; i < n; i++)
                    {
                        dynamic tok = tokens.Item(i);
                        string id = (string)tok.Id;
                        if (id.IndexOf(voiceTokenName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            voice.Voice = tok;
                            break;
                        }
                    }
                }
                catch (Exception ex) { log?.Report($"  voice bind warning: {ex.Message}"); }

                // SPF_ASYNC = 1 — kick off speak asynchronously so we can interrupt.
                // SPF_PURGEBEFORESPEAK = 2 stops any prior utterance.
                const int SVSFAsync = 1;
                const int SVSFPurgeBeforeSpeak = 2;
                voice.Speak(trimmed, SVSFAsync | SVSFPurgeBeforeSpeak);

                // WaitUntilDone(timeoutMs) returns true when finished, false on timeout.
                // We loop with a short timeout so a cancellation request is responsive.
                // Don't poll RunningState — it can read 'Done' for a moment before the
                // engine actually starts speaking (causing premature exit).
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { voice.Speak("", SVSFPurgeBeforeSpeak); } catch { }
                        break;
                    }
                    bool done;
                    try { done = (bool)voice.WaitUntilDone(200); }
                    catch { done = true; }
                    if (done) break;
                }

                // ISpeechVoiceStatus.RealtimePosition is in milliseconds (when populated).
                // Some SAPI engines do not report it on this property; we fall back to a
                // char-rate estimate below.
                try
                {
                    double ms = Convert.ToDouble(voice.Status.RealtimePosition);
                    if (ms > 0) audioSeconds = ms / 1000.0;
                }
                catch { /* ignore */ }
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
            }, ct);
        }
        catch (OperationCanceledException) { error = "cancelled"; }
        catch (Exception ex) { error = ex.Message; }

        proc.Refresh();
        long endCpuTicks = proc.TotalProcessorTime.Ticks;
        long endWallMs = Environment.TickCount64;
        peakRssBytes = Math.Max(peakRssBytes, proc.WorkingSet64);

        double synthSec = (endWallMs - startWallMs) / 1000.0;
        double cpuSec = TimeSpan.FromTicks(endCpuTicks - startCpuTicks).TotalSeconds;
        double avgCpuPct = synthSec > 0 ? (cpuSec / synthSec) * 100.0 : 0;

        // If we couldn't read RealtimePosition, estimate at 14 chars/sec (~150 wpm reading rate).
        if (audioSeconds <= 0) audioSeconds = trimmed.Length / 14.0;

        double rtf = audioSeconds > 0 ? synthSec / audioSeconds : double.NaN;

        return new BenchmarkResult(
            preset,
            settings.TotalStep,
            settings.EngineSpeed,
            trimmed.Length,
            words,
            audioSeconds,
            synthSec,
            rtf,
            peakRssBytes / (1024.0 * 1024.0),
            avgCpuPct,
            underruns,
            error);
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
        if (r.Rtf < 0.5)  return ("#4CAF50", "Plenty of headroom");
        if (r.Rtf < 1.0)  return ("#FFB300", "Real-time, tight");
        return ("#F44336", "Will not keep up");
    }
}

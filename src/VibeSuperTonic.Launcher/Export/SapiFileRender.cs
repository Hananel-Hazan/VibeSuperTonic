using VibeSuperTonic.Launcher.Host;

namespace VibeSuperTonic.Launcher.Export;

/// <summary>
/// Drives the Export pipeline by applying the export-specific quality overrides
/// to the global <see cref="EngineSettings"/>, then running the
/// render(+encode) in a SEPARATE helper process
/// (<c>render\VibeSuperTonic.RenderHost.exe</c>), restoring prior settings in a
/// finally block.
///
/// Why out-of-process: the Control Panel ships as a self-contained single-file
/// app, and in that host BOTH the neural COM engine (ONNX Runtime, AV
/// 0xc0000005) AND the Media Foundation encoder (MF_E_INVALIDMEDIATYPE
/// 0xC00D36B4) fail. Ordinary SAPI hosts (Balabolka/NVDA/Lingoes) run both fine,
/// so the work is delegated to a plain framework-dependent helper that mirrors
/// that host shape. The engine only sees settings, so we apply them here
/// (registry) and the child reads them on activation.
///
/// Restoring settings on every code path is non-negotiable: a render that errors
/// out mid-way would otherwise leave the global settings with "TotalStep=16,
/// DspRate=1.0" until the next manual edit, slowing every SAPI client.
/// </summary>
internal static class SapiFileRender
{
    // Conservative chars/sec for the preview-length text-prefix estimate.
    private const double CharsPerSecondEstimate = 14.0;

    /// <summary>
    /// Renders to an uncompressed WAV (used by Preview). Honors preview trimming
    /// from <see cref="ExportRequest.PreviewSeconds"/>.
    /// </summary>
    public static void RenderToWav(ExportRequest req, string wavPath,
        IProgress<string>? log, CancellationToken ct,
        IProgress<int>? progress = null, string? controlPath = null)
        => RunPipeline(req, wavPath, "wav", 0, log, ct, progress, controlPath);

    /// <summary>
    /// Renders AND encodes straight to the final MP3/AAC file (used by Save).
    /// The helper does both steps so neither runs in this single-file process.
    /// </summary>
    public static void RenderToFile(ExportRequest req, string outPath,
        IProgress<string>? log, CancellationToken ct,
        IProgress<int>? progress = null, string? controlPath = null)
        => RunPipeline(req, outPath, req.Format == ExportFormat.Aac ? "aac" : "mp3",
                       req.Bitrate, log, ct, progress, controlPath);

    private static void RunPipeline(ExportRequest req, string outPath, string format, int bitrate,
        IProgress<string>? log, CancellationToken ct, IProgress<int>? progress, string? controlPath)
    {
        // The scope restores on dispose AND leaves a recovery record on disk, so
        // a render that takes the Control Panel down with it does not strand the
        // user's global quality at the export's settings.
        using var restore = EngineSettingsRegistry.BeginTemporaryChange(log);
        ApplyExportSettings(restore.Snapshot, req);

        log?.Report($"Settings: TotalStep={req.TotalStep} DspRate={req.DspRate:F2} " +
                    $"DML={(req.ForceDirectML ? "on" : "off")}");

        string textToSpeak = req.Text;
        if (req.PreviewSeconds is int sec && sec > 0)
        {
            int charCap = Math.Max(80, (int)(sec * CharsPerSecondEstimate));
            if (textToSpeak.Length > charCap)
            {
                int end = charCap;
                while (end < textToSpeak.Length && !char.IsWhiteSpace(textToSpeak[end])) end++;
                textToSpeak = textToSpeak[..Math.Min(end, textToSpeak.Length)];
            }
            log?.Report($"Preview: rendering first ~{sec}s ({textToSpeak.Length} chars).");
        }

        RunRenderHost(req.VoiceId, textToSpeak, outPath, format, bitrate, log, ct, progress, controlPath);
    }

    /// <summary>
    /// Builds the helper's argument list for a render and hands it to
    /// <see cref="RenderHostProcess"/>, which owns spawning, stdout/progress
    /// streaming, cancellation and exit-code handling for every caller.
    /// </summary>
    private static void RunRenderHost(string voiceId, string text, string outPath, string format, int bitrate,
        IProgress<string>? log, CancellationToken ct, IProgress<int>? progress, string? controlPath)
    {
        var args = new List<string>
        {
            "--mode", "render",
            "--voice", voiceId,
            "--out", outPath,
            "--format", format,
        };
        if (format is "mp3" or "aac") { args.Add("--bitrate"); args.Add(bitrate.ToString()); }
        if (controlPath is not null) { args.Add("--control"); args.Add(controlPath); }

        log?.Report($"Spawning render helper for voice {voiceId} (format {format})…");
        RenderHostProcess.RunWithText(args, text, log, ct, progress);
    }

    private static void ApplyExportSettings(EngineSettings saved, ExportRequest req)
    {
        // Keep the user's Tier B knobs (chunking, ONNX threads) but override
        // synthesis quality + GPU for this render only.
        var snapshot = saved.Clone();
        snapshot.TotalStep = req.TotalStep;
        snapshot.EngineSpeed = 1.0f; // locked — see TuneTab notes
        snapshot.DspRate = req.DspRate;
        snapshot.VolumeTrimDb = req.VolumeTrimDb;
        snapshot.DefaultVoice = req.VoiceId;
        if (req.ForceDirectML) snapshot.UseDirectML = true;
        snapshot.Preset = QualityPreset.Custom;
        snapshot.PerVoice.Remove(req.VoiceId);
        EngineSettingsRegistry.Save(snapshot);
    }
}

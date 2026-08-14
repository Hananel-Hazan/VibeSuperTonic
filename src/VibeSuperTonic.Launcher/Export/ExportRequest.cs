namespace VibeSuperTonic.Launcher.Export;

internal enum ExportFormat { Mp3, Aac }

/// <summary>
/// Parameters for one Export-tab render. Built by <see cref="Ui.ExportTab"/>
/// and consumed by <see cref="SapiFileRender"/> + <see cref="MfAudioEncoder"/>.
/// Carries an override snapshot that's applied to the global EngineSettings
/// for the duration of the render and then restored — same pattern as the
/// Benchmark tab.
/// </summary>
internal sealed class ExportRequest
{
    public required string Text { get; init; }
    public required string VoiceId { get; init; }
    public required string OutputPath { get; init; }
    public ExportFormat Format { get; init; } = ExportFormat.Mp3;
    public int Bitrate { get; init; } = 320_000;

    // Quality knobs applied to the in-memory snapshot only; never persisted.
    public int TotalStep { get; init; } = 16;
    public float DspRate { get; init; } = 1.0f;
    public float VolumeTrimDb { get; init; } = 0f;
    public bool ForceDirectML { get; init; } = true;

    /// <summary>
    /// When set, render is truncated to ~this many seconds of audio. Used by
    /// the Preview button; <see cref="SapiFileRender"/> uses a character-rate
    /// estimate (~14 chars/sec) to pick a text prefix.
    /// </summary>
    public int? PreviewSeconds { get; init; }
}

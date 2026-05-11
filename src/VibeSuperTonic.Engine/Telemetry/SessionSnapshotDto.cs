using System.Text.Json.Serialization;

namespace VibeSuperTonic.Engine.Telemetry;

/// <summary>
/// On-disk session snapshot — the engine writes this as JSON to
/// <c>%LOCALAPPDATA%\VibeSuperTonic\sessions\&lt;pid&gt;.json</c>; the launcher reads it
/// to populate the Monitor tab. Any field added here must round-trip through the
/// source-generated <see cref="SnapshotJsonContext"/> below — keep names stable.
/// </summary>
internal sealed class SessionSnapshotDto
{
    public int SchemaVersion { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public bool IsActive { get; set; }
    public string VoiceId { get; set; } = "";
    public string CurrentText { get; set; } = "";
    public string LastError { get; set; } = "";
    public int TotalStep { get; set; }
    public float EngineSpeed { get; set; }
    public float DspRate { get; set; }
    public double FirstByteLatencyMs { get; set; }
    public double RollingRtf { get; set; }
    public int PipelineDepth { get; set; }
    public double InterChunkGapMs { get; set; }
    public double EngineCpuPct { get; set; }
    public double EngineRssMb { get; set; }
    public int OnnxThreads { get; set; }
    public int UnderrunCount { get; set; }
    public int DeviceLossCount { get; set; }
    public bool DmlLatchedOff { get; set; }
    public DateTime SampleTimeUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionSnapshotDto))]
internal partial class SnapshotJsonContext : JsonSerializerContext { }

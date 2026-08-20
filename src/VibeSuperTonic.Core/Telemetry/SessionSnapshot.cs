using System.Text.Json.Serialization;

namespace VibeSuperTonic.Core.Telemetry;

/// <summary>
/// The on-disk session snapshot: one JSON file per engine instance under
/// <c>&lt;DataDir&gt;/sessions/&lt;pid&gt;.json</c>. The engine writes it; the
/// Control Panel's Monitor tab reads it.
///
/// This type existed twice — <c>SessionSnapshotDto</c> in the engine and
/// <c>TelemetrySnapshot</c> in the launcher — with twenty identical properties
/// and a comment on each telling the reader to keep them in sync by hand. That
/// is the same arrangement the pronunciation types had before Core, and it fails
/// the same way: the two sides are only ever *intended* to agree, and a field
/// added to the writer is invisible to the reader until someone notices the
/// column is always empty.
///
/// The serializer context lives here too, so the two sides cannot disagree about
/// options either — the previous pair each declared their own
/// <c>SnapshotJsonContext</c>, which happened to match.
/// </summary>
public sealed class SessionSnapshot
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

    /// <summary>
    /// Whether this Speak ran with real-time write pacing disabled — see
    /// <see cref="Synthesis.BenchSwitches"/>. False for every ordinary SAPI host,
    /// and true only inside a benchmark helper that asked for it.
    ///
    /// Published rather than assumed for the same reason
    /// <see cref="OnnxThreads"/> is: the benchmark asks for unpaced rendering by
    /// setting an environment variable before it loads the engine, and an engine
    /// too old to know about the variable ignores it and produces a full set of
    /// perfectly plausible numbers six times more slowly. Reading it back is how
    /// the caller can tell "the engine did what I asked" from "the engine is the
    /// one left behind by a half-finished upgrade" — trap 16's symptom.
    /// </summary>
    public bool Unpaced { get; set; }

    /// <summary>
    /// Whether the session was built from a stored benchmark profile rather than
    /// from the settings as written — see
    /// <see cref="Synthesis.BenchSwitches.NoProfileVariable"/>.
    ///
    /// <para>The sweep asserts this is false on every row. It is the only way to
    /// verify the <c>auto</c> candidate, which the thread read-back exempts by
    /// design because it asks for 0 and the engine answers with what ORT chose.
    /// It is also the honest answer to "why this thread count" for
    /// <see cref="OnnxThreads"/>, which W2 has to report.</para>
    /// </summary>
    public bool ProfileApplied { get; set; }

    public DateTime SampleTimeUtc { get; set; }

    /// <summary>
    /// When the reader observed this file, taken from its mtime — the heartbeat
    /// that says the writing process is still alive. A snapshot older than the
    /// reader's staleness window belongs to an engine instance whose host has
    /// gone away, usually by crashing rather than exiting.
    ///
    /// Never serialized: it describes the file, not its contents, and writing it
    /// would make the value stale the instant it landed on disk.
    /// </summary>
    [JsonIgnore]
    public DateTime FileMtimeUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionSnapshot))]
public partial class SnapshotJsonContext : JsonSerializerContext { }

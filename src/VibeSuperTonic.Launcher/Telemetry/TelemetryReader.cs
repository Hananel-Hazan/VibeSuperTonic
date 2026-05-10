using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace VibeSuperTonic.Launcher.Telemetry;

/// <summary>
/// Snapshot of the engine's most recent state. The engine writes this struct
/// (Phase 2) into a named shared-memory segment. The Control Panel's Monitor
/// tab polls this reader at ~5 Hz. Until Phase 2 lands the segment will not
/// exist — IsAvailable returns false and the Monitor tab shows "Idle".
/// </summary>
internal sealed class TelemetrySnapshot
{
    public string VoiceId { get; set; } = "";
    public string TextSnippet { get; set; } = "";
    public int    TotalStep { get; set; }
    public float  EngineSpeed { get; set; }
    public float  DspRate { get; set; }
    public double FirstByteLatencyMs { get; set; }
    public double RollingRtf { get; set; }
    public int    PipelineDepth { get; set; }
    public double InterChunkGapMs { get; set; }
    public double EngineCpuPct { get; set; }
    public double EngineRssMb { get; set; }
    public int    OnnxThreads { get; set; }
    public int    UnderrunCount { get; set; }
    public string LastError { get; set; } = "";
    public DateTime SampleTimeUtc { get; set; }
    public bool   IsActive { get; set; }
}

internal static class TelemetryReader
{
    public const string SegmentName = @"Local\VibeSuperTonic.Telemetry";
    public const int SegmentSize = 4096;
    private const int Magic = 0x56535453; // 'VSTS'

    public static bool IsAvailable()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SegmentName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static TelemetrySnapshot? TryRead()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SegmentName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, SegmentSize, MemoryMappedFileAccess.Read);
            view.Read(0, out RawHeader hdr);
            if (hdr.Magic != Magic) return null;

            var snap = new TelemetrySnapshot
            {
                IsActive = hdr.IsActive != 0,
                TotalStep = hdr.TotalStep,
                EngineSpeed = hdr.EngineSpeed,
                DspRate = hdr.DspRate,
                FirstByteLatencyMs = hdr.FirstByteLatencyMs,
                RollingRtf = hdr.RollingRtf,
                PipelineDepth = hdr.PipelineDepth,
                InterChunkGapMs = hdr.InterChunkGapMs,
                EngineCpuPct = hdr.EngineCpuPct,
                EngineRssMb = hdr.EngineRssMb,
                OnnxThreads = hdr.OnnxThreads,
                UnderrunCount = hdr.UnderrunCount,
                SampleTimeUtc = DateTime.FromBinary(hdr.SampleTimeBinary),
            };

            // Strings live after the header. Each is a UTF-8 length-prefixed buffer.
            int offset = Marshal.SizeOf<RawHeader>();
            snap.VoiceId = ReadLenPrefixedString(view, ref offset);
            snap.TextSnippet = ReadLenPrefixedString(view, ref offset);
            snap.LastError = ReadLenPrefixedString(view, ref offset);
            return snap;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception) { return null; }
    }

    private static string ReadLenPrefixedString(MemoryMappedViewAccessor view, ref int offset)
    {
        if (offset + 4 > SegmentSize) return "";
        int len = view.ReadInt32(offset);
        offset += 4;
        if (len <= 0 || offset + len > SegmentSize) return "";
        var buf = new byte[len];
        view.ReadArray(offset, buf, 0, len);
        offset += len;
        return System.Text.Encoding.UTF8.GetString(buf);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RawHeader
    {
        public int    Magic;
        public int    IsActive;
        public int    TotalStep;
        public float  EngineSpeed;
        public float  DspRate;
        public double FirstByteLatencyMs;
        public double RollingRtf;
        public int    PipelineDepth;
        public double InterChunkGapMs;
        public double EngineCpuPct;
        public double EngineRssMb;
        public int    OnnxThreads;
        public int    UnderrunCount;
        public long   SampleTimeBinary;
    }
}

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeSuperTonic.Engine.Telemetry;

/// <summary>
/// Writes a single-snapshot view (most-recent state, not a full ring buffer) of
/// the engine into a named shared-memory segment. The Control Panel's Monitor
/// tab opens it read-only and refreshes ~5 Hz. Layout MUST exactly match
/// VibeSuperTonic.Launcher.Telemetry.TelemetryReader.RawHeader.
/// </summary>
internal static class TelemetryWriter
{
    public const string SegmentName = @"Local\VibeSuperTonic.Telemetry";
    public const int SegmentSize = 4096;
    private const int Magic = 0x56535453; // 'VSTS'

    private static readonly object _gate = new();
    private static MemoryMappedFile? _mmf;
    private static MemoryMappedViewAccessor? _view;
    private static bool _initFailed;

    private static long _engineCpuStartTicks;
    private static long _engineCpuStartWallMs;

    public static void Update(
        bool isActive,
        string voiceId,
        string textSnippet,
        int totalStep,
        float engineSpeed,
        float dspRate,
        double firstByteLatencyMs,
        double rollingRtf,
        int pipelineDepth,
        double interChunkGapMs,
        int onnxThreads,
        int underrunCount,
        string lastError)
    {
        if (_initFailed) return;
        try
        {
            EnsureOpen();
            if (_view is null) return;

            // Compute engine CPU% over the time delta since last update.
            var proc = Process.GetCurrentProcess();
            long nowCpuTicks = proc.TotalProcessorTime.Ticks;
            long nowWallMs = Environment.TickCount64;
            double cpuPct = 0;
            if (_engineCpuStartWallMs > 0)
            {
                long wallDelta = nowWallMs - _engineCpuStartWallMs;
                long cpuDelta = nowCpuTicks - _engineCpuStartTicks;
                if (wallDelta > 0)
                    cpuPct = TimeSpan.FromTicks(cpuDelta).TotalMilliseconds / (double)wallDelta * 100.0;
            }
            _engineCpuStartTicks = nowCpuTicks;
            _engineCpuStartWallMs = nowWallMs;
            double rssMb = proc.WorkingSet64 / (1024.0 * 1024.0);

            var hdr = new RawHeader
            {
                Magic = Magic,
                IsActive = isActive ? 1 : 0,
                TotalStep = totalStep,
                EngineSpeed = engineSpeed,
                DspRate = dspRate,
                FirstByteLatencyMs = firstByteLatencyMs,
                RollingRtf = rollingRtf,
                PipelineDepth = pipelineDepth,
                InterChunkGapMs = interChunkGapMs,
                EngineCpuPct = cpuPct,
                EngineRssMb = rssMb,
                OnnxThreads = onnxThreads,
                UnderrunCount = underrunCount,
                SampleTimeBinary = DateTime.UtcNow.ToBinary(),
            };

            lock (_gate)
            {
                _view.Write(0, ref hdr);
                int offset = Marshal.SizeOf<RawHeader>();
                WriteLenPrefixedString(_view, ref offset, voiceId ?? "");
                WriteLenPrefixedString(_view, ref offset, Truncate(textSnippet, 80));
                WriteLenPrefixedString(_view, ref offset, lastError ?? "");
            }
        }
        catch
        {
            // If anything goes wrong (security, OOM, GC during write), turn off telemetry
            // for this process. Engine MUST keep speaking — telemetry is best-effort.
            _initFailed = true;
            try { _view?.Dispose(); _mmf?.Dispose(); } catch { }
            _view = null; _mmf = null;
        }
    }

    public static void MarkIdle()
    {
        Update(
            isActive: false, voiceId: "", textSnippet: "",
            totalStep: 0, engineSpeed: 0, dspRate: 0,
            firstByteLatencyMs: 0, rollingRtf: double.NaN,
            pipelineDepth: 0, interChunkGapMs: 0,
            onnxThreads: 0, underrunCount: 0, lastError: "");
    }

    private static void EnsureOpen()
    {
        if (_view is not null) return;
        lock (_gate)
        {
            if (_view is not null) return;
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(SegmentName, SegmentSize, MemoryMappedFileAccess.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, SegmentSize, MemoryMappedFileAccess.ReadWrite);
            }
            catch
            {
                _initFailed = true;
                _view = null;
                _mmf?.Dispose();
                _mmf = null;
            }
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max);
    }

    private static void WriteLenPrefixedString(MemoryMappedViewAccessor view, ref int offset, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        if (offset + 4 + bytes.Length > SegmentSize) return;
        view.Write(offset, bytes.Length);
        offset += 4;
        if (bytes.Length > 0)
        {
            view.WriteArray(offset, bytes, 0, bytes.Length);
            offset += bytes.Length;
        }
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

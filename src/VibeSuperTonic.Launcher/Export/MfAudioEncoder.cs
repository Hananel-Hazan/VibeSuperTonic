using System.Runtime.InteropServices;

namespace VibeSuperTonic.Launcher.Export;

/// <summary>
/// Media Foundation transcoder: reads a 44.1 kHz 16-bit mono WAV file and
/// writes an MP3 or AAC/M4A file at the requested bitrate. Built on the
/// SourceReader → SinkWriter pattern — the simplest MF pipeline for one-shot
/// transcoding. No external dependencies; uses only Windows-shipped DLLs
/// (mfplat, mfreadwrite, mfuuid).
///
/// Why MF and not LAME / NAudio:
/// - Zero extra binaries to ship (the project already values its zero-dep posture).
/// - Both MP3 and AAC encoders are built into every Windows install since Vista/7.
/// - Same code path serves both formats; container is inferred from the .mp3/.m4a extension.
///
/// Bitrate clamping: the AAC encoder accepts only a small set of average-bytes-per-
/// second values; anything else fails SetOutputMediaType silently. We map the
/// requested kbps to the closest supported value before configuring the sink.
/// </summary>
internal static class MfAudioEncoder
{
    public static void TranscodeWavToOutput(
        string wavPath, string outputPath,
        ExportFormat fmt, int bitrateBps,
        IProgress<string>? log, CancellationToken ct)
    {
        Check(MfApi.MFStartup(MfApi.MF_VERSION, 0), "MFStartup");
        try
        {
            TranscodeCore(wavPath, outputPath, fmt, bitrateBps, log, ct);
        }
        finally
        {
            try { MfApi.MFShutdown(); } catch { }
        }
    }

    private static void TranscodeCore(string wavPath, string outputPath,
        ExportFormat fmt, int bitrateBps,
        IProgress<string>? log, CancellationToken ct)
    {
        Check(MfApi.MFCreateSourceReaderFromURL(wavPath, IntPtr.Zero, out var reader), "MFCreateSourceReaderFromURL");
        Check(MfApi.MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, IntPtr.Zero, out var writer), "MFCreateSinkWriterFromURL");

        try
        {
            ConfigureSource(reader);
            int streamIndex = ConfigureSink(writer, fmt, bitrateBps, log);

            Check(writer.BeginWriting(), "SinkWriter.BeginWriting");

            long lastReportTicks = 0;
            long totalBytes = 0;
            // Source-reader stream selection 0 == the first (only) audio stream
            // for a WAV; ReadSample's anyStream output goes ignored.
            while (!ct.IsCancellationRequested)
            {
                int hr = reader.ReadSample(
                    MfApi.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                    0,
                    out _,
                    out uint flags,
                    out long timestamp,
                    out IntPtr sample);
                Check(hr, "SourceReader.ReadSample");

                if ((flags & MfApi.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    log?.Report("Source reader EOS.");
                    break;
                }
                if (sample == IntPtr.Zero) continue; // gap / no buffer yet

                try
                {
                    Check(writer.WriteSample((uint)streamIndex, sample), "SinkWriter.WriteSample");
                }
                finally
                {
                    Marshal.Release(sample);
                }

                long nowTicks = Environment.TickCount64;
                if (nowTicks - lastReportTicks >= 500)
                {
                    lastReportTicks = nowTicks;
                    double sec = timestamp / 1e7; // hns → seconds
                    log?.Report($"Encoded {sec:F1}s of audio…");
                    totalBytes += 1; // counter ticks; full byte total available via GetStatistics if ever needed
                }
            }

            Check(writer.Finalize_(), "SinkWriter.Finalize");
            log?.Report(ct.IsCancellationRequested ? "Transcode cancelled (output may be truncated)." : "Transcode complete.");
        }
        finally
        {
            Marshal.FinalReleaseComObject(writer);
            Marshal.FinalReleaseComObject(reader);
        }
    }

    /// <summary>
    /// Forces the source reader to deliver PCM 44.1 kHz 16-bit mono. Our WAV
    /// is already in that format (the engine renders natively at 44.1 mono),
    /// so this is a no-op conversion — but setting it explicitly avoids the
    /// reader picking up some other intermediate compressed format.
    /// </summary>
    private static void ConfigureSource(IMFSourceReader reader)
    {
        Check(MfApi.MFCreateMediaType(out var pcmType), "MFCreateMediaType(PCM)");
        try
        {
            SetMajor(pcmType, MfApi.MFMediaType_Audio);
            SetSubtype(pcmType, MfApi.MFAudioFormat_PCM);
            SetU32(pcmType, MfApi.MF_MT_AUDIO_NUM_CHANNELS, 1);
            SetU32(pcmType, MfApi.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
            SetU32(pcmType, MfApi.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetU32(pcmType, MfApi.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
            SetU32(pcmType, MfApi.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 88_200);
            Check(reader.SetCurrentMediaType(MfApi.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, pcmType),
                "SourceReader.SetCurrentMediaType(PCM)");
            Check(reader.SetStreamSelection(MfApi.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true), "SetStreamSelection");
        }
        finally { Marshal.FinalReleaseComObject(pcmType); }
    }

    /// <summary>
    /// Output type first (MP3 or AAC at target bitrate), then PCM input type.
    /// MF docs are strict about this order — SetInputMediaType validates the
    /// encoder pairing against the already-set output type. Returns the
    /// stream index assigned by AddStream (always 0 here; we have one stream).
    /// </summary>
    private static int ConfigureSink(IMFSinkWriter writer, ExportFormat fmt, int bitrateBps, IProgress<string>? log)
    {
        // AAC accepts a hand-built output type; MP3 only accepts one of the
        // encoder's advertised types (else MF_E_INVALIDMEDIATYPE).
        IMFMediaType outType = fmt == ExportFormat.Aac
            ? BuildAacOutputType(bitrateBps, log)
            : GetMp3OutputType(bitrateBps, log);
        Check(MfApi.MFCreateMediaType(out var inType), "MFCreateMediaType(input PCM)");
        try
        {
            Check(writer.AddStream(outType, out uint streamIdx), "SinkWriter.AddStream");

            SetMajor(inType, MfApi.MFMediaType_Audio);
            SetSubtype(inType, MfApi.MFAudioFormat_PCM);
            SetU32(inType, MfApi.MF_MT_AUDIO_NUM_CHANNELS, 1);
            SetU32(inType, MfApi.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
            SetU32(inType, MfApi.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetU32(inType, MfApi.MF_MT_AUDIO_BLOCK_ALIGNMENT, 2);
            SetU32(inType, MfApi.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 88_200);
            Check(writer.SetInputMediaType(streamIdx, inType, IntPtr.Zero), "SinkWriter.SetInputMediaType(PCM)");

            return (int)streamIdx;
        }
        finally
        {
            Marshal.FinalReleaseComObject(outType);
            Marshal.FinalReleaseComObject(inType);
        }
    }

    private static IMFMediaType BuildAacOutputType(int bitrateBps, IProgress<string>? log)
    {
        Check(MfApi.MFCreateMediaType(out var t), "MFCreateMediaType(AAC out)");
        int effectiveBps = ClampAacBitrate(bitrateBps);
        SetMajor(t, MfApi.MFMediaType_Audio);
        SetSubtype(t, MfApi.MFAudioFormat_AAC);
        SetU32(t, MfApi.MF_MT_AUDIO_NUM_CHANNELS, 1);
        SetU32(t, MfApi.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
        SetU32(t, MfApi.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        SetU32(t, MfApi.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(effectiveBps / 8));
        SetU32(t, MfApi.MF_MT_AAC_PAYLOAD_TYPE, 0);                       // raw AAC
        SetU32(t, MfApi.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);  // AAC LC
        if (effectiveBps != bitrateBps) log?.Report($"Bitrate clamped to {effectiveBps / 1000} kbps for AAC encoder.");
        return t;
    }

    /// <summary>
    /// Picks a COMPLETE MP3 output type from the encoder's advertised set —
    /// filtered to our PCM input shape (mono, 44.1 kHz) and the bitrate closest
    /// to the target. Hand-built MP3 types fail SetInputMediaType with
    /// MF_E_INVALIDMEDIATYPE because they lack the MPEG layer-3 user-data blob.
    /// </summary>
    private static IMFMediaType GetMp3OutputType(int bitrateBps, IProgress<string>? log)
    {
        Check(MfApi.MFTranscodeGetAudioOutputAvailableTypes(
                MfApi.MFAudioFormat_MP3, MfApi.MFT_ENUM_FLAG_ALL, IntPtr.Zero, out var coll),
            "MFTranscodeGetAudioOutputAvailableTypes(MP3)");
        try
        {
            Check(coll.GetElementCount(out uint count), "IMFCollection.GetElementCount");
            IMFMediaType? best = null;
            int bestDelta = int.MaxValue, chosenBps = 0;
            for (uint i = 0; i < count; i++)
            {
                Check(coll.GetElement(i, out object elem), "IMFCollection.GetElement");
                var mt = (IMFMediaType)elem;
                bool kept = false;
                if (mt.GetUINT32(MfApi.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint sr) == 0 && sr == 44100 &&
                    mt.GetUINT32(MfApi.MF_MT_AUDIO_NUM_CHANNELS, out uint ch) == 0 && ch == 1 &&
                    mt.GetUINT32(MfApi.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, out uint abps) == 0)
                {
                    int bps = (int)abps * 8;
                    int d = Math.Abs(bps - bitrateBps);
                    if (d < bestDelta)
                    {
                        bestDelta = d; chosenBps = bps;
                        if (best is not null) Marshal.FinalReleaseComObject(best);
                        best = mt; kept = true;
                    }
                }
                if (!kept) Marshal.FinalReleaseComObject(mt);
            }
            if (best is null)
                throw new InvalidOperationException("MP3 encoder advertised no mono 44.1 kHz output type.");
            log?.Report($"MP3 encoder output: {chosenBps / 1000} kbps (mono 44.1 kHz).");
            return best;
        }
        finally { Marshal.FinalReleaseComObject(coll); }
    }

    private static int ClampAacBitrate(int bps)
    {
        // The Microsoft AAC encoder accepts exactly four average-bytes-per-second
        // values: 12000, 16000, 20000, 24000 — i.e. 96, 128, 160, 192 kbps.
        int[] supported = { 96_000, 128_000, 160_000, 192_000 };
        return PickClosest(bps, supported);
    }

    private static int ClampMp3Bitrate(int bps)
    {
        int[] supported = { 96_000, 128_000, 160_000, 192_000, 256_000, 320_000 };
        return PickClosest(bps, supported);
    }

    private static int PickClosest(int target, int[] choices)
    {
        int best = choices[0];
        int bestDelta = Math.Abs(target - best);
        for (int i = 1; i < choices.Length; i++)
        {
            int d = Math.Abs(target - choices[i]);
            if (d < bestDelta) { best = choices[i]; bestDelta = d; }
        }
        return best;
    }

    private static void SetMajor(IMFMediaType t, Guid g) => Check(t.SetGUID(MfApi.MF_MT_MAJOR_TYPE, g), "SetGUID(MAJOR_TYPE)");
    private static void SetSubtype(IMFMediaType t, Guid g) => Check(t.SetGUID(MfApi.MF_MT_SUBTYPE, g), "SetGUID(SUBTYPE)");
    private static void SetU32(IMFMediaType t, Guid attr, uint v) => Check(t.SetUINT32(attr, v), "SetUINT32");

    private static void Check(int hr, string op)
    {
        if (hr < 0) throw new InvalidOperationException($"{op} failed (HRESULT 0x{hr:X8})");
    }
}

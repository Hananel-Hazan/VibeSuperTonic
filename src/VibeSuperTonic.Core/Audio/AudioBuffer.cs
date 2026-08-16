using System.Buffers.Binary;

namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// PCM conversion and a minimal WAV writer. Both hosts need these and neither
/// needs a dependency for them: the engine's native output is 44100 Hz mono
/// s16le, which is also what SAPI wants and what the PulseAudio sink will be
/// opened with, so nothing in the chain resamples.
/// </summary>
public static class AudioBuffer
{
    /// <summary>
    /// Float samples to 16-bit PCM, clamped. Values outside ±1 are produced by
    /// the vocoder often enough to matter; without the clamp they wrap and the
    /// result is a loud click rather than a quiet clip.
    /// </summary>
    public static short[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            if (s > 1f) s = 1f;
            else if (s < -1f) s = -1f;
            pcm[i] = (short)(s * 32767f);
        }
        return pcm;
    }

    /// <summary>Number of whole samples in a byte count of 16-bit PCM.</summary>
    public static int BytesToSamples(long bytes) => (int)(bytes / sizeof(short));

    /// <summary>Duration of a 16-bit mono PCM buffer.</summary>
    public static TimeSpan Duration(int sampleCount, int sampleRate) =>
        sampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)sampleCount / sampleRate);
}

/// <summary>
/// 44-byte canonical RIFF/WAVE header plus samples. Enough for mono 16-bit PCM
/// and deliberately nothing more — the moment this needs formats, it should
/// become a real encoder rather than grow options.
/// </summary>
public static class WavWriter
{
    public static void WriteMono16(Stream stream, ReadOnlySpan<short> samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int dataBytes = samples.Length * sizeof(short);
        Span<byte> header = stackalloc byte[44];

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);          // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);           // mono
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);           // block align
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);          // bits per sample
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);

        stream.Write(header);

        var bytes = new byte[dataBytes];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        stream.Write(bytes);
    }

    public static void WriteMono16(string path, ReadOnlySpan<short> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        WriteMono16(fs, samples, sampleRate);
    }
}

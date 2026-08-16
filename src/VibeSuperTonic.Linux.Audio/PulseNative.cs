using System.Runtime.InteropServices;

namespace VibeSuperTonic.Linux.Audio;

/// <summary>
/// The libpulse "simple" API, which is all this product needs: one blocking
/// playback stream, a latency reading, flush and drain.
///
/// The asynchronous <c>pa_stream</c> API is the alternative and is strictly more
/// capable — notably <c>pa_stream_get_time</c>, which is authoritative where
/// <c>pa_simple_get_latency</c> is merely good. It also brings a mainloop, a
/// callback threading model and roughly five times the code. The port plan's
/// contingency is to move if the simple API's clock proves inaccurate under
/// pipewire-pulse, so the measurement that decides it is in
/// <c>spike/linux-play --calibrate</c> rather than in an opinion here.
/// </summary>
internal static partial class PulseNative
{
    /// <summary>
    /// Ships in <c>libpulse0</c>, present on any Mint desktop and on anything
    /// running pipewire-pulse. Probed explicitly at sink construction so a
    /// missing package is an install hint rather than a
    /// <c>DllNotFoundException</c> out of the middle of a speak.
    /// </summary>
    internal const string LibSimple = "libpulse-simple.so.0";

    /// <summary><c>pa_strerror</c> lives in the main library, not the simple wrapper.</summary>
    internal const string LibPulse = "libpulse.so.0";

    internal const int PA_STREAM_PLAYBACK = 1;
    internal const int PA_SAMPLE_S16LE = 3;

    /// <summary>Fields left at this value take libpulse's default.</summary>
    internal const uint Default = uint.MaxValue;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;      // pa_sample_format_t
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;    // target buffer fill — this is the stop-latency knob
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;   // recording only
    }

    [LibraryImport(LibSimple, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial IntPtr pa_simple_new(
        string? server,
        string name,
        int dir,
        string? dev,
        string streamName,
        SampleSpec* ss,
        IntPtr channelMap,
        BufferAttr* attr,
        int* error);

    [LibraryImport(LibSimple)]
    internal static unsafe partial int pa_simple_write(IntPtr s, void* data, nuint bytes, int* error);

    [LibraryImport(LibSimple)]
    internal static unsafe partial ulong pa_simple_get_latency(IntPtr s, int* error);

    [LibraryImport(LibSimple)]
    internal static unsafe partial int pa_simple_drain(IntPtr s, int* error);

    [LibraryImport(LibSimple)]
    internal static unsafe partial int pa_simple_flush(IntPtr s, int* error);

    [LibraryImport(LibSimple)]
    internal static partial void pa_simple_free(IntPtr s);

    [LibraryImport(LibPulse)]
    internal static partial IntPtr pa_strerror(int error);

    internal static string Describe(int error)
    {
        try
        {
            IntPtr p = pa_strerror(error);
            return p == IntPtr.Zero ? $"error {error}" : Marshal.PtrToStringUTF8(p) ?? $"error {error}";
        }
        catch (DllNotFoundException)
        {
            // Only reachable if libpulse-simple loaded but libpulse did not,
            // which should be impossible — but losing the real error behind a
            // secondary failure would be worse than a bare number.
            return $"error {error}";
        }
    }
}

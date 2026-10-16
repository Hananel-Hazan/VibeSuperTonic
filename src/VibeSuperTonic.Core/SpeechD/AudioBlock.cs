using System.Globalization;

namespace VibeSuperTonic.Core.SpeechD;

/// <summary>
/// A <c>705</c> audio block: samples handed back to speech-dispatcher instead of
/// played.
///
/// <para><b>This is the whole reason route B costs no audio backend.</b> The
/// module describes a block of PCM and returns it; the <em>server</em> plays it.
/// So this process never opens a device, never chooses between PulseAudio,
/// PipeWire and ALSA, and — the part that matters to a screen-reader user —
/// speech-dispatcher can stop what it started, because it owns the playback.
/// docs/SPEECHD-PLAN.md, trap 3.</para>
///
/// <para>The framing, measured rather than assumed — see <see cref="Hdlc"/> and
/// trap 17:</para>
/// <code>
/// 705-bits=16\n 705-num_channels=1\n 705-sample_rate=R\n
/// 705-num_samples=N\n 705-big_endian=0\n
/// 705-AUDIO&lt;NUL&gt;          &lt;- a NUL byte, NOT a newline
/// &lt;HDLC-escaped PCM&gt;
/// \n705 AUDIO\n
/// </code>
/// </summary>
public static class AudioBlock
{
    /// <summary>
    /// How much PCM goes in one block, matching speech-dispatcher's own
    /// <c>MAX_CHUNK</c>.
    ///
    /// <para><b>It is a latency bound, not a buffer size.</b> Upstream reads its
    /// input again between blocks, and that is the only thing that lets a
    /// <c>STOP</c> interrupt an utterance already in flight — a module that hands
    /// back one large block cannot be stopped until it has finished writing it.
    /// Orca stops on very nearly every keystroke, so this is not a tuning knob.
    /// At 22.05 kHz mono it is 227 ms.</para>
    /// </summary>
    public const int MaxChunkBytes = 10000;

    /// <summary>
    /// Write one block. <paramref name="pcm"/> must be whole frames.
    /// </summary>
    /// <param name="bigEndian">
    /// Always false here. Present because the field is in the protocol and a
    /// reader of this code should not have to guess that we send little-endian.
    /// </param>
    public static void Write(
        Stream output,
        ReadOnlySpan<byte> pcm,
        int sampleRate,
        int channels = 1,
        int bits = 16,
        bool bigEndian = false)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bits <= 0 || bits % 8 != 0) throw new ArgumentOutOfRangeException(nameof(bits));

        int frame = channels * bits / 8;
        if (pcm.Length % frame != 0)
            throw new ArgumentException(
                $"{pcm.Length} bytes is not a whole number of {frame}-byte frames", nameof(pcm));

        int samples = pcm.Length / frame;

        // Invariant culture on every number. A module inherits the environment
        // speech-dispatcher was started in, which on a French desktop means a
        // locale where "22050" is fine but a decimal comma is one refactor away —
        // and the server parses these with strtol.
        var header =
            $"705-bits={bits.ToString(CultureInfo.InvariantCulture)}\n" +
            $"705-num_channels={channels.ToString(CultureInfo.InvariantCulture)}\n" +
            $"705-sample_rate={sampleRate.ToString(CultureInfo.InvariantCulture)}\n" +
            $"705-num_samples={samples.ToString(CultureInfo.InvariantCulture)}\n" +
            $"705-big_endian={(bigEndian ? 1 : 0)}\n" +
            "705-AUDIO";

        output.Write(System.Text.Encoding.ASCII.GetBytes(header));
        output.WriteByte(0);                     // the NUL, not a newline
        output.Write(Hdlc.Escape(pcm));
        output.WriteByte(Hdlc.Newline);          // the unescaped newline that ends it
        output.Write("705 AUDIO\n"u8);
        output.Flush();
    }
}

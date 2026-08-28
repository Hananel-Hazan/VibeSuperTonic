namespace VibeSuperTonic.Core.SpeechD;

/// <summary>
/// The format at the front of a WAV stream, read without seeking.
///
/// <para><b>One reader for both voices.</b> <c>vst-ctl render --out -</c> and the
/// bundled <c>espeak-ng --stdout</c> both write a WAV to a pipe, so the module
/// has a single audio path and the choice between the neural voice and the fast
/// one changes nothing downstream of here.</para>
///
/// <para><b>Nothing may be over-read.</b> The source is a pipe carrying an
/// utterance that is still being synthesised; a buffered read that grabbed
/// "whatever is available" would swallow the first samples and hand back audio
/// that starts a syllable late. Every read here is for an exact, known count.</para>
///
/// <para><b>And the sizes in the header are a lie, on purpose.</b> A streaming
/// WAV cannot know its own length — <see cref="Ipc.RenderWav.WriteHeader"/>
/// writes <c>0xFFFFFFFF</c> for both, and espeak-ng writes its own placeholder.
/// So the <c>data</c> chunk's declared size is ignored and the samples are read
/// until the pipe closes. A reader that trusted it would stop after 4 GB or,
/// worse, after 0 bytes.</para>
/// </summary>
/// <param name="SampleRate">Hz, as the producing voice reported it.</param>
/// <param name="Channels">1 for everything this product renders.</param>
/// <param name="Bits">16 for everything this product renders.</param>
public readonly record struct WavHeader(int SampleRate, int Channels, int Bits)
{
    /// <summary>Bytes in one frame — the unit a block must not be split inside.</summary>
    public int FrameBytes => Channels * Bits / 8;

    /// <summary>
    /// Read the header, leaving <paramref name="input"/> positioned at the first
    /// sample byte. Returns null when the stream is not a WAV at all — which is
    /// what a synthesiser that printed an error message to stdout looks like.
    /// </summary>
    public static WavHeader? Read(Stream input)
    {
        Span<byte> riff = stackalloc byte[12];
        if (!ReadExactly(input, riff)) return null;

        if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8))
            return null;

        int rate = 0, channels = 0, bits = 0;
        bool haveFormat = false;

        // Walk the chunks. 'fmt ' is not required to be first and 'data' is not
        // required to be second: espeak-ng and .NET both happen to write them in
        // the obvious order, and depending on that would be a silent break the
        // day one of them adds a LIST chunk.
        Span<byte> head = stackalloc byte[8];
        while (true)
        {
            if (!ReadExactly(input, head)) return null;

            uint size = BitConverter.ToUInt32(head[4..8]);

            if (head[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16) return null;

                Span<byte> fmt = stackalloc byte[16];
                if (!ReadExactly(input, fmt)) return null;

                channels = BitConverter.ToInt16(fmt[2..4]);
                rate = BitConverter.ToInt32(fmt[4..8]);
                bits = BitConverter.ToInt16(fmt[14..16]);
                haveFormat = true;

                if (!Skip(input, size - 16)) return null;
            }
            else if (head[..4].SequenceEqual("data"u8))
            {
                // The size here is ignored on purpose — see the class remarks.
                if (!haveFormat || rate <= 0 || channels <= 0 || bits <= 0 || bits % 8 != 0)
                    return null;

                return new WavHeader(rate, channels, bits);
            }
            else
            {
                // An unknown chunk, padded to an even length by the spec.
                if (!Skip(input, size + (size & 1))) return null;
            }
        }
    }

    /// <summary>
    /// Fill <paramref name="buffer"/> completely, or report that the stream ended.
    /// <see cref="Stream.Read(Span{byte})"/> on a pipe returns what has arrived,
    /// not what was asked for, and treating a short read as an error would fail on
    /// perfectly good audio that simply had not finished being synthesised.
    /// </summary>
    private static bool ReadExactly(Stream input, Span<byte> buffer)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int n = input.Read(buffer[at..]);
            if (n <= 0) return false;
            at += n;
        }

        return true;
    }

    private static bool Skip(Stream input, long count)
    {
        Span<byte> bin = stackalloc byte[512];
        while (count > 0)
        {
            int want = (int)Math.Min(count, bin.Length);
            if (!ReadExactly(input, bin[..want])) return false;
            count -= want;
        }

        return true;
    }
}

using System.Text;
using VibeSuperTonic.Core.SpeechD;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The 705 audio block and its escaping — docs/SPEECHD-PLAN.md trap 17, which
/// cost the gate two days and was misdiagnosed twice before it was read out of
/// speech-dispatcher's own source.
///
/// <para><b>Why this is worth a test file of its own.</b> Getting it wrong does
/// not raise an error anywhere: the server reads a truncated block, plays it, and
/// then waits forever for an end-of-utterance that has been desynchronised into
/// the sample data. To the user that is a screen reader which says one thing and
/// then goes quiet permanently — and the only symptom on this side is a module
/// that looks like it is working.</para>
/// </summary>
public class SpeechdAudioTests
{
    // ------------------------------------------------------------------ HDLC

    [Fact]
    public void Audio_with_nothing_special_in_it_is_untouched()
    {
        var pcm = new byte[] { 0x01, 0x02, 0x7C, 0x7E, 0xFF, 0x00 };
        Assert.Equal(pcm, Hdlc.Escape(pcm));
    }

    /// <summary>
    /// THE BYTE THAT ENDS THE BLOCK. It is a sample value like any other, and in
    /// real audio it arrives within milliseconds.
    /// </summary>
    [Fact]
    public void A_newline_in_the_samples_is_escaped()
    {
        Assert.Equal(
            new byte[] { 0x01, Hdlc.EscapeByte, 0x0A ^ 0x20, 0x02 },
            Hdlc.Escape(new byte[] { 0x01, 0x0A, 0x02 }));
    }

    [Fact]
    public void The_escape_byte_itself_is_escaped()
    {
        Assert.Equal(
            new byte[] { Hdlc.EscapeByte, 0x7D ^ 0x20 },
            Hdlc.Escape(new byte[] { 0x7D }));
    }

    /// <summary>
    /// The only check of an escape that is worth anything: every byte value, in
    /// every adjacent pair, survives the round trip. An escape that is not
    /// reversible produces audio that is subtly wrong rather than absent.
    /// </summary>
    [Fact]
    public void Every_byte_pair_survives_a_round_trip()
    {
        var pcm = new byte[256 * 256 * 2];
        int at = 0;
        for (int a = 0; a < 256; a++)
        for (int b = 0; b < 256; b++)
        {
            pcm[at++] = (byte)a;
            pcm[at++] = (byte)b;
        }

        Assert.Equal(pcm, Hdlc.Unescape(Hdlc.Escape(pcm)));
    }

    [Fact]
    public void Escaping_nothing_produces_nothing()
    {
        Assert.Empty(Hdlc.Escape(Array.Empty<byte>()));
    }

    // ------------------------------------------------------------ the block

    private static byte[] Block(byte[] pcm, int rate = 22050, int channels = 1, int bits = 16)
    {
        var ms = new MemoryStream();
        AudioBlock.Write(ms, pcm, rate, channels, bits);
        return ms.ToArray();
    }

    private static (string Header, byte[] Body) Split(byte[] block)
    {
        int nul = Array.IndexOf(block, (byte)0);
        Assert.True(nul > 0, "the block has no NUL separating its header from its samples");
        return (Encoding.ASCII.GetString(block, 0, nul), block[(nul + 1)..]);
    }

    [Fact]
    public void The_header_names_the_format_the_server_needs()
    {
        var (header, _) = Split(Block(new byte[8], rate: 44100));

        Assert.Contains("705-bits=16\n", header);
        Assert.Contains("705-num_channels=1\n", header);
        Assert.Contains("705-sample_rate=44100\n", header);
        Assert.Contains("705-num_samples=4\n", header);      // 8 bytes of 16-bit mono
        Assert.Contains("705-big_endian=0\n", header);
        Assert.EndsWith("705-AUDIO", header);
    }

    /// <summary>
    /// A NUL, NOT A NEWLINE — the one place in this protocol where the obvious
    /// guess is wrong, and half of what made the gate hang.
    /// </summary>
    [Fact]
    public void The_header_is_separated_from_the_samples_by_a_nul()
    {
        byte[] block = Block(new byte[] { 1, 2, 3, 4 });
        int marker = Encoding.ASCII.GetString(block).IndexOf("705-AUDIO", StringComparison.Ordinal);

        Assert.True(marker >= 0);
        Assert.Equal(0, block[marker + "705-AUDIO".Length]);
    }

    [Fact]
    public void The_block_ends_with_an_unescaped_newline_and_the_terminator()
    {
        byte[] block = Block(new byte[] { 1, 2, 3, 4 });
        string tail = Encoding.ASCII.GetString(block[^11..]);

        Assert.Equal("\n705 AUDIO\n", tail);
    }

    /// <summary>
    /// End to end: samples containing the two dangerous bytes come back exactly
    /// as they went in, and the terminator is still findable because the only
    /// unescaped newline in the whole block is the one that ends it.
    /// </summary>
    [Fact]
    public void Samples_full_of_newlines_survive_and_the_block_still_ends_where_it_should()
    {
        var pcm = new byte[] { 0x0A, 0x0A, 0x7D, 0x7D, 0x0A, 0x00, 0x7D, 0x0A };
        byte[] block = Block(pcm);
        var (_, body) = Split(block);

        // The body is the escaped samples, then \n, then "705 AUDIO\n".
        byte[] escaped = body[..^"\n705 AUDIO\n".Length];
        Assert.Equal(pcm, Hdlc.Unescape(escaped));

        // AND THIS IS THE PROPERTY THE SERVER ACTUALLY DEPENDS ON. It scans from
        // the NUL for the newline that ends the block; within that region there
        // must be exactly one, and it must be the last byte. Anywhere else and
        // the block ends early, in the middle of the audio, with no error.
        byte[] scanned = body[..^"705 AUDIO\n".Length];

        int unescaped = 0, lastAt = -1;
        for (int i = 0; i < scanned.Length; i++)
        {
            if (scanned[i] == Hdlc.EscapeByte) { i++; continue; }
            if (scanned[i] == (byte)'\n') { unescaped++; lastAt = i; }
        }

        Assert.Equal(1, unescaped);
        Assert.Equal(scanned.Length - 1, lastAt);
    }

    /// <summary>
    /// num_samples is frames, not bytes. Getting it wrong makes the server play
    /// half the audio or run off the end of the buffer — and stereo is where a
    /// bytes-for-frames slip stops being invisible.
    /// </summary>
    [Theory]
    [InlineData(1, 16, 8, 4)]
    [InlineData(2, 16, 8, 2)]
    [InlineData(1, 8, 8, 8)]
    public void Num_samples_counts_frames(int channels, int bits, int bytes, int expected)
    {
        var (header, _) = Split(Block(new byte[bytes], channels: channels, bits: bits));
        Assert.Contains($"705-num_samples={expected}\n", header);
    }

    [Fact]
    public void A_partial_frame_is_refused_rather_than_sent()
    {
        // Three bytes is one and a half 16-bit samples. Sending it would put a
        // click at the block boundary and shift every sample after it by a byte.
        Assert.Throws<ArgumentException>(() => Block(new byte[3]));
    }

    /// <summary>
    /// The chunk size is speech-dispatcher's own MAX_CHUNK, and it is a latency
    /// bound rather than a buffer size: it is how often a STOP can be noticed.
    /// </summary>
    [Fact]
    public void The_chunk_size_matches_the_servers_own()
    {
        Assert.Equal(10000, AudioBlock.MaxChunkBytes);
    }
}

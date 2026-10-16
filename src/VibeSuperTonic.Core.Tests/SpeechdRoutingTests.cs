using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.SpeechD;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Routing by message type, and reading the WAV both voices produce.
///
/// <para>The routing is the reason this is a native module rather than a generic
/// one: S0 measured 383 ms for a single character against 3.3 ms for the espeak
/// this archive already ships, and a generic module cannot tell a keystroke from
/// a sentence at all. docs/SPEECHD-PLAN.md, traps 10 and 15.</para>
/// </summary>
public class SpeechdRoutingTests
{
    // --------------------------------------------------------------- routing

    /// <summary>
    /// THE DOMINANT TRAFFIC OF A SCREEN READER. 383 ms per keystroke is not an
    /// echo — a user typing at speed falls behind within a sentence and never
    /// catches up.
    /// </summary>
    [Theory]
    [InlineData(SpeechdMessageType.Char)]
    [InlineData(SpeechdMessageType.Key)]
    [InlineData(SpeechdMessageType.SoundIcon)]
    public void Short_utterances_go_to_the_fast_voice(SpeechdMessageType type)
    {
        Assert.Equal(RenderVoice.Espeak, ModuleRouting.For(type));
    }

    /// <summary>Reading is what the neural voice is for, and 718 ms before a
    /// paragraph is the same order as the hotkey people already use by choice.</summary>
    [Fact]
    public void Text_goes_to_the_neural_voice()
    {
        Assert.Equal(RenderVoice.Neural, ModuleRouting.For(SpeechdMessageType.Text));
    }

    [Theory]
    [InlineData("SPEAK", SpeechdMessageType.Text)]
    [InlineData("CHAR", SpeechdMessageType.Char)]
    [InlineData("KEY", SpeechdMessageType.Key)]
    [InlineData("SOUND_ICON", SpeechdMessageType.SoundIcon)]
    public void The_four_speak_commands_parse(string command, SpeechdMessageType expected)
    {
        Assert.Equal(expected, ModuleRouting.Parse(command));
    }

    /// <summary>
    /// Everything else must come back null so the module's dispatch stays one
    /// lookup — and so that a command it does not know is answered rather than
    /// silently treated as speech.
    /// </summary>
    [Theory]
    [InlineData("STOP")]
    [InlineData("QUIT")]
    [InlineData("SET")]
    [InlineData("AUDIO")]
    [InlineData("LIST VOICES")]
    [InlineData("speak")]
    [InlineData("")]
    public void Anything_else_is_not_a_speak_command(string command)
    {
        Assert.Null(ModuleRouting.Parse(command));
    }

    // ------------------------------------------------------------ WAV header

    private static MemoryStream Wav(int rate = 22050, int channels = 1, int bits = 16, int dataBytes = 8)
    {
        var ms = new MemoryStream();
        RenderWav.WriteHeader(ms, rate, channels);
        ms.Write(new byte[dataBytes]);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void The_header_our_own_render_writes_reads_back()
    {
        var wav = WavHeader.Read(Wav(rate: 44100));

        Assert.NotNull(wav);
        Assert.Equal(44100, wav.Value.SampleRate);
        Assert.Equal(1, wav.Value.Channels);
        Assert.Equal(16, wav.Value.Bits);
        Assert.Equal(2, wav.Value.FrameBytes);
    }

    /// <summary>
    /// The stream must be left exactly at the first sample. One byte either way
    /// shifts every 16-bit sample by a byte, which is not silence — it is noise
    /// at full amplitude.
    /// </summary>
    [Fact]
    public void The_stream_is_left_on_the_first_sample()
    {
        var ms = new MemoryStream();
        RenderWav.WriteHeader(ms, 22050, 1);
        ms.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        ms.Position = 0;

        Assert.NotNull(WavHeader.Read(ms));
        Assert.Equal(RenderWav.HeaderBytes, ms.Position);

        var rest = new byte[4];
        Assert.Equal(4, ms.Read(rest));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, rest);
    }

    /// <summary>
    /// THE SIZES IN A STREAMING WAV ARE A LIE, and both producers write one.
    /// RenderWav writes 0xFFFFFFFF because a pipe cannot be seeked. A reader that
    /// believed the data size would stop after 4 GB — or, if a producer ever
    /// writes 0, immediately.
    /// </summary>
    [Fact]
    public void An_impossible_data_size_is_ignored_rather_than_believed()
    {
        var ms = Wav(dataBytes: 8);
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(ms.ToArray(), 40));

        var wav = WavHeader.Read(ms);
        Assert.NotNull(wav);
        Assert.Equal(8, ms.Length - ms.Position);
    }

    /// <summary>
    /// espeak-ng and .NET both happen to put 'fmt ' first and 'data' second.
    /// Depending on that would break silently the day either adds a LIST chunk,
    /// which is a normal thing for a WAV to carry.
    /// </summary>
    [Fact]
    public void A_chunk_between_fmt_and_data_is_skipped()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(uint.MaxValue); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000);
        w.Write((short)2); w.Write((short)16);
        w.Write("LIST"u8); w.Write(5); w.Write("hello"u8); w.Write((byte)0);  // odd, padded
        w.Write("data"u8); w.Write(uint.MaxValue);
        w.Write(new byte[] { 1, 2 });
        ms.Position = 0;

        var wav = WavHeader.Read(ms);
        Assert.NotNull(wav);
        Assert.Equal(16000, wav.Value.SampleRate);
        Assert.Equal(2, ms.Length - ms.Position);
    }

    [Fact]
    public void Stereo_and_eight_bit_report_their_own_frame_size()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(uint.MaxValue); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(192000);
        w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(uint.MaxValue);
        ms.Position = 0;

        var wav = WavHeader.Read(ms);
        Assert.NotNull(wav);
        Assert.Equal(2, wav.Value.Channels);
        Assert.Equal(4, wav.Value.FrameBytes);
    }

    /// <summary>
    /// A DECLARED SIZE OF ZERO MUST NOT MEAN "NO AUDIO". Some writers put 0 in a
    /// streaming header rather than 0xFFFFFFFF, and a reader that believed either
    /// number would return no samples from a perfectly good utterance — which
    /// reaches the user as a screen reader that says nothing, with no error
    /// anywhere. Found by sabotage: the earlier tests all used 0xFFFFFFFF, so a
    /// reader that special-cased 0 passed every one of them.
    /// </summary>
    [Fact]
    public void A_declared_data_size_of_zero_is_ignored_like_any_other()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(0u); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(22050); w.Write(44100);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(0u);                 // zero, and there is audio after it
        w.Write(new byte[] { 1, 2, 3, 4 });
        ms.Position = 0;

        var wav = WavHeader.Read(ms);

        Assert.NotNull(wav);
        Assert.Equal(22050, wav.Value.SampleRate);
        Assert.Equal(4, ms.Length - ms.Position);
    }

    /// <summary>
    /// THE MAGIC MUST BE CHECKED, and this is the case that proves it is. The
    /// short non-WAVs below are refused by any reader simply because they run
    /// out of bytes — sabotage showed that deleting the RIFF/WAVE test broke none
    /// of them. This one is long and well-formed after its first twelve bytes, so
    /// only a reader that actually looks at the magic turns it down.
    /// </summary>
    [Fact]
    public void Something_that_is_shaped_like_a_wav_but_is_not_one_is_refused()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("OggS"u8); w.Write(uint.MaxValue); w.Write("XXXX"u8);   // not RIFF/WAVE
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(22050); w.Write(44100);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(uint.MaxValue);
        w.Write(new byte[] { 1, 2, 3, 4 });
        ms.Position = 0;

        Assert.Null(WavHeader.Read(ms));
    }

    /// <summary>
    /// A RENDERER THAT PRINTED AN ERROR TO STDOUT LOOKS EXACTLY LIKE THIS, and
    /// it is what the module's fallback decision hangs on: no WAV means try the
    /// other voice, rather than forward some text to the server as if it were
    /// audio.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("bash: vst-ctl: command not found\n")]
    [InlineData("RIFF")]
    [InlineData("RIFFxxxxWAVE")]
    public void Anything_that_is_not_a_wav_is_refused(string content)
    {
        var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content));
        Assert.Null(WavHeader.Read(ms));
    }

    [Fact]
    public void A_wav_that_never_reaches_its_data_chunk_is_refused()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(uint.MaxValue); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(22050); w.Write(44100);
        w.Write((short)2); w.Write((short)16);
        ms.Position = 0;

        Assert.Null(WavHeader.Read(ms));
    }

    /// <summary>
    /// A pipe hands back what has arrived, not what was asked for. Treating a
    /// short read as the end of the stream would fail on perfectly good audio
    /// that simply had not finished being synthesised — which is every render
    /// this module does, since the point of streaming is to start before the end.
    /// </summary>
    [Fact]
    public void A_header_arriving_a_byte_at_a_time_still_reads()
    {
        var wav = WavHeader.Read(new DribblingStream(Wav().ToArray()));

        Assert.NotNull(wav);
        Assert.Equal(22050, wav.Value.SampleRate);
    }

    private sealed class DribblingStream(byte[] data) : Stream
    {
        private int _at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= data.Length) return 0;
            buffer[offset] = data[_at++];
            return 1;                                   // one byte, every time
        }
    }
}

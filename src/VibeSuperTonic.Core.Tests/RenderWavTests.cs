using System.Text;
using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The reply-reading half of <c>vst-ctl render</c>, which is the piece of the
/// Speech Dispatcher feature standing between a screen-reader user and silence.
///
/// <para><b>Every rule here fails silently in the field.</b> A truncated render
/// that exits 0 is a sentence nobody hears and nothing logs; a header written
/// from the wrong rate is a voice that sounds like a chipmunk and no error at
/// all. docs/SPEECHD-PLAN.md, trap 14: <em>never exit 0 having produced no
/// audio</em>. This file is that rule's evidence.</para>
///
/// <para>It lives in Core.Tests because it is logic and needs no model, network
/// or device — docs/TESTING-PLAN.md, "where a check belongs" — and because it
/// then runs on the Windows runner too, which is the only guard the Linux-only
/// half of the product has against a shared-code change.</para>
/// </summary>
public class RenderWavTests
{
    private const int Rate = 22050;

    // ------------------------------------------------------------- fixtures

    private static string Format(int rate = Rate, int channels = 1) =>
        Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(rate, channels, null, false) });

    private static string Chunk(short[] samples, int rate = Rate)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return Protocol.Encode(new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, Convert.ToBase64String(bytes), false),
        });
    }

    private static string Final(int rate = Rate) =>
        Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(rate, 1, null, true) });

    /// <summary>A render that produced nothing, which the daemon reports with rate 0.</summary>
    private static string NothingToSay() =>
        Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(0, 1, null, true) });

    private sealed record Run(int ExitCode, byte[] Output, string Error);

    private static Run Read(params string?[] lines)
    {
        int at = 0;
        var output = new MemoryStream();
        var error = new StringWriter();

        int code = RenderWav.Read(
            () => at < lines.Length ? lines[at++] : null,
            output, error);

        return new Run(code, output.ToArray(), error.ToString());
    }

    // ------------------------------------------------------- the happy path

    [Fact]
    public void A_complete_render_writes_a_header_then_the_samples()
    {
        var pcm = new short[] { 1, -1, 32767, -32768 };
        var run = Read(Format(), Chunk(pcm), Final());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Error);
        Assert.Equal(RenderWav.HeaderBytes + pcm.Length * 2, run.Output.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(run.Output, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(run.Output, 8, 4));

        // The samples arrive byte for byte: this is 16-bit little-endian mono in
        // and 16-bit little-endian mono out, and any transformation is a bug.
        Assert.Equal(
            new byte[] { 1, 0, 255, 255, 255, 127, 0, 128 },
            run.Output[RenderWav.HeaderBytes..]);
    }

    [Fact]
    public void Several_chunks_concatenate_under_one_header()
    {
        var run = Read(
            Format(),
            Chunk(new short[] { 1, 2 }),
            Chunk(new short[] { 3, 4 }),
            Chunk(new short[] { 5, 6 }),
            Final());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(RenderWav.HeaderBytes + 12, run.Output.Length);
    }

    /// <summary>
    /// A chunk that renders to digital silence is a legitimate thing — a pause,
    /// a run of punctuation — and it is audio. The distinction between "no
    /// samples" and "samples that are zero" is the one trap 14 turns on.
    /// </summary>
    [Fact]
    public void Silent_samples_are_audio()
    {
        var run = Read(Format(), Chunk(new short[8]), Final());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(RenderWav.HeaderBytes + 16, run.Output.Length);
        Assert.Equal("", run.Error);
    }

    // ------------------------------------------------------------ the header

    [Theory]
    [InlineData(16000, 1)]
    [InlineData(22050, 1)]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void The_header_describes_the_format_the_daemon_reported(int rate, int channels)
    {
        var output = new MemoryStream();
        RenderWav.WriteHeader(output, rate, channels);
        byte[] h = output.ToArray();

        Assert.Equal(RenderWav.HeaderBytes, h.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(h, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(h, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(h, 36, 4));

        Assert.Equal(16, BitConverter.ToInt32(h, 16));                  // fmt chunk size
        Assert.Equal(1, BitConverter.ToInt16(h, 20));                   // PCM
        Assert.Equal(channels, BitConverter.ToInt16(h, 22));
        Assert.Equal(rate, BitConverter.ToInt32(h, 24));
        Assert.Equal(rate * channels * 2, BitConverter.ToInt32(h, 28)); // byte rate
        Assert.Equal(channels * 2, BitConverter.ToInt16(h, 32));        // block align
        Assert.Equal(16, BitConverter.ToInt16(h, 34));                  // bits per sample
    }

    /// <summary>
    /// A pipe cannot be seeked, so the two size fields can never be filled in.
    /// 0xFFFFFFFF is what a streaming WAV has always looked like, and a reader
    /// that trusted a real length here would be reading a promise nobody kept.
    /// </summary>
    [Fact]
    public void Both_size_fields_say_unknown_because_this_is_a_stream()
    {
        var output = new MemoryStream();
        RenderWav.WriteHeader(output, Rate, 1);
        byte[] h = output.ToArray();

        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(h, 4));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(h, 40));
    }

    [Fact]
    public void The_header_takes_the_rate_from_the_reply_not_from_a_default()
    {
        var run = Read(Format(rate: 44100), Chunk(new short[] { 7 }, rate: 44100), Final(rate: 44100));

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(44100, BitConverter.ToInt32(run.Output, 24));
    }

    // -------------------------------------------------------- the refusals

    /// <summary>
    /// THE RULE THIS WHOLE FEATURE IS ABOUT. Half a sentence and exit 0 is
    /// indistinguishable, to someone navigating by ear, from a machine that has
    /// stopped responding.
    /// </summary>
    [Fact]
    public void A_stream_that_stops_mid_render_fails_even_though_audio_was_written()
    {
        var run = Read(Format(), Chunk(new short[] { 1, 2, 3, 4 }), null);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("mid-render", run.Error);
    }

    [Fact]
    public void A_stream_that_says_nothing_at_all_fails()
    {
        var run = Read();

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("mid-render", run.Error);
    }

    [Fact]
    public void A_final_reply_carrying_no_samples_at_all_fails()
    {
        var run = Read(Format(), Final());

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("produced no audio", run.Error);
    }

    [Fact]
    public void The_daemons_own_error_reaches_stderr_verbatim()
    {
        string refusal = Protocol.Encode(Response.Fail("busy (Speaking) — this daemon is speaking"));
        var run = Read(refusal);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("busy (Speaking)", run.Error);
        Assert.Empty(run.Output);
    }

    [Fact]
    public void A_refusal_arriving_mid_stream_still_fails_the_render()
    {
        var run = Read(
            Format(),
            Chunk(new short[] { 1, 2 }),
            Protocol.Encode(Response.Fail("the engine went away")));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the engine went away", run.Error);
    }

    [Fact]
    public void An_unparseable_line_fails_rather_than_being_skipped()
    {
        var run = Read(Format(), "{not json", Final());

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("unparseable", run.Error);
    }

    [Fact]
    public void An_ok_reply_with_no_audio_field_fails()
    {
        var run = Read(Format(), Protocol.Encode(Response.Success()));

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("no audio field", run.Error);
    }

    [Fact]
    public void Unreadable_base64_fails_rather_than_writing_noise()
    {
        string bad = Protocol.Encode(new Response
        {
            Ok = true,
            Audio = new AudioChunk(Rate, 1, "not base64 at all!!", false),
        });
        var run = Read(Format(), bad, Final());

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("unreadable audio", run.Error);
    }

    /// <summary>
    /// AudioChunk carries the rate on every chunk so that this is detectable at
    /// all. The header is already out and cannot be rewritten, so an engine that
    /// changed mid-stream would otherwise play back at the wrong speed with
    /// nothing reported. The daemon refuses the switch; this is the belt to that
    /// brace.
    /// </summary>
    [Fact]
    public void A_rate_that_changes_mid_stream_fails_rather_than_playing_a_chipmunk()
    {
        var run = Read(
            Format(rate: 22050),
            Chunk(new short[] { 1, 2 }, rate: 22050),
            Chunk(new short[] { 3, 4 }, rate: 44100),
            Final());

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("changed sample rate", run.Error);
    }

    // --------------------------------------------------- the quiet successes

    /// <summary>
    /// A screen reader sends whatever the focused widget held, and an empty
    /// label happens dozens of times a session. Zero bytes and exit 0 — not a
    /// WAV with no samples, which some players treat as a broken file.
    /// </summary>
    [Fact]
    public void An_empty_render_writes_nothing_and_succeeds()
    {
        var run = Read(NothingToSay());

        Assert.Equal(0, run.ExitCode);
        Assert.Empty(run.Output);
        Assert.Equal("", run.Error);
    }

    /// <summary>
    /// The stop. Under route B speechd terminates the module's child on STOP and
    /// Orca sends STOP on very nearly every keystroke, so if this were reported
    /// as a failure the user's log would fill up with the sound of the product
    /// working normally.
    /// </summary>
    [Fact]
    public void A_stop_mid_render_is_a_success_and_keeps_what_was_already_produced()
    {
        int at = 0;
        bool stopped = false;
        string[] lines = { Format(), Chunk(new short[] { 1, 2, 3, 4 }), Chunk(new short[] { 5, 6 }) };
        var output = new MemoryStream();
        var error = new StringWriter();

        int code = RenderWav.Read(
            () =>
            {
                if (at == 2) { stopped = true; return null; }   // the signal lands here
                return at < lines.Length ? lines[at++] : null;
            },
            output, error, () => stopped);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(RenderWav.HeaderBytes + 8, output.ToArray().Length);
    }

    /// <summary>
    /// A STOP MUST STOP CONSUMING, NOT DRAIN. When the signal lands, the daemon
    /// has usually already written several chunks into the socket buffer — they
    /// are sitting there readable. Reading them out and writing them on would go
    /// on producing audio for an utterance the user has cancelled, which at
    /// Orca's rate means the echo running behind the keystrokes by however much
    /// was in flight. The reply after the stop is left unread.
    /// </summary>
    [Fact]
    public void A_stop_leaves_what_is_already_queued_unread()
    {
        int at = 0;
        bool stopped = false;
        string[] lines =
        {
            Format(),
            Chunk(new short[] { 1, 2, 3, 4 }),
            Chunk(new short[] { 5, 6, 7, 8 }),      // already in the socket buffer
            Chunk(new short[] { 9, 10, 11, 12 }),   // and this one too
            Final(),
        };
        var output = new MemoryStream();
        var error = new StringWriter();

        int code = RenderWav.Read(
            () =>
            {
                string line = lines[at++];
                if (at == 2) stopped = true;        // the signal lands after the first chunk
                return line;
            },
            output, error, () => stopped);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(RenderWav.HeaderBytes + 8, output.ToArray().Length);
        Assert.Equal(2, at);                        // the queued chunks were never read
    }

    [Fact]
    public void A_stop_before_anything_arrives_is_still_a_success()
    {
        var output = new MemoryStream();
        var error = new StringWriter();

        int code = RenderWav.Read(() => null, output, error, () => true);

        Assert.Equal(0, code);
        Assert.Empty(output.ToArray());
        Assert.Equal("", error.ToString());
    }

    /// <summary>
    /// The reader went away — <c>$PLAY_COMMAND</c> was killed, or speechd closed
    /// the pipe. That is the ordinary end of most utterances a screen reader
    /// starts, and it is not this process's failure to report.
    /// </summary>
    [Fact]
    public void A_closed_destination_ends_the_render_without_an_error()
    {
        var run = ReadInto(new ClosedStream(), Format(), Chunk(new short[] { 1, 2 }), Final());

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Error);
    }

    private static Run ReadInto(Stream output, params string?[] lines)
    {
        int at = 0;
        var error = new StringWriter();
        int code = RenderWav.Read(() => at < lines.Length ? lines[at++] : null, output, error);
        return new Run(code, Array.Empty<byte>(), error.ToString());
    }

    /// <summary>A pipe whose reader has gone: every write is EPIPE.</summary>
    private sealed class ClosedStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("broken pipe");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("broken pipe");
    }
}

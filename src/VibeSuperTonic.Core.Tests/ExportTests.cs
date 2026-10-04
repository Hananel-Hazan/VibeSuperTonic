using VibeSuperTonic.Core.Export;
using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The Export feature's logic: format names, ffmpeg detection and arguments, and
/// above all <see cref="ExportRunner"/>'s promise that a destination is either a
/// complete file or untouched. The runner tests use a fake "ffmpeg" shell script
/// (Linux only; skipped elsewhere) so failure paths are exercised without
/// depending on what the build machine has installed.
/// </summary>
public sealed class ExportTests : IDisposable
{
    private const int Rate = 22050;
    private readonly string _dir = Directory.CreateTempSubdirectory("vst-export-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // ---------------------------------------------------------------- fixtures

    private static string Format() =>
        Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(Rate, 1, null, false) });

    private static string Chunk(int samples)
    {
        var bytes = new byte[samples * 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7);
        return Protocol.Encode(new Response
        {
            Ok = true,
            Audio = new AudioChunk(Rate, 1, Convert.ToBase64String(bytes), false),
        });
    }

    private static string Final() =>
        Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(Rate, 1, null, true) });

    private static Func<string?> Lines(params string[] lines)
    {
        var q = new Queue<string>(lines);
        return () => q.Count > 0 ? q.Dequeue() : null;
    }

    private static string[] GoodRender(int samples = 1000) => [Format(), Chunk(samples), Final()];

    private string Leftovers() => string.Join(",", Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order());

    private FfmpegTools Fake(string body)
    {
        string path = Path.Combine(_dir, "bin-fake-ffmpeg");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FfmpegTools(path, new HashSet<string> { "libmp3lame", "aac", "flac" });
    }

    // Fakes copy stdin to the last argument, which is where BuildArgs puts the output.
    private const string CopyToLastArg = "for a; do out=\"$a\"; done\ncat > \"$out\"";

    // ------------------------------------------------------------ pure helpers

    [Theory]
    [InlineData("a/b.WAV", ExportFormat.Wav)]
    [InlineData("x.mp3", ExportFormat.Mp3)]
    [InlineData("x.m4a", ExportFormat.Aac)]
    [InlineData("x.aac", ExportFormat.Aac)]
    [InlineData("x.flac", ExportFormat.Flac)]
    public void The_extension_names_the_format(string path, ExportFormat expected) =>
        Assert.Equal(expected, ExportFormats.FromPath(path));

    [Fact]
    public void An_unknown_extension_is_not_guessed_as_anything()
    {
        Assert.Null(ExportFormats.FromPath("x.ogg"));
        Assert.Null(ExportFormats.FromPath("noextension"));
        Assert.Null(ExportFormats.Parse("ogg"));
        Assert.Equal(ExportFormat.Aac, ExportFormats.Parse("M4A"));
    }

    [Fact]
    public void Every_format_has_an_extension_that_maps_back_to_itself()
    {
        foreach (var f in Enum.GetValues<ExportFormat>())
            Assert.Equal(f, ExportFormats.FromPath("file" + ExportFormats.Extension(f)));
    }

    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         ------
         V....D libx264              libx264 H.264
         A....D aac                  AAC (Advanced Audio Coding)
         A....D libmp3lame           libmp3lame MP3 (MPEG audio layer 3) (codec mp3)
         A..X.D vorbis               Vorbis
        """;

    [Fact]
    public void Encoder_parsing_reads_audio_encoders_and_not_the_legend_or_video()
    {
        var found = FfmpegTools.ParseEncoders(EncodersOutput);

        Assert.Equal(new[] { "aac", "libmp3lame", "vorbis" }, found.Order().ToArray());
    }

    [Fact]
    public void Mp3_prefers_lame_and_falls_back_to_shine_and_aac_needs_an_aac_encoder()
    {
        var shineOnly = new FfmpegTools("/x", new HashSet<string> { "libshine" });
        var lame = new FfmpegTools("/x", new HashSet<string> { "libshine", "libmp3lame" });

        Assert.Equal("libshine", shineOnly.EncoderFor(ExportFormat.Mp3));
        Assert.Equal("libmp3lame", lame.EncoderFor(ExportFormat.Mp3));
        Assert.Null(shineOnly.EncoderFor(ExportFormat.Aac));
        Assert.True(shineOnly.Supports(ExportFormat.Wav));
    }

    [Fact]
    public void Path_search_takes_the_first_hit_and_ignores_relative_entries()
    {
        var files = new HashSet<string> { "/usr/bin/ffmpeg", "./ffmpeg", "bin/ffmpeg" };

        Assert.Equal("/usr/bin/ffmpeg", FfmpegTools.Find(":.:bin:/opt/none:/usr/bin/", files.Contains));
        Assert.Null(FfmpegTools.Find(".:bin", files.Contains));
        Assert.Null(FfmpegTools.Find(null, files.Contains));
        Assert.Null(FfmpegTools.Find("/usr/bin", _ => false));
    }

    [Fact]
    public void Arguments_read_the_wav_from_stdin_name_the_container_and_end_with_the_path()
    {
        var mp3 = FfmpegTools.BuildArgs(ExportFormat.Mp3, "libmp3lame", "/tmp/o u t.part");
        var aac = FfmpegTools.BuildArgs(ExportFormat.Aac, "aac", "/tmp/o.part");

        Assert.Equal("/tmp/o u t.part", mp3[^1]);               // one argument, spaces and all
        Assert.Contains("pipe:0", mp3);
        Assert.Equal("libmp3lame", mp3[mp3.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal("mp3", mp3[mp3.ToList().IndexOf("-f", mp3.ToList().IndexOf("-b:a")) + 1]);
        Assert.Contains("ipod", aac);
        Assert.Contains("-y", mp3);
    }

    [Fact]
    public void Arguments_refuse_wav_and_relative_paths()
    {
        Assert.Throws<ArgumentException>(() => FfmpegTools.BuildArgs(ExportFormat.Wav, "x", "/a"));
        Assert.Throws<ArgumentException>(() => FfmpegTools.BuildArgs(ExportFormat.Mp3, "libmp3lame", "-rm-rf.mp3"));
    }

    [Fact]
    public void Availability_messages_say_what_to_do_and_differ_in_a_sandbox()
    {
        Assert.Null(FfmpegTools.Unavailable(ExportFormat.Wav, null, sandboxed: true));

        string plain = FfmpegTools.Unavailable(ExportFormat.Mp3, null, sandboxed: false)!;
        string boxed = FfmpegTools.Unavailable(ExportFormat.Mp3, null, sandboxed: true)!;
        Assert.Contains("install", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ffmpeg", plain);
        Assert.Contains("WAV", plain);
        Assert.Contains("sandbox", boxed);
        Assert.DoesNotContain("apt install", boxed);

        var noAac = new FfmpegTools("/usr/bin/ffmpeg", new HashSet<string> { "libmp3lame" });
        Assert.Contains("without a AAC encoder", FfmpegTools.Unavailable(ExportFormat.Aac, noAac, false)!);
        Assert.Null(FfmpegTools.Unavailable(ExportFormat.Mp3, noAac, false));
    }

    // ------------------------------------------------------------- RenderWav

    [Fact]
    public void Patching_a_finished_wav_replaces_both_unknown_sizes_with_the_real_ones()
    {
        var ms = new MemoryStream();
        RenderWav.WriteHeader(ms, Rate, 1);
        ms.Write(new byte[2000]);

        Assert.True(RenderWav.PatchSizes(ms));

        var b = ms.ToArray();
        Assert.Equal(2036u, BitConverter.ToUInt32(b, 4));
        Assert.Equal(2000u, BitConverter.ToUInt32(b, 40));
        Assert.False(RenderWav.PatchSizes(new MemoryStream(new byte[100])));     // not a WAV: untouched
    }

    [Fact]
    public void Progress_reports_the_running_sample_count()
    {
        var seen = new List<long>();
        RenderWav.Read(Lines(Format(), Chunk(100), Chunk(50), Final()), new MemoryStream(), TextWriter.Null,
                       progress: seen.Add);

        Assert.Equal(new long[] { 100, 150 }, seen);
    }

    // ----------------------------------------------------------- the WAV path

    [Fact]
    public void A_good_wav_export_lands_whole_with_real_sizes_and_no_temp_file()
    {
        string target = Path.Combine(_dir, "out.wav");

        var outcome = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Wav, target, null);

        Assert.Equal(ExportStatus.Done, outcome.Status);
        Assert.Equal("out.wav", Leftovers());
        var b = File.ReadAllBytes(target);
        Assert.Equal(44 + 2000, b.Length);
        Assert.Equal(2000u, BitConverter.ToUInt32(b, 40));
    }

    [Fact]
    public void A_render_cut_off_mid_stream_leaves_no_file_and_keeps_the_old_one()
    {
        string target = Path.Combine(_dir, "out.wav");
        File.WriteAllText(target, "precious");

        // The daemon "died" after one chunk: no Final reply, then end of stream.
        var outcome = ExportRunner.Run(Lines(Format(), Chunk(1000)), ExportFormat.Wav, target, null);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("mid-render", outcome.Message);
        Assert.Equal("out.wav", Leftovers());                          // no .part-* either
        Assert.Equal("precious", File.ReadAllText(target));
    }

    [Fact]
    public void A_daemon_error_leaves_nothing_and_reports_its_words()
    {
        string target = Path.Combine(_dir, "out.wav");
        string error = Protocol.Encode(Response.Fail("no models installed"));

        var outcome = ExportRunner.Run(Lines(Format(), error), ExportFormat.Wav, target, null);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("no models installed", outcome.Message);
        Assert.Equal("", Leftovers());
    }

    [Fact]
    public void Cancelling_mid_render_leaves_nothing_and_never_replaces_an_existing_file()
    {
        string target = Path.Combine(_dir, "out.wav");
        File.WriteAllText(target, "precious");
        bool cancel = false;
        int reads = 0;

        string? Read()
        {
            // Cancel after the first audio chunk has been written.
            return ++reads switch { 1 => Format(), 2 => Chunk(1000), 3 => ((cancel = true) ? Chunk(1000) : null), _ => Final() };
        }

        var outcome = ExportRunner.Run(Read, ExportFormat.Wav, target, null, () => cancel);

        Assert.Equal(ExportStatus.Cancelled, outcome.Status);
        Assert.Equal("out.wav", Leftovers());
        Assert.Equal("precious", File.ReadAllText(target));
    }

    [Fact]
    public void Empty_text_is_not_an_export()
    {
        string target = Path.Combine(_dir, "out.wav");
        var nothing = Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(0, 1, null, true) });

        var outcome = ExportRunner.Run(Lines(nothing), ExportFormat.Wav, target, null);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("nothing to say", outcome.Message);
        Assert.Equal("", Leftovers());
    }

    [Fact]
    public void A_missing_folder_is_refused_before_anything_is_read()
    {
        int reads = 0;
        var outcome = ExportRunner.Run(() => { reads++; return null; }, ExportFormat.Wav,
                                       Path.Combine(_dir, "nope", "out.wav"), null);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Mp3_without_ffmpeg_is_refused_before_anything_is_read_and_says_how_to_get_it()
    {
        int reads = 0;
        var outcome = ExportRunner.Run(() => { reads++; return null; }, ExportFormat.Mp3,
                                       Path.Combine(_dir, "out.mp3"), null);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal(0, reads);
        Assert.Contains("ffmpeg", outcome.Message);
        Assert.Equal("", Leftovers());
    }

    // ----------------------------------------------------------- the ffmpeg path

    [Fact]
    public void A_good_encode_pipes_the_wav_to_ffmpeg_and_renames_its_output_into_place()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");

        var outcome = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Mp3, target, Fake(CopyToLastArg));

        Assert.Equal(ExportStatus.Done, outcome.Status);
        Assert.Equal("bin-fake-ffmpeg,out.mp3", Leftovers());
        var b = File.ReadAllBytes(target);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));   // what ffmpeg was given
        Assert.Equal(44 + 2000, b.Length);
    }

    [Fact]
    public void An_ffmpeg_that_fails_leaves_no_file_even_though_every_byte_was_accepted()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");
        File.WriteAllText(target, "precious");

        // Reads all of stdin, writes something, then reports failure.
        var bad = Fake("cat > /dev/null\nfor a; do out=\"$a\"; done\necho partial > \"$out\"\necho 'Unknown encoder' >&2\nexit 3");

        var outcome = ExportRunner.Run(Lines(GoodRender()), ExportFormat.Mp3, target, bad);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("Unknown encoder", outcome.Message);
        Assert.Equal("bin-fake-ffmpeg,out.mp3", Leftovers());
        Assert.Equal("precious", File.ReadAllText(target));
    }

    [Fact]
    public void An_ffmpeg_that_dies_early_is_a_failure_not_a_truncated_success()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");

        // Exits at once without reading: the pipe breaks while the render is still writing.
        var dead = Fake("echo boom >&2\nexit 1");
        string[] lots = [Format(), .. Enumerable.Range(0, 40).Select(_ => Chunk(20000)), Final()];

        var outcome = ExportRunner.Run(Lines(lots), ExportFormat.Mp3, target, dead);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("bin-fake-ffmpeg", Leftovers());
    }

    [Fact]
    public void An_ffmpeg_that_stops_reading_but_exits_zero_with_a_partial_file_is_still_a_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");

        // Keeps 100 bytes, closes its input and reports success: the file exists,
        // is non-empty, and is a fraction of the audio. Only the broken pipe says so.
        var partial = Fake("for a; do out=\"$a\"; done\nhead -c 100 > \"$out\"\nexit 0");
        string[] lots = [Format(), .. Enumerable.Range(0, 40).Select(_ => Chunk(20000)), Final()];

        var outcome = ExportRunner.Run(Lines(lots), ExportFormat.Mp3, target, partial);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("bin-fake-ffmpeg", Leftovers());
    }

    [Fact]
    public void An_ffmpeg_that_succeeds_but_writes_nothing_is_a_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");

        var outcome = ExportRunner.Run(Lines(GoodRender()), ExportFormat.Mp3, target, Fake("cat > /dev/null"));

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("empty", outcome.Message);
        Assert.Equal("bin-fake-ffmpeg", Leftovers());
    }

    [Fact]
    public void Cancelling_during_an_encode_kills_ffmpeg_and_leaves_nothing()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");
        bool cancel = false;
        int reads = 0;
        string? Read() => ++reads switch { 1 => Format(), 2 => Chunk(500), 3 => ((cancel = true) ? Chunk(500) : null), _ => Final() };

        // Would run for a minute if it were not killed.
        var slow = Fake("for a; do out=\"$a\"; done\ncat > \"$out\" &\nsleep 60");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = ExportRunner.Run(Read, ExportFormat.Mp3, target, slow, () => cancel);

        Assert.Equal(ExportStatus.Cancelled, outcome.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "cancel waited for ffmpeg");
        Assert.Equal("bin-fake-ffmpeg", Leftovers());
    }

    [Fact]
    public void The_real_ffmpeg_if_there_is_one_makes_a_playable_mp3_aac_and_flac()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = FfmpegDetector.Detect();
        if (real is null) return;                                  // nothing to check on this machine

        foreach (var format in new[] { ExportFormat.Mp3, ExportFormat.Aac, ExportFormat.Flac })
        {
            if (!real.Supports(format)) continue;
            string target = Path.Combine(_dir, "real" + ExportFormats.Extension(format));

            var outcome = ExportRunner.Run(Lines(Format(), Chunk(Rate), Chunk(Rate), Final()), format, target, real);

            Assert.True(outcome.Status == ExportStatus.Done, $"{format}: {outcome.Message}");
            Assert.True(new FileInfo(target).Length > 100);
        }
    }
}

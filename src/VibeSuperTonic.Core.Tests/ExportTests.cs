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

    // The fallback's private staging folder, outside _dir so Leftovers() sees only the destination's.
    private readonly string _private = Directory.CreateTempSubdirectory("vst-export-private-").FullName;

    public void Dispose()
    {
        foreach (string d in new[] { _dir, _private })
        {
            try
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(d, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A folder that refuses every file but the destination, as the document portal may.</summary>
    private ExportStaging Refusing(Func<Stream, Stream>? wrap = null) => new()
    {
        PrivateDir = _private,
        CreateBeside = _ => false,
        WrapDestination = wrap,
    };

    private string PrivateLeftovers() => string.Join(",", Directory.GetFileSystemEntries(_private).Select(Path.GetFileName).Order());

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
        const string Bundled = "/snap/vibesupertonic/7/ffmpeg/ffmpeg";
        Assert.Null(FfmpegTools.Unavailable(ExportFormat.Wav, null, Bundled));

        string plain = FfmpegTools.Unavailable(ExportFormat.Mp3, null, bundled: null)!;
        string boxed = FfmpegTools.Unavailable(ExportFormat.Mp3, null, Bundled)!;
        Assert.Contains("install", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ffmpeg", plain);
        Assert.Contains("WAV", plain);
        Assert.Contains("sandbox", boxed);
        Assert.Contains(Bundled, boxed);                 // says WHICH file is missing
        Assert.Contains("Reinstalling", boxed);
        Assert.DoesNotContain("apt install", boxed);

        // The bundled one present and complete: no message at all, sandbox or not.
        var full = new FfmpegTools(Bundled, new HashSet<string> { "libmp3lame", "aac", "flac" });
        foreach (var f in Enum.GetValues<ExportFormat>())
            Assert.Null(FfmpegTools.Unavailable(f, full, Bundled));

        var noAac = new FfmpegTools("/usr/bin/ffmpeg", new HashSet<string> { "libmp3lame" });
        Assert.Contains("without a AAC encoder", FfmpegTools.Unavailable(ExportFormat.Aac, noAac, null)!);
        Assert.Null(FfmpegTools.Unavailable(ExportFormat.Mp3, noAac, null));
    }

    // ------------------------------------------------- which ffmpeg is used

    [Fact]
    public void The_bundled_ffmpeg_is_under_SNAP_or_the_flatpak_tree_and_nowhere_else()
    {
        Func<string, string?> env = n => n == "SNAP" ? "/snap/vibesupertonic/12/" : null;

        Assert.Equal("/snap/vibesupertonic/12/ffmpeg/ffmpeg", FfmpegDetector.BundledPath(env, inSnap: true, inFlatpak: false));
        Assert.Equal("/app/lib/vibesupertonic/ffmpeg/ffmpeg", FfmpegDetector.BundledPath(env, inSnap: false, inFlatpak: true));
        // Outside a sandbox (and on a Flatpak's host side, which passes inFlatpak:
        // false) there is none: the tarball ships no ffmpeg, and the host's is on PATH.
        Assert.Null(FfmpegDetector.BundledPath(env, inSnap: false, inFlatpak: false));
        // A relative or empty $SNAP names nothing.
        Assert.Null(FfmpegDetector.BundledPath(n => n == "SNAP" ? "snap/x" : null, inSnap: true, inFlatpak: false));
        Assert.Null(FfmpegDetector.BundledPath(_ => null, inSnap: true, inFlatpak: false));
    }

    [Fact]
    public void Search_order_is_VST_FFMPEG_then_the_bundled_one_then_PATH()
    {
        const string Override = "/home/u/my-ffmpeg";
        const string Bundled = "/snap/vibesupertonic/12/ffmpeg/ffmpeg";
        const string OnPath = "/usr/bin/ffmpeg";
        var all = new HashSet<string> { Override, Bundled, OnPath };
        string? Env(string name, bool withOverride) => name switch
        {
            "VST_FFMPEG" => withOverride ? Override : null,
            "PATH" => "/usr/local/bin:/usr/bin",
            _ => null,
        };

        // The override wins over everything, when it names a file.
        Assert.Equal(Override, FfmpegDetector.Locate(n => Env(n, true), all.Contains, Bundled));
        // Without it, the package's own beats whatever is on PATH.
        Assert.Equal(Bundled, FfmpegDetector.Locate(n => Env(n, false), all.Contains, Bundled));
        // An override naming nothing falls through to the bundled one, not to PATH.
        Assert.Equal(Bundled, FfmpegDetector.Locate(n => Env(n, true), new HashSet<string> { Bundled, OnPath }.Contains, Bundled));
        // A bundled path whose file is missing falls through to PATH.
        Assert.Equal(OnPath, FfmpegDetector.Locate(n => Env(n, false), new HashSet<string> { OnPath }.Contains, Bundled));
        // Outside a sandbox: PATH, as before.
        Assert.Equal(OnPath, FfmpegDetector.Locate(n => Env(n, false), all.Contains, bundled: null));
        Assert.Null(FfmpegDetector.Locate(n => Env(n, false), _ => false, Bundled));
    }

    [Fact]
    public void Bundled_ffmpeg_check_uses_the_exact_export_arguments()
    {
        // build/check-ffmpeg-bundle.sh encodes with a copy of BuildArgs's list, so
        // a pack-time check proves the minimal ffmpeg can run what export runs. If
        // BuildArgs gains an option (a filter, a muxer flag) that copy must gain
        // it too, and build-ffmpeg.sh may need the component: otherwise MP3 export
        // breaks in the snap and the Flatpak alone, with every other test green.
        string script = File.ReadAllText(RepoFile("build/check-ffmpeg-bundle.sh"));

        foreach (var (format, encoder, name) in new[]
                 {
                     (ExportFormat.Mp3, "libmp3lame", "mp3"),
                     (ExportFormat.Aac, "aac", "aac"),
                     (ExportFormat.Flac, "flac", "flac"),
                 })
        {
            var args = FfmpegTools.BuildArgs(format, encoder, "/out");
            Assert.Equal("/out", args[^1]);
            string expected = $"args_{name}=\"{string.Join(' ', args.Take(args.Count - 1))}\"";
            Assert.True(script.Contains(expected, StringComparison.Ordinal),
                $"check-ffmpeg-bundle.sh does not encode {format} with BuildArgs's arguments; expected the line\n{expected}");
        }
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relative} not found above {AppContext.BaseDirectory}");
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

    // ------------------------------------- a folder that refuses a temp file

    [Fact]
    public void A_folder_that_refuses_a_temp_file_still_gets_the_whole_wav_staged_privately()
    {
        string target = Path.Combine(_dir, "out.wav");
        File.WriteAllText(target, "old");

        var outcome = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Wav, target, null, staging: Refusing());

        Assert.True(outcome.Status == ExportStatus.Done, outcome.Message);
        Assert.Equal("out.wav", Leftovers());                       // nothing was created beside it
        Assert.Equal("", PrivateLeftovers());                       // and the staging copy is gone
        var b = File.ReadAllBytes(target);
        Assert.Equal(44 + 2000, b.Length);
        Assert.Equal(2000u, BitConverter.ToUInt32(b, 40));          // real sizes, as on the rename path
    }

    [Fact]
    public void A_folder_that_refuses_a_temp_file_gets_ffmpegs_output_and_ffmpeg_writes_privately()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Path.Combine(_dir, "out.mp3");
        string seen = Path.Combine(_private, "..", Path.GetFileName(_private) + "-seen");
        // The fake records where it was told to write, then writes there.
        var fake = Fake($"for a; do out=\"$a\"; done\necho \"$out\" > '{seen}'\ncat > \"$out\"");

        var outcome = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Mp3, target, fake, staging: Refusing());

        Assert.True(outcome.Status == ExportStatus.Done, outcome.Message);
        Assert.StartsWith(_private + "/.part-", File.ReadAllText(seen).Trim());
        File.Delete(seen);
        Assert.Equal("bin-fake-ffmpeg,out.mp3", Leftovers());
        Assert.Equal("", PrivateLeftovers());
        Assert.Equal(44 + 2000, new FileInfo(target).Length);
    }

    [Fact]
    public void In_a_refusing_folder_a_failed_render_or_a_cancel_never_touches_the_destination()
    {
        string target = Path.Combine(_dir, "out.wav");
        File.WriteAllText(target, "precious");

        var cut = ExportRunner.Run(Lines(Format(), Chunk(1000)), ExportFormat.Wav, target, null, staging: Refusing());
        Assert.Equal(ExportStatus.Failed, cut.Status);

        bool cancel = false;
        int reads = 0;
        string? Read() => ++reads switch { 1 => Format(), 2 => Chunk(1000), 3 => ((cancel = true) ? Chunk(1000) : null), _ => Final() };
        var cancelled = ExportRunner.Run(Read, ExportFormat.Wav, target, null, () => cancel, staging: Refusing());
        Assert.Equal(ExportStatus.Cancelled, cancelled.Status);

        Assert.Equal("out.wav", Leftovers());
        Assert.Equal("precious", File.ReadAllText(target));
        Assert.Equal("", PrivateLeftovers());

        // And a destination that did not exist is not created by a failure.
        string fresh = Path.Combine(_dir, "new.wav");
        ExportRunner.Run(Lines(Format(), Chunk(1000)), ExportFormat.Wav, fresh, null, staging: Refusing());
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public void A_copy_that_fails_midway_removes_a_destination_it_created_and_says_so_of_one_it_replaced()
    {
        // A disk that fills after 100 bytes of the copy.
        ExportStaging FullDisk() => Refusing(s => new FailAfter(s, 100));

        string fresh = Path.Combine(_dir, "new.wav");
        var created = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Wav, fresh, null, staging: FullDisk());
        Assert.Equal(ExportStatus.Failed, created.Status);
        Assert.Contains("No space left", created.Message);
        Assert.False(File.Exists(fresh), "a half-copied file this run created was left behind");
        Assert.Equal("", PrivateLeftovers());

        string old = Path.Combine(_dir, "old.wav");
        File.WriteAllText(old, "precious");
        var replaced = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Wav, old, null, staging: FullDisk());
        Assert.Equal(ExportStatus.Failed, replaced.Status);
        Assert.Contains("may now be incomplete", replaced.Message);
        Assert.True(File.Exists(old), "a file the user already had was deleted");
        Assert.Equal("", PrivateLeftovers());
    }

    [Fact]
    public void A_private_folder_that_cannot_be_made_is_a_failure_that_names_both_places()
    {
        string notADir = Path.Combine(_private, "file");
        File.WriteAllText(notADir, "");
        var staging = new ExportStaging { PrivateDir = Path.Combine(notADir, "sub"), CreateBeside = _ => false };
        int reads = 0;

        var outcome = ExportRunner.Run(() => { reads++; return null; }, ExportFormat.Wav, Path.Combine(_dir, "o.wav"), null, staging: staging);

        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("nor in", outcome.Message);
        Assert.Equal(0, reads);                                     // refused before the render was read
        Assert.Equal("", Leftovers());
    }

    [Fact]
    public void A_really_unwritable_folder_with_a_writable_file_in_it_is_exported_into()
    {
        // The real thing, without the seam: a folder where only the chosen file
        // may be written, which is the document portal's shape. Root ignores the
        // folder's mode, so this can only be observed as an ordinary user (CI).
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
        string box = Path.Combine(_dir, "box");
        Directory.CreateDirectory(box);
        string target = Path.Combine(box, "out.wav");
        File.WriteAllText(target, "old");
        File.SetUnixFileMode(box, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var outcome = ExportRunner.Run(Lines(GoodRender(1000)), ExportFormat.Wav, target, null,
                                           staging: new ExportStaging { PrivateDir = _private });

            Assert.True(outcome.Status == ExportStatus.Done, outcome.Message);
            Assert.Equal(new[] { "out.wav" }, Directory.GetFileSystemEntries(box).Select(Path.GetFileName).ToArray());
            Assert.Equal(44 + 2000, new FileInfo(target).Length);
            Assert.Equal("", PrivateLeftovers());
        }
        finally
        {
            File.SetUnixFileMode(box, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("/run/user/1000/doc/a1b2c3/speech", true)]
    [InlineData("/run/user/0/doc/x/y.mp3", true)]
    [InlineData("/run/user/1000/speech.mp3", false)]
    [InlineData("/run/user/abc/doc/x/y", false)]
    [InlineData("/run/user//doc/x/y", false)]
    [InlineData("/home/u/run/user/1000/doc/x", false)]
    public void Document_portal_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, ExportRunner.InDocumentPortal(path));

    /// <summary>Accepts <c>limit</c> bytes, then fails as a full disk does.</summary>
    private sealed class FailAfter(Stream inner, int limit) : Stream
    {
        private int _written;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int take = Math.Min(buffer.Length, limit - _written);
            if (take > 0) { inner.Write(buffer[..take]); _written += take; }
            if (take < buffer.Length) throw new IOException("No space left on device");
        }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

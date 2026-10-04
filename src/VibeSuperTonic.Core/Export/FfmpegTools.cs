namespace VibeSuperTonic.Core.Export;

/// <summary>
/// The system's ffmpeg, as far as an export is concerned: where it is, which
/// encoders it has, and the arguments that turn a WAV on stdin into a file.
/// Pure and testable; the one process it describes is started by
/// <see cref="ExportRunner"/>.
/// </summary>
public sealed record FfmpegTools(string Path, IReadOnlySet<string> Encoders)
{
    /// <summary>
    /// Search a PATH-style string for an executable named <paramref name="name"/>.
    /// <paramref name="isFile"/> is injected so the search is testable without a
    /// filesystem. Relative and empty PATH entries are skipped: "the current
    /// directory" is how a program in a downloads folder gets run by accident.
    /// </summary>
    public static string? Find(string? pathVariable, Func<string, bool> isFile, string name = "ffmpeg")
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (string dir in pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!dir.StartsWith('/')) continue;
            string candidate = dir.TrimEnd('/') + "/" + name;
            if (isFile(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Audio encoder names out of <c>ffmpeg -encoders</c>. Lines look like
    /// <c> A....D libmp3lame           libmp3lame MP3 ...</c>: one space, six flag
    /// characters, a space, the name. The first flag is the type (<c>A</c> for
    /// audio); the legend above the list (<c> A..... = Audio</c>) has an
    /// <c>=</c> where the name would be and must not be read as an encoder.
    /// </summary>
    public static IReadOnlySet<string> ParseEncoders(string output)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 9 || line[0] != ' ' || line[7] != ' ' || line[1] != 'A') continue;
            if (!line.Substring(1, 6).All(c => char.IsLetter(c) || c == '.')) continue;

            string rest = line[8..].TrimStart();
            int end = rest.IndexOfAny([' ', '\t']);
            string name = end < 0 ? rest : rest[..end];
            if (name.Length > 0 && name != "=") found.Add(name);
        }

        return found;
    }

    /// <summary>The first preferred encoder this ffmpeg has, or null.</summary>
    public string? EncoderFor(ExportFormat format) =>
        ExportFormats.EncoderPreference(format).FirstOrDefault(Encoders.Contains);

    public bool Supports(ExportFormat format) =>
        format == ExportFormat.Wav || EncoderFor(format) is not null;

    /// <summary>
    /// The argument list (not a command line: the process is started with
    /// ArgumentList, so a path with spaces cannot be split, and the path must be
    /// absolute so a leading dash cannot become an option). Input is the WAV the
    /// daemon's render produced, on stdin; the container is named with
    /// <c>-f</c> because the output is a temp file whose extension says nothing.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(ExportFormat format, string encoder, string outputPath)
    {
        if (format == ExportFormat.Wav)
            throw new ArgumentException("WAV is written by this process, not ffmpeg", nameof(format));
        if (!System.IO.Path.IsPathRooted(outputPath))
            throw new ArgumentException("the output path must be absolute", nameof(outputPath));

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-y",
            "-f", "wav", "-i", "pipe:0",
            "-vn", "-map_metadata", "-1",
            "-c:a", encoder,
        };

        switch (format)
        {
            case ExportFormat.Mp3:
                args.AddRange(["-b:a", "128k", "-f", "mp3"]);
                break;
            case ExportFormat.Aac:
                // ipod is ffmpeg's .m4a muxer: audio-only MP4 with the right brand.
                args.AddRange(["-b:a", "128k", "-movflags", "+faststart", "-f", "ipod"]);
                break;
            case ExportFormat.Flac:
                args.AddRange(["-f", "flac"]);
                break;
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// The sentence the Export tab shows for a format, or null when it is
    /// available. <paramref name="sandboxed"/> changes the advice, not the
    /// verdict: a snap or Flatpak is simply not shown the host's ffmpeg.
    /// </summary>
    public static string? Unavailable(ExportFormat format, FfmpegTools? tools, bool sandboxed)
    {
        if (format == ExportFormat.Wav) return null;
        string name = ExportFormats.ShortName(format);

        if (tools is null)
        {
            return sandboxed
                ? $"{name} export needs ffmpeg, and this sandboxed install (snap or Flatpak) cannot see the one on your system. "
                  + "Export WAV here, or use the tarball or AppImage build for MP3, AAC and FLAC."
                : $"{name} export needs ffmpeg, which is not installed. Install it (for example `sudo apt install ffmpeg`, "
                  + "`sudo dnf install ffmpeg` or `sudo pacman -S ffmpeg`) and reopen this tab. WAV works without it.";
        }

        if (!tools.Supports(format))
        {
            return $"The ffmpeg at {tools.Path} was built without a {name} encoder. "
                 + "Install a full ffmpeg build, or export WAV.";
        }

        return null;
    }
}

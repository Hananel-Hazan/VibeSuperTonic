namespace VibeSuperTonic.Core.Export;

/// <summary>What an export can produce. Wav needs nothing; the rest need ffmpeg.</summary>
public enum ExportFormat
{
    Wav,
    Mp3,
    Aac,
    Flac,
}

/// <summary>
/// Names, extensions and encoder preferences for each <see cref="ExportFormat"/>.
///
/// <para><b>Why the encoders are an external program.</b> .NET has no MP3 or AAC
/// encoder and Linux has no Media Foundation (which is what the Windows build
/// uses). Bundling one would put a patent-encumbered native library in an
/// archive with a 75 MiB budget; the system's ffmpeg is already there on most
/// desktops, is kept patched by the distro, and is detected at runtime. WAV is
/// written by this process and never needs it. The snap and the Flatpak, which
/// cannot see the host's, carry a minimal LGPL one of their own; see
/// <see cref="FfmpegDetector"/>.</para>
/// </summary>
public static class ExportFormats
{
    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Wav => ".wav",
        ExportFormat.Mp3 => ".mp3",
        ExportFormat.Aac => ".m4a",
        ExportFormat.Flac => ".flac",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string DisplayName(ExportFormat format) => format switch
    {
        ExportFormat.Wav => "WAV (lossless, no extra software)",
        ExportFormat.Mp3 => "MP3",
        ExportFormat.Aac => "AAC (.m4a)",
        ExportFormat.Flac => "FLAC (lossless)",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The short name used in messages.</summary>
    public static string ShortName(ExportFormat format) => format switch
    {
        ExportFormat.Wav => "WAV",
        ExportFormat.Mp3 => "MP3",
        ExportFormat.Aac => "AAC",
        ExportFormat.Flac => "FLAC",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Bitrates (kbit/s) offered for MP3 and AAC. WAV and FLAC have none.</summary>
    public static readonly IReadOnlyList<int> Bitrates = [64, 96, 128, 160, 192, 256];

    public const int DefaultBitrateKbps = 128;

    public static bool HasBitrate(ExportFormat format) => format is ExportFormat.Mp3 or ExportFormat.Aac;

    public static bool NeedsFfmpeg(ExportFormat format) => format != ExportFormat.Wav;

    /// <summary>
    /// A file name -> a format, by extension. Null when the extension says
    /// nothing we make, so the caller can say so instead of writing WAV bytes
    /// into a file called <c>.ogg</c>.
    /// </summary>
    public static ExportFormat? FromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => ExportFormat.Wav,
            ".mp3" => ExportFormat.Mp3,
            // Not ".aac": that name means a raw ADTS stream, while the Aac format is
            // an MP4 container (ffmpeg's ipod muxer; the minimal bundled ffmpeg has
            // no adts muxer), so it would be an .m4a under the wrong name.
            ".m4a" or ".mp4" => ExportFormat.Aac,
            ".flac" => ExportFormat.Flac,
            _ => null,
        };

    /// <summary>Parse a <c>--format</c> value: wav, mp3, aac, m4a, flac. Case-insensitive.</summary>
    public static ExportFormat? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "wav" => ExportFormat.Wav,
            "mp3" => ExportFormat.Mp3,
            "aac" or "m4a" => ExportFormat.Aac,
            "flac" => ExportFormat.Flac,
            _ => null,
        };

    /// <summary>
    /// Encoders worth using, best first. The native <c>aac</c> encoder is in every
    /// ffmpeg build; <c>libmp3lame</c> is in nearly all, <c>libshine</c> is the
    /// fallback some minimal builds carry instead.
    /// </summary>
    public static IReadOnlyList<string> EncoderPreference(ExportFormat format) => format switch
    {
        ExportFormat.Wav => [],
        ExportFormat.Mp3 => ["libmp3lame", "libshine"],
        ExportFormat.Aac => ["aac", "libfdk_aac"],
        ExportFormat.Flac => ["flac"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

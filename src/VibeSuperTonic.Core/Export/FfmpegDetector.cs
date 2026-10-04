using System.Diagnostics;

namespace VibeSuperTonic.Core.Export;

/// <summary>
/// Finds the system's ffmpeg and asks what it can encode. The only impure part of
/// detection; the parsing is <see cref="FfmpegTools.ParseEncoders"/>.
/// </summary>
public static class FfmpegDetector
{
    /// <summary>
    /// Null when there is no ffmpeg on PATH or it does not answer. <c>VST_FFMPEG</c>
    /// names one explicitly (the tests use it; so can a user with a private build).
    /// Reads the whole of ffmpeg's output rather than stopping at a match: an
    /// early-exiting reader closes the pipe under a producer that is still writing.
    /// </summary>
    public static FfmpegTools? Detect()
    {
        string? path = Environment.GetEnvironmentVariable("VST_FFMPEG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = FfmpegTools.Find(Environment.GetEnvironmentVariable("PATH"), File.Exists);
        if (path is null) return null;

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            foreach (string a in (string[])["-hide_banner", "-encoders"]) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return null;

            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            stderr.Wait();

            return process.ExitCode == 0 ? new FfmpegTools(path, FfmpegTools.ParseEncoders(stdout)) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this process runs inside a snap or a Flatpak, which is what decides
    /// the wording when ffmpeg is absent: those sandboxes cannot see the host's.
    /// </summary>
    public static bool IsSandboxed() =>
        Ipc.SnapPeer.Current is not null || Ipc.FlatpakPeer.Current is { Inside: true };
}

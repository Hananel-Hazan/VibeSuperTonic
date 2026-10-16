using System.Diagnostics;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Core.Export;

/// <summary>
/// Finds an ffmpeg and asks what it can encode. The only impure part of
/// detection; the parsing is <see cref="FfmpegTools.ParseEncoders"/> and the
/// search order is <see cref="Locate"/>.
///
/// <para><b>The snap and the Flatpak carry their own</b> (build/build-ffmpeg.sh:
/// LGPL, LAME linked in, nothing but what an export uses), because a sandbox
/// cannot see the host's. It lives at <see cref="BundledSubpath"/> under the
/// package's tree: <c>$SNAP/ffmpeg/ffmpeg</c>, and
/// <c>/app/lib/vibesupertonic/ffmpeg/ffmpeg</c> in the Flatpak. The tarball and
/// the AppImage carry none and use the system's, as before.</para>
/// </summary>
public static class FfmpegDetector
{
    /// <summary>Where a store package puts its ffmpeg, relative to its tree. The
    /// snap's part and both Flatpak manifests install it here.</summary>
    public const string BundledSubpath = "ffmpeg/ffmpeg";

    /// <summary>
    /// The ffmpeg this package carries, or null outside a sandbox. Inside a snap
    /// it is under <c>$SNAP</c>; inside a Flatpak, under the tree's fixed
    /// <see cref="FlatpakPeer.SandboxDir"/>. A host-side process of a Flatpak
    /// install (the hotkey client run from the deployment) gets null: that binary
    /// is built against the Flatpak runtime, and the host has its own PATH.
    /// </summary>
    public static string? BundledPath(Func<string, string?> env, bool inSnap, bool inFlatpak)
    {
        if (inSnap)
        {
            string? root = env("SNAP");
            if (!string.IsNullOrWhiteSpace(root) && root.StartsWith('/'))
                return root.TrimEnd('/') + "/" + BundledSubpath;
        }
        if (inFlatpak) return FlatpakPeer.SandboxDir + "/" + BundledSubpath;
        return null;
    }

    /// <summary>This process's <see cref="BundledPath(Func{string, string?}, bool, bool)"/>.</summary>
    public static string? BundledPath() =>
        BundledPath(Environment.GetEnvironmentVariable,
                    SnapPeer.Current is not null,
                    FlatpakPeer.Current is { Inside: true });

    /// <summary>
    /// The search order, pure: <c>VST_FFMPEG</c> when it names a file (the
    /// tests use it; so can a user with a private build), then the package's own
    /// <paramref name="bundled"/> one, then <c>PATH</c>. Bundled before PATH so a
    /// stray ffmpeg on a sandbox's PATH cannot displace the one that was checked
    /// at pack time.
    /// </summary>
    public static string? Locate(Func<string, string?> env, Func<string, bool> isFile, string? bundled)
    {
        string? chosen = env("VST_FFMPEG");
        if (!string.IsNullOrEmpty(chosen) && isFile(chosen)) return chosen;
        if (!string.IsNullOrEmpty(bundled) && isFile(bundled)) return bundled;
        return FfmpegTools.Find(env("PATH"), isFile);
    }

    /// <summary>
    /// Null when no ffmpeg is found or the one found does not answer.
    /// Reads the whole of ffmpeg's output rather than stopping at a match: an
    /// early-exiting reader closes the pipe under a producer that is still writing.
    /// </summary>
    public static FfmpegTools? Detect()
    {
        string? path = Locate(Environment.GetEnvironmentVariable, File.Exists, BundledPath());
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
    /// Whether this process runs inside a snap or a Flatpak, where the host's
    /// ffmpeg is invisible and only the package's own can be used.
    /// </summary>
    public static bool IsSandboxed() =>
        SnapPeer.Current is not null || FlatpakPeer.Current is { Inside: true };
}

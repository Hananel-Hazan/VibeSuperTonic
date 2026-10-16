using System.Diagnostics;
using System.Reflection;

namespace VibeSuperTonic.Launcher;

/// <summary>
/// What this install is, component by component — the shared reader behind the
/// About tab and <c>--version</c>.
///
/// <para><b>Why more than one version number.</b> A release is four binaries that
/// ship together and are extracted separately: the Control Panel is a
/// self-contained single file at the root, the engine is a framework-dependent
/// pair under <c>engine\</c>, and the render helper is a third under
/// <c>render\</c>. Extracting a new ZIP over an old folder replaces all of them;
/// extracting it beside one, or copying just the exe, replaces some. The result
/// is a Control Panel reporting the new version while SAPI keeps loading last
/// month's engine — and the symptom is a bug that was fixed weeks ago still
/// reproducing.</para>
///
/// <para>Lifted out of <c>AboutTab</c> when <c>--version</c> needed the same
/// answer. Two readers of a version number that disagreed about how to read it
/// would be a smaller version of the defect this class exists to detect.</para>
/// </summary>
internal static class VersionInfo
{
    /// <summary>
    /// This assembly's version, as the release calls it.
    ///
    /// <para>Informational version first because that is the one that carries
    /// <c>&lt;VstVersion&gt;</c> verbatim — "0.2.8" — where
    /// <see cref="AssemblyName.Version"/> pads it to a four-part 0.2.8.0 that
    /// matches no ZIP filename anyone is holding. The SDK appends
    /// "+&lt;commit sha&gt;" to it, which is trimmed: it is provenance for a
    /// build log, not an answer to "which release is this".</para>
    /// </summary>
    public static string Own()
    {
        var informational = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return Trim(informational);

        return typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "(unknown)";
    }

    /// <summary>
    /// The shipped components and where they live, relative to the layout
    /// <c>build/pack-zip.ps1</c> composes.
    ///
    /// <para>A dev run from <c>bin\</c> finds none of them and says so, which is
    /// correct: those components genuinely are not beside that exe.</para>
    /// </summary>
    public static (string Label, string Path)[] Components(string baseDir) => new[]
    {
        ("Engine (x64)",  System.IO.Path.Combine(baseDir, "engine", "x64", "VibeSuperTonic.Engine.dll")),
        ("Engine (x86)",  System.IO.Path.Combine(baseDir, "engine", "x86", "VibeSuperTonic.Engine.dll")),
        ("Render helper", System.IO.Path.Combine(baseDir, "render", "VibeSuperTonic.RenderHost.exe")),
    };

    /// <summary>
    /// A shipped binary's version, or null when it is not there.
    ///
    /// <para><c>ProductVersion</c> rather than <c>FileVersion</c>, so this
    /// compares like with like against <see cref="Own"/> — the same
    /// informational string, with the same commit suffix to trim.</para>
    /// </summary>
    public static string? OfFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string? version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : Trim(version);
        }
        catch
        {
            // A file we cannot stat is reported as absent rather than as a
            // mismatch. Claiming a version disagreement on the strength of an
            // access error would send the user to re-extract a folder that is fine.
            return null;
        }
    }

    /// <summary>
    /// The ONNX Runtime shipped beside the engine, with the bitness it was read
    /// from — the pin that <see cref="Core.Synthesis.BenchmarkProfile"/> numbers
    /// are only comparable across.
    /// </summary>
    public static string OnnxRuntime(string baseDir)
    {
        try
        {
            foreach (var arch in new[] { "x64", "x86" })
            {
                string ort = Path.Combine(baseDir, "engine", arch, "onnxruntime.dll");
                if (File.Exists(ort))
                    return $"{FileVersionInfo.GetVersionInfo(ort).FileVersion} ({arch})";
            }
        }
        catch { }
        return "(not found)";
    }

    private static string Trim(string version)
    {
        int plus = version.IndexOf('+');
        return (plus >= 0 ? version[..plus] : version).Trim();
    }
}

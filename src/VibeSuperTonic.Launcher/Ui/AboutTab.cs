using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

/// <summary>
/// What this install is, component by component.
///
/// <para><b>Why more than one version number.</b> A release is four binaries that
/// ship together and are extracted separately: the Control Panel is a
/// self-contained single file at the root, the engine is a framework-dependent
/// pair under <c>engine\</c>, and the render helper is a third under
/// <c>render\</c>. Extracting a new ZIP over an old folder replaces all of them;
/// extracting it beside one, or copying just the exe, replaces some. The result
/// is a Control Panel reporting the new version while SAPI keeps loading last
/// month's engine — and the symptom is a bug that was fixed weeks ago still
/// reproducing. Reading the versions back off the files on disk is the only way
/// this window can tell, so it reads them and says when they disagree.</para>
///
/// <para>All of them come from <c>&lt;VstVersion&gt;</c> in
/// <c>Directory.Build.props</c>, so agreeing is the normal case and a mismatch
/// is always worth acting on. Before 2026-08-19 the Windows projects stamped no
/// version at all and this tab reported the .NET default, <c>1.0.0.0</c>, for
/// every release up to and including 0.2.8.</para>
/// </summary>
internal sealed class AboutTab : UserControl
{
    public AboutTab()
    {
        string baseDir = Registration.DefaultBaseDir;
        string appVersion = OwnVersion();

        // Relative to the shipped layout that build/pack-zip.ps1 composes. A dev
        // run from bin\ finds none of them and says so, which is correct: those
        // components genuinely are not beside this exe.
        var components = new (string Label, string Path)[]
        {
            ("Engine (x64)",  Path.Combine(baseDir, "engine", "x64", "VibeSuperTonic.Engine.dll")),
            ("Engine (x86)",  Path.Combine(baseDir, "engine", "x86", "VibeSuperTonic.Engine.dll")),
            ("Render helper", Path.Combine(baseDir, "render", "VibeSuperTonic.RenderHost.exe")),
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
            AutoSize = true,
        };
        stack.Controls.Add(new Label
        {
            Text = "VibeSuperTonic Control Panel",
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 14, FontStyle.Bold),
            AutoSize = true,
        });
        stack.Controls.Add(new Label { Text = $"Control Panel: {appVersion}", AutoSize = true });

        var mismatched = new List<string>();
        foreach (var (label, path) in components)
        {
            string? version = FileProductVersion(path);
            stack.Controls.Add(new Label
            {
                Text = $"{label}: {version ?? "(not installed here)"}",
                AutoSize = true,
                // Only a version we could read and that disagrees is a problem. A
                // component that is absent is a different fault, and the Status
                // tab is the place that already diagnoses it properly.
                ForeColor = version is not null && version != appVersion
                    ? Color.FromArgb(176, 0, 0)
                    : SystemColors.ControlText,
            });
            if (version is not null && version != appVersion) mismatched.Add($"{label} is {version}");
        }

        if (mismatched.Count > 0)
        {
            stack.Controls.Add(new Label
            {
                Text = $"These do not match this Control Panel ({appVersion}): {string.Join(", ", mismatched)}.\r\n"
                     + "Your SAPI clients load the engine, not this window — so what you hear comes from the "
                     + "version above, not this one. Extract the release ZIP over this whole folder, then press "
                     + "\"Repair all\" on the Status tab and restart your reader.",
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = Color.FromArgb(176, 0, 0),
                Margin = new Padding(0, 8, 0, 8),
            });
        }

        stack.Controls.Add(new Label { Text = $"BaseDir: {baseDir}", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        stack.Controls.Add(new Label { Text = $".NET runtime: {Environment.Version}", AutoSize = true });
        stack.Controls.Add(new Label { Text = $"ONNX runtime: {TryReadOrtVersion()}", AutoSize = true });

        const string ProjectUrl = "https://github.com/Hananel-Hazan/VibeSuperTonic";
        var link = new LinkLabel { Text = ProjectUrl, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); } catch { }
        };
        stack.Controls.Add(link);

        Controls.Add(stack);
    }

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
    private static string OwnVersion()
    {
        var informational = typeof(AboutTab).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return Trim(informational);

        return typeof(AboutTab).Assembly.GetName().Version?.ToString(3) ?? "(unknown)";
    }

    /// <summary>
    /// A shipped binary's version, or null when it is not there.
    ///
    /// <para><c>ProductVersion</c> rather than <c>FileVersion</c>, so this
    /// compares like with like against <see cref="OwnVersion"/> — the same
    /// informational string, with the same commit suffix to trim.</para>
    /// </summary>
    private static string? FileProductVersion(string path)
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

    private static string Trim(string version)
    {
        int plus = version.IndexOf('+');
        return (plus >= 0 ? version[..plus] : version).Trim();
    }

    private static string TryReadOrtVersion()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            foreach (var arch in new[] { "x64", "x86" })
            {
                string ort = Path.Combine(baseDir, "engine", arch, "onnxruntime.dll");
                if (File.Exists(ort))
                {
                    var info = FileVersionInfo.GetVersionInfo(ort);
                    return $"{info.FileVersion} ({arch})";
                }
            }
        }
        catch { }
        return "(not found)";
    }
}

using System.Diagnostics;
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
///
/// <para><b>The prose is kept in step with the Linux window's About tab</b>,
/// deliberately: two products out of one repository, and a user who reads one of
/// them should not learn something different from the other. What differs
/// between the two screens is only what is genuinely different — how the engine
/// is hosted, and which GPU providers exist on that platform.</para>
/// </summary>
internal sealed class AboutTab : UserControl
{
    public AboutTab()
    {
        string baseDir = Registration.DefaultBaseDir;
        string appVersion = VersionInfo.Own();

        // Relative to the shipped layout that build/pack-zip.ps1 composes. A dev
        // run from bin\ finds none of them and says so, which is correct: those
        // components genuinely are not beside this exe.
        var components = VersionInfo.Components(baseDir);
        string provider = TryReadProvider();

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
        stack.Controls.Add(new Label
        {
            Text = "Reads what you select, out loud, in a neural voice that runs entirely on\r\n"
                 + "this machine. Nothing is sent anywhere.\r\n\r\n"
                 + "It is a SAPI 5 voice, so it speaks through whatever already knows how to\r\n"
                 + "ask Windows for speech — Narrator, a reader, a script. This panel is where\r\n"
                 + "it is configured; it does not have to be open for the voice to work.",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 12),
        });
        stack.Controls.Add(new Label { Text = $"Control Panel: {appVersion}", AutoSize = true });

        var mismatched = new List<string>();
        foreach (var (label, path) in components)
        {
            string? version = VersionInfo.OfFile(path);
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
        stack.Controls.Add(new Label { Text = $"ONNX runtime: {VersionInfo.OnnxRuntime(AppContext.BaseDirectory)}", AutoSize = true });
        stack.Controls.Add(new Label { Text = $"Execution provider: {provider}", AutoSize = true });
        stack.Controls.Add(new Label
        {
            Text = "\r\nLicences. The program is MIT. The voice models are not part of it: they are\r\n"
                 + "distributed by Supertone, Inc. under the OpenRAIL-M licence, which you\r\n"
                 + "accepted when they were downloaded. See LICENSE-MODELS.txt beside the program.",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        });

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
    /// What the ENGINE will ask ONNX Runtime for on its next load, from the same
    /// registry the Advanced tab writes.
    ///
    /// <para>Deliberately phrased as a setting rather than as an outcome: a SAPI
    /// engine is loaded by its host, this panel is a different process, and
    /// DirectML can fail over to the CPU at load time without anything here
    /// knowing. Reporting "DirectML" as though it were a live fact would be a
    /// panel claiming something it cannot see — the engine's own log is where
    /// the outcome is recorded.</para>
    /// </summary>
    private static string TryReadProvider()
    {
        try
        {
            var s = EngineSettingsRegistry.Load();
            return s.UseDirectML
                ? $"DirectML (device {s.DirectMLDeviceId}) when available, CPU otherwise"
                : "CPU";
        }
        catch
        {
            return "(settings unreadable)";
        }
    }

}

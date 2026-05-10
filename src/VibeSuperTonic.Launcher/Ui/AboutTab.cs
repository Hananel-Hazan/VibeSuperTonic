using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class AboutTab : UserControl
{
    public AboutTab()
    {
        var version = typeof(AboutTab).Assembly.GetName().Version?.ToString() ?? "(unknown)";
        var ortVersion = TryReadOrtVersion();

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
            AutoSize = true,
        };
        stack.Controls.Add(new Label { Text = "VibeSuperTonic Control Panel", Font = new Font(SystemFonts.DefaultFont.FontFamily, 14, FontStyle.Bold), AutoSize = true });
        stack.Controls.Add(new Label { Text = $"Version: {version}", AutoSize = true });
        stack.Controls.Add(new Label { Text = $"BaseDir: {Registration.DefaultBaseDir}", AutoSize = true });
        stack.Controls.Add(new Label { Text = $".NET runtime: {Environment.Version}", AutoSize = true });
        stack.Controls.Add(new Label { Text = $"ONNX runtime: {ortVersion}", AutoSize = true });

        const string ProjectUrl = "https://github.com/Hananel-Hazan/VibeSuperTonic";
        var link = new LinkLabel { Text = ProjectUrl, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); } catch { }
        };
        stack.Controls.Add(link);

        Controls.Add(stack);
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

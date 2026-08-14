using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "VibeSuperTonic Control Panel";
        // Sized for the Monitor tab's worst-case layout: sessions grid (180) +
        // detail-metric grid (~170) + Currently-synthesizing pane (140) + Last
        // error pane (220) + tabs/chrome ≈ 880 px tall. Wider than before so the
        // 8-column sessions grid has room for the Process column to breathe.
        Width = 1240;
        Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 780);
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTabPage("Status",         new StatusTab()));
        tabs.TabPages.Add(MakeTabPage("Tune",           new TuneTab()));
        tabs.TabPages.Add(MakeTabPage("Pronunciations", new PronunciationsTab()));
        tabs.TabPages.Add(MakeTabPage("Export",         new ExportTab()));
        tabs.TabPages.Add(MakeTabPage("Benchmark",      new BenchmarkTab()));
        tabs.TabPages.Add(MakeTabPage("Monitor",        new MonitorTab()));
        tabs.TabPages.Add(MakeTabPage("Advanced",       new AdvancedTab()));
        tabs.TabPages.Add(MakeTabPage("About",          new AboutTab()));
        Controls.Add(tabs);
    }

    private static TabPage MakeTabPage(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }
}

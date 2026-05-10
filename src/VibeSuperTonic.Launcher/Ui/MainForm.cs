using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "VibeSuperTonic Control Panel";
        Width = 980;
        Height = 740;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 620);
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(MakeTabPage("Status",    new StatusTab()));
        tabs.TabPages.Add(MakeTabPage("Tune",      new TuneTab()));
        tabs.TabPages.Add(MakeTabPage("Benchmark", new BenchmarkTab()));
        tabs.TabPages.Add(MakeTabPage("Monitor",   new MonitorTab()));
        tabs.TabPages.Add(MakeTabPage("Advanced",  new AdvancedTab()));
        tabs.TabPages.Add(MakeTabPage("About",     new AboutTab()));
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

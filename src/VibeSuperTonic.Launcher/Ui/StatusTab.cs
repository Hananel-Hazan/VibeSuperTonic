using System.Windows.Forms;
using VibeSuperTonic.Launcher.Integrity;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class StatusTab : UserControl
{
    private readonly ListView _list;
    private readonly Button _refresh;
    private readonly Button _repairAll;
    private readonly TextBox _log;
    private IReadOnlyList<CheckResult> _last = Array.Empty<CheckResult>();

    public StatusTab()
    {
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 3,
            RowCount = 1,
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _refresh = new Button { Text = "Refresh", AutoSize = true };
        _refresh.Click += (_, _) => Run();
        _repairAll = new Button { Text = "Repair all", AutoSize = true };
        _repairAll.Click += async (_, _) => await RepairAllAsync();

        top.Controls.Add(_refresh, 0, 0);
        top.Controls.Add(_repairAll, 1, 0);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("", 28);
        _list.Columns.Add("Check", 240);
        _list.Columns.Add("Detail", 380);
        _list.MouseDoubleClick += async (_, _) => await RepairSelectedAsync();

        _log = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            Height = 110,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        Controls.Add(_list);
        Controls.Add(_log);
        Controls.Add(top);

        Run();
    }

    private void Run()
    {
        _last = Checks.RunAll();
        _list.Items.Clear();
        foreach (var r in _last)
        {
            string mark = r.Ok ? "OK" : (r.Severity == CheckSeverity.Error ? "✗" : "!");
            var item = new ListViewItem(mark) { ForeColor = r.Ok ? Color.ForestGreen : (r.Severity == CheckSeverity.Error ? Color.Firebrick : Color.DarkOrange) };
            item.SubItems.Add(r.Title);
            item.SubItems.Add(r.Detail);
            item.Tag = r;
            _list.Items.Add(item);
        }
    }

    private async Task RepairAllAsync()
    {
        _log.Clear();
        var progress = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));
        _repairAll.Enabled = false;
        try
        {
            foreach (var r in _last.Where(r => !r.Ok && r.Repair is not null))
            {
                _log.AppendText($"--- Repair: {r.Title} ---{Environment.NewLine}");
                try
                {
                    await r.Repair!(progress, CancellationToken.None);
                }
                catch (Exception ex) { _log.AppendText($"  exception: {ex.Message}{Environment.NewLine}"); }
            }
            Run();
        }
        finally { _repairAll.Enabled = true; }
    }

    private async Task RepairSelectedAsync()
    {
        if (!_repairAll.Enabled) return; // a repair is already running
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not CheckResult r || r.Repair is null) return;
        _log.Clear();
        _log.AppendText($"--- Repair: {r.Title} ---{Environment.NewLine}");
        var progress = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));
        _repairAll.Enabled = false;
        _list.Enabled = false;
        try
        {
            try { await r.Repair(progress, CancellationToken.None); }
            catch (Exception ex) { _log.AppendText($"  exception: {ex.Message}{Environment.NewLine}"); }
            Run();
        }
        finally
        {
            _repairAll.Enabled = true;
            _list.Enabled = true;
        }
    }
}

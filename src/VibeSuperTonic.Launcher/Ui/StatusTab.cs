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
            Height = 150,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        Controls.Add(_list);
        Controls.Add(_log);
        Controls.Add(top);

        Run();
    }

    /// <param name="clearLog">
    /// False when re-running straight after a repair, so the repair transcript
    /// stays on screen and the remaining problems are appended below it rather
    /// than replacing it.
    /// </param>
    private void Run(bool clearLog = true)
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
        ShowFixHints(clearLog);
    }

    /// <summary>
    /// Write the outstanding problems and their fixes into the log pane.
    ///
    /// <c>CheckResult.FixHint</c> was populated for every check and rendered
    /// nowhere: the grid has three columns and none of them is the hint. So the
    /// winget command a user needed to make 32-bit clients work was computed,
    /// carried around, and discarded — leaving a truncated one-line Detail as the
    /// only clue. The log pane is directly below the grid and sat empty until a
    /// repair ran, which is the obvious place to put it.
    /// </summary>
    private void ShowFixHints(bool clear)
    {
        var failures = _last.Where(r => !r.Ok).ToArray();
        if (clear) _log.Clear();
        else _log.AppendText(Environment.NewLine);

        if (failures.Length == 0)
        {
            _log.AppendText("All checks passed." + Environment.NewLine);
            return;
        }

        _log.AppendText($"{failures.Length} problem(s) found:{Environment.NewLine}");
        foreach (var r in failures)
        {
            _log.AppendText($"{Environment.NewLine}[{(r.Severity == CheckSeverity.Error ? "ERROR" : "WARN")}] {r.Title}{Environment.NewLine}");
            _log.AppendText($"  {r.Detail}{Environment.NewLine}");
            if (!string.IsNullOrWhiteSpace(r.FixHint))
                _log.AppendText($"  Fix: {r.FixHint}{Environment.NewLine}");
            else if (r.Repair is not null)
                _log.AppendText("  Fix: press \"Repair all\", or double-click this row." + Environment.NewLine);
        }
        // Scroll back to the top: the first failure is the one to read, and a
        // TextBox left at the bottom hides it. ReadOnly still allows selection,
        // so the winget command can be copied straight out of here.
        _log.SelectionStart = 0;
        _log.SelectionLength = 0;
        _log.ScrollToCaret();
    }

    /// <summary>
    /// Ask before a repair that changes the machine rather than this install.
    /// Shows the exact command, because "may I install something" is not a
    /// question anyone can answer well without seeing what.
    /// </summary>
    private bool ConfirmConsent(CheckResult r) =>
        MessageBox.Show(
            this,
            $"{r.Title}: {r.Detail}\n\n" +
            "VibeSuperTonic can install this for you using winget. It will ask for " +
            "administrator rights, and it installs the runtime machine-wide — not " +
            "just for this program.\n\n" +
            $"{r.FixHint}\n\n" +
            "Install it now?",
            "Install the .NET runtime?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

    private async Task RepairAllAsync()
    {
        _log.Clear();
        var progress = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));
        _repairAll.Enabled = false;
        try
        {
            foreach (var r in _last.Where(r => !r.Ok && r.Repair is not null))
            {
                // Registry writes and model downloads are what this button
                // promises. Installing a machine-wide .NET runtime is not, so it
                // is asked for separately rather than swept along with the rest.
                if (r.NeedsConsent && !ConfirmConsent(r))
                {
                    _log.AppendText($"--- Skipped: {r.Title} (not confirmed){Environment.NewLine}");
                    continue;
                }
                _log.AppendText($"--- Repair: {r.Title} ---{Environment.NewLine}");
                try
                {
                    await r.Repair!(progress, CancellationToken.None);
                }
                catch (Exception ex) { _log.AppendText($"  exception: {ex.Message}{Environment.NewLine}"); }
            }
            Run(clearLog: false);   // keep the repair transcript above the new hints
        }
        finally { _repairAll.Enabled = true; }
    }

    private async Task RepairSelectedAsync()
    {
        if (!_repairAll.Enabled) return; // a repair is already running
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not CheckResult r || r.Repair is null) return;
        if (r.NeedsConsent && !ConfirmConsent(r)) return;
        _log.Clear();
        _log.AppendText($"--- Repair: {r.Title} ---{Environment.NewLine}");
        var progress = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));
        _repairAll.Enabled = false;
        _list.Enabled = false;
        try
        {
            try { await r.Repair(progress, CancellationToken.None); }
            catch (Exception ex) { _log.AppendText($"  exception: {ex.Message}{Environment.NewLine}"); }
            Run(clearLog: false);   // keep the repair transcript above the new hints
        }
        finally
        {
            _repairAll.Enabled = true;
            _list.Enabled = true;
        }
    }
}

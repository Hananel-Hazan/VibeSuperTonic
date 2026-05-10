using System.Windows.Forms;
using VibeSuperTonic.Launcher.Telemetry;
using VibeSuperTonic.Launcher.Integrity;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class MonitorTab : UserControl
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _state;
    private readonly Label _voice;
    private readonly Label _resolved;
    private readonly Label _latency;
    private readonly Label _rtf;
    private readonly Label _pipeline;
    private readonly Label _gap;
    private readonly Label _cpu;
    private readonly Label _rss;
    private readonly Label _threads;
    private readonly Label _underruns;
    private readonly Label _holders;
    private readonly TextBox _text;
    private readonly TextBox _error;

    public MonitorTab()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 6,
            Padding = new Padding(8),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        for (int i = 0; i < 4; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        _state    = AddCell(grid, 0, 0, "State",         "Idle");
        _voice    = AddCell(grid, 0, 1, "Voice",         "—");
        _resolved = AddCell(grid, 0, 2, "Resolved knobs", "—");
        _latency  = AddCell(grid, 0, 3, "1st-byte latency", "—");
        _rtf      = AddCell(grid, 1, 0, "RTF (rolling)", "—");
        _pipeline = AddCell(grid, 1, 1, "Pipeline depth", "—");
        _gap      = AddCell(grid, 1, 2, "Inter-chunk gap", "—");
        _cpu      = AddCell(grid, 1, 3, "Engine CPU",    "—");
        _rss      = AddCell(grid, 2, 0, "Engine RAM",    "—");
        _threads  = AddCell(grid, 2, 1, "ONNX threads",  "—");
        _underruns= AddCell(grid, 2, 2, "Underruns",     "0");
        _holders  = AddCell(grid, 2, 3, "Engine in use by", "—");

        var textBox = new GroupBox { Text = "Currently synthesizing", Dock = DockStyle.Top, Height = 90 };
        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericSerif, 9.5f),
        };
        textBox.Controls.Add(_text);

        var errBox = new GroupBox { Text = "Last error", Dock = DockStyle.Top, Height = 60 };
        _error = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
            ForeColor = Color.Firebrick,
        };
        errBox.Controls.Add(_error);

        Controls.Add(errBox);
        Controls.Add(textBox);
        Controls.Add(grid);

        _timer = new System.Windows.Forms.Timer { Interval = 200 };
        _timer.Tick += (_, _) => { if (!IsDisposed && Visible) RefreshTelemetry(); };
        _timer.Start();

        // Pause timer entirely when this tab isn't the active one — RefreshTelemetry
        // is cheap (memory-mapped file read) but UpdateHolders calls Restart Manager
        // which is genuinely expensive. No need to do either if the user can't see it.
        VisibleChanged += (_, _) =>
        {
            if (Visible) _timer.Start(); else _timer.Stop();
        };
        HandleDestroyed += (_, _) => _timer.Stop();
        Disposed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    // LockProbe walks Restart Manager to enumerate processes holding the engine
    // DLLs — that's a heavyweight kernel call. Throttle it to once per ~5 seconds
    // (the holders list rarely changes faster than that anyway).
    private DateTime _lastHoldersCheck = DateTime.MinValue;
    private static readonly TimeSpan HoldersInterval = TimeSpan.FromSeconds(5);

    private void RefreshTelemetry()
    {
        if (!TelemetryReader.IsAvailable())
        {
            _state.Text = "Engine not loaded — start a SAPI client (or run Benchmark) to wake it";
            _voice.Text = _resolved.Text = _latency.Text = "—";
            _rtf.Text = _pipeline.Text = _gap.Text = _cpu.Text = "—";
            _rss.Text = _threads.Text = "—";
            _underruns.Text = "0";
            _text.Text = "";
            _error.Text = "";
            UpdateHolders();
            return;
        }

        var snap = TelemetryReader.TryRead();
        if (snap is null)
        {
            _state.Text = "Segment present but unreadable (struct mismatch?)";
            UpdateHolders();
            return;
        }
        // The segment can stay around after MarkIdle; that's fine — we display IsActive explicitly.

        _state.Text    = snap.IsActive ? "Speaking" : "Idle";
        _voice.Text    = string.IsNullOrEmpty(snap.VoiceId) ? "—" : snap.VoiceId;
        _resolved.Text = $"steps={snap.TotalStep}  speed={snap.EngineSpeed:F2}x  dsp={snap.DspRate:F2}x";
        _latency.Text  = $"{snap.FirstByteLatencyMs:F0} ms";
        _rtf.Text      = double.IsNaN(snap.RollingRtf) ? "—" : $"{snap.RollingRtf:F2}";
        _pipeline.Text = snap.PipelineDepth.ToString();
        _gap.Text      = $"{snap.InterChunkGapMs:F0} ms";
        _cpu.Text      = $"{snap.EngineCpuPct:F0} %";
        _rss.Text      = $"{snap.EngineRssMb:F0} MB";
        _threads.Text  = snap.OnnxThreads.ToString();
        _underruns.Text= snap.UnderrunCount.ToString();
        _text.Text     = snap.TextSnippet;
        _error.Text    = snap.LastError;
        UpdateHolders();
    }

    private void UpdateHolders()
    {
        // Throttle: Restart Manager calls cost real CPU. The holders list almost never
        // changes within a couple of seconds; refresh at 0.2 Hz instead of 5 Hz.
        if (DateTime.UtcNow - _lastHoldersCheck < HoldersInterval) return;
        _lastHoldersCheck = DateTime.UtcNow;
        try
        {
            var s = Registration.Inspect();
            var paths = new[] { s.X64ComHostPath, s.X86ComHostPath }.Where(File.Exists).ToArray();
            if (paths.Length == 0) { _holders.Text = "—"; return; }
            var holders = LockProbe.GetHolders(paths);
            _holders.Text = holders.Count == 0
                ? "(none)"
                : string.Join(", ", holders.Select(h => $"{h.FriendlyName} ({h.Pid})"));
        }
        catch { _holders.Text = "—"; }
    }

    private static Label AddCell(TableLayoutPanel grid, int row, int col, string title, string initial)
    {
        var cell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), Margin = new Padding(4), BorderStyle = BorderStyle.FixedSingle };
        var titleLbl = new Label { Text = title, AutoSize = true, ForeColor = Color.Gray, Font = new Font(SystemFonts.DefaultFont, FontStyle.Regular) };
        var value = new Label { Text = initial, AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 11f, FontStyle.Bold), Top = 18 };
        cell.Controls.Add(value);
        cell.Controls.Add(titleLbl);
        grid.Controls.Add(cell, col, row);
        return value;
    }
}

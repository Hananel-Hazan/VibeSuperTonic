using System.Windows.Forms;
using VibeSuperTonic.Launcher.Bench;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class BenchmarkTab : UserControl
{
    private readonly TextBox _text;
    private readonly ComboBox _wordCount;
    private readonly ComboBox _voicePicker;
    private readonly CheckedListBox _presetPicker;
    private readonly Button _run;
    private readonly Button _stop;
    private readonly Label _progressLabel;
    private readonly ListView _results;
    private readonly TextBox _log;
    private CancellationTokenSource? _cts;

    public BenchmarkTab()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, ColumnCount = 4, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        top.Controls.Add(new Label { Text = "Words:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 4, 0) }, 0, 0);
        _wordCount = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        _wordCount.Items.AddRange(new object[] { 50, 100, 500, 1000, 5000, 10_000, 100_000 });
        _wordCount.SelectedIndex = 2;
        top.Controls.Add(_wordCount, 1, 0);

        top.Controls.Add(new Label { Text = "Voice:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 4, 0) }, 2, 0);
        _voicePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        foreach (var v in Voices.All) _voicePicker.Items.Add(v.DisplayName);
        _voicePicker.SelectedIndex = 0;
        top.Controls.Add(_voicePicker, 3, 0);

        top.Controls.Add(new Label { Text = "Presets:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 4, 0) }, 0, 1);
        _presetPicker = new CheckedListBox
        {
            CheckOnClick = true,
            Width = 360,
            Height = 60,
            MultiColumn = true,
            ColumnWidth = 90,
        };
        _presetPicker.Items.Add("Draft", true);
        _presetPicker.Items.Add("Balanced", true);
        _presetPicker.Items.Add("Quality", true);
        _presetPicker.Items.Add("HiFi", true);
        top.Controls.Add(_presetPicker, 1, 1);
        top.SetColumnSpan(_presetPicker, 2);

        var buttonsPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(8, 18, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left };
        _run = new Button { Text = "Run", AutoSize = true };
        _run.Click += async (_, _) => await RunAsync();
        _stop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
        _stop.Click += (_, _) =>
        {
            _cts?.Cancel();
            _stop.Enabled = false;
            // Field-init-order: _log is assigned later in the ctor; the lambda only fires
            // post-construction so this null-check just appeases the compiler.
            _log?.AppendText("--- cancel requested ---" + Environment.NewLine);
        };
        buttonsPanel.Controls.Add(_run);
        buttonsPanel.Controls.Add(_stop);
        top.Controls.Add(buttonsPanel, 3, 1);

        _progressLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(8, 4, 8, 0),
            Text = "Idle.",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            BackColor = Color.FromArgb(245, 248, 252),
        };

        _text = new TextBox
        {
            Dock = DockStyle.Top,
            Multiline = true,
            Height = 130,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericSerif, 9.5f),
            Text = LoadSampleText(),
        };

        _results = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        _results.Columns.Add("Preset", 90);
        _results.Columns.Add("Steps", 60);
        _results.Columns.Add("RTF", 80);
        _results.Columns.Add("CPU %", 70);
        _results.Columns.Add("Peak RAM", 90);
        _results.Columns.Add("Verdict", 220);
        _results.Columns.Add("Notes", 300);

        _log = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            Height = 90,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        var help = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(248, 250, 255),
            Text = "How to read this:  RTF is the engine's synthesis speed — seconds of compute per second of speech.  < 0.5 = plenty of headroom (green).  0.5–1.0 = real-time but tight (yellow).  ≥ 1.0 = will not keep up (red, drop a preset).  CPU and RAM affect comfort, not whether playback works.  Pick the highest preset that stays green.\r\n"
                 + "Runs happen in the render helper, not in this window, and each one plays no audio.  Elapsed time is NOT the measurement: the engine paces its output to real time so playback keeps its last word, so a run always takes about as long as the speech it produces.",
        };

        Controls.Add(_results);
        Controls.Add(help);
        Controls.Add(_log);
        Controls.Add(_text);
        Controls.Add(_progressLabel);
        Controls.Add(top);
    }

    private static string LoadSampleText()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "samples", "twenty-thousand-leagues.txt");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        catch { }
        return DefaultSample;
    }

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _results.Items.Clear();
        _log.Clear();
        _run.Enabled = false;
        _stop.Enabled = true;

        string voiceId = Voices.All[Math.Max(0, _voicePicker.SelectedIndex)].Id;
        int wordCap = _wordCount.SelectedItem is int w ? w : 500;
        string text = _text.Text;

        var presets = new List<QualityPreset>();
        for (int i = 0; i < _presetPicker.Items.Count; i++)
            if (_presetPicker.GetItemChecked(i))
                presets.Add(Enum.Parse<QualityPreset>((string)_presetPicker.Items[i]));

        var progress = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));

        // Snapshot current settings so we always restore — even on cancel, error,
        // or a crash that never reaches the finally (the scope also writes the
        // snapshot to disk, and the next launch puts it back).
        var restore = EngineSettingsRegistry.BeginTemporaryChange(progress);

        try
        {
            int idx = 0;
            int benchWords = Bench.Benchmark.CountWords(Bench.Benchmark.TrimToWords(text, wordCap));
            foreach (var preset in presets)
            {
                idx++;
                if (ct.IsCancellationRequested) break;
                string prefix = $"Running {preset} ({idx}/{presets.Count}) on {voiceId} — {benchWords} words";
                _progressLabel.Text = prefix + "…";
                var pct = new Progress<int>(p => _progressLabel.Text = $"{prefix} — {p}%");
                var r = await Benchmark.RunAsync(preset, voiceId, text, wordCap, progress, ct, pct);
                AddResultRow(r);
                if (ct.IsCancellationRequested) { _progressLabel.Text = $"Cancelled at {preset} ({idx}/{presets.Count})."; break; }
            }
            if (!ct.IsCancellationRequested) _progressLabel.Text = $"Done. {presets.Count} preset(s).";
        }
        catch (Exception ex) { _log.AppendText($"Benchmark failed: {ex.Message}{Environment.NewLine}"); _progressLabel.Text = "Failed."; }
        finally
        {
            restore.Dispose();
            _run.Enabled = true;
            _stop.Enabled = false;
        }
    }

    private void AddResultRow(BenchmarkResult r)
    {
        var (colour, verdict) = Benchmark.Verdict(r);
        var item = new ListViewItem(r.Preset.ToString());
        item.SubItems.Add(r.TotalStep.ToString());
        item.SubItems.Add(double.IsNaN(r.Rtf) ? "—" : r.Rtf.ToString("F2"));
        item.SubItems.Add(r.AverageCpuPct.ToString("F0"));
        item.SubItems.Add($"{r.PeakProcessRssMb:F0} MB");
        item.SubItems.Add(verdict);
        // Model load and first-audio latency are called out separately from RTF:
        // they are one-off costs a reader pays at the start of a passage, not
        // throughput, and folding them into the headline number is what made the
        // old benchmark unreadable.
        item.SubItems.Add(r.Error ?? $"start {r.StartupSeconds:F1}s · first audio {r.FirstByteMs:F0}ms · {r.AudioSeconds:F0}s audio");
        item.UseItemStyleForSubItems = false;
        item.SubItems[5].BackColor = ColorTranslator.FromHtml(colour);
        item.SubItems[5].ForeColor = Color.White;
        _results.Items.Add(item);
    }

    private const string DefaultSample =
        "The deep sea, sir, is unknown to us. What may exist in its abysmal depths? " +
        "What beings can or do live twelve or fifteen miles below the surface of the ocean? " +
        "Of what organisation are these animals? It is scarcely possible to conjecture. " +
        "However, the solution of this problem may be reserved for me. To you. Either I will know, " +
        "or I will not exist.  — adapted from Twenty Thousand Leagues Under the Sea, Jules Verne (Project Gutenberg).";
}

using System.Windows.Forms;
using VibeSuperTonic.Launcher.Integrity;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class AdvancedTab : UserControl
{
    private readonly NumericUpDown _maxChunk, _minChunk, _interChunkSilence, _onnxThreads, _onnxInterOpThreads;
    private readonly NumericUpDown _synthSilence, _rateClamp;
    private readonly CheckBox _useDirectML;
    private readonly Button _save, _reset;

    public AdvancedTab()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(8),
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        _maxChunk          = AddNumeric(grid, row++, "Max chunk chars",          80,  400, 1,
            "Streaming chunk size. Larger = fewer model invocations (less overhead) but higher per-chunk latency.");
        _minChunk          = AddNumeric(grid, row++, "Min chunk chars",          40,  200, 1,
            "Below this, the chunker merges with neighbors so tiny sentences don't trigger their own model run.");
        _interChunkSilence = AddNumeric(grid, row++, "Inter-chunk silence (ms)",  0,  500, 10,
            "Audible pause between streamed chunks. Lower = tighter playback, higher = more breathing room.");
        _synthSilence      = AddNumeric(grid, row++, "Synthesis silence (s)",     0,  10,  1,
            "Silence the model itself inserts between sub-chunks before they're streamed.");
        _synthSilence.DecimalPlaces = 1; _synthSilence.Increment = 0.1m; _synthSilence.Maximum = 1.0m; _synthSilence.Minimum = 0;
        _rateClamp         = AddNumeric(grid, row++, "Rate clamp ceiling",        11, 15, 1,
            "Hard upper limit on the speed sent to the model. Above ~1.34× the model starts dropping syllables.");
        _rateClamp.DecimalPlaces = 2; _rateClamp.Increment = 0.05m; _rateClamp.Minimum = 1.10m; _rateClamp.Maximum = 1.50m;
        _onnxThreads       = AddNumeric(grid, row++, "ONNX intra-op threads (0 = auto)",   0,  64, 1,
            "Threads ORT uses inside a single op (e.g. inside one matmul). 0 = auto (cores/2). For these small TTS models, 1–4 often beats auto because cache locality matters more than parallelism.");
        _onnxInterOpThreads = AddNumeric(grid, row++, "ONNX inter-op threads",   1,  16, 1,
            "Threads ORT uses across ops. We run one model at a time, so 1 is correct for almost everyone. Raising it adds scheduler overhead with no benefit for our pipeline.");

        // GPU toggle row — adapter picker removed because DirectML's device_id
        // mapping doesn't reliably match any DXGI enumeration on all systems.
        // To control which GPU is used, configure Windows Settings → System →
        // Display → Graphics → "Add an app" → VibeSuperTonic.exe → "High
        // performance" (uses dGPU) or "Power saving" (uses iGPU).
        grid.Controls.Add(new Label { Text = "Use DirectML (GPU)", AutoSize = true, Margin = new Padding(0, 8, 12, 0) }, 0, row);
        var gpuPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _useDirectML = new CheckBox { Text = "On", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
        var dmlTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
        dmlTip.SetToolTip(_useDirectML, "When checked, the engine offloads inference to the GPU via DirectML (Direct3D 12).\n" +
            "Falls back to CPU automatically if DirectML init fails.\n\n" +
            "To choose between iGPU and dGPU on a laptop, use Windows Settings:\n" +
            "  Settings → System → Display → Graphics → Add VibeSuperTonic.exe →\n" +
            "  pick 'High performance' (uses dGPU) or 'Power saving' (uses iGPU).");
        gpuPanel.Controls.Add(_useDirectML);
        gpuPanel.Controls.Add(new Label
        {
            Text = "Use Windows → Graphics Settings to pick which GPU.",
            AutoSize = true,
            Margin = new Padding(12, 8, 0, 0),
            ForeColor = Color.DimGray,
        });
        grid.Controls.Add(gpuPanel, 1, row);
        row++;

        var info = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 90,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(255, 247, 220),
            Text = "These knobs affect engine performance. Defaults are tuned for general use; change only if you know what you want. Reset returns everything (Tier A and B) to factory defaults.\n\n" +
                "IMPORTANT: GPU and ONNX-thread changes take effect on the next engine load. To apply, you MUST close every program currently using the engine — every SAPI client (Lingoes, Balabolka, NVDA, …) AND this Control Panel itself if you've used the Benchmark tab. Then re-open them.",
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
        _save  = new Button { Text = "Save", AutoSize = true };
        _reset = new Button { Text = "Reset all to defaults", AutoSize = true };
        _save.Click  += (_, _) => Save();
        _reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset every engine knob to factory defaults?", "Reset",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            {
                EngineSettingsRegistry.ResetToDefaults();
                Load();
            }
        };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_reset);

        // Danger zone — destructive action moved out of About tab.
        var dangerBox = new GroupBox
        {
            Text = "Danger zone",
            Dock = DockStyle.Bottom,
            Height = 80,
            Padding = new Padding(8, 16, 8, 8),
            ForeColor = Color.Firebrick,
        };
        var unregister = new Button
        {
            Text = "Unregister VibeSuperTonic (removes all voices, requires UAC)",
            AutoSize = true,
            ForeColor = Color.Firebrick,
            Margin = new Padding(0),
        };
        unregister.Click += async (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Remove all SAPI voices and registry entries?\n\nYou can re-register later by relaunching VibeSuperTonic.exe and clicking Repair on the Status tab.",
                    "Unregister VibeSuperTonic",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            unregister.Enabled = false;
            try { await Task.Run(() => Registration.Unregister()); }
            finally { unregister.Enabled = true; }
            MessageBox.Show(this, "Unregister complete.", "VibeSuperTonic", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        dangerBox.Controls.Add(unregister);

        Controls.Add(dangerBox);
        Controls.Add(buttons);
        Controls.Add(grid);
        Controls.Add(info);
        Load();
    }

    private static NumericUpDown AddNumeric(TableLayoutPanel grid, int row, string title, int min, int max, int step, string? helpText = null)
    {
        grid.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 8, 12, 0) }, 0, row);
        var nud = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Width = 120 };
        if (!string.IsNullOrEmpty(helpText))
        {
            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
            tip.SetToolTip(nud, helpText);
        }
        grid.Controls.Add(nud, 1, row);
        return nud;
    }

    private new void Load()
    {
        var s = EngineSettingsRegistry.Load();
        _maxChunk.Value          = Clamp(s.MaxChunkChars, _maxChunk);
        _minChunk.Value          = Clamp(s.MinChunkChars, _minChunk);
        _interChunkSilence.Value = Clamp(s.InterChunkSilenceMs, _interChunkSilence);
        _synthSilence.Value      = (decimal)Math.Clamp(s.SynthesisSilenceSec, 0f, 1f);
        _rateClamp.Value         = (decimal)Math.Clamp(s.RateClampCeiling, 1.10f, 1.50f);
        _onnxThreads.Value       = Clamp(s.OnnxThreads, _onnxThreads);
        _onnxInterOpThreads.Value = Clamp(s.OnnxInterOpThreads, _onnxInterOpThreads);
        _useDirectML.Checked     = s.UseDirectML;
    }

    private static decimal Clamp(int v, NumericUpDown nud) => Math.Min(nud.Maximum, Math.Max(nud.Minimum, v));

    private void Save()
    {
        var s = EngineSettingsRegistry.Load();
        s.MaxChunkChars       = (int)_maxChunk.Value;
        s.MinChunkChars       = (int)_minChunk.Value;
        s.InterChunkSilenceMs = (int)_interChunkSilence.Value;
        s.SynthesisSilenceSec = (float)_synthSilence.Value;
        s.RateClampCeiling    = (float)_rateClamp.Value;
        s.OnnxThreads         = (int)_onnxThreads.Value;
        s.OnnxInterOpThreads  = (int)_onnxInterOpThreads.Value;
        s.UseDirectML         = _useDirectML.Checked;
        // DirectMLDeviceId left as-is in registry; user controls GPU selection
        // via Windows Settings → Graphics now (the in-app picker was unreliable).
        EngineSettingsRegistry.Save(s);
    }
}

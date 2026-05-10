using System.Windows.Forms;

namespace VibeSuperTonic.Launcher.Ui;

internal sealed class TuneTab : UserControl
{
    private readonly ComboBox _scope;
    private readonly RadioButton _rDraft, _rBalanced, _rQuality, _rHiFi, _rCustom;
    private readonly TrackBar _totalStep;
    private readonly Label _totalStepLabel;
    private readonly TrackBar _engineSpeed;
    private readonly Label _engineSpeedLabel;
    private readonly TrackBar _dspRate;
    private readonly Label _dspRateLabel;
    private readonly TrackBar _volumeTrim;
    private readonly Label _volumeTrimLabel;
    private readonly ComboBox _defaultVoice;
    private readonly Button _save, _testVoice, _clearOverrides;
    private readonly Label _bannerLabel;
    private bool _suppress;

    public TuneTab()
    {
        var settings = EngineSettingsRegistry.Load();

        // Banner row
        _bannerLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(255, 248, 220),
            Text = "Tip: open the Benchmark tab to find the best preset for *this* hardware. Balanced works fine if you skip it.",
        };

        // Scope row
        var scopeRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 3,
        };
        scopeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scopeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scopeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scopeRow.Controls.Add(new Label { Text = "Apply to:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 4, 0) }, 0, 0);
        _scope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            DropDownWidth = 360,
        };
        _scope.Items.Add("All voices (global)");
        foreach (var v in Voices.All) _scope.Items.Add($"Per voice: {v.DisplayName}");
        _scope.SelectedIndex = 0;
        _scope.SelectedIndexChanged += (_, _) => LoadFromSettings();
        scopeRow.Controls.Add(_scope, 1, 0);

        _clearOverrides = new Button { Text = "Clear all per-voice overrides", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 8, 0) };
        _clearOverrides.Click += (_, _) =>
        {
            var s = EngineSettingsRegistry.Load();
            s.PerVoice.Clear();
            EngineSettingsRegistry.Save(s);
            LoadFromSettings();
        };
        scopeRow.Controls.Add(_clearOverrides, 2, 0);

        // Body — preset radios + sliders
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(8),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        body.Controls.Add(new Label { Text = "Quality preset", AutoSize = true, Margin = new Padding(0, 8, 12, 0) }, 0, 0);
        var presetPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _rDraft    = MakeRadio("Draft");
        _rBalanced = MakeRadio("Balanced");
        _rQuality  = MakeRadio("Quality");
        _rHiFi     = MakeRadio("HiFi");
        _rCustom   = MakeRadio("Custom");
        presetPanel.Controls.AddRange(new Control[] { _rDraft, _rBalanced, _rQuality, _rHiFi, _rCustom });
        body.Controls.Add(presetPanel, 1, 0);

        (_totalStep,    _totalStepLabel)    = AddSliderRow(body, 1, "Diffusion steps (CPU)", 2, 16, helpText:
            "How many denoising iterations the model runs per chunk. Linear CPU cost — 8 takes 2x longer than 4. Below 6, you may hear dropped syllables; 8+ is the model's sweet spot. 12+ has diminishing returns.");
        // Engine speed locked at 1.0x: the Supertonic duration predictor under-renders
        // the trailing phoneme at speeds > 1.0, which manifests as the last word being
        // truncated. We always render at 1.0x and route any user-requested speedup
        // through the DSP path (phase vocoder), which preserves the full audio.
        (_engineSpeed,  _engineSpeedLabel)  = AddSliderRow(body, 2, "Engine speed (model)",   100, 100, 100, helpText:
            "Locked at 1.0x. The Supertonic model truncates the last phoneme at higher engine speeds. All speed adjustments now go through DSP rate, which handles them cleanly.");
        _engineSpeed.Enabled = false;
        (_dspRate,      _dspRateLabel)      = AddSliderRow(body, 3, "DSP rate (post)",        50, 200, 100, helpText:
            "Pitch-preserving phase-vocoder time-stretch applied after the model. 1.0 = no DSP. The new phase vocoder handles 0.5x - 2x cleanly without the robotic artifacts the previous WSOLA version had above 1.6x.");
        (_volumeTrim,   _volumeTrimLabel)   = AddSliderRow(body, 4, "Volume trim (dB)",      -12, 6,    1, helpText:
            "Pre-output gain on top of the SAPI client's volume slider. -6 dB = half volume; +6 dB = double (may clip).");

        body.Controls.Add(new Label { Text = "Default voice", AutoSize = true, Margin = new Padding(0, 8, 12, 0) }, 0, 5);
        _defaultVoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        foreach (var v in Voices.All) _defaultVoice.Items.Add(v.DisplayName);
        _defaultVoice.SelectedIndexChanged += (_, _) => OnAnyChanged();
        body.Controls.Add(_defaultVoice, 1, 5);

        // Buttons
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8) };
        _save = new Button { Text = "Save", AutoSize = true };
        _save.Click += (_, _) => SaveSettings();
        _testVoice = new Button { Text = "Test voice", AutoSize = true };
        _testVoice.Click += async (_, _) => await TestAsync();
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_testVoice);

        // Wiring radios
        _rDraft.CheckedChanged    += (_, _) => { if (_rDraft.Checked    && !_suppress) ApplyPreset(QualityPreset.Draft); };
        _rBalanced.CheckedChanged += (_, _) => { if (_rBalanced.Checked && !_suppress) ApplyPreset(QualityPreset.Balanced); };
        _rQuality.CheckedChanged  += (_, _) => { if (_rQuality.Checked  && !_suppress) ApplyPreset(QualityPreset.Quality); };
        _rHiFi.CheckedChanged     += (_, _) => { if (_rHiFi.Checked     && !_suppress) ApplyPreset(QualityPreset.HiFi); };

        _totalStep.ValueChanged    += (_, _) => OnAnyChanged();
        _engineSpeed.ValueChanged  += (_, _) => OnAnyChanged();
        _dspRate.ValueChanged      += (_, _) => OnAnyChanged();
        _volumeTrim.ValueChanged   += (_, _) => OnAnyChanged();

        Controls.Add(body);
        Controls.Add(buttons);
        Controls.Add(scopeRow);
        Controls.Add(_bannerLabel);

        LoadFromSettings();
    }

    private static RadioButton MakeRadio(string text) =>
        new RadioButton { Text = text, AutoSize = true, Margin = new Padding(8, 6, 8, 0) };

    private static (TrackBar tb, Label lbl) AddSliderRow(TableLayoutPanel body, int row, string title, int min, int max, int defaultValue = -1, string? helpText = null)
    {
        var titleLabel = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
        if (!string.IsNullOrEmpty(helpText))
        {
            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
            tip.SetToolTip(titleLabel, helpText);
        }
        body.Controls.Add(titleLabel, 0, row);
        var pane = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        // Pin the tooltip to the slider container too — hovering anywhere on the row shows help.
        if (!string.IsNullOrEmpty(helpText))
        {
            var tip2 = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
            tip2.SetToolTip(pane, helpText);
        }
        var tb = new TrackBar { Minimum = min, Maximum = max, Width = 360, TickFrequency = Math.Max(1, (max - min) / 12) };
        if (defaultValue >= 0) tb.Value = Math.Clamp(defaultValue, min, max);
        var lbl = new Label { AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(8, 8, 0, 0) };
        pane.Controls.Add(tb);
        pane.Controls.Add(lbl);
        body.Controls.Add(pane, 1, row);
        return (tb, lbl);
    }

    private void OnAnyChanged()
    {
        if (_suppress) return;
        // Adjusting a slider implies "Custom" preset.
        _suppress = true;
        try { _rCustom.Checked = true; } finally { _suppress = false; }
        UpdateLabels();
    }

    private void ApplyPreset(QualityPreset p)
    {
        _suppress = true;
        try
        {
            switch (p)
            {
                case QualityPreset.Draft:    SetSliders(4,  100, 100); break;
                case QualityPreset.Balanced: SetSliders(6,  100, 100); break;
                case QualityPreset.Quality:  SetSliders(8,  100, 100); break;
                case QualityPreset.HiFi:     SetSliders(12, 100, 100); break;
            }
            UpdateLabels();
        }
        finally { _suppress = false; }
    }

    private void SetSliders(int totalStep, int engineSpeedX100, int dspRateX100)
    {
        _totalStep.Value = totalStep;
        _engineSpeed.Value = engineSpeedX100;
        _dspRate.Value = dspRateX100;
    }

    private void UpdateLabels()
    {
        _totalStepLabel.Text   = $"{_totalStep.Value}";
        _engineSpeedLabel.Text = $"{_engineSpeed.Value / 100.0:F2}x";
        _dspRateLabel.Text     = $"{_dspRate.Value / 100.0:F2}x";
        _volumeTrimLabel.Text  = $"{_volumeTrim.Value:+#;-#;0} dB";
    }

    private void LoadFromSettings()
    {
        _suppress = true;
        try
        {
            var s = EngineSettingsRegistry.Load();
            EngineSettings target = s;
            if (_scope.SelectedIndex > 0)
            {
                var voiceId = Voices.All[_scope.SelectedIndex - 1].Id;
                target = s.PerVoice.TryGetValue(voiceId, out var pv) ? pv : (EngineSettings)s.Clone();
                if (!s.PerVoice.ContainsKey(voiceId)) target.Preset = s.Preset; // start from global
            }
            _totalStep.Value   = Math.Clamp(target.TotalStep, _totalStep.Minimum, _totalStep.Maximum);
            _engineSpeed.Value = Math.Clamp((int)Math.Round(target.EngineSpeed * 100), _engineSpeed.Minimum, _engineSpeed.Maximum);
            _dspRate.Value     = Math.Clamp((int)Math.Round(target.DspRate * 100), _dspRate.Minimum, _dspRate.Maximum);
            _volumeTrim.Value  = Math.Clamp((int)Math.Round(target.VolumeTrimDb), _volumeTrim.Minimum, _volumeTrim.Maximum);

            int idx = Array.FindIndex(Voices.All, v => v.Id == target.DefaultVoice);
            _defaultVoice.SelectedIndex = idx < 0 ? 0 : idx;

            switch (target.Preset)
            {
                case QualityPreset.Draft:    _rDraft.Checked    = true; break;
                case QualityPreset.Balanced: _rBalanced.Checked = true; break;
                case QualityPreset.Quality:  _rQuality.Checked  = true; break;
                case QualityPreset.HiFi:     _rHiFi.Checked     = true; break;
                default:                     _rCustom.Checked   = true; break;
            }
            UpdateLabels();
            _clearOverrides.Visible = s.PerVoice.Count > 0;
        }
        finally { _suppress = false; }
    }

    private void SaveSettings()
    {
        var s = EngineSettingsRegistry.Load();
        var snapshot = new EngineSettings
        {
            TotalStep = _totalStep.Value,
            EngineSpeed = _engineSpeed.Value / 100f,
            DspRate = _dspRate.Value / 100f,
            VolumeTrimDb = _volumeTrim.Value,
            DefaultVoice = Voices.All[Math.Max(0, _defaultVoice.SelectedIndex)].Id,
            Preset = CurrentPreset(),
            VocoderMode = s.VocoderMode, // preserve any value (user can override via --set vocodermode=N)
            // Tier B knobs preserved from existing settings
            MaxChunkChars = s.MaxChunkChars,
            MinChunkChars = s.MinChunkChars,
            InterChunkSilenceMs = s.InterChunkSilenceMs,
            SynthesisSilenceSec = s.SynthesisSilenceSec,
            RateClampCeiling = s.RateClampCeiling,
            OnnxThreads = s.OnnxThreads,
            OnnxInterOpThreads = s.OnnxInterOpThreads,
            UseDirectML = s.UseDirectML,
            DirectMLDeviceId = s.DirectMLDeviceId,
        };

        if (_scope.SelectedIndex == 0)
        {
            // Global write — preserve per-voice overrides.
            snapshot.PerVoice = s.PerVoice;
            EngineSettingsRegistry.Save(snapshot);
        }
        else
        {
            var voiceId = Voices.All[_scope.SelectedIndex - 1].Id;
            s.PerVoice[voiceId] = snapshot;
            EngineSettingsRegistry.Save(s);
        }
        LoadFromSettings();
    }

    private QualityPreset CurrentPreset()
    {
        if (_rDraft.Checked) return QualityPreset.Draft;
        if (_rBalanced.Checked) return QualityPreset.Balanced;
        if (_rQuality.Checked) return QualityPreset.Quality;
        if (_rHiFi.Checked) return QualityPreset.HiFi;
        return QualityPreset.Custom;
    }

    private async Task TestAsync()
    {
        SaveSettings();
        _testVoice.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                Type? sapi = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (sapi is null) return;
                dynamic voice = Activator.CreateInstance(sapi)!;
                try
                {
                    string voiceId = Voices.All[Math.Max(0, _defaultVoice.SelectedIndex)].Id;
                    dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
                    int n = tokens.Count;
                    for (int i = 0; i < n; i++)
                    {
                        dynamic tok = tokens.Item(i);
                        if (((string)tok.Id).IndexOf($"VibeSuperTonic_{voiceId}", StringComparison.OrdinalIgnoreCase) >= 0)
                        { voice.Voice = tok; break; }
                    }
                }
                catch { }
                voice.Speak("This is a test of the VibeSuperTonic voice with current settings.", 0);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(voice);
            });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { _testVoice.Enabled = true; }
    }
}

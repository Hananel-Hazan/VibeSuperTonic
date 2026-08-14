using System.Media;
using System.Windows.Forms;
using VibeSuperTonic.Launcher.Export;

namespace VibeSuperTonic.Launcher.Ui;

/// <summary>
/// Converts text to an MP3 or AAC/M4A file via the SAPI engine plus a Media
/// Foundation transcoder. This tab is an offline export path — quality is
/// pinned high (TotalStep 16 by default), DirectML is force-enabled regardless
/// of the global toggle, and quality knobs here never persist to the global
/// EngineSettings (they're applied for the render and restored on completion).
///
/// Pronunciation rules from the Pronunciations tab are honored automatically —
/// the engine applies them inside SAPI's Speak path before synthesis.
/// </summary>
internal sealed class ExportTab : UserControl
{
    private readonly TextBox _text;
    private readonly ComboBox _voicePicker;
    private readonly ComboBox _formatPicker;
    private readonly ComboBox _bitratePicker;
    private readonly ComboBox _previewLen;
    private readonly TrackBar _totalStep;
    private readonly Label _totalStepLabel;
    private readonly TrackBar _dspRate;
    private readonly Label _dspRateLabel;
    private readonly CheckBox _forceDml;
    private readonly Label _pronStatus;
    private readonly Button _loadFile;
    private readonly Button _preview;
    private readonly Button _save;
    private readonly Button _pause;
    private readonly Button _cancel;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly TextBox _log;
    private CancellationTokenSource? _cts;
    private string? _controlFile;          // pause/resume channel to the render helper
    private IProgress<int>? _renderProgress; // determinate progress sink for the active render
    private bool _paused;

    public ExportTab()
    {
        var settings = EngineSettingsRegistry.Load();

        var banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(220, 240, 255),
            Text = "Offline export — settings here apply only to this render and don't change Tune/SAPI defaults.",
        };

        _text = new TextBox
        {
            Dock = DockStyle.Top,
            Multiline = true,
            Height = 200,
            // MaxLength = 0 removes WinForms' default 32,767-char cap so whole
            // chapters/books can be pasted or loaded without silent truncation.
            MaxLength = 0,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Font = new Font(FontFamily.GenericSerif, 10f),
            Text = "Paste or type the text you want converted to audio, or click " +
                   "“Load text file…” to import a .txt. " +
                   "Long passages (chapters, articles, documents) are supported — synthesis " +
                   "streams to disk as it renders. Click Preview to hear the first few seconds " +
                   "with the current settings, or Save… to render the whole text.",
        };

        // Controls panel: two rows of param pickers, then quality sliders, then DML row.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 168,
            ColumnCount = 4,
            RowCount = 4,
            Padding = new Padding(4),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Row 0: Voice + Format
        grid.Controls.Add(MakeLabel("Voice:"), 0, 0);
        _voicePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        foreach (var v in Voices.All) _voicePicker.Items.Add(v.DisplayName);
        int defIdx = Array.FindIndex(Voices.All, v => v.Id == settings.DefaultVoice);
        _voicePicker.SelectedIndex = Math.Max(0, defIdx);
        grid.Controls.Add(_voicePicker, 1, 0);

        grid.Controls.Add(MakeLabel("Format:"), 2, 0);
        _formatPicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _formatPicker.Items.Add("MP3 (.mp3)  —  universal");
        _formatPicker.Items.Add("AAC (.m4a)  —  better at low bitrates");
        _formatPicker.SelectedIndex = 0;
        _formatPicker.SelectedIndexChanged += (_, _) => RefreshBitrateChoices();
        grid.Controls.Add(_formatPicker, 3, 0);

        // Row 1: Bitrate + Preview length
        grid.Controls.Add(MakeLabel("Bitrate:"), 0, 1);
        _bitratePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        grid.Controls.Add(_bitratePicker, 1, 1);

        grid.Controls.Add(MakeLabel("Preview length:"), 2, 1);
        _previewLen = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        _previewLen.Items.Add("5 seconds");
        _previewLen.Items.Add("10 seconds");
        _previewLen.Items.Add("20 seconds");
        _previewLen.SelectedIndex = 1;
        grid.Controls.Add(_previewLen, 3, 1);

        // Row 2: TotalStep slider — defaults to 16 (max quality for offline)
        grid.Controls.Add(MakeLabel("Quality (steps):"), 0, 2);
        _totalStep = new TrackBar
        {
            Minimum = 4, Maximum = 24, Value = 16, TickFrequency = 2, Width = 360,
        };
        _totalStepLabel = new Label { AutoSize = true, MinimumSize = new Size(48, 0), Margin = new Padding(8, 8, 0, 0) };
        var stepPane = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        stepPane.Controls.Add(_totalStep);
        stepPane.Controls.Add(_totalStepLabel);
        grid.SetColumnSpan(stepPane, 3);
        grid.Controls.Add(stepPane, 1, 2);
        var stepTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
        stepTip.SetToolTip(_totalStep,
            "Diffusion steps per chunk. Higher = better quality, linearly more compute. " +
            "Offline export defaults to 16 — well above the realtime sweet spot (8). " +
            "20–24 has diminishing returns but is harmless for one-off renders.");
        _totalStep.ValueChanged += (_, _) => _totalStepLabel.Text = _totalStep.Value.ToString();
        _totalStepLabel.Text = _totalStep.Value.ToString();

        // Row 3: DSP rate slider (kept on this tab so the user can deliberately
        // produce a faster/slower export without touching global Tune settings)
        grid.Controls.Add(MakeLabel("DSP rate:"), 0, 3);
        _dspRate = new TrackBar
        {
            Minimum = 70, Maximum = 130, Value = 100, TickFrequency = 5, Width = 360,
        };
        _dspRateLabel = new Label { AutoSize = true, MinimumSize = new Size(48, 0), Margin = new Padding(8, 8, 0, 0) };
        var dspPane = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        dspPane.Controls.Add(_dspRate);
        dspPane.Controls.Add(_dspRateLabel);
        grid.SetColumnSpan(dspPane, 3);
        grid.Controls.Add(dspPane, 1, 3);
        _dspRate.ValueChanged += (_, _) => _dspRateLabel.Text = $"{_dspRate.Value / 100.0:F2}x";
        _dspRateLabel.Text = "1.00x";

        // DML + pronunciations status row
        var dmlRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            ColumnCount = 3,
            Padding = new Padding(8, 0, 8, 0),
        };
        dmlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dmlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dmlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _forceDml = new CheckBox { Text = "Force DirectML (GPU) for this export", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 16, 0) };
        var dmlTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 400 };
        dmlTip.SetToolTip(_forceDml,
            "Forces the engine to load on DirectML for this export, regardless of the global setting. " +
            "If DirectML init fails on this machine, the engine's existing fallback kicks in automatically " +
            "and the render completes on CPU.");
        dmlRow.Controls.Add(_forceDml, 0, 0);

        _pronStatus = new Label { AutoSize = true, Margin = new Padding(0, 8, 0, 0), ForeColor = Color.DimGray };
        dmlRow.Controls.Add(_pronStatus, 1, 0);
        RefreshPronStatus();

        // Action row
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 4, 8, 4),
        };
        _loadFile = new Button { Text = "Load text file…", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        _loadFile.Click += (_, _) => LoadTextFile();
        _preview = new Button { Text = "Preview", AutoSize = true };
        _preview.Click += async (_, _) => await DoPreviewAsync();
        _save = new Button { Text = "Save…", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _save.Click += async (_, _) => await DoSaveAsync();
        _pause = new Button { Text = "Pause", AutoSize = true, Enabled = false, Margin = new Padding(8, 0, 0, 0) };
        // Pause/Resume toggles a control file the render helper polls — lets the
        // user give the machine a breather on a long export without losing work.
        _pause.Click += (_, _) =>
        {
            if (_controlFile is null) return;
            _paused = !_paused;
            try { File.WriteAllText(_controlFile, _paused ? "pause" : "resume"); } catch { }
            _pause.Text = _paused ? "Resume" : "Pause";
            _status!.Text = _paused ? "Paused — click Resume to continue." : "Rendering…";
        };
        _cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false, Margin = new Padding(8, 0, 0, 0) };
        // Field-init order: _status is assigned later in this same constructor.
        // The lambda fires post-construction so the null-forgive is correct.
        _cancel.Click += (_, _) => { _cts?.Cancel(); _cancel.Enabled = false; _status!.Text = "Cancelling…"; };
        actions.Controls.Add(_loadFile);
        actions.Controls.Add(_preview);
        actions.Controls.Add(_save);
        actions.Controls.Add(_pause);
        actions.Controls.Add(_cancel);

        // Progress + status + log
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Continuous, Margin = new Padding(8, 0, 8, 0) };
        _status = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(8, 4, 8, 0), Text = "Idle." };
        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        Controls.Add(_log);
        Controls.Add(_status);
        Controls.Add(_progress);
        Controls.Add(actions);
        Controls.Add(dmlRow);
        Controls.Add(grid);
        Controls.Add(_text);
        Controls.Add(banner);

        RefreshBitrateChoices();
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 8, 4, 0) };

    private void RefreshBitrateChoices()
    {
        _bitratePicker.Items.Clear();
        if (_formatPicker.SelectedIndex == 0)
        {
            // MP3 — full ladder including 320 (max quality)
            _bitratePicker.Items.Add("320 kbps (max quality)");
            _bitratePicker.Items.Add("256 kbps");
            _bitratePicker.Items.Add("192 kbps");
            _bitratePicker.Items.Add("128 kbps");
            _bitratePicker.Items.Add("96 kbps (small file)");
        }
        else
        {
            // AAC — MS encoder maxes out at 192 kbps
            _bitratePicker.Items.Add("192 kbps (max for AAC)");
            _bitratePicker.Items.Add("160 kbps");
            _bitratePicker.Items.Add("128 kbps");
            _bitratePicker.Items.Add("96 kbps (small file)");
        }
        _bitratePicker.SelectedIndex = 0;
    }

    private void RefreshPronStatus()
    {
        try
        {
            string path = PronunciationsRegistry.Path;
            if (!File.Exists(path))
            {
                _pronStatus.Text = "Pronunciations: none configured.";
                return;
            }
            var cfg = PronunciationsRegistry.Load();
            int active = cfg.Enabled ? cfg.Rules.Count(r => r.Enabled && !string.IsNullOrEmpty(r.Match)) : 0;
            _pronStatus.Text = cfg.Enabled
                ? $"Pronunciations: {active} active rule(s) — will be applied to export."
                : "Pronunciations: master switch off — rules will NOT be applied.";
        }
        catch { _pronStatus.Text = ""; }
    }

    private int SelectedBitrateBps()
    {
        bool aac = _formatPicker.SelectedIndex == 1;
        return aac switch
        {
            true => _bitratePicker.SelectedIndex switch
            {
                0 => 192_000,
                1 => 160_000,
                2 => 128_000,
                _ => 96_000,
            },
            false => _bitratePicker.SelectedIndex switch
            {
                0 => 320_000,
                1 => 256_000,
                2 => 192_000,
                3 => 128_000,
                _ => 96_000,
            },
        };
    }

    private ExportFormat SelectedFormat() => _formatPicker.SelectedIndex == 1 ? ExportFormat.Aac : ExportFormat.Mp3;

    /// <summary>
    /// Returns the model files the engine needs for <paramref name="voiceId"/>
    /// that are missing from disk. An empty list means the engine has what it
    /// needs. We check here so the user gets a clear, actionable message instead
    /// of a silent empty WAV (engine throws, SAPI swallows the error) or — when
    /// they hit Save — a walk into the crashy shell dialog for nothing.
    /// </summary>
    private static List<string> MissingModelFiles(string voiceId)
    {
        string baseDir = DataPaths.BaseDir;
        var required = new List<string>
        {
            Path.Combine(baseDir, "models", "voice_styles", $"{voiceId}.json"),
            Path.Combine(baseDir, "models", "onnx", "text_encoder.onnx"),
            Path.Combine(baseDir, "models", "onnx", "duration_predictor.onnx"),
            Path.Combine(baseDir, "models", "onnx", "vector_estimator.onnx"),
            Path.Combine(baseDir, "models", "onnx", "vocoder.onnx"),
        };
        return required.Where(p => !File.Exists(p)).ToList();
    }

    /// <summary>
    /// Shows a clear "models missing" message if the engine can't synthesize the
    /// selected voice. Returns true if synthesis should be blocked.
    /// </summary>
    private bool BlockedByMissingModels(string title)
    {
        string voiceId = Voices.All[Math.Max(0, _voicePicker.SelectedIndex)].Id;
        var missing = MissingModelFiles(voiceId);
        if (missing.Count == 0) return false;

        string list = string.Join(Environment.NewLine, missing.Select(p => "  • " + p));
        string msg =
            $"This install is missing the model data the engine needs to synthesize voice “{voiceId}”.\n\n" +
            "Synthesis cannot run until these files are present:\n\n" + list + "\n\n" +
            "Place the model files under the install's models\\ folder (models\\onnx and " +
            "models\\voice_styles), then try again. See the Status tab → Repair for details.";
        DiagLog.Write($"[Export] Blocked: {missing.Count} model file(s) missing for voice {voiceId}.");
        foreach (var p in missing) DiagLog.Write($"[Export]   missing: {p}");
        MessageBox.Show(this, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _status.Text = "Model data missing — synthesis blocked.";
        return true;
    }

    private async Task DoPreviewAsync()
    {
        string text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { MessageBox.Show(this, "Type some text first.", "Preview"); return; }
        if (BlockedByMissingModels("Preview")) return;
        int seconds = _previewLen.SelectedIndex switch { 0 => 5, 1 => 10, _ => 20 };

        string tempWav = Path.Combine(Path.GetTempPath(), $"vst_preview_{Guid.NewGuid():N}.wav");
        var req = BuildRequest(text, tempWav, previewSec: seconds);

        // Preview: render WAV with the same engine settings, then play via SoundPlayer.
        // We skip the MF transcode — uncompressed WAV plays fine over the default audio device,
        // and preview length is short enough that file size is irrelevant.
        await RunWithButtonsDisabled(async ct =>
        {
            await Task.Run(() =>
            {
                var log = new Progress<string>(LogLine);
                SapiFileRender.RenderToWav(req, tempWav, log, ct, _renderProgress, _controlFile);
            }, ct);

            if (ct.IsCancellationRequested || !File.Exists(tempWav)) return;
            // A valid WAV header is 44 bytes; anything at/near that means SAPI
            // wrote a header but the engine produced no audio samples (which is
            // exactly what an engine-side Synthesize failure looks like from
            // here — SAPI swallows the engine's E_FAIL and returns "success").
            long wavBytes = new FileInfo(tempWav).Length;
            LogLine($"Preview WAV size: {wavBytes:N0} bytes ({(wavBytes <= 64 ? "EMPTY — engine produced no audio; see engine.log" : "has audio data")}).");
            _status.Text = "Playing preview…";
            try
            {
                using var player = new SoundPlayer(tempWav);
                player.PlaySync();
            }
            catch (Exception ex) { LogLine($"Preview playback failed: {ex.Message}"); }
            finally
            {
                try { File.Delete(tempWav); } catch { }
            }
        }, "Preview rendering…");
    }

    private async Task DoSaveAsync()
    {
        string text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { MessageBox.Show(this, "Type some text first.", "Save"); return; }
        // Check models BEFORE opening the file dialog: no point walking into the
        // (separately crash-prone) shell dialog if synthesis can't run anyway.
        if (BlockedByMissingModels("Save")) return;

        var fmt = SelectedFormat();
        string ext = fmt == ExportFormat.Aac ? "m4a" : "mp3";
        DiagLog.Write($"[Export] Save: opening SaveFileDialog (fmt={fmt}).");
        string outPath;
        try
        {
            using var dlg = new SaveFileDialog
            {
                Title = $"Save as {fmt}",
                Filter = fmt == ExportFormat.Aac
                    ? "AAC audio (*.m4a)|*.m4a|All files (*.*)|*.*"
                    : "MP3 audio (*.mp3)|*.mp3|All files (*.*)|*.*",
                DefaultExt = ext,
                FileName = $"vibesupertonic_export.{ext}",
                // AutoUpgradeEnabled=false forces the classic Win32 dialog, which
                // loads far fewer shell extensions than the modern Vista-style
                // dialog. A native crash inside SaveFileDialog (process dies with
                // no managed exception — see launcher.log) is almost always a
                // third-party shell extension faulting in the dialog's host
                // process; the classic dialog sidesteps most of them.
                AutoUpgradeEnabled = false,
                RestoreDirectory = true,
                CheckPathExists = true,
                OverwritePrompt = true,
            };
            var dr = dlg.ShowDialog(this);
            DiagLog.Write($"[Export] Save: SaveFileDialog returned {dr}.");
            if (dr != DialogResult.OK) return;
            outPath = dlg.FileName;
        }
        catch (Exception ex)
        {
            // A crash here points at the shell file dialog, not synthesis.
            DiagLog.WriteException("ExportTab.DoSaveAsync/SaveFileDialog", ex);
            MessageBox.Show(this, $"Could not open the Save dialog:\n\n{ex.Message}", "Save",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DiagLog.Write($"[Export] Save: target path = {outPath}");
        var req = BuildRequest(text, outPath, previewSec: null);

        await RunWithButtonsDisabled(async ct =>
        {
            await Task.Run(() =>
            {
                var log = new Progress<string>(LogLine);
                // The helper renders AND encodes straight to the final file —
                // both steps out-of-process, so neither ONNX nor Media Foundation
                // runs inside this self-contained single-file Control Panel.
                SapiFileRender.RenderToFile(req, outPath, log, ct, _renderProgress, _controlFile);
            }, ct);

            if (!ct.IsCancellationRequested && File.Exists(outPath))
            {
                long bytes = new FileInfo(outPath).Length;
                _status.Text = $"Saved {outPath} ({bytes / 1024.0:F0} KB).";
            }
            else if (ct.IsCancellationRequested)
            {
                _status.Text = "Cancelled.";
            }
            else
            {
                _status.Text = "Export failed — see log.";
            }
        }, "Rendering full export…");
    }

    private ExportRequest BuildRequest(string text, string outPath, int? previewSec)
    {
        string voiceId = Voices.All[Math.Max(0, _voicePicker.SelectedIndex)].Id;
        return new ExportRequest
        {
            Text = text,
            VoiceId = voiceId,
            OutputPath = outPath,
            Format = SelectedFormat(),
            Bitrate = SelectedBitrateBps(),
            TotalStep = _totalStep.Value,
            DspRate = _dspRate.Value / 100f,
            VolumeTrimDb = 0f,
            ForceDirectML = _forceDml.Checked,
            PreviewSeconds = previewSec,
        };
    }

    private async Task RunWithButtonsDisabled(Func<CancellationToken, Task> body, string initialStatus)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Per-render pause/resume channel + determinate progress sink. Progress<T>
        // marshals the callback to the UI thread, so updating the bar is safe.
        _controlFile = Path.Combine(Path.GetTempPath(), $"vst_ctrl_{Guid.NewGuid():N}.txt");
        try { File.WriteAllText(_controlFile, "resume"); } catch { }
        _paused = false;
        _renderProgress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));

        _log.Clear();
        _loadFile.Enabled = false;
        _preview.Enabled = false;
        _save.Enabled = false;
        _pause.Enabled = true;
        _pause.Text = "Pause";
        _cancel.Enabled = true;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = 100;
        _progress.Value = 0;
        _status.Text = initialStatus;
        RefreshPronStatus();

        try { await body(ct); }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception ex) { LogLine($"ERROR: {ex.Message}"); _status.Text = "Failed — see log."; }
        finally
        {
            _progress.Value = 0;
            _loadFile.Enabled = true;
            _preview.Enabled = true;
            _save.Enabled = true;
            _pause.Enabled = false;
            _pause.Text = "Pause";
            _paused = false;
            _cancel.Enabled = false;
            try { if (_controlFile is not null) File.Delete(_controlFile); } catch { }
            _controlFile = null;
            _renderProgress = null;
        }
    }

    private void LoadTextFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Load text file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Read with encoding detection (BOM-aware). For very large files this
            // loads the whole document into the box — that's intended: the export
            // path streams it to SAPI in one Speak call regardless of size.
            string content = File.ReadAllText(dlg.FileName);
            _text.Text = content;
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
            _status.Text = $"Loaded {Path.GetFileName(dlg.FileName)} ({content.Length:N0} chars).";
        }
        catch (Exception ex)
        {
            DiagLog.WriteException("ExportTab.LoadTextFile", ex);
            MessageBox.Show(this, $"Could not read the file:\n\n{ex.Message}", "Load text file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LogLine(string msg)
    {
        // Tee to the on-disk launcher log first (flushed per line) so the trail
        // survives a hard COM/SAPI crash that never returns to the UI thread.
        DiagLog.Write($"[Export] {msg}");
        if (InvokeRequired) { BeginInvoke(new Action<string>(LogLine), msg); return; }
        _log.AppendText(msg + Environment.NewLine);
    }
}

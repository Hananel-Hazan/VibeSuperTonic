using System.Windows.Forms;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Launcher.Bench;

namespace VibeSuperTonic.Launcher.Ui;

/// <summary>
/// "Measure this machine" — the thread and provider sweep, and what it decided.
///
/// <para><b>Why this is a different question from the preset benchmark next to
/// it.</b> That tab asks which quality setting this machine can keep up with, and
/// the answer is a preference. This one asks how the engine should be configured
/// to run <em>any</em> of them, and the answer is a fact about the hardware that
/// nobody can guess: measured on a 20-thread i7-12800H, four threads beat ORT's
/// own pick on wall clock while using a quarter of the machine, and eight was the
/// worst row on the board — slower than two.</para>
///
/// <para><b>Everything on screen is meant to be argued with.</b> The whole table
/// is shown rather than only the winner, each row carries the spread of its own
/// runs, and the pick states which rule chose it. A number in a settings file with
/// no provenance is a number nobody dares change.</para>
/// </summary>
internal sealed class MachineSweepPanel : UserControl
{
    private readonly ComboBox _voicePicker;
    private readonly CheckBox _includeGpu;
    private readonly CheckBox _force;
    private readonly Button _run;
    private readonly Button _stop;
    private readonly Label _progress;
    private readonly Label _current;
    private readonly ListView _results;
    private readonly TextBox _notes;
    private readonly TextBox _log;
    private CancellationTokenSource? _cts;

    public MachineSweepPanel()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 68, ColumnCount = 5, RowCount = 2, AutoSize = false };
        for (int i = 0; i < 4; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        top.Controls.Add(new Label { Text = "Voice:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 10, 4, 0) }, 0, 0);
        _voicePicker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 6, 8, 0) };
        foreach (var v in Voices.All) _voicePicker.Items.Add(v.DisplayName);
        _voicePicker.SelectedIndex = 0;
        top.Controls.Add(_voicePicker, 1, 0);

        _includeGpu = new CheckBox
        {
            Text = "Include DirectML",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(8, 8, 8, 0),
        };
        var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
        tip.SetToolTip(_includeGpu,
            "Adds one GPU row. The engine ships with DirectML on by default and nothing has ever\n" +
            "compared it against the CPU on real hardware — this is the measurement that would.\n" +
            "If DirectML cannot initialise here, the row is recorded as failed rather than quietly\n" +
            "falling back to the CPU and being labelled as GPU.");
        top.Controls.Add(_includeGpu, 2, 0);

        _run = new Button { Text = "Measure this machine", AutoSize = true, Margin = new Padding(8, 4, 4, 0) };
        _run.Click += async (_, _) => await RunAsync();
        top.Controls.Add(_run, 3, 0);

        _stop = new Button { Text = "Stop", AutoSize = true, Enabled = false, Margin = new Padding(4, 4, 0, 0) };
        _stop.Click += (_, _) =>
        {
            _cts?.Cancel();
            _stop.Enabled = false;
            _log?.AppendText("--- cancel requested ---" + Environment.NewLine);
        };
        top.Controls.Add(_stop, 4, 0);

        _force = new CheckBox
        {
            Text = "Measure anyway if the machine is busy",
            AutoSize = true,
            Margin = new Padding(8, 4, 0, 0),
        };
        tip.SetToolTip(_force,
            "A sweep run while something else is working measures that instead, and the result is\n" +
            "saved with a timestamp as if it were sound. Leave this off unless you have a reason.");
        top.Controls.Add(_force, 1, 1);
        top.SetColumnSpan(_force, 4);

        _current = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 46,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.FromArgb(245, 248, 252),
        };

        _progress = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(8, 4, 8, 0),
            Text = "Idle.",
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _results = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        _results.Columns.Add("Configuration", 130);
        _results.Columns.Add("Provider", 80);
        // "Cost" rather than "Time": this is the engine's own synthesis cost for the
        // sample, not how long the run took. A SAPI render is paced to real time, so
        // wall clock through this path is pinned near the audio length whatever the
        // thread count — calling that column "Time" is how the number gets believed.
        _results.Columns.Add("Cost (ms)", 90);
        _results.Columns.Add("RTF", 70);
        _results.Columns.Add("Cores", 70);
        _results.Columns.Add("Core-s", 70);
        _results.Columns.Add("Spread", 70);
        _results.Columns.Add("Note", 420);

        _notes = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            Height = 96,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(252, 252, 248),
        };

        _log = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            Height = 80,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 8.25f),
        };

        var help = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(248, 250, 255),
            Text =
                "How to read this:  Cost is the engine's own synthesis time for a fixed sample — lower is faster.  "
              + "Cores is how much of the machine that row occupied.  Spread is how far the three runs of that row "
              + "varied, and it is the number that says whether a difference is real: two rows 2% apart with 18% "
              + "spreads are not different.\r\n"
              + "The pick is the FASTEST row, then the one using LEAST of the machine among those within the tie "
              + "band — you asked to be read to while you carry on working, so between two rows that finish together "
              + "the quiet one wins.",
        };

        Controls.Add(_results);
        Controls.Add(help);
        Controls.Add(_notes);
        Controls.Add(_log);
        Controls.Add(_progress);
        Controls.Add(_current);
        Controls.Add(top);

        ShowStoredProfile();
    }

    /// <summary>
    /// What is in force right now and where it came from — shown before anything
    /// is measured, because "never measured on this machine" is an answer the user
    /// needs as much as a table is.
    /// </summary>
    private void ShowStoredProfile()
    {
        try
        {
            var settings = EngineSettingsRegistry.Load();
            var stored = BenchmarkStore.Load(DataPaths.BenchmarkFilePath);

            if (settings.OnnxThreads != CpuBudget.Auto)
            {
                _current.Text =
                    $"In force: {settings.OnnxThreads} threads, set by hand on the Advanced tab. "
                  + "A measurement will be recorded but will NOT be applied while that box is non-zero — "
                  + "set it back to 0 to let the benchmark decide.";
                return;
            }

            if (stored is null)
            {
                _current.Text =
                    "Never measured on this machine. The engine is using ONNX Runtime's own thread pick, "
                  + "which measured as the worst configuration on the machine that motivated this feature.";
                return;
            }

            var now = MachineFacts.Current(
                MachineFacts.ModelsRoot, settings.TotalStep, VoiceId(), settings.Language);
            var stale = stored.StalenessAgainst(now);

            string measured = stored.MeasuredUtc.Length >= 10 ? stored.MeasuredUtc[..10] : stored.MeasuredUtc;
            string pick = stored.Threads == CpuBudget.Auto ? "auto threads" : $"{stored.Threads} threads";

            _current.Text = stale.Count == 0
                ? $"In force: {stored.Provider.ToUpperInvariant()}, {pick} (benchmark {measured})."
                : $"Stored profile ({stored.Provider.ToUpperInvariant()}, {pick}, {measured}) does NOT apply here — "
                  + string.Join("; ", stale) + ". The engine is using ONNX Runtime's own pick instead.";
        }
        catch (Exception ex)
        {
            _current.Text = $"Could not read the current profile: {ex.Message}";
        }
    }

    private string VoiceId() => Voices.All[Math.Max(0, _voicePicker.SelectedIndex)].Id;

    private async Task RunAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _results.Items.Clear();
        _log.Clear();
        _notes.Clear();
        _run.Enabled = false;
        _stop.Enabled = true;
        _progress.Text = "Checking how busy this machine is…";

        var log = new Progress<string>(msg => _log.AppendText(msg + Environment.NewLine));
        int total = 0;
        var onRow = new Progress<BenchmarkProgress>(p =>
        {
            total = p.Total;
            AddRow(p.Row);
            _progress.Text = $"Measured {p.Index} of {p.Total}…";
        });

        // Snapshot the user's settings so they are restored even on cancel, error,
        // or a crash that never reaches the finally — the scope also writes the
        // snapshot to disk and the next launch puts it back. A sweep applies a
        // thread count to EVERY SAPI client on this machine while it runs, so this
        // is not a nicety.
        var restore = EngineSettingsRegistry.BeginTemporaryChange(log);
        var started = DateTime.UtcNow;

        try
        {
            var outcome = await ThreadSweep.RunAsync(
                VoiceId(), _includeGpu.Checked, _force.Checked, log, onRow, ct);

            var elapsed = DateTime.UtcNow - started;

            if (outcome.Refusal is not null)
            {
                _progress.Text = "Refused.";
                _notes.Text = outcome.Refusal;
                return;
            }

            var lines = new List<string>();
            if (outcome.Profile is { } profile)
            {
                string pick = profile.Threads == CpuBudget.Auto ? "auto threads" : $"{profile.Threads} threads";
                lines.Add($"Picked: {profile.Provider.ToUpperInvariant()}, {pick} — "
                        + $"fastest within {profile.TieBand:P0}, then least of the machine. "
                        + $"Worst row spread {profile.MaxSpread:P0}.");
                _progress.Text = $"Done — {total} configurations in {elapsed.TotalSeconds:F0} s.";
            }
            else
            {
                _progress.Text = "Nothing was measured.";
            }

            lines.AddRange(outcome.Notes);
            _notes.Text = string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "Cancelled.";
            _notes.Text = "Cancelled — nothing was saved, and your settings have been put back.";
        }
        catch (Exception ex)
        {
            _progress.Text = "Failed.";
            _notes.Text = $"The sweep failed: {ex.Message}";
            _log.AppendText($"Sweep failed: {ex}{Environment.NewLine}");
        }
        finally
        {
            restore.Dispose();
            _run.Enabled = true;
            _stop.Enabled = false;
            ShowStoredProfile();
        }
    }

    private void AddRow(BenchmarkRow row)
    {
        var item = new ListViewItem(row.Label);
        item.SubItems.Add(row.Provider);
        item.SubItems.Add(row.Failed ? "—" : row.MedianWallMs.ToString("F0"));
        item.SubItems.Add(row.Failed ? "—" : row.Rtf.ToString("F3"));
        item.SubItems.Add(row.Failed ? "—" : row.AvgCores.ToString("F1"));
        item.SubItems.Add(row.Failed ? "—" : row.CoreSeconds.ToString("F1"));
        item.SubItems.Add(row.Failed ? "—" : row.Spread.ToString("P0"));
        item.SubItems.Add(row.Error ?? "");
        item.UseItemStyleForSubItems = false;
        if (row.Failed) item.SubItems[7].ForeColor = Color.FromArgb(176, 0, 0);
        _results.Items.Add(item);
    }
}

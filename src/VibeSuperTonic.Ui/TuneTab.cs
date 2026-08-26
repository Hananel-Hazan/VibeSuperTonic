using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The knobs, and the one place this window writes rather than asks.
///
/// <para>Every field here is a key in <c>settings.json</c>, edited through
/// <see cref="SettingsFile"/> so that saving cannot erase a key this build has
/// never heard of. Nothing is sent over the socket: the daemon notices the
/// mtime and re-reads, which is the same arrangement the Windows Control Panel
/// and engine have, and it keeps exactly one writer.</para>
///
/// <para><b>What a save does and does not reach.</b> Most values apply to the
/// next utterance. <c>MaxCpuPercent</c> does not — ORT sizes its thread pool
/// when the session is built — so the tab says so beside the field rather than
/// leaving the user to conclude the setting is broken.</para>
/// </summary>
public sealed class TuneTab : UserControl
{
    private readonly DaemonClient _client;
    private readonly TextBlock _status = Ui.Label("");
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly TextBlock _benchmarkStatus = Ui.Label("");
    private readonly TextBlock _inForce = Ui.Label("");
    private readonly Button _benchmark;
    private readonly CheckBox _clipboardFallback = new() { Content = "Fall back to the clipboard when nothing is selected" };

    /// <summary>
    /// auto / cpu / gpu. Not a text box, because there are exactly three answers
    /// and a typo in this one silently means auto.
    /// </summary>
    private readonly ComboBox _provider = new()
    {
        Width = 120,
        ItemsSource = new[] { "auto", "cpu", "gpu" },
    };

    private readonly CheckBox _gpuOnBattery = new()
    {
        Content = "Keep using the GPU on battery",
    };

    /// <summary>
    /// Installed voices, both engines, as engine-qualified ids. A picker rather
    /// than the text box this was, because since P4 the set of valid answers is
    /// knowable — the daemon can list them — and a typo in a free-text voice
    /// field is a hotkey that refuses with a sentence about a voice nobody meant
    /// to type.
    /// </summary>
    private readonly ComboBox _voice = new() { Width = 260, MinWidth = 200 };

    /// <summary>Said beside the controls a Piper voice cannot honour, and blank otherwise.</summary>
    private readonly TextBlock _engineNote = Ui.Label("");

    private string? _settingsPath;

    /// <summary>Suppresses the selection handler while <see cref="RefreshAsync"/> populates the picker.</summary>
    private bool _loading;

    /// <summary>The last voice list, so the engine rules can read a row's details without a round trip.</summary>
    private List<VoiceEntry> _installedVoices = [];

    /// <summary>
    /// The keys this tab owns, with the units a person needs to type one. Held
    /// as a list so the control, the read and the save cannot drift apart.
    /// </summary>
    private static readonly (string Key, string Label, string Hint)[] Numbers =
    [
        ("TotalStep", "Model steps", "8 by default. Fewer is faster and rougher."),
        ("EngineSpeed", "Engine speed", "1.05 by default. The model's own rate."),
        ("DspRate", "Playback rate", "1.0 by default. Time-stretch after synthesis, up to 2.0."),
        ("VolumeTrimDb", "Volume trim (dB)", "0 by default."),
        ("MaxChunkChars", "Chunk maximum (characters)", "200 by default."),
        ("MinChunkChars", "Chunk minimum (characters)", "100 by default."),
        ("InterChunkSilenceMs", "Silence between chunks (ms)", "200 by default. Moves sentence boundary timing."),
        ("MaxCpuPercent", "CPU share (%)", "20 by default. Applies on the next start, not this one — and a benchmark beats it."),
    ];

    public TuneTab(DaemonClient client)
    {
        _client = client;

        var language = new TextBox { Width = 120 };
        _fields["Language"] = language;

        _voice.SelectionChanged += (_, _) => { if (!_loading) ApplyEngineRules(); };

        var grid = new StackPanel { Spacing = 8 };
        grid.Children.Add(Row("Voice", _voice,
            "Installed voices, both engines. Add and remove them in the Voices tab."));
        grid.Children.Add(Row("Language", language, "en — a Supertonic code, not en-US."));
        grid.Children.Add(_engineNote);

        foreach (var (key, label, hint) in Numbers)
        {
            var box = new TextBox { Width = 120 };
            _fields[key] = box;
            grid.Children.Add(Row(label, box, hint));
        }

        grid.Children.Add(Row("Execution provider", _provider,
            "auto follows the benchmark. gpu needs the optional provider pack — without it, "
            + "auto and gpu both mean CPU, and the line below says so."));
        grid.Children.Add(_clipboardFallback);
        grid.Children.Add(_gpuOnBattery);

        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) => await SaveAsync();

        var revert = new Button { Content = "Revert", HorizontalAlignment = HorizontalAlignment.Left };
        revert.Click += async (_, _) => await RefreshAsync();

        // WIRED IN PHASE 8B, and it was an exit criterion: parity does not
        // permit a dead control to reach v1. It calls the same verb `vst-ctl
        // benchmark` does — not a private path into the daemon — so the window
        // and the terminal cannot disagree about what a measurement is.
        _benchmark = new Button
        {
            Content = "Measure this machine…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _benchmark.Click += async (_, _) => await BenchmarkAsync(force: false);

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                Ui.Label("Saved to data/settings.json. The daemon notices the file changing and "
                       + "re-reads it, so a save applies to the next utterance without restarting anything."),
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { save, revert },
                },
                _status,
                new Separator(),
                _inForce,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _benchmark },
                },
                _benchmarkStatus,
                Ui.Label("About a minute. It renders a fixed sample at every thread count worth "
                       + "trying — and on each GPU provider this machine can actually use — three "
                       + "times each, then keeps the whole table in data/benchmark.json. The daemon "
                       + "picks the result up on the next thing you ask it to read; no restart.")),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private static Control Row(string label, Control field, string hint) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center },
            field,
            new TextBlock
            {
                Text = hint,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
        },
    };

    /// <summary>
    /// Read the daemon's effective configuration for the paths and the truth
    /// about what is in force, and the file itself for the values to edit. Both,
    /// because they answer different questions: the file says what was asked
    /// for, and <c>config</c> says what the daemon made of it.
    /// </summary>
    private async Task RefreshAsync()
    {
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is not { } c)
        {
            _status.Text = "no daemon is running, so there is nothing to read or write yet.";
            return;
        }

        _settingsPath = Path.Combine(c.DataDir, "settings.json");

        JsonObject root;
        try { root = SettingsFile.Read(_settingsPath); }
        catch (Exception ex)
        {
            _status.Text = $"{_settingsPath} could not be read: {ex.Message}. Saving is disabled "
                         + "until it parses — overwriting a file we cannot read would discard settings.";
            _settingsPath = null;
            return;
        }

        await RefreshVoicePickerAsync(c.Voice);

        // Blank means "not set in the file", and the placeholder shows what the
        // daemon is using instead. An empty box that silently means 8 would make
        // "clear it to get the default" indistinguishable from "it is 8".
        _fields["Language"].Text = root.String("Language") ?? "";
        _fields["Language"].Watermark = c.Language;

        foreach (var (key, _, _) in Numbers)
            _fields[key].Text = root.Number(key)?.ToString(CultureInfo.InvariantCulture) ?? "";

        _fields["TotalStep"].Watermark = c.TotalStep.ToString(CultureInfo.InvariantCulture);
        _fields["MaxChunkChars"].Watermark = c.MaxChunkChars.ToString(CultureInfo.InvariantCulture);
        _fields["MinChunkChars"].Watermark = c.MinChunkChars.ToString(CultureInfo.InvariantCulture);
        _fields["InterChunkSilenceMs"].Watermark = c.InterChunkSilenceMs.ToString(CultureInfo.InvariantCulture);

        _clipboardFallback.IsChecked = root.Bool("ClipboardFallback") ?? false;
        _gpuOnBattery.IsChecked = root.Bool("GpuOnBattery") ?? false;
        _provider.SelectedItem = root.String("Provider") is { Length: > 0 } p
                                 && new[] { "auto", "cpu", "gpu" }.Contains(p)
            ? p
            : "auto";

        _status.Text = $"read from {_settingsPath}"
                     + (c.DataDirWritable ? "" : " — this data directory is NOT writable, so a save will fail");

        // The daemon's answer, not the file's. "cuda (benchmark 2026-08-24)" and
        // "cpu, 2 threads (on battery, benchmark 2026-08-24)" are the same
        // profile reporting two different outcomes, and the second one is a
        // question the user would otherwise ask by unplugging things and
        // guessing.
        string threads = c.IntraOpThreads == 0 ? "auto" : $"{c.IntraOpThreads} threads";
        _inForce.Text = $"In force now: {c.Provider}, {threads} ({c.ThreadsReason})."
                      + (c.Benchmark is { } b
                          ? $" Last measured {b.MeasuredUtc[..Math.Min(10, b.MeasuredUtc.Length)]}"
                            + (b.Applied ? "." : $" — not applied: {string.Join("; ", b.Staleness)}.")
                          : " This machine has never been measured.");
    }

    /// <summary>
    /// Fill the picker from <c>voices</c>, keeping whatever is selected if it is
    /// still installed.
    ///
    /// <para>The daemon is asked rather than the models directory read, because
    /// this window is a client: the same rule that put the sweep behind a verb.
    /// A daemon that does not answer leaves the picker holding just the voice in
    /// force, so the tab still shows the truth and simply cannot offer
    /// alternatives.</para>
    /// </summary>
    private async Task RefreshVoicePickerAsync(string inForce)
    {
        var reply = await _client.SendAsync(new Request { Verb = RequestVerb.Voices });

        var ids = reply?.Voices?.Installed.Select(v => v.Id).ToList() ?? [];
        _installedVoices = reply?.Voices?.Installed.ToList() ?? [];

        // The voice actually in force may not be installed — the setting can name
        // a voice someone removed. Listing it anyway is what makes that visible
        // in the one control whose job is to show which voice is chosen.
        var qualified = VibeSuperTonic.Core.Synthesis.VoiceId.Parse(inForce).ToString();
        if (!ids.Contains(qualified)) ids.Insert(0, qualified);

        _loading = true;
        _voice.ItemsSource = ids;
        _voice.SelectedItem = ids.Contains(qualified) ? qualified : ids.FirstOrDefault();
        _loading = false;

        ApplyEngineRules();
    }

    /// <summary>
    /// Grey out what the selected engine cannot honour, and say why.
    ///
    /// <para>A Piper voice IS a language and it has no diffusion steps — asking
    /// it for either is not a thing that can be done, rather than a thing that is
    /// ignored. The tab already does this for <c>MaxCpuPercent</c>, whose value
    /// is real but does not apply until the next start; the principle is the
    /// same and so is the remedy: a control that cannot apply must say so, not
    /// sit there accepting input.</para>
    /// </summary>
    private void ApplyEngineRules()
    {
        string? selected = _voice.SelectedItem as string;
        bool piper = selected is not null
                     && VibeSuperTonic.Core.Synthesis.VoiceId.Parse(selected).Engine
                        == VibeSuperTonic.Core.Synthesis.VoiceEngine.Piper;

        _fields["Language"].IsEnabled = !piper;
        _fields["TotalStep"].IsEnabled = !piper;

        var entry = _installedVoices.FirstOrDefault(v => v.Id == selected);

        _engineNote.Text = piper
            ? "Language and Model steps do not apply to a Piper voice: it is trained for one "
              + "language, which is baked into its own config, and it has no diffusion steps."
              + (entry?.Calibrated == false
                  ? "  This voice has not been calibrated yet, so a requested rate is approximate "
                    + "until the daemon finishes measuring it."
                  : "")
            : "";
    }

    private async Task SaveAsync()
    {
        if (_settingsPath is null) { _status.Text = "nothing to save to."; return; }

        JsonObject root;
        try { root = SettingsFile.Read(_settingsPath); }
        catch (Exception ex) { _status.Text = $"could not re-read the file: {ex.Message}"; return; }

        // Re-read immediately before writing rather than editing the copy loaded
        // when the tab opened: settings.json is a file a person also edits in an
        // editor, and a save that silently reverts a hand edit made five minutes
        // ago is the worst kind of correct.
        // The shared writer, so this picker and the Voices tab's Use button
        // cannot disagree about what choosing a voice means.
        if (_voice.SelectedItem is string chosen && chosen.Length > 0) root.SetVoice(chosen);

        root.Set("Language", Text(_fields["Language"]));

        foreach (var (key, label, _) in Numbers)
        {
            string raw = _fields[key].Text?.Trim() ?? "";
            if (raw.Length == 0) { root.Remove(key); continue; }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                _status.Text = $"{label}: \"{raw}\" is not a number. Nothing was saved.";
                return;
            }

            // Integers stay integers on the wire. A TotalStep of 8.0 parses
            // everywhere but reads as a mistake in a file people open.
            root.Set(key, value == Math.Floor(value) ? JsonValue.Create((long)value) : JsonValue.Create(value));
        }

        root.Set("ClipboardFallback", JsonValue.Create(_clipboardFallback.IsChecked == true));
        root.Set("GpuOnBattery", JsonValue.Create(_gpuOnBattery.IsChecked == true));

        // Written even when it is "auto", which is the default: it is a setting a
        // person went looking for, and a key that vanishes when set back to its
        // default reads as a save that did not take.
        root.Set("Provider", JsonValue.Create((string)(_provider.SelectedItem ?? "auto")));

        try { SettingsFile.Write(_settingsPath, root); }
        catch (Exception ex) { _status.Text = $"could not write {_settingsPath}: {ex.Message}"; return; }

        _status.Text = "saved — waiting for the daemon to pick it up…";

        // Ask the daemon rather than asserting. `reload` is a verb vst-ctl has,
        // and the answer that follows is the daemon's own reading of the file,
        // which is the only report worth showing.
        await _client.SendAsync(new Request { Verb = RequestVerb.Reload });
        await RefreshAsync();

        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is { } after)
            _status.Text = $"saved. In force now: voice {after.Voice}/{after.Language}, "
                         + $"{after.TotalStep} steps, chunks {after.MinChunkChars}–{after.MaxChunkChars}, "
                         + $"{after.InterChunkSilenceMs} ms between them."
                         + (after.Notes.Count > 0 ? " " + string.Join(" ", after.Notes) : "");
    }

    /// <summary>
    /// Run the sweep, showing each row as it lands.
    ///
    /// <para><b>The refusals are the interesting part.</b> The daemon declines to
    /// measure a machine that is busy, or one that is speaking, because a sweep
    /// run then records whatever else was running and stamps it with a timestamp
    /// as though it were sound. Those come back as ordinary failures with a
    /// sentence, and the sentence is shown verbatim rather than replaced with
    /// "benchmark failed" — the load guard in particular is one the user can
    /// answer, either by waiting or by insisting.</para>
    /// </summary>
    private async Task BenchmarkAsync(bool force)
    {
        _benchmark.IsEnabled = false;
        _benchmarkStatus.Text = force
            ? "measuring anyway…"
            : "measuring… this takes about a minute, and the daemon cannot speak while it runs.";

        try
        {
            var reply = await _client.SendStreamingAsync(
                new Request { Verb = RequestVerb.Benchmark, Force = force ? true : null },
                progress =>
                {
                    if (progress.Progress is not { } p) return;

                    // Off the socket's thread and onto the UI's. Avalonia controls
                    // are single-threaded and this is the one place in the window
                    // where something else is talking.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        _benchmarkStatus.Text = p.Row.Failed
                            ? $"{p.Index} of {p.Total}: {p.Row.Label} could not be measured ({p.Row.Error})"
                            : $"{p.Index} of {p.Total}: {p.Row.Label} — "
                              + $"{p.Row.MedianWallMs:F0} ms, RTF {p.Row.Rtf:F3}, "
                              + $"{p.Row.AvgCores:F1} cores busy");
                });

            if (reply is null)
            {
                _benchmarkStatus.Text = "no daemon answered, so nothing was measured.";
                return;
            }

            if (!reply.Ok)
            {
                _benchmarkStatus.Text = reply.Error ?? "the benchmark failed without saying why.";

                // Only the load guard can be overridden, and only deliberately.
                // A second button beats a checkbox nobody reads before clicking.
                if (!force && (reply.Error?.Contains("busy") ?? false))
                {
                    _benchmarkStatus.Text += " A result measured now is worth less; measure anyway?";
                    var anyway = new Button
                    {
                        Content = "Measure anyway",
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    anyway.Click += async (_, _) =>
                    {
                        ((StackPanel)_benchmark.Parent!).Children.Remove(anyway);
                        await BenchmarkAsync(force: true);
                    };
                    ((StackPanel)_benchmark.Parent!).Children.Add(anyway);
                }
                return;
            }

            if (reply.Benchmark is not { } result)
            {
                _benchmarkStatus.Text = "the daemon ended the sweep without a result.";
                return;
            }

            string picked = result.Profile.Threads == 0 ? "auto" : $"{result.Profile.Threads} threads";
            _benchmarkStatus.Text =
                $"measured: {result.Profile.Provider}, {picked}"
                + (result.Profile.Winner is { } w ? $" — RTF {w.Rtf:F3}, {w.AvgCores:F1} cores busy" : "")
                + (result.Saved ? $". Saved to {result.Path}." : ". NOT saved — this install is read-only.")
                + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "");

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _benchmarkStatus.Text = $"the sweep could not be run: {ex.Message}";
        }
        finally
        {
            _benchmark.IsEnabled = true;
        }
    }

    private static JsonNode? Text(TextBox box) =>
        box.Text?.Trim() is { Length: > 0 } value ? JsonValue.Create(value) : null;
}

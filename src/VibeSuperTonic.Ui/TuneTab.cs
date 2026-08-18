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
    private readonly CheckBox _clipboardFallback = new() { Content = "Fall back to the clipboard when nothing is selected" };

    private string? _settingsPath;

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

        var voice = new TextBox { Width = 120 };
        var language = new TextBox { Width = 120 };
        _fields["DefaultVoice"] = voice;
        _fields["Language"] = language;

        var grid = new StackPanel { Spacing = 8 };
        grid.Children.Add(Row("Voice", voice, "M1 … F5, as the models directory names them."));
        grid.Children.Add(Row("Language", language, "en — a Supertonic code, not en-US."));

        foreach (var (key, label, hint) in Numbers)
        {
            var box = new TextBox { Width = 120 };
            _fields[key] = box;
            grid.Children.Add(Row(label, box, hint));
        }

        grid.Children.Add(_clipboardFallback);

        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += async (_, _) => await SaveAsync();

        var revert = new Button { Content = "Revert", HorizontalAlignment = HorizontalAlignment.Left };
        revert.Click += async (_, _) => await RefreshAsync();

        // Ships disabled, with the note. `vst-ctl benchmark` exists and works
        // headless; wiring this button is a Phase 8 exit criterion, because
        // parity does not permit a dead control to reach v1 — and a control that
        // silently does nothing is worse than one that says why.
        var benchmark = new Button
        {
            Content = "Measure this machine…",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

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
                benchmark,
                Ui.Label("Disabled until Phase 8 wires it. The measurement itself works today: "
                       + "run `vst-ctl benchmark`, then `vst-ctl shutdown` to apply it.")),
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

        // Blank means "not set in the file", and the placeholder shows what the
        // daemon is using instead. An empty box that silently means 8 would make
        // "clear it to get the default" indistinguishable from "it is 8".
        _fields["DefaultVoice"].Text = root.String("DefaultVoice") ?? "";
        _fields["DefaultVoice"].Watermark = c.Voice;
        _fields["Language"].Text = root.String("Language") ?? "";
        _fields["Language"].Watermark = c.Language;

        foreach (var (key, _, _) in Numbers)
            _fields[key].Text = root.Number(key)?.ToString(CultureInfo.InvariantCulture) ?? "";

        _fields["TotalStep"].Watermark = c.TotalStep.ToString(CultureInfo.InvariantCulture);
        _fields["MaxChunkChars"].Watermark = c.MaxChunkChars.ToString(CultureInfo.InvariantCulture);
        _fields["MinChunkChars"].Watermark = c.MinChunkChars.ToString(CultureInfo.InvariantCulture);
        _fields["InterChunkSilenceMs"].Watermark = c.InterChunkSilenceMs.ToString(CultureInfo.InvariantCulture);

        _clipboardFallback.IsChecked = root.Bool("ClipboardFallback") ?? false;

        _status.Text = $"read from {_settingsPath}"
                     + (c.DataDirWritable ? "" : " — this data directory is NOT writable, so a save will fail");
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
        root.Set("DefaultVoice", Text(_fields["DefaultVoice"]));
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

    private static JsonNode? Text(TextBox box) =>
        box.Text?.Trim() is { Length: > 0 } value ? JsonValue.Create(value) : null;
}

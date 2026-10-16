using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Settings;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The rewrite table applied to every fragment before it reaches the model.
///
/// <para><b>The preview runs the product's own code.</b> Rules are compiled with
/// <see cref="PronunciationsConfig.Compile"/> and applied with
/// <see cref="PronunciationsConfig.Apply"/> — the same methods the daemon calls
/// — so the sample cannot disagree with what will be spoken. That is the reason
/// these types live in Core rather than once here and once there, and it is the
/// mistake the Windows side made and then fixed.</para>
///
/// <para>Each rule keeps the JSON object it was read from and only the fields
/// this editor owns are written back, so a key from a later release survives a
/// save here — the same discipline as <see cref="SettingsFile"/>, for the same
/// reason: the file crosses platforms.</para>
/// </summary>
public sealed class PronunciationsTab : UserControl
{
    private readonly DaemonClient _client;

    private readonly ListBox _list = new() { Height = 220 };
    private readonly CheckBox _enabledAll = new() { Content = "Apply pronunciation rules" };
    private readonly TextBox _match = new();
    private readonly TextBox _replace = new();
    private readonly TextBox _notes = new();
    private readonly CheckBox _wholeWord = new() { Content = "Whole word" };
    private readonly CheckBox _caseSensitive = new() { Content = "Case sensitive" };
    private readonly CheckBox _ruleEnabled = new() { Content = "Enabled" };

    private readonly TextBox _sample = new()
    {
        Text = "Dr. Tcl weighs 40 kg, i.e. not much.",
        AcceptsReturn = true,
        Height = 60,
    };

    private readonly TextBlock _preview = Ui.Body();
    private readonly TextBlock _status = Ui.Label("");

    private readonly List<JsonObject> _rules = [];

    /// <summary>
    /// Whether a reload may replace these rules.
    ///
    /// <para><b>This tab re-reads on every visit, and a tab switch IS a
    /// visit.</b> So writing three rules, glancing at the Voices tab and coming
    /// back used to discard all three — no warning, no dialog, and nothing a
    /// user would connect to the thing they did. It is the same defect as the
    /// Tune tab's reverting rate, in the one tab that was never given the
    /// guard.</para>
    /// </summary>
    private readonly PendingEdits _edits = new();
    private JsonObject _root = new();
    private string? _path;
    private bool _loading;
    private bool _rebuilding;

    public PronunciationsTab(DaemonClient client)
    {
        _client = client;

        _list.SelectionChanged += (_, _) => ShowSelected();

        foreach (var box in new[] { _match, _replace, _notes })
        {
            box.TextChanged += (_, _) => WriteBack();     // WriteBack marks the edit
            box.LostFocus += (_, _) => RefreshLabels();
        }

        foreach (var check in new[] { _wholeWord, _caseSensitive, _ruleEnabled })
            check.IsCheckedChanged += (_, _) => { WriteBack(); RefreshLabels(); };

        _enabledAll.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _edits.Edited();
            Preview();
        };
        _sample.TextChanged += (_, _) => Preview();

        var add = new Button { Content = "Add" };
        add.Click += (_, _) => Add();

        var remove = new Button { Content = "Remove" };
        remove.Click += (_, _) => Remove();

        var save = new Button { Content = "Save" };
        save.Click += async (_, _) => await SaveAsync();

        // The one button that is allowed to lose work, because it is the one
        // somebody pressed to lose it.
        var revert = new Button { Content = "Revert" };
        revert.Click += async (_, _) =>
        {
            _edits.Discard(() => { });
            await RefreshAsync();
        };

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                Ui.Label("Saved to data/pronunciations.json and applied from the next utterance, "
                       + "never the one in flight."),
                _enabledAll,
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { add, remove, save, revert },
                },
                Field("Match", _match, "The text to look for. A regular expression unless Whole word is set."),
                Field("Replace with", _replace, "What the model should say instead."),
                Field("Notes", _notes, "For you, not for the model."),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children = { _ruleEnabled, _wholeWord, _caseSensitive },
                },
                _status,
                new Separator(),
                new TextBlock { Text = "Try it", FontWeight = FontWeight.SemiBold },
                _sample,
                _preview),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private static Control Field(string label, Control field, string hint) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = label, Opacity = 0.7 },
            field,
            new TextBlock { Text = hint, Opacity = 0.5, FontSize = 11, TextWrapping = TextWrapping.Wrap },
        },
    };

    // ------------------------------------------------------------------ file

    private async Task RefreshAsync()
    {
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is not { } c)
        {
            _status.Text = "no daemon is running, so there is nothing to read or write yet.";
            return;
        }

        _path = Path.Combine(c.DataDir, "pronunciations.json");

        try { _root = SettingsFile.Read(_path); }
        catch (Exception ex)
        {
            _status.Text = $"{_path} could not be read: {ex.Message}. Saving is disabled until it parses.";
            _path = null;
            return;
        }

        // ONLY WHEN THERE IS NOTHING TO LOSE. This runs on every visit to the
        // tab, which includes switching away and back, and it used to clear the
        // list unconditionally.
        bool filled = _edits.Load(() =>
        {
            _loading = true;
            _rules.Clear();
            if (_root["Rules"] is JsonArray array)
                foreach (var node in array)
                    if (node is JsonObject rule) _rules.Add(rule);

            _enabledAll.IsChecked = _root.Bool("Enabled") ?? true;
            RebuildList(select: _rules.Count > 0 ? 0 : -1);
            _loading = false;
        });

        // And SAY which of the two is on screen. A tab that silently ignores the
        // file is as confusing as one that silently discards the edit.
        _status.Text = filled
            ? $"{_rules.Count} rule{(_rules.Count == 1 ? "" : "s")} in {_path}"
              + (c.DataDirWritable ? "" : " — this data directory is NOT writable, so a save will fail")
            : $"{_rules.Count} rule{(_rules.Count == 1 ? "" : "s")}, and these are YOUR unsaved edits "
              + $"rather than what is in {_path}. Save keeps them; Revert discards them.";
        Preview();
    }

    private async Task SaveAsync()
    {
        if (_path is null) { _status.Text = "nothing to save to."; return; }

        _root["Enabled"] = JsonValue.Create(_enabledAll.IsChecked == true);
        _root["Rules"] = new JsonArray(_rules.Select(r => (JsonNode)r.DeepClone()).ToArray());

        try { SettingsFile.Write(_path, _root); }
        catch (Exception ex) { _status.Text = $"could not write {_path}: {ex.Message}"; return; }

        _edits.Saved();

        // The daemon reloads on mtime by itself; asking makes the answer
        // immediate and gives it somewhere to report a rule that will not
        // compile.
        await _client.SendAsync(new Request { Verb = RequestVerb.Reload });

        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        _status.Text = config?.Config is { } c
            ? $"saved. The daemon loaded {c.RuleCount} rule{(c.RuleCount == 1 ? "" : "s")}, "
              + (c.RulesEnabled ? "applied." : "currently disabled.")
              + (c.Notes.Count > 0 ? " " + string.Join(" ", c.Notes) : "")
            : "saved.";
    }

    // ------------------------------------------------------------------ rules

    private void Add()
    {
        _rules.Add(new JsonObject
        {
            ["Enabled"] = true,
            ["Match"] = "",
            ["Replace"] = "",
            ["WholeWord"] = true,
            ["CaseSensitive"] = true,
            ["Notes"] = "",
        });

        _edits.Edited();
        RebuildList(select: _rules.Count - 1);
        _match.Focus();
    }

    private void Remove()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _rules.Count) return;

        _edits.Edited();
        _rules.RemoveAt(index);
        RebuildList(select: Math.Min(index, _rules.Count - 1));
        Preview();
    }

    /// <summary>Redraw the labels in place, keeping whatever is selected.</summary>
    private void RefreshLabels()
    {
        if (_rebuilding) return;
        RebuildList(_list.SelectedIndex);
    }

    private void RebuildList(int select)
    {
        // Assigning ItemsSource takes SelectedIndex to -1 first, and the
        // SelectionChanged that comes with it must not be mistaken for the user
        // choosing a different rule.
        _rebuilding = true;

        _list.ItemsSource = _rules.Select(Describe).ToList();
        _list.SelectedIndex = select;

        _rebuilding = false;
        ShowSelected();
    }

    private static string Describe(JsonObject rule)
    {
        string match = rule["Match"]?.GetValue<string>() ?? "";
        string replace = rule["Replace"]?.GetValue<string>() ?? "";
        bool enabled = rule["Enabled"]?.GetValue<bool>() ?? true;

        string body = match.Length == 0 ? "(new rule)" : $"{match}  →  {replace}";
        return enabled ? body : $"{body}   (off)";
    }

    private void ShowSelected()
    {
        if (_rebuilding) return;

        int index = _list.SelectedIndex;
        bool has = index >= 0 && index < _rules.Count;

        _loading = true;
        var rule = has ? _rules[index] : null;

        _match.Text = rule?["Match"]?.GetValue<string>() ?? "";
        _replace.Text = rule?["Replace"]?.GetValue<string>() ?? "";
        _notes.Text = rule?["Notes"]?.GetValue<string>() ?? "";
        _ruleEnabled.IsChecked = rule?["Enabled"]?.GetValue<bool>() ?? true;
        _wholeWord.IsChecked = rule?["WholeWord"]?.GetValue<bool>() ?? true;
        _caseSensitive.IsChecked = rule?["CaseSensitive"]?.GetValue<bool>() ?? true;

        foreach (var control in new Control[] { _match, _replace, _notes, _ruleEnabled, _wholeWord, _caseSensitive })
            control.IsEnabled = has;

        _loading = false;
    }

    /// <summary>
    /// Edits land in the rule's own JSON object as they are typed. Only the six
    /// fields this editor owns are touched, so anything else in the object
    /// survives the round trip.
    /// </summary>
    private void WriteBack()
    {
        if (_loading) return;

        int index = _list.SelectedIndex;
        if (index < 0 || index >= _rules.Count) return;

        _edits.Edited();

        var rule = _rules[index];
        rule["Match"] = _match.Text ?? "";
        rule["Replace"] = _replace.Text ?? "";
        rule["Notes"] = _notes.Text ?? "";
        rule["Enabled"] = _ruleEnabled.IsChecked == true;
        rule["WholeWord"] = _wholeWord.IsChecked == true;
        rule["CaseSensitive"] = _caseSensitive.IsChecked == true;

        // The label is refreshed when the field loses focus, NOT here. Replacing
        // ItemsSource resets SelectedIndex to -1 on the way through, which fires
        // SelectionChanged, which clears and disables the very box being typed
        // into — so a rebuild per keystroke ate every keystroke after the first.
        Preview();
    }

    private void Preview()
    {
        var config = new PronunciationsConfig
        {
            Enabled = _enabledAll.IsChecked == true,
            Rules = [.. _rules.Select(ToRule)],
        };

        try
        {
            var compiled = config.Rules.Select(PronunciationsConfig.Compile).ToList();
            string before = _sample.Text ?? "";
            string after = config.Apply(before, compiled);

            _preview.Text = before == after ? "no rule changes this text." : after;
        }
        catch (Exception ex)
        {
            // A half-typed regular expression is the ordinary state of this
            // control, not an error worth a dialog.
            _preview.Text = $"cannot apply yet: {ex.Message}";
        }
    }

    private static PronunciationRule ToRule(JsonObject rule) => new()
    {
        Enabled = rule["Enabled"]?.GetValue<bool>() ?? true,
        Match = rule["Match"]?.GetValue<string>() ?? "",
        Replace = rule["Replace"]?.GetValue<string>() ?? "",
        WholeWord = rule["WholeWord"]?.GetValue<bool>() ?? true,
        CaseSensitive = rule["CaseSensitive"]?.GetValue<bool>() ?? true,
        Notes = rule["Notes"]?.GetValue<string>() ?? "",
    };
}

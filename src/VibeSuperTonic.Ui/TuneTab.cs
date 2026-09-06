using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Settings;
using VibeSuperTonic.Core.Synthesis;

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

    /// <summary>
    /// What the boxes should say, and whether anything in them is unsaved.
    ///
    /// <para><b>This tab does not decide where a value comes from, and that is
    /// deliberate.</b> Two paths used to fill these boxes — the read from disk
    /// and the scope selector — and the read ran second and always used the
    /// file's top level, so a scoped value was overwritten by the global one
    /// within the same repaint. Reported 2026-08-30 as "the volume will not
    /// stick": the file held <c>PerEngine.supertonic.VolumeTrimDb 5</c>, the box
    /// showed 0, and the next Save wrote that 0 back over the 5. It was the
    /// third bug in three days of the same family — a display disagreeing with
    /// what was saved — so the remedy is structural rather than another guard:
    /// <see cref="ScopedFields"/> derives the source from the scope, and nothing
    /// here can pass it a different one.</para>
    ///
    /// <para>It also carries the touched rule the rate bug produced: a refresh
    /// must never silently discard what somebody typed, because the read is
    /// asynchronous and lands after a person has already typed a rate. Revert is
    /// the button that discards on purpose, and it says so.</para>
    /// </summary>
    private readonly ScopedFields _state = new(["Language", .. Numbers.Select(n => n.Key)]);
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
    private readonly ComboBox _voice = new() { Width = 320, MinWidth = 200 };

    /// <summary>
    /// The three levels above the voice. A flat list of everything installed is
    /// what this replaces: with the English Piper voices in, libritts and
    /// libritts_r carry 904 speakers each and vctk another 109, so "every voice
    /// and every speaker" is eighteen hundred rows with the ten Supertonic
    /// styles somewhere inside it. Each level offers only what the one above it
    /// left standing, and no control ever shows the whole catalog.
    /// </summary>
    private readonly ComboBox _engine = new() { Width = 160 };
    private readonly ComboBox _voiceLanguage = new() { Width = 220 };
    private readonly ComboBox _speaker = new() { Width = 220 };
    private readonly TextBlock _speakerLabel = Ui.Label("Speaker");

    /// <summary>Said beside the controls a Piper voice cannot honour, and blank otherwise.</summary>
    private readonly TextBlock _engineNote = Ui.Label("");
    /// <summary>
    /// Who the settings below apply to: every voice, every voice of this engine,
    /// or this voice alone.
    ///
    /// <para>The fields always show what the CHOSEN scope resolves to, so what
    /// is on screen is what that scope will produce. Saving writes only into
    /// that scope — the numbers a voice inherits are not copied down into it,
    /// because an override that silently pins every value is one nobody can
    /// undo by changing the global setting.</para>
    /// </summary>
    private readonly ComboBox _scope = new() { Width = 320 };
    private readonly Button _clearScope = new() { Content = "Clear these overrides" };
    private readonly TextBlock _scopeNote = Ui.Label("");

    private readonly Button _test = new() { Content = "Save & test" };
    private readonly Button _stop = new() { Content = "Stop" };

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
        language.TextChanged += (_, _) => { if (!_loading) _state.Typed("Language", language.Text); };

        // Each level rebuilds the ones below it. Guarded by _loading so filling
        // the controls during a refresh does not look like a user choosing.
        _engine.SelectionChanged += (_, _) => { if (!_loading) { _voiceTouched = true; RebuildLanguages(); } };
        _voiceLanguage.SelectionChanged += (_, _) => { if (!_loading) { _voiceTouched = true; RebuildVoices(); } };
        _voice.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _voiceTouched = true;
            RebuildSpeakers();
            RebuildScopes();
            ApplyEngineRules();
        };
        _speaker.SelectionChanged += (_, _) => { if (!_loading) _voiceTouched = true; };

        _scope.SelectionChanged += (_, _) => { if (!_loading) ApplyScope(); };
        _clearScope.Click += async (_, _) => await ClearScopeAsync();

        var grid = new StackPanel { Spacing = 8 };
        grid.Children.Add(Row("These settings apply to", _scope,
            "Choose a voice below, then decide whether what you set here is for every voice, "
            + "for that whole engine, or for that voice alone."));
        grid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _clearScope, _scopeNote },
        });
        grid.Children.Add(Row("Engine", _engine,
            "Supertonic is one model set with ten styles. Piper is one voice per download; "
            + "add and remove them in the Voices tab."));
        grid.Children.Add(Row("Voice language", _voiceLanguage,
            "A Piper voice is trained for one language. Supertonic has no such level — it "
            + "takes the language per utterance, from the Language field below."));
        grid.Children.Add(Row("Voice", _voice,
            "Each row shows its licence: a NonCommercial voice says so here, before you use it."));
        grid.Children.Add(Row("Speaker", _speaker,
            "Only for voices that carry several. LibriTTS has 904; most voices have one and "
            + "this row is hidden."));
        grid.Children.Add(Row("Language", language, "en — a Supertonic code, not en-US."));
        grid.Children.Add(_engineNote);

        // HEAR IT. Every number on this tab is a claim about how a voice sounds,
        // and none of them can be judged by reading. It saves first because a
        // Speak request carries text, voice and language and nothing else — the
        // daemon speaks from settings.json, so testing an unsaved rate would
        // test the old one and quietly say so with the wrong voice. The button
        // says "Save & test" for that reason rather than hiding it.
        _test.Click += async (_, _) =>
        {
            await SaveAsync();
            var reply = await _client.SendAsync(new Request
            {
                Verb = RequestVerb.Speak,
                Voice = ChosenVoice() is { Length: > 0 } v ? v : null,
                Text = "The quick brown fox jumps over the lazy dog. "
                     + "This is how the current settings sound.",
            });
            if (reply?.Ok == false) _status.Text = reply.Error ?? "the daemon refused to speak.";
        };
        _stop.Click += async (_, _) => await _client.SendAsync(new Request { Verb = RequestVerb.Stop });
        grid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _test, _stop },
        });

        foreach (var (key, label, hint) in Numbers)
        {
            var box = new TextBox { Width = 120 };
            box.TextChanged += (_, _) => { if (!_loading) _state.Typed(key, box.Text); };
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
        // The one button that discards on purpose, so it clears the flag first —
        // otherwise the refresh it triggers would politely keep the very edits
        // the user just asked to throw away.
        revert.Click += async (_, _) => { _state.Discard(); ShowFields(); await RefreshAsync(); };

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

        _file = root;
        _state.Load(root);

        // The picker cascade ends in ApplyScope, which is the ONE place the
        // boxes are filled — from whatever scope is selected, which is what this
        // read cannot know and must not assume.
        await RefreshVoicePickerAsync(c.Voice);

        // Blank means "not set in the file", and the placeholder shows what the
        // daemon is using instead. An empty box that silently means 8 would make
        // "clear it to get the default" indistinguishable from "it is 8".
        _fields["Language"].Watermark = c.Language;

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
        _installedVoices = reply?.Voices?.Installed.ToList() ?? [];

        // Where the cascade should stand for the voice in force — which also
        // turns a settings file still holding a bare "M4" into supertonic:M4,
        // rather than offering the same voice twice under two spellings.
        _at = VoicePicker.Locate(_installedVoices, inForce);
        _voiceTouched = false;

        _loading = true;
        Fill(_engine, VoicePicker.Engines(_installedVoices), _at.Engine);
        _loading = false;

        RebuildLanguages();
    }

    /// <summary>Where the cascade currently stands. Rebuilt from the daemon's list.</summary>
    private VoiceSelection _at = new("supertonic", "", "", null);

    /// <summary>
    /// Whether the user has touched the voice ON THIS TAB.
    ///
    /// <para><b>Two tabs can set the voice, and without this they fight.</b> The
    /// Voices tab's Use button writes the voice; this tab's Save writes every
    /// setting it shows, the voice among them. So a Save here — made to change a
    /// rate, or a provider — would quietly put back whatever voice this tab was
    /// showing when it last read the file, undoing a choice made next door. The
    /// tab re-reads on every visit, which makes that window small rather than
    /// closed; this closes it. Save writes the voice only when the voice is what
    /// the user came here to change.</para>
    /// </summary>
    private bool _voiceTouched;

    /// <summary>
    /// The settings file as last read. Held so the scope selector can show what
    /// a scope resolves to without going back to disk on every change — and so
    /// "does this scope have overrides" is answered from the same copy the
    /// fields were filled from.
    /// </summary>
    private JsonObject? _file;

    private void RebuildLanguages()
    {
        string engine = Selected(_engine) ?? _at.Engine;
        var rows = VoicePicker.Languages(_installedVoices, engine);

        _loading = true;
        Fill(_voiceLanguage, rows, _at.Language);
        // Supertonic has no language LEVEL — it is one model set that takes its
        // language per utterance, in the Language field further down this tab.
        // Hiding the row is the honest form of that: an empty dropdown would
        // read as "no languages", which is the opposite of true.
        RowOf(_voiceLanguage).IsVisible = rows.Count > 0;
        _loading = false;

        RebuildVoices();
    }

    private void RebuildVoices()
    {
        string engine = Selected(_engine) ?? _at.Engine;
        string language = Selected(_voiceLanguage) ?? _at.Language;
        var rows = VoicePicker.Voices(_installedVoices, engine, language);

        _loading = true;
        Fill(_voice, rows, _at.Voice);
        _loading = false;

        RebuildSpeakers();
        RebuildScopes();
        ApplyEngineRules();
    }

    /// <summary>
    /// The three scopes, named for the voice that is selected — "all Piper
    /// voices" and "only piper:en_GB-cori-high" are only meaningful once there
    /// is a voice to point at.
    /// </summary>
    private void RebuildScopes()
    {
        var (engine, voiceKey) = ScopeNames();
        string engineLabel = engine == "piper" ? "Piper" : "Supertonic";

        // The NAME the voice picker is showing, not the settings file's key for
        // it. "Only piper:en_GB-cori-high" is what the file needs to say and not
        // what a person choosing a scope needs to read.
        var rows = new List<PickerRow>
        {
            new(nameof(SettingsScopeKind.All), "All voices"),
            new(nameof(SettingsScopeKind.Engine), $"All {engineLabel} voices"),
            new(nameof(SettingsScopeKind.Voice), $"Only {ShortVoiceName(voiceKey)}"),
        };

        _loading = true;
        Fill(_scope, rows, Selected(_scope) ?? nameof(SettingsScopeKind.All));
        _loading = false;

        ApplyScope();
    }

    /// <summary>
    /// A voice's name as the picker above shows it, short enough to sit inside
    /// another sentence.
    ///
    /// <para>The voice rows carry more than a name — "cori-high · 2 speakers ·
    /// CC BY" — because a person choosing a VOICE wants the licence and the
    /// speaker count in front of them. A person choosing a SCOPE has already
    /// chosen the voice, so everything after the first separator is noise there.
    /// Falls back to the settings key, which is at least unambiguous.</para>
    /// </summary>
    private string ShortVoiceName(string fallback)
    {
        if ((_voice.SelectedItem as PickerRow)?.Label is not { Length: > 0 } label) return fallback;
        int sep = label.IndexOf('·');
        return (sep > 0 ? label[..sep] : label).Trim();
    }

    private SettingsScopeKind ScopeKind() =>
        Enum.TryParse(Selected(_scope), out SettingsScopeKind kind) ? kind : SettingsScopeKind.All;

    private (string Engine, string Voice) ScopeNames()
    {
        string voice = Selected(_voice) ?? _at.Voice;
        var id = VoiceId.Parse(voice.Length > 0 ? voice : "supertonic:M1");
        return (Selected(_engine) ?? _at.Engine, SettingsScope.KeyFor(id));
    }

    /// <summary>
    /// Show what the chosen scope resolves to, and say whether it has anything
    /// of its own.
    ///
    /// <para>Without that second half a user cannot tell an override from an
    /// inherited value, which is the difference between "this voice is slower"
    /// and "everything is slower" — and the first thing they would do about it
    /// is set a number that was already correct, pinning it forever.</para>
    /// </summary>
    private void ApplyScope()
    {
        if (_file is null) return;

        var (engine, voiceKey) = ScopeNames();
        var kind = ScopeKind();

        // Switching scope SHOULD change these boxes — each scope has its own
        // values — but not at the cost of throwing away an edit in progress. The
        // state holds both rules; the note below says which of the two is on
        // screen, so neither is a surprise.
        _state.Scoped(kind, engine, voiceKey);
        ShowFields();

        bool has = SettingsScope.Has(_file, kind, engine, voiceKey);
        _clearScope.IsVisible = kind != SettingsScopeKind.All;
        _clearScope.IsEnabled = has;

        if (_state.Touched)
        {
            _scopeNote.Text = "The boxes still show YOUR unsaved values, not this scope's. "
                            + "Save writes them here; Revert discards them and shows what is on disk.";
            return;
        }

        if (kind != SettingsScopeKind.All)
        {
            _scopeNote.Text = has
                ? "This scope has values of its own. Saving writes only what is set here."
                : "Nothing is set for this scope yet — what you see is inherited. Saving writes "
                  + "these values here, and they stop following the ones above.";
            return;
        }

        // A SAVE HERE THAT CHANGES NOTHING AUDIBLE IS THE WORST OUTCOME on this
        // tab. It used to be easy to reach — saving a scope wrote every field
        // into it, so one deliberate override left the whole tab shadowed for
        // that voice, and this is what reached the user as "piper does not obey
        // the speed change". SettingsScope.Save now keeps only what the scope
        // does not inherit, so a scope holds what somebody meant by it. This note
        // stays because a hand-edited file can still shadow anything, and because
        // a deliberate override IS a reason a global edit will not be heard.
        var shadowed = SettingsScope.Shadowed(_file, engine, voiceKey);
        _scopeNote.Text = shadowed.Count == 0
            ? "The values every voice starts from."
            : $"The values every voice starts from — but {voiceKey} does not take all of them: "
              + $"{string.Join(", ", shadowed)} {(shadowed.Count == 1 ? "is" : "are")} set for that "
              + "voice or its engine, so changing them here will not change how it sounds. Pick that "
              + "scope above to change them.";
    }

    /// <summary>
    /// Put the state on screen. The one place these boxes are written, and it
    /// takes no argument on purpose — see <see cref="_state"/>.
    /// </summary>
    private void ShowFields()
    {
        _loading = true;
        foreach (var (key, box) in _fields) box.Text = _state[key];
        _loading = false;
    }

    private async Task ClearScopeAsync()
    {
        if (_settingsPath is null) return;

        var (engine, voiceKey) = ScopeNames();
        JsonObject root;
        try { root = SettingsFile.Read(_settingsPath); }
        catch (Exception ex) { _status.Text = $"could not re-read the file: {ex.Message}"; return; }

        SettingsScope.Clear(root, ScopeKind(), engine, voiceKey);
        try { SettingsFile.Write(_settingsPath, root); }
        catch (Exception ex) { _status.Text = $"could not write {_settingsPath}: {ex.Message}"; return; }

        _status.Text = "overrides cleared — this scope follows the ones above it again.";
        await _client.SendAsync(new Request { Verb = RequestVerb.Reload });
        await RefreshAsync();
    }

    private void RebuildSpeakers()
    {
        var rows = VoicePicker.Speakers(_installedVoices, Selected(_voice) ?? "");

        _loading = true;
        Fill(_speaker, rows, _at.Speaker?.ToString() ?? "0");
        RowOf(_speaker).IsVisible = rows.Count > 0;
        _loading = false;
    }

    /// <summary>The id the settings file should hold, from where the cascade stands.</summary>
    private string ChosenVoice()
    {
        string voice = Selected(_voice) ?? "";
        int? speaker = null;
        if (RowOf(_speaker).IsVisible && int.TryParse(Selected(_speaker), out int sid)) speaker = sid;
        return VoicePicker.Compose(voice, speaker);
    }

    private static string? Selected(ComboBox box) => (box.SelectedItem as PickerRow)?.Value;

    /// <summary>
    /// Fill a level, keeping <paramref name="want"/> selected when it survived
    /// and falling to the first row when it did not — which is what happens when
    /// the level above changes and the old choice belongs to another engine.
    /// </summary>
    private static void Fill(ComboBox box, IReadOnlyList<PickerRow> rows, string want)
    {
        box.ItemsSource = rows;
        box.SelectedItem = rows.FirstOrDefault(r => r.Value == want) ?? rows.FirstOrDefault();
    }

    /// <summary>
    /// The row a control sits in, so a whole line can be hidden rather than left
    /// empty. Row() builds a two-column grid, so the control's parent IS it.
    /// </summary>
    private static Control RowOf(Control control) => control.Parent as Control ?? control;

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
        string? selected = Selected(_voice);
        bool piper = selected is not null
                     && VibeSuperTonic.Core.Synthesis.VoiceId.Parse(selected).Engine
                        == VibeSuperTonic.Core.Synthesis.VoiceEngine.Piper;

        _fields["Language"].IsEnabled = !piper;
        _fields["TotalStep"].IsEnabled = !piper;

        var entry = _installedVoices.FirstOrDefault(v =>
            selected is not null
            && VibeSuperTonic.Core.Synthesis.VoiceId.Parse(v.Id).WithSpeaker(null).ToString()
               == VibeSuperTonic.Core.Synthesis.VoiceId.Parse(selected).WithSpeaker(null).ToString());

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
        // From the whole cascade, not the voice row alone: a Piper voice with a
        // chosen speaker is saved as piper:<id>#<sid>.
        if (_voiceTouched && ChosenVoice() is { Length: > 0 } chosen) root.SetVoice(chosen);

        // WHERE the scoped values land. "All voices" is the file itself, which is
        // what every save did before this existed; the others are sections, made
        // only when something is actually written into them.
        var (scopeEngine, scopeVoice) = ScopeNames();
        var kind = ScopeKind();

        // WHAT THE SCOPE IS ASKED TO HOLD, collected before anything is written.
        // SettingsScope.Save then keeps only the values the scope does not
        // already inherit — see it for the report this answers: writing every
        // field into a scope pinned all nine of them, and left the "All voices"
        // boxes unable to change how that voice sounded.
        var scoped = new Dictionary<string, JsonNode?>();

        void Offer(string key, JsonNode? value)
        {
            // A GREYED-OUT BOX IS NOT AN ANSWER. Language and Model steps are
            // disabled for a Piper voice because it has neither, and saving them
            // anyway is how PerEngine.piper acquired the TotalStep that made the
            // benchmark warn about a step count nothing runs. Left untouched
            // rather than cleared: with "All voices" selected the box is still
            // disabled, and the file's own value is what Supertonic speaks at.
            if (!_fields[key].IsEnabled) return;

            if (SettingsScope.IsScoped(key)) scoped[key] = value;
            // Unscoped numbers always belong to the file: a thread budget or a
            // provider is about this machine, and a voice cannot have its own.
            else if (value is null) root.Remove(key);
            else root.Set(key, value);
        }

        Offer("Language", Text(_fields["Language"]));

        foreach (var (key, label, _) in Numbers)
        {
            string raw = _fields[key].Text?.Trim() ?? "";
            if (raw.Length == 0) { Offer(key, null); continue; }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                _status.Text = $"{label}: \"{raw}\" is not a number. Nothing was saved.";
                return;
            }

            // Integers stay integers on the wire. A TotalStep of 8.0 parses
            // everywhere but reads as a mistake in a file people open.
            Offer(key, value == Math.Floor(value)
                ? JsonValue.Create((long)value)
                : JsonValue.Create(value));
        }

        SettingsScope.Save(root, kind, scopeEngine, scopeVoice, scoped);

        root.Set("ClipboardFallback", JsonValue.Create(_clipboardFallback.IsChecked == true));
        root.Set("GpuOnBattery", JsonValue.Create(_gpuOnBattery.IsChecked == true));

        // Written even when it is "auto", which is the default: it is a setting a
        // person went looking for, and a key that vanishes when set back to its
        // default reads as a save that did not take.
        root.Set("Provider", JsonValue.Create((string)(_provider.SelectedItem ?? "auto")));

        try { SettingsFile.Write(_settingsPath, root); }
        catch (Exception ex) { _status.Text = $"could not write {_settingsPath}: {ex.Message}"; return; }

        _state.Committed();
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
    /// <summary>
    /// Run the sweep from outside this tab — the install banner's "Re-measure"
    /// button, which fires when a move or a new machine invalidated the profile.
    ///
    /// <para>Routed through the tab's own method rather than sending the verb
    /// directly, so the progress, the disabled button and the result all land in
    /// the one place that already renders them. A second caller with its own
    /// progress reporting would be a second opinion about the same sweep.</para>
    /// </summary>
    internal Task RunBenchmarkAsync() => BenchmarkAsync(force: false);

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// Where voices are found — installed above, the catalog below.
///
/// <para><b>Every row is a verb.</b> The rule this window is built on, and here
/// it is literal: <c>voices</c>, <c>voice install</c>, <c>voice remove</c>,
/// <c>speak --voice</c>. The install streams progress the way <c>benchmark</c>
/// already does, through the same <see cref="DaemonClient.SendStreamingAsync"/>,
/// so there is no client path here that <c>vst-ctl</c> does not have.</para>
///
/// <para><b>Use is the one exception, and it is the stated one.</b> Choosing a
/// voice writes <c>settings.json</c>, because configuration is a file with
/// exactly one writer — the same arrangement the Windows Control Panel and
/// engine have. It goes through <see cref="SettingsFile.SetVoice"/>, shared with
/// the Tune tab's picker so two writers cannot disagree: the qualified
/// <c>VoiceId</c> always, and <c>DefaultVoice</c> only for a Supertonic style, so
/// a Windows install reading the same file is kept in step for the one choice it
/// can honour and left alone for the one it cannot.</para>
///
/// <para><b>The licence is shown before the bytes arrive, and the download is
/// gated on it.</b> Same principle as the OpenRAIL-M screen, a different licence,
/// and a different one per voice — trap 7. The daemon refuses an install that
/// does not carry the acceptance, so this is not politeness on the client's
/// part: the checkbox is load-bearing.</para>
/// </summary>
public sealed class VoicesTab : UserControl
{
    private readonly DaemonClient _client;

    private readonly StackPanel _installed = new() { Spacing = 4 };
    private readonly StackPanel _available = new() { Spacing = 4 };
    private readonly TextBlock _status = Ui.Label("");
    private readonly TextBlock _notes = Ui.Body();
    private readonly TextBox _search = new() { Width = 200, Watermark = "search" };

    private readonly ComboBox _language = new() { Width = 200, MinWidth = 160 };
    private readonly ComboBox _quality = new()
    {
        Width = 120,
        ItemsSource = new[] { "any tier", "high", "medium", "low", "x_low" },
        SelectedIndex = 0,
    };

    /// <summary>The last reply, kept so filtering does not need a round trip.</summary>
    private VoicesPayload? _payload;

    /// <summary>Where settings.json is, learned from <c>config</c>. Null until the first refresh.</summary>
    private string? _settingsPath;

    /// <summary>One install at a time here as well as in the daemon, so the buttons can say so.</summary>
    private bool _busy;

    public VoicesTab(DaemonClient client)
    {
        _client = client;

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await RefreshAsync();

        _search.TextChanged += (_, _) => Render();
        _language.SelectionChanged += (_, _) => Render();
        _quality.SelectionChanged += (_, _) => Render();

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { refresh, _status },
                },
                Heading("INSTALLED"),
                _installed,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        Heading("AVAILABLE"),
                        _search,
                        _language,
                        _quality,
                    },
                },
                _available,
                _notes),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ------------------------------------------------------------------ refresh

    private async Task RefreshAsync()
    {
        var reply = await _client.SendAsync(new Request { Verb = RequestVerb.Voices });
        if (reply?.Voices is not { } payload)
        {
            _status.Text = "no daemon is running — the next hotkey press starts one.";
            _installed.Children.Clear();
            _available.Children.Clear();
            return;
        }

        _payload = payload;

        // Where to write a choice. Asked once per refresh rather than cached for
        // the life of the window: an AppImage's data directory is decided at
        // start and a user can move the store between runs.
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        _settingsPath = config?.Config is { } c ? Path.Combine(c.DataDir, "settings.json") : null;

        var languages = payload.Available
            .Select(v => v.Language)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .OrderBy(l => l, StringComparer.CurrentCulture)
            .ToList();
        languages.Insert(0, "all languages");

        string? chosen = _language.SelectedItem as string;
        _language.ItemsSource = languages;
        _language.SelectedItem = chosen is not null && languages.Contains(chosen) ? chosen : languages[0];

        _status.Text = $"{payload.Installed.Count} installed, {payload.Available.Count} available" +
                       (payload.CatalogRevision is { } rev ? $" (catalog pinned to {rev})" : "");

        Render();
    }

    private void Render()
    {
        _installed.Children.Clear();
        _available.Children.Clear();

        if (_payload is not { } payload) return;

        foreach (var v in payload.Installed) _installed.Children.Add(InstalledRow(v));

        string search = _search.Text?.Trim() ?? "";
        string language = _language.SelectedItem as string ?? "all languages";
        string quality = _quality.SelectedItem as string ?? "any tier";

        var shown = payload.Available.Where(v =>
            (language == "all languages" || v.Language == language) &&
            (quality == "any tier" || v.Quality == quality) &&
            (search.Length == 0
             || v.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
             || (v.Language ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)));

        foreach (var v in shown) _available.Children.Add(AvailableRow(v));

        if (_available.Children.Count == 0 && payload.Available.Count > 0)
            _available.Children.Add(Ui.Label("nothing matches that filter."));

        _notes.Text = string.Join("\n", (payload.Notes ?? Array.Empty<string>())
            .Prepend($"store: {payload.StoreRoot}"));
    }

    // --------------------------------------------------------------------- rows

    private Control InstalledRow(VoiceEntry v)
    {
        var bits = new List<string> { v.Engine };
        if (v.Quality is { } q) bits.Add(q);
        if (v.Language is { } lang) bits.Add(lang);
        if (v.SampleRate is { } rate) bits.Add($"{rate / 1000} kHz");
        if (v.Bytes is > 0) bits.Add($"{v.Bytes / (1024 * 1024)} MB");
        if (v.Licence is { } lic) bits.Add(lic);

        // "styles" and "speakers" are not the same thing and the row should not
        // pretend they are: a style is a separate voice file, a speaker is a sid
        // inside one graph.
        if (v.Speakers is > 1)
            bits.Add($"{v.Speakers} {(v.Engine == "supertonic" ? "styles" : "speakers")}");

        // Worth its own mark rather than a footnote: until the curve is measured
        // a requested rate is served by the reciprocal and can be 20% out, which
        // is audible and would otherwise have no name.
        if (v.Calibrated == false) bits.Add("not yet calibrated");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = v.IsDefault ? "●" : "○",
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = v.Id,
                    Width = 260,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = v.IsDefault ? FontWeight.Bold : FontWeight.Normal,
                },
                Ui.Label(string.Join(" · ", bits)),
            },
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        if (!v.IsDefault)
        {
            var use = new Button { Content = "Use" };
            use.Click += async (_, _) => await UseAsync(v);
            buttons.Children.Add(use);
        }

        var sample = new Button { Content = "Sample" };
        sample.Click += async (_, _) => await _client.SendAsync(new Request
        {
            Verb = RequestVerb.Speak,
            Voice = v.Id,
            Text = SampleText(v),
        });
        buttons.Children.Add(sample);

        // Supertonic has no Remove: it is the product's default engine and one
        // shared model set, and a button that deleted 383 MB of it from a voice
        // list would be a different feature wearing this one's clothes.
        if (v.Engine == "piper")
        {
            var remove = new Button { Content = "Remove" };
            remove.Click += async (_, _) => await RemoveAsync(v);
            buttons.Children.Add(remove);
        }

        row.Children.Add(buttons);
        return row;
    }

    private Control AvailableRow(VoiceEntry v)
    {
        var licence = new TextBlock
        {
            Text = v.LicenceClass == "nc" ? $"{v.Licence} — NON-COMMERCIAL" : v.Licence ?? "",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            // The one licence fact that constrains what the USER may do rather
            // than what we may do, so it is the one that gets a colour.
            Foreground = v.LicenceClass == "nc" ? Brushes.OrangeRed : null,
        };

        var progress = new ProgressBar { Width = 160, Minimum = 0, Maximum = 1, IsVisible = false };
        var progressText = Ui.Label("");

        var install = new Button { Content = "Download" };
        install.Click += async (_, _) => await InstallAsync(v, install, progress, progressText);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = v.Id, Width = 274, VerticalAlignment = VerticalAlignment.Center },
                Ui.Label($"{v.Quality} · {v.SampleRate / 1000} kHz · {v.Bytes / (1024 * 1024)} MB"),
                licence,
                install,
                progress,
                progressText,
            },
        };
    }

    /// <summary>
    /// What Sample says. A Piper voice is one language and cannot be asked for
    /// another, so an English sentence out of a Ukrainian voice would demonstrate
    /// espeak-ng's fallback rather than the voice.
    /// </summary>
    private static string SampleText(VoiceEntry v) =>
        v.Engine == "supertonic"
            ? "This is the Supertonic voice."
            : $"This is {v.Name}.";

    // ------------------------------------------------------------------ actions

    private async Task UseAsync(VoiceEntry v)
    {
        if (_settingsPath is not { } path)
        {
            _status.Text = "cannot write settings — no daemon answered `config`.";
            return;
        }

        try
        {
            var root = SettingsFile.Read(path);

            // One helper, shared with the Tune tab's picker, so the two writers
            // cannot disagree about what choosing a voice means. See SetVoice for
            // why DefaultVoice moves for a Supertonic style and not for a Piper
            // voice.
            root.SetVoice(v.Id);
            SettingsFile.Write(path, root);
        }
        catch (Exception ex)
        {
            _status.Text = $"could not write {path}: {ex.Message}";
            return;
        }

        // No verb needed: the daemon notices the mtime and re-reads. The refresh
        // is so the dot moves now rather than at the next press.
        _status.Text = $"{v.Id} will be used from the next utterance.";
        await RefreshAsync();
    }

    private async Task RemoveAsync(VoiceEntry v)
    {
        var reply = await _client.SendAsync(new Request
        {
            Verb = RequestVerb.VoiceRemove,
            Voice = v.Id,
        });

        // The daemon refuses to remove the configured default, and that refusal
        // is the useful one — it is shown rather than swallowed, and the fix is
        // to click Use on another row first.
        _status.Text = reply?.Voice?.Message ?? reply?.Error ?? "no daemon answered.";
        await RefreshAsync();
    }

    private async Task InstallAsync(VoiceEntry v, Button install, ProgressBar bar, TextBlock text)
    {
        if (_busy)
        {
            _status.Text = "one download at a time.";
            return;
        }

        if (!await ConfirmLicenceAsync(v)) return;

        _busy = true;
        install.IsEnabled = false;
        install.Content = "Downloading…";
        bar.IsVisible = true;

        try
        {
            var final = await _client.SendStreamingAsync(
                new Request
                {
                    Verb = RequestVerb.VoiceInstall,
                    Voice = v.Id,
                    AcceptLicence = true,
                },
                reply => Dispatcher.UIThread.Post(() =>
                {
                    if (reply.VoiceProgress is not { } p) return;

                    if (p.Message is { } message) { text.Text = message; return; }
                    if (p.BytesTotal <= 0) return;

                    bar.Value = (double)p.BytesReceived / p.BytesTotal;
                    text.Text = $"{p.BytesReceived / (1024 * 1024)}/{p.BytesTotal / (1024 * 1024)} MB";
                }));

            if (final is null)
            {
                _status.Text = "the daemon closed the connection mid-download.";
                return;
            }

            _status.Text = final.Voice?.Message ?? final.Error ?? "install finished.";

            // Calibration continues in the background after the reply, so the row
            // is refreshed and the notice kept: the voice works now, and its rate
            // is approximate for the next few seconds.
            if (final.Notice is { } notice) _status.Text += "  " + notice;
        }
        finally
        {
            _busy = false;
            install.IsEnabled = true;
            install.Content = "Download";
            bar.IsVisible = false;
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Show this voice's terms and require an explicit acceptance.
    ///
    /// <para>Trap 7, in front of the user rather than in a comment: the engine's
    /// licence and the voices' are two different axes, each voice differs, and
    /// three of the ones on offer are NonCommercial. The daemon refuses an
    /// install without the acceptance flag, so this dialog is the only thing that
    /// can honestly set it.</para>
    /// </summary>
    private async Task<bool> ConfirmLicenceAsync(VoiceEntry v)
    {
        var accepted = new TaskCompletionSource<bool>();

        var agree = new CheckBox { Content = "I accept these terms" };
        var download = new Button { Content = "Download", IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };

        agree.IsCheckedChanged += (_, _) => download.IsEnabled = agree.IsChecked == true;

        var dialog = new Window
        {
            Title = $"{v.Id} — licence",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var body = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"{v.Id} — {v.Language}, {v.Quality}, " +
                           $"{v.Bytes / (1024 * 1024)} MB at {v.SampleRate / 1000} kHz",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = v.Licence ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = v.LicenceUrl ?? "",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                },
                new TextBlock
                {
                    Text = v.LicenceClass == "nc"
                        ? "This voice is licensed for NON-COMMERCIAL use only. That is a term on "
                          + "what you may do with the audio it produces, not on this program."
                        : "The voice's terms are its own, and separate from this program's licence.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = v.LicenceClass == "nc" ? Brushes.OrangeRed : null,
                },
                agree,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, download },
                },
            },
        };

        dialog.Content = body;
        download.Click += (_, _) => { accepted.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { accepted.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => accepted.TrySetResult(false);

        if (TopLevel.GetTopLevel(this) is Window owner) await dialog.ShowDialog(owner);
        else dialog.Show();

        return await accepted.Task;
    }
}

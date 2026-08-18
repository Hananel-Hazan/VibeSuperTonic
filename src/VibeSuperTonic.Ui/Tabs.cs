using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The four tabs that are views over <c>status</c> and <c>config</c>. They share
/// a file because they share a shape — ask a verb, render the reply — and
/// splitting four thin readers across four files would suggest four ideas.
/// </summary>
internal static class Ui
{
    public static TextBlock Label(string text) => new()
    {
        Text = text,
        Opacity = 0.7,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Body(string text = "") => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("monospace"),
    };

    public static StackPanel Page(params Control[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
}

/// <summary>
/// What this daemon is, where it read its configuration, and what it made of it.
/// Monitor folded into here: there is one daemon, and "which host is speaking"
/// stopped being a question when the N SAPI hosts went away.
/// </summary>
public sealed class StatusTab : UserControl
{
    private readonly DaemonClient _client;
    private readonly ReaderTab _reader;
    private readonly TextBlock _body = Ui.Body();

    public StatusTab(DaemonClient client, ReaderTab reader)
    {
        _client = client;
        _reader = reader;

        var refresh = new Button { Content = "Refresh", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await RefreshAsync();

        Content = new ScrollViewer { Content = Ui.Page(refresh, _body) };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var status = await _client.SendAsync(new Request { Verb = RequestVerb.Status });
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });

        if (status?.Status is not { } s || config?.Config is not { } c)
        {
            _body.Text = "no daemon is running.\n\n"
                       + "the next hotkey press starts one; this window reconnects by itself.";
            return;
        }

        string benchmark = c.Benchmark is { } b
            ? $"{b}"
            : "never measured on this machine — run `vst-ctl benchmark`";

        // The reason travels with the number. "cpu, 2 threads (benchmark
        // 2026-08-17)" and "cpu, 4 threads (20% of 20 logical processors, never
        // benchmarked)" are the same field answered two different ways, and only
        // one of them is a measurement.
        _body.Text = $"""
            daemon      {s.Version}, {s.State}{(s.Paused ? " (paused)" : "")}
            model       {(s.ModelLoaded ? "loaded" : "not loaded yet")}
            voice       {s.Voice} / {s.Language}

            inference   {c.Provider}, {c.IntraOpThreads} threads ({c.ThreadsReason})
            benchmark   {benchmark}

            base dir    {c.BaseDir}
            data dir    {c.DataDir}{(c.DataDirWritable ? "" : "   (NOT writable)")}
            models      {c.ModelsRoot}
            settings    {(c.SettingsFound ? "found" : "not found — defaults in use")}
            rules       {c.RuleCount} loaded, {(c.RulesEnabled ? "applied" : "disabled")}

            chunking    {c.MinChunkChars}–{c.MaxChunkChars} chars, {c.InterChunkSilenceMs} ms between chunks
            steps       {c.TotalStep}

            acknowledge → painted   {Latency()}
            {string.Join("\n", c.Notes.Select(n => "note        " + n))}
            """;
    }

    /// <summary>
    /// The half of Phase 6's latency criterion this process can see. The other
    /// half — press → acknowledge — was measured at 28 ms in Phase 3 and belongs
    /// to the daemon. Two numbers that can each be wrong on their own beat one
    /// number nothing can check.
    /// </summary>
    private string Latency() => double.IsNaN(_reader.LastPaintLatencyMs)
        ? "nothing has been read yet this session"
        : $"{_reader.LastPaintLatencyMs:F1} ms (last utterance)";
}

/// <summary>
/// The knobs. Read-only for now, and honestly so — the writer this tab needs is
/// the second writer of <c>settings.json</c>, which is why
/// <c>[JsonExtensionData]</c> went onto the Linux settings type before anything
/// here could erase a Windows key.
/// </summary>
public sealed class TuneTab : UserControl
{
    private readonly DaemonClient _client;
    private readonly TextBlock _body = Ui.Body();

    public TuneTab(DaemonClient client)
    {
        _client = client;

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
                Ui.Label("Settings are edited in data/settings.json. The daemon re-reads it "
                       + "when the file changes, so an edit applies without restarting anything."),
                _body,
                benchmark,
                Ui.Label("Disabled until Phase 8 wires it. The measurement itself works today: "
                       + "run `vst-ctl benchmark`, then `vst-ctl shutdown` to apply it.")),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is not { } c) { _body.Text = "no daemon is running."; return; }

        _body.Text = $"""
            voice           {c.Voice}
            language        {c.Language}
            steps           {c.TotalStep}
            chunk           {c.MinChunkChars}–{c.MaxChunkChars} characters
            chunk silence   {c.InterChunkSilenceMs} ms
            inference       {c.Provider}, {c.IntraOpThreads} threads ({c.ThreadsReason})
            """;
    }
}

/// <summary>
/// The pronunciation rules, and the one verb that makes an edit take effect
/// without waiting for an mtime poll.
/// </summary>
public sealed class PronunciationsTab : UserControl
{
    private readonly DaemonClient _client;
    private readonly TextBlock _body = Ui.Body();

    public PronunciationsTab(DaemonClient client)
    {
        _client = client;

        var reload = new Button { Content = "Reload", HorizontalAlignment = HorizontalAlignment.Left };
        reload.Click += async (_, _) =>
        {
            await _client.SendAsync(new Request { Verb = RequestVerb.Reload });
            await RefreshAsync();
        };

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                Ui.Label("Rules live in data/pronunciations.json and apply from the next "
                       + "utterance, never the one in flight."),
                _body,
                reload),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var config = await _client.SendAsync(new Request { Verb = RequestVerb.Config });
        if (config?.Config is not { } c) { _body.Text = "no daemon is running."; return; }

        _body.Text = $"""
            file      {Path.Combine(c.DataDir, "pronunciations.json")}
            state     {(c.PronunciationsFound ? "found" : "not found")}
            rules     {c.RuleCount}, {(c.RulesEnabled ? "applied" : "disabled")}
            """;
    }
}

/// <summary>What this is and how it is driven.</summary>
public sealed class AboutTab : UserControl
{
    public AboutTab()
    {
        string version = typeof(AboutTab).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                new TextBlock { Text = "VibeSuperTonic", FontSize = 20, FontWeight = FontWeight.SemiBold },
                Ui.Body($"""
                    window      {version}

                    Select text anywhere and press the hotkey. The engine loads itself on the
                    first press and stays resident afterwards; nothing starts at login.

                    This window is a client. Everything it does, `vst-ctl` does too — and
                    the tray icon belongs to the engine, so it is there whether this window
                    is open or not.
                    """))
        };
    }
}

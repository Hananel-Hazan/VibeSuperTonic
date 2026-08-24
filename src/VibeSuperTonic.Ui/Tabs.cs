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

/// <summary>What this is and how it is driven.</summary>
public sealed class AboutTab : UserControl
{
    /// <summary>
    /// Kept in step with the Windows Control Panel's About tab, deliberately:
    /// two products out of one repository, and a user who reads one of them
    /// should not learn something different from the other. What differs between
    /// the two screens is only what is genuinely different — how the engine is
    /// hosted, and which GPU providers exist on that platform.
    ///
    /// <para>What is IN FORCE lives on the Status tab, which asks the daemon.
    /// This screen says what the product is, and it must not paraphrase a live
    /// value: an About tab claiming "CUDA" while the machine is on battery would
    /// be wrong in exactly the way the provider work exists to prevent.</para>
    /// </summary>
    public AboutTab()
    {
        string version = typeof(AboutTab).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                new TextBlock { Text = "VibeSuperTonic", FontSize = 20, FontWeight = FontWeight.SemiBold },
                Ui.Body($"""
                    Reads what you select, out loud, in a neural voice that runs entirely on
                    this machine. Nothing is sent anywhere.

                    window      {version}

                    Select text anywhere and press the hotkey. The engine loads itself on the
                    first press and stays resident afterwards; nothing starts at login.

                    This window is a client. Everything it does, `vst-ctl` does too — and
                    the tray icon belongs to the engine, so it is there whether this window
                    is open or not.

                    SPEED. The engine measures this machine and uses what it measured:
                    `vst-ctl benchmark`, or the button on the Tune tab, tries every thread
                    count worth trying and keeps the whole table in data/benchmark.json. It
                    applies to the next thing you ask it to read — no restart.

                    An NVIDIA GPU can do the work instead, and on the machine this was
                    developed on it takes the wait before the first word from 802 ms to
                    77 ms. It is not in the download: run ./install-gpu.sh once, which
                    fetches about 3.1 GB, then benchmark again. ON BATTERY THE ENGINE STAYS
                    ON THE CPU — a discrete GPU is the difference between a laptop that
                    lasts an afternoon and one that does not — unless you set GpuOnBattery.
                    The Status tab says which is in force and why.

                    LICENCES. The program is MIT. The voice models are not part of it: they
                    are downloaded from Hugging Face on first run and are distributed by
                    Supertone, Inc. under the OpenRAIL-M licence, which you accepted on the
                    first-run screen. See LICENSE-MODELS.txt beside the program.

                    https://github.com/Hananel-Hazan/VibeSuperTonic
                    """))
        };
    }
}

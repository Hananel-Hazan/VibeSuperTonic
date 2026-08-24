using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VibeSuperTonic.Core.Models;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The screen a fresh install opens on: the licence, the download, and the two
/// keys.
///
/// <para><b>Why one screen does all three.</b> Nothing autostarts — no
/// <c>~/.config/autostart</c> entry at all — so there is no tray icon until the
/// first press, and a person who has just unpacked a folder has been told
/// nothing. This is the moment they are definitely paying attention, and it is
/// also the only honest place for the OpenRAIL-M acceptance: the models are
/// fetched from Hugging Face rather than redistributed, and the licence has to
/// be a human agreeing to something rather than a step a script performed.</para>
///
/// <para><b>Triggered on "no models present", never on a marker file.</b> The
/// daemon already makes exactly that check, it is self-healing if the folder is
/// copied to another machine, and it needs no write — which matters, because
/// Phase 4b measured a legitimate read-only data directory and a marker file
/// would either fail there or re-show this screen forever.</para>
/// </summary>
public sealed class FirstRunView : UserControl
{
    private readonly TextBlock _log = Ui.Body();
    private readonly ScrollViewer _logScroller;
    private readonly Button _download;
    private readonly CheckBox _accept;

    /// <summary>Raised once the models are on disk, so the window can become the Reader.</summary>
    public event Action? Completed;

    /// <param name="programRoot">
    /// Where <c>models-manifest.json</c> ships — beside the binaries, which for
    /// an AppImage is a read-only mount.
    /// </param>
    /// <param name="storeRoot">
    /// Where the bytes go. The same directory as <paramref name="programRoot"/>
    /// for every install that is not an AppImage; the manifest's paths are
    /// relative to it.
    /// </param>
    /// <param name="modelsRoot">Shown to the user, so they know what is about to fill up.</param>
    public FirstRunView(string programRoot, string storeRoot, string modelsRoot)
    {
        _accept = new CheckBox
        {
            Content = "I accept Supertone's OpenRAIL-M licence for the Supertonic models",
        };

        _download = new Button
        {
            Content = "Download the voices (about 400 MB)",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _accept.IsCheckedChanged += (_, _) => _download.IsEnabled = _accept.IsChecked == true;
        _download.Click += async (_, _) => await DownloadAsync(programRoot, storeRoot);

        _logScroller = new ScrollViewer { Content = _log, Height = 180 };

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                new TextBlock { Text = "VibeSuperTonic", FontSize = 22, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Select text anywhere and press a key; this reads it aloud.",
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                },

                Section("The two keys", """
                    Ctrl + `          read what is selected now, interrupting anything playing
                    Ctrl + Shift + `  stop

                    The reading key never means stop. Pressing it while something is being read
                    starts the new selection, which is the obvious thing and saves a press; that
                    is only safe because stop has a key of its own.
                    """),

                Section("Nothing starts at login", """
                    The engine loads itself on the first press and stays resident afterwards, so
                    the first press of a session waits a moment for the model and the rest do not.
                    There is no autostart entry and no tray icon until then.

                    Everything this window does, `vst-ctl` also does — and the tray menu's last
                    item releases the engine's memory without disabling anything: the next press
                    brings it straight back.
                    """),

                Section("The voices", $"""
                    The models are not shipped with this folder. They are downloaded directly from
                    Hugging Face — about 400 MB, into {modelsRoot} — and checked against pinned
                    sha256 hashes, so nothing else can be substituted for them.

                    They are Supertone's, distributed under the OpenRAIL-M licence:
                    https://huggingface.co/Supertone/supertonic-3
                    """),

                _accept,
                _download,
                _logScroller),
        };
    }

    private static Control Section(string heading, string body) => new StackPanel
    {
        Spacing = 4,
        Margin = new Thickness(0, 8, 0, 0),
        Children =
        {
            new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
        },
    };

    private async Task DownloadAsync(string programRoot, string storeRoot)
    {
        _download.IsEnabled = false;
        _accept.IsEnabled = false;

        var manifest = Manifest.TryLoad(programRoot);
        if (manifest is null)
        {
            // The manifest ships beside the binaries. Saying which file is
            // missing beats "download failed", because the fix is to unpack the
            // release properly rather than to try again.
            Write($"models-manifest.json was not found in {programRoot}, so there is nothing to fetch.");
            Write("This folder is not a complete release — unpack the published archive and run it from there.");
            return;
        }

        Write($"{manifest.Files.Count} files to fetch. This takes a few minutes on a home connection.");

        // The same downloader the Windows launcher uses, from Core: one
        // implementation of resume, mirrors and hash checking rather than a
        // second one written for this window.
        var downloader = new ModelDownloader(storeRoot, manifest);
        var progress = new Progress<string>(Write);

        try
        {
            bool ok = await downloader.EnsureAllAsync(progress, CancellationToken.None);
            if (ok)
            {
                Write("");
                Write("Done. Press Ctrl + ` with something selected.");
                Completed?.Invoke();
                return;
            }

            Write("");
            Write("Some files could not be fetched. Nothing was left half-written — the hashes are "
                + "checked before a file counts as present, so running this again resumes.");
        }
        catch (Exception ex)
        {
            Write($"{ex.GetType().Name}: {ex.Message}");
        }

        _accept.IsEnabled = true;
        _download.IsEnabled = _accept.IsChecked == true;
    }

    private void Write(string line) => Dispatcher.UIThread.Post(() =>
    {
        _log.Text = _log.Text is { Length: > 0 } existing ? $"{existing}\n{line}" : line;
        _logScroller.ScrollToEnd();
    });
}

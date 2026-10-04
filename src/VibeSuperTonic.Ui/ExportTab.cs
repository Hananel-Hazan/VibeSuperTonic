using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VibeSuperTonic.Core.Export;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Ui;

/// <summary>
/// Render text to an audio file instead of the speakers. The Linux counterpart of
/// the Windows launcher's Export tab: type or load text, pick a voice and a
/// format, watch it render, cancel if it was a mistake.
///
/// <para><b>It goes through the daemon, like everything here.</b> The tab sends
/// <c>render</c> — the verb <c>vst-ctl render</c> and the Speech Dispatcher module
/// use — so the voice, speed, pronunciations and volume are exactly what the
/// hotkey would speak with. Nothing is synthesised in this process.</para>
///
/// <para><b>Formats.</b> WAV is written here. MP3, AAC and FLAC are encoded by
/// the system's ffmpeg, found at runtime (see <see cref="ExportFormats"/> for why
/// nothing is bundled); a format the machine cannot encode is greyed out with
/// the reason beside it rather than failing after a long render. A snap or
/// Flatpak cannot see the host's ffmpeg and says so.</para>
///
/// <para><b>The file is whole or absent</b> — <see cref="ExportRunner"/> writes a
/// sibling temp file and renames it, so Cancel leaves nothing behind and an
/// existing file at the destination survives a failed export.</para>
///
/// <para>Preview speaks the first few sentences through the daemon's ordinary
/// <c>speak</c> verb, which is the same audio the file will contain.</para>
/// </summary>
public sealed class ExportTab : UserControl
{
    /// <summary>A text file larger than this is refused rather than read into a text box.</summary>
    private const long MaxTextFileBytes = 5 * 1024 * 1024;

    /// <summary>Preview is a taste, not the document.</summary>
    private const int PreviewChars = 400;

    private readonly DaemonClient _client;
    private readonly ReaderTab _reader;

    private readonly TextBox _text = new()
    {
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        MinHeight = 160,
        Watermark = "Type or paste text, or load a text file…",
    };
    private readonly ComboBox _voice = new() { Width = 320 };
    private readonly ComboBox _format = new() { Width = 260 };
    private readonly TextBlock _formatNote = Ui.Label("");
    private readonly TextBlock _status = Ui.Label("");
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, IsVisible = false };
    private readonly Button _export = new() { Content = "Export…" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _preview = new() { Content = "Preview" };
    private readonly Button _stopPreview = new() { Content = "Stop preview" };

    private const string DefaultVoiceLabel = "Default voice (from Tune)";
    private List<VoiceEntry> _voices = [];
    private FfmpegTools? _ffmpeg;
    private bool _sandboxed;

    /// <summary>The render in flight, so Cancel can wake its blocked read.</summary>
    private RenderSession? _session;
    private volatile bool _cancelRequested;

    public ExportTab(DaemonClient client, ReaderTab reader)
    {
        _client = client;
        _reader = reader;

        _format.ItemsSource = Enum.GetValues<ExportFormat>().ToList();
        _format.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ExportFormat>(
            (f, _) => new TextBlock { Text = ExportFormats.DisplayName(f) + Suffix(f) });
        _format.SelectedIndex = 0;
        _format.SelectionChanged += (_, _) => UpdateFormatNote();

        var load = new Button { Content = "Load text file…" };
        load.Click += async (_, _) => await LoadFileAsync();

        var useReader = new Button { Content = "Use the last text read" };
        useReader.Click += (_, _) =>
        {
            if (_reader.Document.Length > 0) _text.Text = _reader.Document;
            else _status.Text = "Nothing has been read in this window yet.";
        };

        _export.Click += async (_, _) => await ExportAsync();
        _cancel.Click += (_, _) => Cancel();
        _preview.Click += async (_, _) => await PreviewAsync();
        _stopPreview.Click += async (_, _) => await _client.SendAsync(new Request { Verb = RequestVerb.Stop });

        Content = new ScrollViewer
        {
            Content = Ui.Page(
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { load, useReader } },
                _text,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { Ui.Label("Voice"), _voice, Ui.Label("Format"), _format },
                },
                _formatNote,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _export, _cancel, _preview, _stopPreview },
                },
                _progress,
                _status,
                Ui.Label("Speed, volume and pronunciations come from the Tune and Pronunciations tabs, as they do when reading.")),
        };

        AttachedToVisualTree += async (_, _) => await RefreshAsync();
    }

    // -------------------------------------------------------------- the machine

    private string Suffix(ExportFormat f) =>
        FfmpegTools.Unavailable(f, _ffmpeg, _sandboxed) is null ? "" : "  — unavailable";

    /// <summary>Re-ask what this machine can do. On every show, so installing ffmpeg needs no restart.</summary>
    private async Task RefreshAsync()
    {
        _sandboxed = FfmpegDetector.IsSandboxed();
        _ffmpeg = await Task.Run(FfmpegDetector.Detect);

        // Rebuild the items so the "unavailable" suffixes re-render.
        int keep = Math.Max(0, _format.SelectedIndex);
        _format.ItemsSource = null;
        _format.ItemsSource = Enum.GetValues<ExportFormat>().ToList();
        _format.SelectedIndex = keep;
        UpdateFormatNote();

        var reply = await _client.SendAsync(new Request { Verb = RequestVerb.Voices });
        _voices = reply?.Voices?.Installed.ToList() ?? [];
        string? chosen = _voice.SelectedItem as string;
        _voice.ItemsSource = new[] { DefaultVoiceLabel }.Concat(_voices.Select(v => v.Id)).ToList();
        _voice.SelectedItem = chosen is not null && (chosen == DefaultVoiceLabel || _voices.Any(v => v.Id == chosen))
            ? chosen
            : DefaultVoiceLabel;
    }

    private ExportFormat ChosenFormat => _format.SelectedItem is ExportFormat f ? f : ExportFormat.Wav;

    private void UpdateFormatNote()
    {
        _formatNote.Text = FfmpegTools.Unavailable(ChosenFormat, _ffmpeg, _sandboxed) ?? "";
        _formatNote.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
    }

    private string? ChosenVoice =>
        _voice.SelectedItem is string s && s != DefaultVoiceLabel ? s : null;

    // ----------------------------------------------------------------- loading

    private async Task LoadFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a text file",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Text") { Patterns = ["*.txt", "*.md", "*.text"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > MaxTextFileBytes)
            {
                _status.Text = $"{files[0].Name} is larger than {MaxTextFileBytes / (1024 * 1024)} MB; split it first.";
                return;
            }
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[(int)MaxTextFileBytes + 1];
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > MaxTextFileBytes)
            {
                _status.Text = $"{files[0].Name} is larger than {MaxTextFileBytes / (1024 * 1024)} MB; split it first.";
                return;
            }
            _text.Text = new string(buffer, 0, read);
            _status.Text = $"Loaded {files[0].Name} ({read:N0} characters).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not read {files[0].Name}: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- preview

    private async Task PreviewAsync()
    {
        string text = (_text.Text ?? "").Trim();
        if (text.Length == 0) { _status.Text = "Nothing to preview — the text is empty."; return; }
        if (text.Length > PreviewChars) text = text[..PreviewChars];

        var reply = await _client.SendAsync(new Request
        {
            Verb = RequestVerb.Speak,
            Voice = ChosenVoice,
            Text = text,
        });

        _status.Text = reply is null ? "No daemon is running."
            : reply.Ok ? "Previewing the first part of the text…"
            : reply.Error ?? "The daemon refused to speak.";
    }

    // ----------------------------------------------------------------- export

    private async Task ExportAsync()
    {
        string text = _text.Text ?? "";
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = "Nothing to export — the text is empty."; return; }

        ExportFormat format = ChosenFormat;
        if (FfmpegTools.Unavailable(format, _ffmpeg, _sandboxed) is { } why) { _status.Text = why; return; }

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        string ext = ExportFormats.Extension(format);
        var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export audio",
            SuggestedFileName = "speech" + ext,
            DefaultExtension = ext.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(ExportFormats.ShortName(format)) { Patterns = ["*" + ext] }],
        });
        if (target?.TryGetLocalPath() is not { } path) return;

        // The extension decides what the player sees; keep it honest if the user
        // typed a different one.
        if (!string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase))
            path += ext;

        var session = _client.OpenRender(new Request
        {
            Verb = RequestVerb.Render,
            Voice = ChosenVoice,
            Text = text,
        });
        if (session is null) { _status.Text = "No daemon is running. Press the hotkey once, or start it from the banner."; return; }

        _session = session;
        _cancelRequested = false;
        SetBusy(true);
        _status.Text = "Rendering…";

        var ffmpeg = _ffmpeg;
        int rate = (_voices.FirstOrDefault(v => v.Id == ChosenVoice) ?? _voices.FirstOrDefault(v => v.IsDefault))
            ?.SampleRate ?? 22050;
        ExportOutcome outcome = await Task.Run(() =>
        {
            try
            {
                return ExportRunner.Run(
                    session.ReadLine, format, path, ffmpeg,
                    () => _cancelRequested,
                    samples => Dispatcher.UIThread.Post(() =>
                        _status.Text = $"Rendering… {samples / (double)rate:F0} s of audio so far"));
            }
            finally
            {
                session.Dispose();
            }
        });

        _session = null;
        SetBusy(false);

        _status.Text = outcome.Status switch
        {
            ExportStatus.Done => $"Saved {path}",
            ExportStatus.Cancelled => "Cancelled. No file was written.",
            _ => $"Export failed: {outcome.Message}",
        };
    }

    private void Cancel()
    {
        _cancelRequested = true;
        _status.Text = "Cancelling…";
        _session?.Abort();
    }

    private void SetBusy(bool busy)
    {
        _export.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
        _preview.IsEnabled = !busy;
        _text.IsEnabled = !busy;
        _progress.IsVisible = busy;
    }
}

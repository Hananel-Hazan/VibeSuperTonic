using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The selection, with the word being spoken highlighted, and a click anywhere in
/// it to start again from there.
///
/// <para><b>Why a slice and not the whole document.</b> R-9 caps a selection at
/// 100 KB and the truncation Phase 4 measured was 102,338 characters. Handing
/// that to one control and moving <c>SelectionStart</c> was measured at
/// <b>110 ms per move</b> — <c>spike/avalonia-bigtext</c>, 2026-08-18 — because
/// changing a selection invalidates a visual the size of the document, and
/// rebuilding <c>Inlines</c> instead was no better at 95 ms. A highlight moves
/// three to four times a second, so both spend most of a 250 ms budget on one
/// word, and both look perfect on a test paragraph.</para>
///
/// <para>So the control is only ever given <see cref="WindowChars"/> characters
/// around the current offset: 14 ms per move at the same cap, and the cost stops
/// depending on how much the user selected. The slice is re-cut only when the
/// offset approaches its edge, which the measurement did not assume — the real
/// thing is cheaper than the number above.</para>
///
/// <para>Everything here is in <em>whole-text</em> coordinates on the wire.
/// <see cref="_sliceStart"/> is the only place the local ones exist, and it is
/// added back before anything leaves this class.</para>
/// </summary>
public sealed class ReaderTab : UserControl
{
    /// <summary>
    /// How much of the document the control holds. 12,000 measured at 14 ms
    /// median / 84 ms p95 per move against a 250 ms budget; 25,000 costs 155 ms
    /// p95 and 50,000 costs 159 ms with a 301 ms tail. This is three to four
    /// screenfuls, so scrolling around the word being read stays local.
    /// </summary>
    private const int WindowChars = 12_000;

    /// <summary>
    /// How close the offset may come to the slice's edge before it is re-cut.
    /// Without it the slice moves on every word and pays its layout cost every
    /// time, which is the measurement's assumption rather than its conclusion.
    /// </summary>
    private const int ReslicMargin = 3_000;

    /// <summary>A short history, bounded here because nothing else will bound it.</summary>
    private const int HistoryLimit = 20;

    private readonly DaemonClient _client;
    private readonly SelectableTextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        SelectionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
        FontSize = 15,
        LineHeight = 24,
    };

    private readonly ScrollViewer _scroller;
    private readonly TextBlock _notice = new() { Foreground = Brushes.Goldenrod, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold };
    private readonly ListBox _history = new() { Height = 120 };

    /// <summary>The whole utterance, in the coordinates every event uses.</summary>
    private string _document = "";
    private int _sliceStart;
    private int _offset;
    private int _length;

    /// <summary>
    /// Set when a Preparing transition arrives, read when the paint that shows it
    /// returns. Phase 6's finding 6: "the tray shows Preparing within 150 ms" is
    /// two numbers — press → acknowledge, measured at 28 ms in Phase 3, and
    /// acknowledge → painted, which had no instrument until this one. One number
    /// spanning two processes and a repaint cannot be checked by anyone.
    /// </summary>
    private long _preparingAtTicks;

    public ReaderTab(DaemonClient client)
    {
        _client = client;

        _scroller = new ScrollViewer
        {
            Content = _text,
            Padding = new Thickness(12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // Tunnel, so the click is seen before the control's own selection
        // handling consumes it.
        _text.AddHandler(PointerPressedEvent, OnTextPressed, RoutingStrategies.Tunnel);

        Content = new DockPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Top,
                    Spacing = 4,
                    Children = { _state, _notice },
                },
                new StackPanel
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "Recently read", Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) },
                        _history,
                    },
                },
                _scroller,
            },
        };

        Show(SpeechState.Idle);
    }

    /// <summary>Milliseconds from the Preparing event arriving to the paint that showed it.</summary>
    public double LastPaintLatencyMs { get; private set; } = double.NaN;

    // ------------------------------------------------------------- the stream

    /// <summary>
    /// The first line of a subscription. Opening the window mid-read is supposed
    /// to snap straight to the right word, and this is the whole mechanism: the
    /// snapshot carries state, text and offset together, so there is no second
    /// call and no gap to race.
    /// </summary>
    public void ApplySnapshot(StatusPayload status)
    {
        if (status.Text is { } text && !ReferenceEquals(text, _document) && text != _document)
            SetDocument(text, notice: null, remember: true);

        if (status.SourceOffset is { } offset)
            MoveTo(offset, status.SourceLength ?? 0);

        Show(status.State);
    }

    public void Apply(SessionEvent evt)
    {
        switch (evt.Kind)
        {
            case SessionEventKind.StateChanged when evt.State == SpeechState.Preparing:
                _preparingAtTicks = Stopwatch.GetTimestamp();

                // Text and Notice ride on this transition and only this one —
                // it is the earliest moment (28 ms, not the ~750 ms first audio)
                // and the only one where either changes.
                if (evt.Text is { } text) SetDocument(text, evt.Notice, remember: true);
                Show(SpeechState.Preparing);
                MeasurePaint();
                break;

            case SessionEventKind.StateChanged when evt.State is { } state:
                Show(state);
                break;

            case SessionEventKind.WordBoundary or SessionEventKind.SentenceBoundary:
                if (evt.SourceOffset is { } offset) MoveTo(offset, evt.SourceLength ?? 0);
                break;

            case SessionEventKind.Error:
                _notice.Text = evt.Message;
                break;
        }
    }

    /// <summary>
    /// Stop the clock on the frame that actually showed the transition, not on
    /// the line after the property was set. The plan says "again after the icon
    /// update returns"; a returned setter is not a pixel, and the difference is
    /// the entire quantity being claimed.
    /// </summary>
    private void MeasurePaint()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        long started = _preparingAtTicks;
        top.RequestAnimationFrame(_ =>
        {
            LastPaintLatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Console.Error.WriteLine($"acknowledge → painted: {LastPaintLatencyMs:F1} ms");
        });
    }

    public void ShowDisconnected()
    {
        // Not blank, and not pretending. The daemon owning the tray means killing
        // it takes the icon away; the window stays, says so, and comes back by
        // itself on the next press.
        _state.Text = "not connected — the next hotkey press starts the engine again";
        _text.SelectionStart = _text.SelectionEnd = 0;
    }

    // ------------------------------------------------------------- the window

    private void SetDocument(string text, string? notice, bool remember)
    {
        _document = text;
        _notice.Text = notice ?? "";
        _notice.IsVisible = notice is not null;
        _sliceStart = -1;                       // force a cut on the next move
        MoveTo(0, 0);

        if (remember && text.Length > 0)
        {
            _history.Items.Insert(0, Summarize(text));
            while (_history.Items.Count > HistoryLimit)
                _history.Items.RemoveAt(_history.Items.Count - 1);
        }
    }

    private void MoveTo(int offset, int length)
    {
        if (_document.Length == 0) return;

        _offset = Math.Clamp(offset, 0, _document.Length);
        _length = Math.Clamp(length, 0, _document.Length - _offset);

        Reslice();

        int local = _offset - _sliceStart;
        int sliceLength = _text.Text?.Length ?? 0;
        _text.SelectionStart = Math.Clamp(local, 0, sliceLength);
        _text.SelectionEnd = Math.Clamp(local + Math.Max(_length, 1), 0, sliceLength);

        BringIntoView(local);
    }

    /// <summary>
    /// Re-cut the slice only when the offset is near an edge — or when there is
    /// no slice yet.
    /// </summary>
    private void Reslice()
    {
        bool needed = _sliceStart < 0
                      || _offset < _sliceStart + ReslicMargin
                      || _offset > _sliceStart + WindowChars - ReslicMargin;

        if (!needed) return;

        int start = Math.Max(0, _offset - WindowChars / 2);
        int length = Math.Min(_document.Length - start, WindowChars);

        _sliceStart = start;
        _text.Text = _document.Substring(start, length);
    }

    private void BringIntoView(int localOffset)
    {
        // The layout has to exist before it can be hit-tested; after a re-cut it
        // does not yet.
        _text.Measure(new Size(_scroller.Viewport.Width, double.PositiveInfinity));

        var rect = _text.TextLayout.HitTestTextPosition(localOffset);
        double target = rect.Y - _scroller.Viewport.Height / 2;
        double max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);

        _scroller.Offset = new Vector(0, Math.Clamp(target, 0, max));
    }

    private void Show(SpeechState state) => _state.Text = state switch
    {
        SpeechState.Idle => "idle",
        SpeechState.Preparing => "preparing…",
        SpeechState.Speaking => "speaking",
        SpeechState.Stopping => "stopping…",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Summarize(string text)
    {
        string one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 90 ? one : one[..90] + "…";
    }

    // ------------------------------------------------------- click a word

    /// <summary>
    /// Click-a-word-to-jump. The <c>seek</c> verb, not <c>ApplySkip</c>: seek
    /// renders from the offset while keeping boundaries in whole-text
    /// coordinates, which is the one coordinate space on the wire.
    ///
    /// <para>Deliberately not debounced. A click is aimed at a word, and dropping
    /// it silently would leave the user having clicked and heard nothing.</para>
    /// </summary>
    private async void OnTextPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_document.Length == 0) return;
        if (!e.GetCurrentPoint(_text).Properties.IsLeftButtonPressed) return;

        var point = e.GetPosition(_text);
        var hit = _text.TextLayout.HitTestPoint(point);
        if (!hit.IsInside) return;

        // Local to whole-text, once, here. The daemon snaps to the start of the
        // containing word, so the click need not be precise — and it uses the
        // same word rule the highlight does.
        int offset = _sliceStart + hit.TextPosition;

        await _client.SendAsync(new Request { Verb = RequestVerb.Seek, Offset = offset });
    }
}

using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VibeSuperTonic.Core.Audio;

// bigtext — what does moving a highlight through an R-9-sized document cost?
//
//   bigtext [--chars 102338] [--moves 400] [--window 6000] [--sequential] [--still] [--noscroll]
//           [--strategy all|selectable|inlines|windowed] [--file PATH]
//
// Phase 6's finding 5, measured before the Reader tab picks a control. The cap
// is 100 KB; the truncation Phase 4 recorded in the field was 102,338
// characters, which is the default here. A highlight moves three to four times
// a second, so the budget per move is ~250 ms — and a control that re-measures
// the whole document on every move spends far more than that while looking
// perfect on a test paragraph.
//
// Three strategies, in the order a person tends to reach for them:
//
//   inlines     TextBlock whose Inlines are rebuilt as three runs — before,
//               the word, after. The obvious implementation, and the one that
//               throws away the entire text layout on every move.
//   selectable  One SelectableTextBlock holding the whole document, with
//               SelectionStart/SelectionEnd moved. Layout happens once; a move
//               is a property change the control renders from the layout it
//               already has.
//   windowed    A SelectableTextBlock holding only ±window/2 characters around
//               the current offset. Re-lays out on every move, but of a slice
//               whose size we choose, so the cost stops depending on how much
//               the user selected. The fallback if the other two disappoint,
//               and it costs the user the ability to scroll the whole text.
//
// Each move is timed twice, because they answer different questions:
//
//   apply   UI-thread milliseconds to set the properties and run layout to
//           completion (RunJobs at Render priority). This is what blocks the
//           window: if it exceeds the budget the highlight falls behind and
//           nothing else in the UI responds either.
//   frame   Wall milliseconds between composited frames, via
//           TopLevel.RequestAnimationFrame. Bounded below by the compositor's
//           own cadence (~16 ms at 60 Hz), so a fast strategy reports the
//           refresh rate rather than its true cost — which is the point: it
//           means the cost disappeared into a frame that was going to happen.
//
// The highlight is spread evenly across the whole document by default rather
// than walking adjacent words, so scrolling and layout locality are exercised
// the way a full reading exercises them. --sequential walks word by word from
// the start instead, which is what the first few seconds of a real read look
// like, and is the kinder measurement.
//
// Auto-scroll is part of the job and is included in `apply`: after each move the
// highlighted word is brought to the middle of the viewport via
// TextLayout.HitTestTextPosition, which is also the call Phase 6 needs in the
// other direction for click-a-word-to-jump.
//
// Read the result as follows:
//
//   p95 apply well under 250 ms   -> the control keeps up at the cap. Pick it.
//   p95 apply near or over 250 ms -> the Reader would stutter on exactly the
//                                    selection the cap exists to survive. Pick
//                                    the next strategy down, and say so in the
//                                    plan rather than discovering it in use.

int chars = 102_338;         // the truncation Phase 4 measured, not a round number
int moves = 400;
int window = 6_000;
bool sequential = false;
bool still = false;
bool noScroll = false;
string strategy = "all";
string? file = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--chars" when i + 1 < args.Length: chars = int.Parse(args[++i]); break;
        case "--moves" when i + 1 < args.Length: moves = int.Parse(args[++i]); break;
        case "--window" when i + 1 < args.Length: window = int.Parse(args[++i]); break;
        case "--strategy" when i + 1 < args.Length: strategy = args[++i]; break;
        case "--file" when i + 1 < args.Length: file = args[++i]; break;
        case "--sequential": sequential = true; break;
        case "--still": still = true; break;
        case "--noscroll": noScroll = true; break;
        case "--help" or "-h":
            Console.WriteLine("bigtext [--chars N] [--moves N] [--window N] [--sequential] " +
                              "[--strategy all|selectable|inlines|windowed] [--file PATH]");
            return 0;
    }
}

Bench.Text = file is not null ? File.ReadAllText(file) : Corpus.Generate(chars);

// A second document of the same size and different content. The relayout
// measurement needs one: Avalonia compares Text by value, so handing a control a
// fresh instance of an identical string changes nothing and measures nothing —
// which it reported as 0.1 ms until this existed.
Bench.Other = file is not null
    ? new string(Bench.Text.Reverse().ToArray())
    : Corpus.Generate(Bench.Text.Length, seed: 999);
Bench.Moves = moves;
Bench.Window = window;
Bench.Sequential = sequential;
Bench.Still = still;
Bench.Scroll = !noScroll;
Bench.Strategies = strategy == "all"
    ? [Strategy.Inlines, Strategy.Selectable, Strategy.Windowed]
    : [Enum.Parse<Strategy>(strategy, ignoreCase: true)];

Console.WriteLine($"bigtext: {Bench.Text.Length:N0} characters, {moves} moves, " +
                  $"{(still ? "STILL (offset never moves)" : sequential ? "sequential" : "spread across the document")}" +
                  $"{(noScroll ? ", no scroll" : "")}, " +
                  $"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}");
Console.WriteLine();

return AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .StartWithClassicDesktopLifetime(args);

internal enum Strategy { Inlines, Selectable, Windowed }

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window
            {
                Title = "bigtext",
                Width = 1100,
                Height = 800,
            };

            // Measuring before the window is on screen would measure a layout
            // nobody composited. Opened fires once the platform window exists.
            window.Opened += async (_, _) =>
            {
                try { await Bench.RunAll(window); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                desktop.Shutdown();
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Prose to fill the cap with. Generated rather than shipped: the measurement is
/// about the size and shape of the text, and a 100 KB file in the repository to
/// say "words, of varying length, in paragraphs" would be 100 KB of repository.
/// </summary>
internal static class Corpus
{
    private static readonly string[] Sentences =
    [
        "The machine measures itself before it decides anything.",
        "A threshold is a claim about the world, and the only place to check it is the world.",
        "Every press that produces no sound has to be visible somewhere a person will look.",
        "There is one coordinate space on the wire, and it is the whole text.",
        "The daemon acknowledges in twenty-eight milliseconds; the first audio arrives around seven hundred and fifty.",
        "A half-true comment is worse than a wrong one, because a reader has no reason to doubt it.",
        "Layout happens once if you let it, and on every move if you do not.",
        "The window is a client, and not a privileged one.",
    ];

    public static string Generate(int chars, int seed = 20260818)
    {
        var sb = new StringBuilder(chars + 256);
        var rng = new Random(seed);

        while (sb.Length < chars)
        {
            // Paragraphs of varying length, because a document of uniform lines
            // is a document whose wrapping never has to work.
            int sentences = rng.Next(3, 9);
            for (int i = 0; i < sentences; i++)
            {
                sb.Append(Sentences[rng.Next(Sentences.Length)]);
                sb.Append(' ');
            }
            sb.Append("\n\n");
        }

        return sb.ToString(0, chars);
    }
}

internal static class Bench
{
    public static string Text = "";
    public static string Other = "";
    public static int Moves;
    public static int Window;
    public static bool Sequential;

    /// <summary>
    /// Attribution flags. <c>--still</c> re-applies the same offset every move,
    /// so nothing about the text or the selection changes and what remains is
    /// the cost of a frame at this document size. <c>--noscroll</c> drops the
    /// hit-test and the scroll. Between them they say whether a strategy's cost
    /// is the move, the scroll, or merely holding the document at all — three
    /// answers with three different consequences for the Reader tab.
    /// </summary>
    public static bool Still;
    public static bool Scroll = true;

    /// <summary>
    /// The document the control is currently showing. Separate from
    /// <see cref="Text"/> so the relayout measurement can hand the control a
    /// string it has never laid out before, which is what a press does.
    /// </summary>
    private static string Current = "";
    public static Strategy[] Strategies = [];

    public static async Task RunAll(Window window)
    {
        // The same word rule the boundaries use, so the highlight this measures
        // is the highlight the product would draw.
        var words = WordStarts(Text);
        Console.WriteLine($"{words.Count:N0} words");
        Console.WriteLine();

        var results = new List<Result>();
        foreach (var s in Strategies)
            results.Add(await Run(window, s, words));

        Console.WriteLine();
        Console.WriteLine("  strategy      first frame    apply p50    apply p95    apply max    frame p50  new document");
        foreach (var r in results)
            Console.WriteLine($"  {r.Name,-12} {r.FirstFrameMs,10:F0} ms {r.ApplyP50,9:F1} ms {r.ApplyP95,9:F1} ms " +
                              $"{r.ApplyMax,9:F1} ms {r.FrameP50,9:F1} ms {r.RelayoutP50,10:F1} ms");

        Console.WriteLine();
        const double BudgetMs = 250;   // a highlight moves 3-4 times a second
        foreach (var r in results)
            Console.WriteLine(r.ApplyP95 < BudgetMs
                ? $"  {r.Name,-12} keeps up at the cap ({r.ApplyP95:F1} ms p95 vs a {BudgetMs:F0} ms budget)"
                : $"  {r.Name,-12} DOES NOT keep up ({r.ApplyP95:F1} ms p95 vs a {BudgetMs:F0} ms budget)");
    }

    private static async Task<Result> Run(Window window, Strategy strategy, List<int> words)
    {
        Current = Text;
        var scroller = new ScrollViewer { Padding = new Thickness(16) };
        TextBlock text = strategy switch
        {
            Strategy.Inlines => new TextBlock { TextWrapping = TextWrapping.Wrap },
            _ => new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                SelectionBrush = Brushes.Goldenrod,
            },
        };
        scroller.Content = text;
        window.Content = scroller;

        // First frame: what opening the window on a capped selection costs, and
        // the one number that is about the document rather than about a move.
        var first = Stopwatch.StartNew();
        Apply(strategy, text, scroller, words[0]);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        await NextFrame(window);
        double firstFrameMs = first.Elapsed.TotalMilliseconds;

        var apply = new List<double>(Moves);
        var frame = new List<double>(Moves);
        var sinceLastFrame = Stopwatch.StartNew();
        int step = Sequential ? 1 : Math.Max(1, words.Count / Moves);

        for (int i = 0; i < Moves; i++)
        {
            int offset = Still ? words[0] : words[Math.Min(words.Count - 1, i * step)];

            var a = Stopwatch.StartNew();
            Apply(strategy, text, scroller, offset);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            apply.Add(a.Elapsed.TotalMilliseconds);

            await NextFrame(window);
            frame.Add(sinceLastFrame.Elapsed.TotalMilliseconds);
            sinceLastFrame.Restart();
        }

        // Every press replaces the document — SessionEvent.Text rides on the
        // Preparing transition — so the Reader lays out a *new* 100 KB string on
        // the same schedule the user presses the hotkey. That is a different
        // cost from the cold first frame (which pays for the toolkit warming up
        // and happens once) and from a move (which reuses the layout), and it
        // lands in the 28 ms → ~750 ms gap where the window is supposed to look
        // like it noticed.
        var relayout = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            Current = i % 2 == 0 ? Other : Text;        // different content, never laid out
            var r = Stopwatch.StartNew();
            Apply(strategy, text, scroller, words[0]);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            relayout.Add(r.Elapsed.TotalMilliseconds);
            await NextFrame(window);
        }

        var result = new Result(strategy.ToString().ToLowerInvariant(), firstFrameMs,
                                Percentile(apply, 50), Percentile(apply, 95), apply.Max(),
                                Percentile(frame, 50), Percentile(relayout, 50));

        Console.WriteLine($"  {result.Name,-12} first frame {firstFrameMs,7:F0} ms   " +
                          $"apply p50 {result.ApplyP50,7:F1} ms  p95 {result.ApplyP95,7:F1} ms  " +
                          $"max {result.ApplyMax,7:F1} ms   frame p50 {result.FrameP50,6:F1} ms   " +
                          $"new document {result.RelayoutP50,7:F1} ms");
        return result;
    }

    /// <summary>
    /// Move the highlight to the word starting at <paramref name="offset"/> in
    /// whole-text coordinates, and bring it into view. Everything a real move
    /// does, so that everything a real move costs is inside the stopwatch.
    /// </summary>
    private static void Apply(Strategy strategy, TextBlock text, ScrollViewer scroller, int offset)
    {
        int end = WordEnd(Current, offset);
        int local;

        switch (strategy)
        {
            case Strategy.Inlines:
                // Three runs, rebuilt. The layout the control had is gone.
                text.Inlines = new InlineCollection
                {
                    new Run(Current[..offset]),
                    new Run(Current[offset..end]) { Background = Brushes.Goldenrod },
                    new Run(Current[end..]),
                };
                local = offset;
                break;

            case Strategy.Selectable:
                text.Text = Current;                    // a new instance only when the document changes
                ((SelectableTextBlock)text).SelectionStart = offset;
                ((SelectableTextBlock)text).SelectionEnd = end;
                local = offset;
                break;

            default:
                int start = Math.Max(0, offset - Window / 2);
                int length = Math.Min(Current.Length - start, Window);
                text.Text = Current.Substring(start, length);
                local = offset - start;
                ((SelectableTextBlock)text).SelectionStart = local;
                ((SelectableTextBlock)text).SelectionEnd = Math.Min(length, end - start);
                break;
        }

        if (!Scroll) return;

        // Layout must exist before it can be hit-tested, and on the rebuild
        // strategies it does not yet. This is part of the move's cost.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        var rect = text.TextLayout.HitTestTextPosition(local);
        double target = rect.Y - scroller.Viewport.Height / 2;
        scroller.Offset = new Vector(0, Math.Clamp(target, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
    }

    private static Task NextFrame(TopLevel top)
    {
        var tcs = new TaskCompletionSource();
        top.RequestAnimationFrame(_ => tcs.SetResult());
        return tcs.Task;
    }

    private static List<int> WordStarts(string text)
    {
        var starts = new List<int>();
        for (int i = 0; i < text.Length; i++)
            if (BoundaryPlanner.IsWordChar(text[i]) && (i == 0 || !BoundaryPlanner.IsWordChar(text[i - 1])))
                starts.Add(i);
        return starts;
    }

    private static int WordEnd(string text, int start)
    {
        int i = start;
        while (i < text.Length && BoundaryPlanner.IsWordChar(text[i])) i++;
        return i;
    }

    private static double Percentile(List<double> values, int p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private readonly record struct Result(
        string Name, double FirstFrameMs, double ApplyP50, double ApplyP95, double ApplyMax, double FrameP50,
        double RelayoutP50);
}

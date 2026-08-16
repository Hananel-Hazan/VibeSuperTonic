using VibeSuperTonic.Daemon;

// selcheck — capture the selection N times through the real X11SelectionSource
// and print what each capture decided.
//
//   selcheck first second                    # two captures, fallback off
//   selcheck --clipboard-fallback a b        # two captures, fallback on
//
// ONE source instance across all captures, deliberately. The staleness check
// compares against what THIS instance saw last time, so constructing a fresh
// one per capture would report every read as fresh and quietly test nothing.

bool fallback = args.Contains("--clipboard-fallback");
var source = new X11SelectionSource(fallback);

foreach (string label in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
{
    SelectionResult r = source.Capture();
    string text = r.Text.Length > 40 ? r.Text[..40] + "…" : r.Text;

    Console.WriteLine($"[{label}] ok={r.Ok} chars={r.Text.Length} text=\"{text}\"");
    Console.WriteLine($"        reason={r.Reason ?? "-"}");
    Console.WriteLine($"        notice={r.Notice ?? "-"}");
}

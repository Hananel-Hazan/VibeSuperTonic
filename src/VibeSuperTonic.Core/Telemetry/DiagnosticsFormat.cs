using System.Globalization;
using System.Text;

namespace VibeSuperTonic.Core.Telemetry;

/// <summary>
/// A <see cref="SessionSnapshot"/> as the rows a person reads — shared by the
/// Status tab and <c>vst-ctl diagnostics</c>, so the window and the terminal
/// cannot word the same number two ways.
///
/// <para>Plain string building on purpose: <c>vst-ctl</c> is NativeAOT and runs on
/// a hotkey's budget, so nothing here reflects, nothing uses a format provider
/// that needs ICU (the culture is always invariant), and the work is a few
/// hundred bytes of concatenation.</para>
///
/// <para><b>Zero means "not measured yet", and is never printed as a measurement.</b>
/// An RTF of 0.00 would read as infinitely fast, a latency of 0 ms as instant, and
/// a window that said so on a daemon that had not spoken would be reporting the
/// absence of data as good news.</para>
/// </summary>
public static class DiagnosticsFormat
{
    public readonly record struct Row(string Label, string Value);

    public static IReadOnlyList<Row> Rows(SessionSnapshot s, DateTime nowUtc)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<Row>(12)
        {
            new("Daemon", $"pid {s.Pid.ToString(inv)}, up {Duration(s.UptimeSec)}"),
            new("State", s.IsActive ? "speaking" : "idle"),
        };

        if (s.VoiceId.Length > 0) rows.Add(new("Voice", s.VoiceId));

        rows.Add(new("Inference", Inference(s)));

        rows.Add(new("Last render", s.LastRtf > 0
            ? $"RTF {s.LastRtf.ToString("0.00", inv)} (average of recent renders "
              + $"{s.RollingRtf.ToString("0.00", inv)}; under 1.0 is faster than speech)"
            : "nothing rendered yet"));

        rows.Add(new("Pipeline latency", s.FirstByteLatencyMs > 0
            ? $"{s.FirstByteLatencyMs.ToString("0", inv)} ms from the request to the first audio"
            : "not measured yet"));

        rows.Add(new("Underruns", s.UnderrunCount == 0
            ? "none since the daemon started"
            : $"{s.UnderrunCount.ToString(inv)} since the daemon started — inference fell behind playback"));

        rows.Add(new("Memory", s.EngineRssMb > 0
            ? $"{s.EngineRssMb.ToString("0", inv)} MB resident"
              + (s.EngineCpuPct > 0 ? $", {s.EngineCpuPct.ToString("0.0", inv)}% of one core since the last look" : "")
            : "unavailable"));

        string text = s.TextLength > 0
            ? $"{s.TextLength.ToString("N0", inv)} characters ({(s.IsActive ? "being read" : "last read")})"
            : "nothing read yet";
        if (s.CurrentText.Length > 0) text += $": {s.CurrentText}";
        rows.Add(new("Text", text));

        rows.Add(new("Last error", s.LastError.Length == 0
            ? "none"
            : s.LastErrorUtc is { } at
                ? $"{s.LastError} ({Ago(nowUtc - at)})"
                : s.LastError));

        return rows;
    }

    /// <summary>The rows as aligned text, one per line — what <c>vst-ctl diagnostics</c> prints.</summary>
    public static string Text(SessionSnapshot s, DateTime nowUtc)
    {
        var rows = Rows(s, nowUtc);
        int width = 0;
        foreach (var r in rows) width = Math.Max(width, r.Label.Length);

        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(r.Label.PadRight(width)).Append("  ").Append(r.Value).Append('\n');
        return sb.ToString();
    }

    private static string Inference(SessionSnapshot s)
    {
        if (s.Provider.Length == 0) return "not reported";

        var inv = CultureInfo.InvariantCulture;
        string intra = s.OnnxThreads == 0 ? "ORT's own pick of intra-op threads" : $"{s.OnnxThreads.ToString(inv)} intra-op threads";
        string inter = s.OnnxInterOpThreads > 0 ? $", {s.OnnxInterOpThreads.ToString(inv)} inter-op" : "";
        return $"{s.Provider}, {intra}{inter}" + (s.ProfileApplied ? " (from the benchmark)" : "");
    }

    /// <summary>"3 s", "4 min", "2 h 5 min", "1 d 3 h" — coarse on purpose; this is an uptime, not a stopwatch.</summary>
    public static string Duration(double seconds)
    {
        if (!(seconds >= 0)) return "0 s";
        long total = (long)seconds;
        if (total < 60) return $"{total} s";
        if (total < 3600) return $"{total / 60} min";
        if (total < 86400) return $"{total / 3600} h {total % 3600 / 60} min";
        return $"{total / 86400} d {total % 86400 / 3600} h";
    }

    private static string Ago(TimeSpan age) =>
        age < TimeSpan.FromSeconds(1) ? "just now" : Duration(age.TotalSeconds) + " ago";
}

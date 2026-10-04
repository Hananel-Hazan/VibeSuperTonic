using System.Diagnostics;

namespace VibeSuperTonic.Core.Telemetry;

/// <summary>
/// What the daemon remembers about its own recent speech, for the
/// <c>diagnostics</c> verb — the Linux counterpart of the Windows engine's
/// <c>TelemetryWriter</c>, minus the file: nothing here is written anywhere, a
/// client asks and gets the current answer.
///
/// <para><b>Why a class and not fields on the server.</b> Every number here is
/// derived from events that arrive on different threads — the render thread
/// reports an RTF, the audio thread reports the first sample, the session reports
/// an error — and each has a rule that a bug would break silently: an RTF of 0 or
/// NaN must not become "the last RTF"; a first-audio latency must be measured from
/// <em>this</em> utterance's start and counted once; a rolling mean must forget.
/// Those are the testable part, and they are testable only when the clock is
/// injected, which is why this takes one.</para>
///
/// <para><b>Privacy.</b> The words being read are the user's, and the daemon's
/// status already carries them to any client of its socket (which is uid-checked
/// and mode 0600). Diagnostics is a different habit — people paste it into bug
/// reports — so it carries the <em>length</em> always and a short snippet only for
/// the window's own display, and <see cref="Snippet"/> is where that is bounded.</para>
/// </summary>
public sealed class DiagnosticsTracker
{
    /// <summary>How many recent renders the rolling RTF averages over — about one paragraph.</summary>
    public const int RollingWindow = 8;

    /// <summary>The longest snippet of the text ever reported.</summary>
    public const int SnippetChars = 80;

    /// <summary>The longest last-error string kept. A CUDA failure is ~900 characters and the first line is the point.</summary>
    public const int ErrorChars = 300;

    private readonly Func<long> _nowMs;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly double[] _window = new double[RollingWindow];

    private int _windowCount;
    private int _windowNext;
    private double _lastRtf;

    private long _utteranceStartMs = -1;
    private double _firstAudioMs;
    private bool _firstAudioSeen;
    private bool _active;
    private int _utterances;
    private int _textLength;
    private string _snippet = "";
    private string _lastError = "";
    private DateTime? _lastErrorUtc;

    public DiagnosticsTracker(Func<long>? nowMs = null, Func<DateTime>? utcNow = null)
    {
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// One render finished. <paramref name="wallSeconds"/> over
    /// <paramref name="audioSeconds"/> is the same quantity the benchmark and
    /// <c>usage-stats.json</c> call RTF, so the three can be compared directly.
    /// A render that produced no audio, or whose clock ran backwards, is not a
    /// measurement and is dropped rather than clamped — a zero in the window
    /// would read as "infinitely fast".
    /// </summary>
    public void RecordRender(double wallSeconds, double audioSeconds)
    {
        if (!(audioSeconds > 0) || !(wallSeconds >= 0)
            || double.IsInfinity(audioSeconds) || double.IsInfinity(wallSeconds)) return;

        double rtf = wallSeconds / audioSeconds;
        if (double.IsNaN(rtf) || double.IsInfinity(rtf) || rtf <= 0) return;

        lock (_gate)
        {
            _lastRtf = rtf;
            _window[_windowNext] = rtf;
            _windowNext = (_windowNext + 1) % RollingWindow;
            if (_windowCount < RollingWindow) _windowCount++;
        }
    }

    /// <summary>
    /// An utterance is being prepared: the clock for "pipeline latency" starts
    /// here, which is the moment the daemon accepted the work, not the moment the
    /// first sample is heard.
    /// </summary>
    public void UtteranceStarting(string? text)
    {
        lock (_gate)
        {
            _utteranceStartMs = _nowMs();
            _firstAudioSeen = false;
            _firstAudioMs = 0;
            _active = true;
            _utterances++;
            _textLength = text?.Length ?? 0;
            _snippet = Snippet(text);
        }
    }

    /// <summary>
    /// The device accepted the first audio. Only the first call after
    /// <see cref="UtteranceStarting"/> counts: the event this hangs off is
    /// raised once per utterance, but a second one must not be able to restate
    /// latency as the length of the whole reading.
    /// </summary>
    public void FirstAudio()
    {
        lock (_gate)
        {
            if (_utteranceStartMs < 0 || _firstAudioSeen) return;
            _firstAudioSeen = true;
            _firstAudioMs = Math.Max(0, _nowMs() - _utteranceStartMs);
        }
    }

    /// <summary>The utterance ended, however it ended.</summary>
    public void UtteranceEnded()
    {
        lock (_gate) _active = false;
    }

    /// <summary>
    /// Something failed. Only the most recent is kept, with its time: a
    /// diagnostics panel's question is "what went wrong last", and the daemon log
    /// has the history.
    /// </summary>
    public void RecordError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        string text = message.Trim();
        int newline = text.IndexOf('\n');
        if (newline > 0) text = text[..newline].TrimEnd();
        if (text.Length > ErrorChars) text = text[..ErrorChars].TrimEnd() + "…";

        lock (_gate)
        {
            _lastError = text;
            _lastErrorUtc = _utcNow();
        }
    }

    /// <summary>
    /// The tracked half of a snapshot. The caller fills in what only the process
    /// knows — pid, resident set, provider and thread counts, the underrun count
    /// the session owns — because none of those are events and all of them are
    /// cheaper to ask for at the moment somebody wants them.
    /// </summary>
    /// <param name="includeSnippet">
    /// Whether <see cref="SessionSnapshot.CurrentText"/> carries the truncated
    /// words. The window asks for them (the Windows Monitor tab shows the same);
    /// a plain <c>vst-ctl diagnostics</c> does not.
    /// </param>
    public SessionSnapshot Snapshot(bool includeSnippet)
    {
        lock (_gate)
        {
            double rolling = 0;
            if (_windowCount > 0)
            {
                for (int i = 0; i < _windowCount; i++) rolling += _window[i];
                rolling /= _windowCount;
            }

            return new SessionSnapshot
            {
                IsActive = _active,
                CurrentText = includeSnippet ? _snippet : "",
                TextLength = _textLength,
                LastError = _lastError,
                LastErrorUtc = _lastErrorUtc,
                FirstByteLatencyMs = _firstAudioSeen ? _firstAudioMs : 0,
                LastRtf = _lastRtf,
                RollingRtf = rolling,
                UtteranceCount = _utterances,
                SampleTimeUtc = _utcNow(),
            };
        }
    }

    /// <summary>
    /// The first <see cref="SnippetChars"/> characters with whitespace collapsed,
    /// ending in an ellipsis when something was cut — and never in the middle of a
    /// surrogate pair, which would put a replacement character in the window.
    /// </summary>
    public static string Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new System.Text.StringBuilder(SnippetChars + 1);
        bool space = false;
        bool cut = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            int need = (space ? 1 : 0) + 1;
            if (sb.Length + need > SnippetChars) { cut = true; break; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }

        // A high surrogate left at the end lost its partner to the cut.
        if (cut && sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;

        return cut ? sb.ToString().TrimEnd() + "…" : sb.ToString();
    }
}

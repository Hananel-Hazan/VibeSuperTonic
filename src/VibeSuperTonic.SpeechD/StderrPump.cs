using System.Collections.Concurrent;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.SpeechD;

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// Reads a renderer's stderr on a small thread, sorting it into SSML bookmarks
/// (<see cref="MarkLine"/>) and everything else (the diagnostic that explains a
/// failure).
///
/// <para><b>A thread, though the module is otherwise single-threaded, and the
/// reason is narrow.</b> The module's loop is single-threaded so that nothing
/// races stdout; this thread never touches stdout. It only fills two queues the
/// loop reads between audio blocks. Without it the loop would have to block on
/// stderr to see a mark — and a render that has not printed one yet would stall
/// the audio, where here it simply has none to report yet.</para>
///
/// <para>It also fixes a latent hazard: nobody drained a renderer's stderr until
/// the process had finished, so one that wrote more than a pipe buffer would
/// have blocked mid-utterance.</para>
/// </summary>
internal sealed class StderrPump
{
    private readonly ConcurrentQueue<RenderMark> _marks = new();
    private readonly Thread _thread;
    private volatile string? _firstMessage;

    /// <summary>
    /// Every diagnostic line, up to <see cref="MaxLines"/>, so a caller can ask
    /// whether one particular message was printed. espeak-ng's only report of a
    /// missing dictionary is such a line, with exit code 0.
    /// </summary>
    private readonly ConcurrentQueue<string> _lines = new();

    private const int MaxLines = 64;

    internal StderrPump(TextReader stderr)
    {
        _thread = new Thread(() =>
        {
            try
            {
                while (stderr.ReadLine() is { } line)
                {
                    if (MarkLine.TryParse(line, out var mark)) _marks.Enqueue(mark);
                    else if (line.Trim() is { Length: > 0 } said)
                    {
                        _firstMessage ??= said;
                        if (_lines.Count < MaxLines) _lines.Enqueue(said);
                    }
                }
            }
            catch (Exception)
            {
                // The process was killed and its pipe disposed under us: the end
                // of the stream, however it came.
            }
        })
        {
            IsBackground = true,
            Name = "vst-speechd-stderr",
        };
        _thread.Start();
    }

    /// <summary>The next mark the renderer has reported, if any.</summary>
    internal bool TryTake(out RenderMark mark) => _marks.TryDequeue(out mark!);

    /// <summary>
    /// Wait for the renderer's stderr to end, so every mark it printed has been
    /// queued. Called once the process has finished producing audio.
    /// </summary>
    internal void WaitForEnd(int milliseconds = 2000) => _thread.Join(milliseconds);

    /// <summary>
    /// The first diagnostic line containing <paramref name="fragment"/>, or null.
    /// Complete only after <see cref="WaitForEnd"/>.
    /// </summary>
    internal string? Find(string fragment) =>
        _lines.FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal));

    /// <summary>The first line of ordinary diagnostic output, for the log.</summary>
    internal string Message => _firstMessage ?? "no message";
}

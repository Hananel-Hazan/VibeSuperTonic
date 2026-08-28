using System.Runtime.InteropServices;

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// Lines from speech-dispatcher, readable either blocking or not.
///
/// <para><b>The non-blocking mode is the whole point.</b> Between audio blocks
/// the module has to answer the question "has a STOP arrived?" without waiting
/// for one, because the common case is that none has and the next block is due.
/// Upstream does this with <c>module_process(fd, 0)</c>; there is no equivalent
/// on <see cref="Stream"/>, whose Read blocks until at least one byte exists —
/// so readiness is asked of the file descriptor with <c>poll(2)</c> and a zero
/// timeout, which is what that call does underneath.</para>
///
/// <para><b>A partial line survives between calls.</b> A non-blocking read can
/// stop in the middle of one, and discarding what had arrived would lose a
/// command outright. Bytes accumulate here until a newline completes them.</para>
/// </summary>
internal sealed class LineReader
{
    /// <summary>speech-dispatcher talks to a module on its standard input.</summary>
    internal const int StdinFd = 0;

    private const short PollIn = 0x001;

    /// <summary>
    /// The descriptor readiness is asked about, which must be the one
    /// <see cref="_stream"/> actually reads. A negative value means "do not ask"
    /// — for a stream that cannot block, such as the in-memory ones the tests
    /// drive this with. Passing an fd rather than assuming 0 is not only for the
    /// tests: a reader polling a descriptor it is not reading from would report
    /// readiness about the wrong file, and the symptom would be a module that
    /// misses stops or spins.
    /// </summary>
    private readonly int _fd;

    private readonly Stream _stream;
    private readonly Queue<string> _pushedBack = new();
    private readonly List<byte> _pending = new();
    private readonly byte[] _chunk = new byte[4096];
    private bool _eof;

    internal LineReader(Stream stream, int fd = StdinFd)
    {
        _stream = stream;
        _fd = fd;
    }

    /// <summary>
    /// Put a line back so the main loop sees it next. Used when a command turns
    /// up during audio that is not a stop: the utterance keeps going and the
    /// command is handled after it, rather than being thrown away.
    /// </summary>
    internal void PushBack(string line) => _pushedBack.Enqueue(line);

    /// <summary>
    /// The next line, or null — at end of input when <paramref name="block"/> is
    /// true, or when nothing is ready when it is false.
    /// </summary>
    internal string? ReadLine(bool block)
    {
        if (_pushedBack.Count > 0) return _pushedBack.Dequeue();

        while (true)
        {
            if (TakeLine() is { } line) return line;
            if (_eof) return null;

            if (!block && !Readable(_fd)) return null;

            int n;
            try { n = _stream.Read(_chunk, 0, _chunk.Length); }
            catch (IOException) { _eof = true; return null; }

            if (n <= 0)
            {
                _eof = true;

                // A last line with no trailing newline is still a line. speechd
                // always sends one, but a stream that ends mid-command should
                // deliver what it had rather than silently drop it.
                return TakeRemainder();
            }

            _pending.AddRange(_chunk.AsSpan(0, n));
        }
    }

    private string? TakeLine()
    {
        int at = _pending.IndexOf((byte)'\n');
        if (at < 0) return null;

        string line = Decode(0, at);
        _pending.RemoveRange(0, at + 1);
        return line;
    }

    private string? TakeRemainder()
    {
        if (_pending.Count == 0) return null;

        string line = Decode(0, _pending.Count);
        _pending.Clear();
        return line;
    }

    private string Decode(int start, int count)
    {
        // \r is trimmed rather than kept: the protocol is \n-delimited, but a
        // command compared with == against "STOP" would silently never match if
        // one ever arrived with a carriage return.
        while (count > start && _pending[start + count - 1] == (byte)'\r') count--;
        return System.Text.Encoding.UTF8.GetString(_pending.GetRange(start, count).ToArray());
    }

    /// <summary>Is there anything to read right now?</summary>
    private static bool Readable(int fd)
    {
        if (fd < 0) return true;                 // a stream that cannot block

        var fds = new PollFd { Fd = fd, Events = PollIn, Revents = 0 };
        int ready = poll(ref fds, 1, 0);
        return ready > 0 && (fds.Revents & PollIn) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        internal int Fd;
        internal short Events;
        internal short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint nfds, int timeoutMs);
}

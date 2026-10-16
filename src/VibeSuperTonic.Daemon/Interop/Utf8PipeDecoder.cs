using System.Text;

namespace VibeSuperTonic.Daemon.Interop;

/// <summary>
/// Decodes UTF-8 arriving in arbitrarily-sized blocks — which is what a pipe
/// hands back, and what a selection transfer is.
///
/// <para><b>Why this is a type rather than three lines at the call site.</b>
/// <c>read(2)</c> returns whatever bytes have arrived; it has no idea where
/// characters begin. So a multi-byte character straddles a block boundary
/// whenever the selection is longer than the read buffer and the alignment falls
/// that way, and <c>Encoding.UTF8.GetString</c> per block turns both halves into
/// U+FFFD. Measured on a 35,000-character selection through
/// <see cref="WaylandNative"/>: the words at byte 16384 and 32768 came back
/// mangled, which reaches the user as a word eaten in the middle of a sentence
/// with nothing anywhere reporting a fault.</para>
///
/// <para>Whether it bites depends on where the boundary lands, so the defect
/// passes for every ASCII selection and for most others. That is exactly the
/// shape that survives testing by hand, which is why the mechanism is extracted
/// to somewhere a test can chop a string at <em>every</em> offset instead of at
/// a lucky one. Curly quotes, dashes and accented letters are all multi-byte, so
/// ordinary English prose is affected — this is not only about other
/// alphabets.</para>
///
/// <para><see cref="X11SelectionSource"/> has never needed this: it receives the
/// whole property as one array and decodes it once.</para>
/// </summary>
internal sealed class Utf8PipeDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private char[] _chars = new char[1024];

    /// <summary>Add one block. <paramref name="count"/> may split a character.</summary>
    public void Feed(byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return;

        // GetChars throws rather than truncating if the destination is short, so
        // it is sized for the worst case: every byte its own character.
        if (_chars.Length < count + 1) _chars = new char[count + 1];
        _text.Append(_chars, 0, _decoder.GetChars(buffer, 0, count, _chars, 0));
    }

    /// <summary>
    /// Everything fed so far. Flushes the decoder, so a transfer that ended
    /// mid-character emits the replacement here rather than dropping the bytes
    /// silently — a truncated selection should look truncated.
    /// </summary>
    public string Finish()
    {
        _text.Append(_chars, 0, _decoder.GetChars([], 0, 0, _chars, 0, flush: true));
        return _text.ToString();
    }
}

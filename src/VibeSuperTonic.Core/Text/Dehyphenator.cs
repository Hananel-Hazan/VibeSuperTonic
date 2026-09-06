using System.Text;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// Puts back together a word that a line break cut in half.
///
/// <para><b>Reported 2026-09-06 from the running install</b>, reading a paper:
/// "Alternative Ground-Truth Configu-\nrations" came out as "configyoo" and then
/// "rations". Nothing anywhere joined them. The chunker collapses the newline to
/// a space, so the phonemiser is handed <c>Configu-</c> and <c>rations</c> as two
/// words and says exactly that — with the pause a chunk boundary can put between
/// them.</para>
///
/// <para>This is not an exotic shape. Every justified PDF, every two-column
/// paper and every hard-wrapped mail hyphenates at the margin, and reading those
/// aloud is what this product is for. It is also invisible to every check the
/// project has: the text is valid, the phonemes are valid, the audio is valid,
/// and only a person listening knows it is wrong.</para>
///
/// <para><b>Why it stops at the line break, and does not try to be cleverer.</b>
/// A hyphen inside a line is a real hyphen — <c>Ground-Truth</c>, <c>well-known</c>
/// — and joining those invents words nobody wrote. At a break the evidence is
/// the case of what follows: a lowercase letter is the tail of a split word,
/// while a capital is far more often a compound that happened to wrap
/// (<c>Ground-\nTruth</c>) or a new sentence. Getting the capital case wrong is
/// silent and permanent; leaving it alone is at worst the bug as it is today, for
/// a shape that is rare. So the rule is deliberately the conservative half.</para>
/// </summary>
public static class Dehyphenator
{
    /// <summary>
    /// U+00AD, the SOFT hyphen: invisible, and meaningful only to a renderer
    /// choosing where to break a line.
    ///
    /// <para>It arrives in text copied out of a browser or out of Word.
    /// <see cref="TextSanitizer"/>'s ranges stop at U+009F and do not reach it,
    /// and espeak-ng treats it as a word boundary — so a character nobody can see
    /// turns one spoken word into two, which is the same failure as the visible
    /// hyphen and arrives with no line break to explain it.</para>
    /// </summary>
    public const char SoftHyphen = '­';

    /// <summary>Join without tracking offsets. Same result as the mapping overload.</summary>
    public static string Join(string text) => Join(text, out _);

    /// <summary>
    /// Join, and report where each surviving character came from.
    ///
    /// <para>Text with nothing to join — nearly all text — returns the input
    /// instance and an identity map, allocating nothing. This runs on every
    /// utterance.</para>
    /// </summary>
    /// <param name="map">
    /// Maps an index in the returned string back to an index in
    /// <paramref name="text"/>. Without it, seek and the reader's highlight land
    /// in the wrong place the moment anything is dropped — R-2.
    /// </param>
    public static string Join(string text, out TextOffsetMap map)
    {
        if (string.IsNullOrEmpty(text))
        {
            map = TextOffsetMap.Identity(text?.Length ?? 0);
            return text;
        }

        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == SoftHyphen || DropLength(text, i) > 0) { first = i; break; }
        }

        if (first < 0)
        {
            map = TextOffsetMap.Identity(text.Length);
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var origins = new int[text.Length];
        int kept = 0;

        sb.Append(text, 0, first);
        for (int i = 0; i < first; i++) origins[kept++] = i;

        for (int i = first; i < text.Length; i++)
        {
            if (text[i] == SoftHyphen) continue;

            int drop = DropLength(text, i);
            if (drop > 0) { i += drop - 1; continue; }

            sb.Append(text[i]);
            origins[kept++] = i;
        }

        Array.Resize(ref origins, kept);
        map = TextOffsetMap.FromOrigins(origins, text.Length);
        return sb.ToString();
    }

    /// <summary>
    /// How many characters starting at <paramref name="at"/> are a hyphenation —
    /// the hyphen, the line break and the next line's indent — or 0 when this is
    /// not one.
    ///
    /// <para>All four conditions have to hold, and each is a shape that would be
    /// wrong to join: a letter before the hyphen (so a bullet "-" or a dash on
    /// its own line is left alone), exactly one newline (so a paragraph break is
    /// not a hyphenation), no other newline in the indent that follows, and a
    /// lowercase letter after it.</para>
    /// </summary>
    private static int DropLength(string text, int at)
    {
        if (text[at] != '-') return 0;
        if (at == 0 || !char.IsLetter(text[at - 1])) return 0;

        int i = at + 1;
        if (i < text.Length && text[i] == '\r') i++;
        if (i >= text.Length || text[i] != '\n') return 0;
        i++;

        // The next line's indent. A second newline is a paragraph, and no word
        // has ever been hyphenated across one.
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        if (i >= text.Length || !char.IsLower(text[i])) return 0;

        return i - at;
    }
}

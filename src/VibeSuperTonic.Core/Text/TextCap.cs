namespace VibeSuperTonic.Core.Text;

/// <summary>
/// Bounds a block of text and says what it dropped.
///
/// <para>Exists for [R-9]: Ctrl+A in a book then the hotkey would otherwise
/// produce an unbounded render queue and a lot of resident memory. Cutting at a
/// sentence boundary rather than at the character limit matters because the
/// alternative is speech that stops mid-word, which sounds like a crash rather
/// than a limit.</para>
///
/// <para>Platform-neutral and in Core rather than beside the X11 code that calls
/// it, for one reason: it is the only part of selection capture that can be
/// tested without a display, and the edge cases are the interesting part — no
/// sentence boundary anywhere in the capped region, a boundary at exactly the
/// limit, text already inside it.</para>
/// </summary>
public static class TextCap
{
    /// <summary>
    /// Sentence terminators, plus the paragraph break — a selection from a PDF
    /// or a code comment can run for pages without a full stop, and a blank line
    /// is a better cut than a hard one.
    /// </summary>
    private static readonly char[] Terminators = ['.', '!', '?', '\n'];

    /// <summary>
    /// How far back from the limit a sentence boundary is still worth taking:
    /// take it if it lands in the second half of what would be kept.
    ///
    /// <para>Without a floor, a 100 KB selection whose only full stop sits at
    /// character 12 would be cut to 12 characters — technically a sentence
    /// boundary, and a spectacular misreading of the intent. Past this point,
    /// cutting mid-sentence loses less than cutting almost everything.</para>
    ///
    /// <para>Half rather than something stricter, because the tests caught the
    /// stricter version refusing boundaries it should plainly have taken: at
    /// 0.75, a two-sentence selection whose full stop sat 73% of the way to the
    /// limit got a mid-word cut instead. The floor is meant to catch the
    /// pathological case, not to adjudicate reasonable ones — and every value
    /// here is a judgement call, so it should be the permissive one.</para>
    /// </summary>
    private const double MinKeptFraction = 0.5;

    /// <param name="notice">
    /// What was dropped, as a sentence for a user. Null when nothing was.
    /// </param>
    /// <returns>The text, capped at <paramref name="maxChars"/>.</returns>
    public static string Apply(string text, int maxChars, out string? notice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (text.Length <= maxChars)
        {
            notice = null;
            return text;
        }

        int cut = text.LastIndexOfAny(Terminators, maxChars - 1);

        // LastIndexOfAny lands ON the terminator, so keep it.
        if (cut >= 0) cut++;

        if (cut < maxChars * MinKeptFraction) cut = maxChars;

        int dropped = text.Length - cut;
        notice = $"selection was {text.Length:N0} characters; " +
                 $"reading the first {cut:N0} and dropping {dropped:N0}.";
        return text[..cut];
    }
}

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// How the fast voice says one <c>CHAR</c>: which espeak-ng switches it gets.
/// The default is ordinary text, which is every other message.
/// </summary>
/// <param name="Punctuation">Pass <c>--punct</c>, so a comma is read as "comma".</param>
/// <param name="Capitals">espeak-ng's <c>-k</c>: 0 off, 1 a sound, 2 the word "capital".</param>
/// <param name="Ssml">The text is SSML the module wrote, so pass <c>-m</c>.</param>
internal readonly record struct EspeakEcho(bool Punctuation, int Capitals, bool Ssml);

/// <summary>
/// Character echo (SSIP <c>CHAR</c>) of characters espeak-ng says nothing for when
/// given them as text (item 2 of docs/CODE-REVIEW-2026-11-09.md). SSIP <c>KEY</c>
/// messages do not come through here and are still spoken as English key names:
/// item 104 of that file.
///
/// <para><b>Whitespace.</b> SSIP sends a space as the word <c>space</c>, because a
/// bare space cannot travel on its command line (ssip.info, "CHAR"). Read as
/// text that word is English in every voice: a German one says
/// <c>(en)sp'eIs(de)</c>. A bare whitespace character is worse: it is empty, so
/// the module used to answer BEGIN and END with no audio at all. Both are
/// handed to espeak-ng as <c>&lt;say-as interpret-as="tts:char"&gt;</c>, which
/// names the character in the voice's own language: "space", "Leerzeichen",
/// "espace", "tab", measured against the shipped payload with <c>-x</c>. The same
/// format string is in speech-dispatcher 0.12.1's own <c>sd_espeak-ng</c> (found
/// with <c>strings</c>, not read in its source).</para>
/// </summary>
internal static class CharEcho
{
    /// <summary>
    /// The SSML that names <paramref name="text"/>, when it is one whitespace
    /// character or SSIP's word for a space; otherwise null.
    /// </summary>
    internal static string? WhitespaceSsml(string text)
    {
        int? code = text == "space" ? ' '
            : text.Length == 1 && char.IsWhiteSpace(text[0]) ? text[0]
            : null;

        return code is { } c ? $"<say-as interpret-as=\"tts:char\">&#{c};</say-as>" : null;
    }

    /// <summary>
    /// speech-dispatcher's <c>cap_let_recogn</c> as espeak-ng's <c>-k</c>. The
    /// SSIP values are <c>none</c>, <c>spell</c> and <c>icon</c> (ssip.info,
    /// "CAP_LET_RECOGN"). espeak-ng's 1 is its own short sound rather than the
    /// user's configured sound icon, which this module cannot play.
    /// </summary>
    internal static int Capitals(string? capLetRecogn) => capLetRecogn switch
    {
        "spell" => 2,
        "icon" => 1,
        _ => 0,
    };
}

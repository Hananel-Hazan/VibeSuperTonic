namespace VibeSuperTonic.Core.Text;

/// <summary>
/// Removes SSML markup from text that is about to be synthesised.
///
/// <para><b>This exists because the tags arrive whether or not anyone asked for
/// them.</b> Speech Dispatcher clients can set SSML mode, and the probe in
/// <c>spike/speechd-705-gate</c> measured what that actually means at the module:
/// a plain <c>spd-say "hello"</c> reaches it as <c>&lt;speak&gt;hello&lt;/speak&gt;</c>.
/// Handed to the model unchanged, "speak" is a word and the user hears it —
/// which is not a crash, not a log line, and unmistakably broken.
/// docs/SPEECHD-PLAN.md, trap 11.</para>
///
/// <para><b>The rule is deliberately narrow: text that is not an SSML document
/// is returned untouched.</b> Stripping anything that looks like a tag from
/// every render would quietly eat the angle brackets out of source code, shell
/// snippets and mathematics — text people read aloud on purpose. So the strip
/// only happens when the text opens with <c>&lt;speak</c>, which is what makes
/// it an SSML document rather than a document containing an angle bracket.</para>
///
/// <para><b>And inside a document, a bare <c>&lt;</c> is still a bare
/// <c>&lt;</c>.</b> <c>&lt;speak&gt;a &lt; b&lt;/speak&gt;</c> is not well-formed
/// XML, but it is what a client that forgot to escape produces, and the naive
/// "delete from &lt; to the next &gt;" would swallow <c>" b&lt;/speak"</c> and
/// speak the word "a" alone. A <c>&lt;</c> opens a tag only when what follows it
/// could begin one.</para>
/// </summary>
public static class Ssml
{
    /// <summary>
    /// True when <paramref name="text"/> is an SSML document — the only case in
    /// which <see cref="Strip"/> changes anything.
    /// </summary>
    public static bool IsDocument(string? text)
    {
        if (text is null) return false;

        int at = 0;
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;

        // "<speak>" and "<speak version=...>" both, and "<speaker" neither: the
        // name has to end where a tag name is allowed to end.
        if (!text.AsSpan(at).StartsWith("<speak", StringComparison.OrdinalIgnoreCase))
            return false;

        int after = at + "<speak".Length;
        return after >= text.Length
               || text[after] is '>' or '/'
               || char.IsWhiteSpace(text[after]);
    }

    /// <summary>
    /// Strip the markup out of an SSML document, or return non-SSML unchanged.
    ///
    /// <para>Tags become a single space rather than nothing, because
    /// <c>&lt;s&gt;One&lt;/s&gt;&lt;s&gt;Two&lt;/s&gt;</c> is two sentences and
    /// "OneTwo" is one word. Runs of whitespace then collapse, which is safe
    /// here for the same reason the whole function is: this only runs on text
    /// that announced itself as a document.</para>
    /// </summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IsDocument(text)) return text ?? "";

        var sb = new System.Text.StringBuilder(text.Length);

        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '<' && TagEnd(text, i) is { } end)
            {
                sb.Append(' ');
                i = end + 1;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return CollapseWhitespace(Entities.Decode(sb.ToString()));
    }

    /// <summary>
    /// The index of the <c>&gt;</c> closing the tag that starts at
    /// <paramref name="start"/>, or null when this <c>&lt;</c> is not a tag.
    ///
    /// <para>Attribute values are honoured, so <c>&lt;mark name="a&gt;b"/&gt;</c>
    /// ends at the right bracket. An unterminated tag is not a tag: better to
    /// speak a stray angle bracket than to silently drop the rest of the
    /// utterance.</para>
    /// </summary>
    private static int? TagEnd(string text, int start)
    {
        if (start + 1 >= text.Length) return null;

        char c = text[start + 1];
        bool opensTag = char.IsLetter(c) || c is '/' or '!' or '?';
        if (!opensTag) return null;

        char quote = '\0';
        for (int i = start + 1; i < text.Length; i++)
        {
            char ch = text[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (ch == '>') return i;
        }

        return null;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The five XML entities and numeric character references.
    ///
    /// <para><b>Decoded after the tags are removed, never before.</b> A document
    /// carrying <c>&amp;lt;speak&amp;gt;</c> as literal text means the words, and
    /// decoding first would turn it into markup and then delete it — the one
    /// ordering bug this function can have.</para>
    /// </summary>
    private static class Entities
    {
        internal static string Decode(string text)
        {
            if (text.IndexOf('&') < 0) return text;

            var sb = new System.Text.StringBuilder(text.Length);

            for (int i = 0; i < text.Length;)
            {
                if (text[i] != '&') { sb.Append(text[i++]); continue; }

                int semi = text.IndexOf(';', i + 1);

                // An unterminated & is an ampersand. So is a "reference" long
                // enough to be prose that happens to contain a semicolon.
                if (semi < 0 || semi - i > 10) { sb.Append(text[i++]); continue; }

                string name = text[(i + 1)..semi];
                string? value = name switch
                {
                    "amp" => "&",
                    "lt" => "<",
                    "gt" => ">",
                    "quot" => "\"",
                    "apos" => "'",
                    _ => Numeric(name),
                };

                if (value is null) { sb.Append(text[i++]); continue; }

                sb.Append(value);
                i = semi + 1;
            }

            return sb.ToString();
        }

        private static string? Numeric(string name)
        {
            if (name.Length < 2 || name[0] != '#') return null;

            bool hex = name[1] is 'x' or 'X';
            string digits = hex ? name[2..] : name[1..];
            if (digits.Length == 0) return null;

            if (!int.TryParse(
                    digits,
                    hex ? System.Globalization.NumberStyles.HexNumber
                        : System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int code))
                return null;

            // Surrogates and out-of-range values are not characters. Left as
            // written rather than replaced, so nothing is invented.
            if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
                return null;

            return char.ConvertFromUtf32(code);
        }
    }
}

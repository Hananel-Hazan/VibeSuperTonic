using System.Text;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Engine;

/// <summary>
/// The two SAPI speaking modes that change WHAT is said rather than how:
/// <c>SPF_NLP_SPEAK_PUNC</c> (a screen reader set to "all punctuation") and
/// <c>SPVA_SpellOut</c> (<c>&lt;spell&gt;</c>, and how readers spell a word
/// on request). Review item 48: both used to reach the model as ordinary text,
/// so the first was silent and the second read the word as a word.
///
/// <para>Both rewrite the text the model will speak, and both return a map
/// back into the text they were given, so that a word event for "comma" or
/// for the spelled "S" lands on the character the user sees — the R-2 rule.
/// The caller chains it with the pipeline's own map.</para>
///
/// <para>The names exist in four languages. Every other language Supertonic
/// speaks hears the English names, which is wrong and is still better than the
/// silence it replaces; Linux names punctuation through espeak-ng, which
/// Windows does not ship.</para>
/// </summary>
internal static class SapiTextModes
{
    /// <summary>
    /// Marks that also do a job in the sentence: they end it or pause it. For
    /// these the name is spoken and the mark is kept after it, so the chunker
    /// still splits on the full stop and the model still pauses at the comma.
    /// Every other mark is replaced by its name.
    /// </summary>
    private const string Prosodic = ".,;:!?…。、！？¿¡";

    public static string SpeakPunctuation(string text, string lang, out TextOffsetMap map)
    {
        var names = NamesFor(lang);
        var sb = new StringBuilder(text.Length + 32);
        var origins = new List<int>(text.Length + 32);
        bool changed = false;

        void Put(char ch, int origin) { sb.Append(ch); origins.Add(origin); }
        void Space(int origin) { if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) Put(' ', origin); }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (!names.TryGetValue(ch, out var name))
            {
                Put(ch, i);
                continue;
            }
            changed = true;
            Space(i);
            foreach (char n in name) Put(n, i);
            if (Prosodic.IndexOf(ch) >= 0) Put(ch, i);
            if (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) Put(' ', i);
        }

        if (!changed)
        {
            map = TextOffsetMap.Identity(text.Length);
            return text;
        }
        map = TextOffsetMap.FromOrigins(origins.ToArray(), text.Length);
        return sb.ToString();
    }

    /// <summary>
    /// One spoken item per character, separated by commas so the model pauses
    /// between them and cannot run them back into a word. Letters are spoken
    /// as capitals, which every voice reads as the letter's name ("a" alone is
    /// read as the article); marks by their names; whitespace only separates.
    /// </summary>
    public static string SpellOut(string text, string lang, out TextOffsetMap map)
    {
        var names = NamesFor(lang);
        var sb = new StringBuilder(text.Length * 3);
        var origins = new List<int>(text.Length * 3);

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) continue;

            string item;
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                item = text.Substring(i, 2);
            }
            else if (names.TryGetValue(ch, out var name)) item = name;
            else item = char.ToUpperInvariant(ch).ToString();

            if (sb.Length > 0)
            {
                // The separator belongs to the item it introduces.
                sb.Append(", ");
                origins.Add(i);
                origins.Add(i);
            }
            foreach (char c in item) { sb.Append(c); origins.Add(i); }
            if (item.Length == 2 && char.IsHighSurrogate(item[0])) { origins[^1] = i; i++; }
        }

        if (sb.Length == 0)
        {
            map = TextOffsetMap.Identity(text.Length);
            return text;
        }
        map = TextOffsetMap.FromOrigins(origins.ToArray(), text.Length);
        return sb.ToString();
    }

    private static IReadOnlyDictionary<char, string> NamesFor(string lang) =>
        lang switch
        {
            "de" => German,
            "fr" => French,
            "es" => Spanish,
            _ => English,
        };

    private static readonly Dictionary<char, string> English = new()
    {
        ['.'] = "period", [','] = "comma", [';'] = "semicolon", [':'] = "colon",
        ['!'] = "exclamation mark", ['?'] = "question mark", ['…'] = "dot dot dot",
        ['\''] = "apostrophe", ['’'] = "apostrophe", ['ʼ'] = "apostrophe", ['‘'] = "quote",
        ['"'] = "quote", ['“'] = "quote", ['”'] = "quote", ['«'] = "quote", ['»'] = "quote",
        ['('] = "left paren", [')'] = "right paren", ['['] = "left bracket", [']'] = "right bracket",
        ['{'] = "left brace", ['}'] = "right brace",
        ['-'] = "dash", ['–'] = "dash", ['—'] = "dash",
        ['/'] = "slash", ['\\'] = "backslash", ['@'] = "at", ['#'] = "number", ['$'] = "dollar",
        ['%'] = "percent", ['&'] = "and", ['*'] = "star", ['+'] = "plus", ['='] = "equals",
        ['<'] = "less than", ['>'] = "greater than", ['_'] = "underline", ['~'] = "tilde",
        ['^'] = "caret", ['|'] = "bar", ['`'] = "grave", ['¿'] = "inverted question mark",
        ['¡'] = "inverted exclamation mark",
    };

    private static readonly Dictionary<char, string> German = new()
    {
        ['.'] = "Punkt", [','] = "Komma", [';'] = "Semikolon", [':'] = "Doppelpunkt",
        ['!'] = "Ausrufezeichen", ['?'] = "Fragezeichen", ['…'] = "Auslassungspunkte",
        ['\''] = "Apostroph", ['’'] = "Apostroph", ['ʼ'] = "Apostroph", ['‘'] = "Anführungszeichen",
        ['"'] = "Anführungszeichen", ['“'] = "Anführungszeichen", ['”'] = "Anführungszeichen",
        ['„'] = "Anführungszeichen", ['«'] = "Anführungszeichen", ['»'] = "Anführungszeichen",
        ['('] = "Klammer auf", [')'] = "Klammer zu", ['['] = "eckige Klammer auf", [']'] = "eckige Klammer zu",
        ['{'] = "geschweifte Klammer auf", ['}'] = "geschweifte Klammer zu",
        ['-'] = "Bindestrich", ['–'] = "Gedankenstrich", ['—'] = "Gedankenstrich",
        ['/'] = "Schrägstrich", ['\\'] = "Backslash", ['@'] = "at", ['#'] = "Raute", ['$'] = "Dollar",
        ['%'] = "Prozent", ['&'] = "und", ['*'] = "Stern", ['+'] = "plus", ['='] = "gleich",
        ['<'] = "kleiner als", ['>'] = "größer als", ['_'] = "Unterstrich", ['~'] = "Tilde",
        ['^'] = "Zirkumflex", ['|'] = "senkrechter Strich", ['`'] = "Gravis",
    };

    private static readonly Dictionary<char, string> French = new()
    {
        ['.'] = "point", [','] = "virgule", [';'] = "point-virgule", [':'] = "deux-points",
        ['!'] = "point d'exclamation", ['?'] = "point d'interrogation", ['…'] = "points de suspension",
        ['\''] = "apostrophe", ['’'] = "apostrophe", ['ʼ'] = "apostrophe", ['‘'] = "guillemet",
        ['"'] = "guillemet", ['“'] = "guillemet", ['”'] = "guillemet", ['«'] = "guillemet", ['»'] = "guillemet",
        ['('] = "parenthèse ouvrante", [')'] = "parenthèse fermante", ['['] = "crochet ouvrant", [']'] = "crochet fermant",
        ['{'] = "accolade ouvrante", ['}'] = "accolade fermante",
        ['-'] = "tiret", ['–'] = "tiret", ['—'] = "tiret",
        ['/'] = "barre oblique", ['\\'] = "barre oblique inversée", ['@'] = "arobase", ['#'] = "dièse", ['$'] = "dollar",
        ['%'] = "pour cent", ['&'] = "et commercial", ['*'] = "astérisque", ['+'] = "plus", ['='] = "égal",
        ['<'] = "inférieur à", ['>'] = "supérieur à", ['_'] = "tiret bas", ['~'] = "tilde",
        ['^'] = "accent circonflexe", ['|'] = "barre verticale", ['`'] = "accent grave",
    };

    private static readonly Dictionary<char, string> Spanish = new()
    {
        ['.'] = "punto", [','] = "coma", [';'] = "punto y coma", [':'] = "dos puntos",
        ['!'] = "cierra exclamación", ['?'] = "cierra interrogación", ['…'] = "puntos suspensivos",
        ['¡'] = "abre exclamación", ['¿'] = "abre interrogación",
        ['\''] = "apóstrofo", ['’'] = "apóstrofo", ['ʼ'] = "apóstrofo", ['‘'] = "comillas",
        ['"'] = "comillas", ['“'] = "comillas", ['”'] = "comillas", ['«'] = "comillas", ['»'] = "comillas",
        ['('] = "abre paréntesis", [')'] = "cierra paréntesis", ['['] = "abre corchete", [']'] = "cierra corchete",
        ['{'] = "abre llave", ['}'] = "cierra llave",
        ['-'] = "guion", ['–'] = "raya", ['—'] = "raya",
        ['/'] = "barra", ['\\'] = "barra invertida", ['@'] = "arroba", ['#'] = "almohadilla", ['$'] = "dólar",
        ['%'] = "por ciento", ['&'] = "y", ['*'] = "asterisco", ['+'] = "más", ['='] = "igual",
        ['<'] = "menor que", ['>'] = "mayor que", ['_'] = "guion bajo", ['~'] = "tilde",
        ['^'] = "circunflejo", ['|'] = "barra vertical", ['`'] = "acento grave",
    };
}

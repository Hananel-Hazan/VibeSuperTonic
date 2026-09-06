using System.Text.RegularExpressions;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// The text path every host runs before synthesis: line-break hyphenation, then
/// pronunciation rules, then the invisible-character sanitizer, then one map back
/// to the client's coordinates.
///
/// Exists as a single call so the steps cannot be composed in the wrong order or
/// — the R-2 failure — composed correctly while the offsets are not composed at
/// all. Hosts call this; they do not chain the pieces themselves.
///
/// <para><b>The join goes first, and the order is the point.</b> A pronunciation
/// rule matches words, and a word the line break has cut in half is not the word
/// the rule names — so a rule for "kubernetes" would silently stop applying to
/// every copy of the text that happened to wrap inside it. Joining first means
/// every later stage sees whole words.</para>
/// </summary>
public static class SynthTextPipeline
{
    /// <summary>
    /// Prepare <paramref name="text"/> for the model.
    /// </summary>
    /// <param name="pron">Rules to apply, or null to skip straight to sanitizing.</param>
    /// <param name="compiled">Cached regexes matching <c>pron.Rules</c> by index, or null to compile on demand.</param>
    /// <param name="map">Maps an index in the returned string back to an index in <paramref name="text"/>.</param>
    public static string Prepare(
        string text,
        PronunciationsConfig? pron,
        IReadOnlyList<Regex?>? compiled,
        out TextOffsetMap map)
    {
        if (string.IsNullOrEmpty(text))
        {
            map = TextOffsetMap.Identity(0);
            return text;
        }

        string joined = Dehyphenator.Join(text, out var joinMap);

        string rewritten;
        TextOffsetMap pronMap;
        if (pron is null)
        {
            rewritten = joined;
            pronMap = TextOffsetMap.Identity(joined.Length);
        }
        else
        {
            rewritten = pron.Apply(joined, compiled, out pronMap);
        }

        string spoken = TextSanitizer.SanitizeForSynth(rewritten, out var sanitizerMap);

        // sanitizerMap: spoken -> rewritten. pronMap: rewritten -> joined.
        // joinMap: joined -> source.
        map = TextOffsetMap.Chain(TextOffsetMap.Chain(sanitizerMap, pronMap), joinMap);
        return spoken;
    }
}

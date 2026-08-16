using System.Text.RegularExpressions;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// The text path every host runs before synthesis: pronunciation rules, then the
/// invisible-character sanitizer, then one map back to the client's coordinates.
///
/// Exists as a single call so the two steps cannot be composed in the wrong
/// order or — the R-2 failure — composed correctly while the offsets are not
/// composed at all. Hosts call this; they do not chain the pieces themselves.
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

        string rewritten;
        TextOffsetMap pronMap;
        if (pron is null)
        {
            rewritten = text;
            pronMap = TextOffsetMap.Identity(text.Length);
        }
        else
        {
            rewritten = pron.Apply(text, compiled, out pronMap);
        }

        string spoken = TextSanitizer.SanitizeForSynth(rewritten, out var sanitizerMap);

        // sanitizerMap: spoken -> rewritten. pronMap: rewritten -> source.
        map = TextOffsetMap.Chain(sanitizerMap, pronMap);
        return spoken;
    }
}

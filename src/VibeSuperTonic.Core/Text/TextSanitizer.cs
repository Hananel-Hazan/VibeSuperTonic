using System.Text;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// Strips characters that are invisible to the user but have non-deterministically
/// wedged the synth in the field — control characters, zero-width and
/// bidirectional marks, private-use glyphs.
///
/// Moved out of <c>SupertonicAdapter</c>, where it was the only thing that did
/// not touch ONNX Runtime or the registry. The adapter is per-platform (it holds
/// the execution-provider split and reads HKCU); this is pure text work that
/// both hosts need, so it belongs on this side of the line.
/// </summary>
public static class TextSanitizer
{
    /// <summary>
    /// True for characters the synth should never see. The set is unchanged from
    /// the original implementation — each range was added in response to a
    /// specific field report, so this is deliberately not "tidied up".
    /// </summary>
    private static bool IsStripped(char c) =>
        (c < 0x20 && c != '\t' && c != '\n' && c != '\r')   // C0 controls, keeping whitespace
        || c == 0x7F                                        // DEL
        || (c >= 0x80   && c <= 0x9F)                       // C1 controls
        || (c >= 0x200B && c <= 0x200F)                     // zero-width + LTR/RTL marks
        || (c >= 0x202A && c <= 0x202E)                     // bidi embedding/override
        || (c >= 0x2060 && c <= 0x2064)                     // word joiner, invisible operators
        || c == 0xFEFF                                      // BOM / zero-width no-break space
        || (c >= 0xE000 && c <= 0xF8FF);                    // private use area

    /// <summary>Strip without tracking offsets. Same result as the mapping overload.</summary>
    public static string SanitizeForSynth(string text) => SanitizeForSynth(text, out _);

    /// <summary>
    /// Strip, and report where each surviving character came from.
    ///
    /// Text containing nothing strippable — nearly all text — returns the input
    /// instance and an identity map, allocating nothing.
    /// </summary>
    public static string SanitizeForSynth(string text, out TextOffsetMap map)
    {
        if (string.IsNullOrEmpty(text))
        {
            map = TextOffsetMap.Identity(0);
            return text;
        }

        int firstStripped = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsStripped(text[i])) { firstStripped = i; break; }
        }

        if (firstStripped < 0)
        {
            map = TextOffsetMap.Identity(text.Length);
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var origins = new int[text.Length];
        int kept = 0;

        // Everything before the first stripped character is already known good.
        sb.Append(text, 0, firstStripped);
        for (int i = 0; i < firstStripped; i++) origins[kept++] = i;

        for (int i = firstStripped; i < text.Length; i++)
        {
            if (IsStripped(text[i])) continue;
            sb.Append(text[i]);
            origins[kept++] = i;
        }

        Array.Resize(ref origins, kept);
        map = TextOffsetMap.FromOrigins(origins, text.Length);
        return sb.ToString();
    }
}

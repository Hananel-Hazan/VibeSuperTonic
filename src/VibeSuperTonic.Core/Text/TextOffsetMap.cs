namespace VibeSuperTonic.Core.Text;

/// <summary>
/// Maps an index in rewritten text back to the index it came from in the source
/// text.
///
/// Everything the user sees highlighted — SAPI word and sentence boundary
/// events, and the Linux Reader tab — is expressed in <em>source</em>
/// coordinates, because that is the text the client still holds. But the engine
/// speaks <em>rewritten</em> text: pronunciation rules substitute ("kg" →
/// "kilograms", +7 chars) and the sanitizer strips invisible characters
/// (−1 char each). Any length-changing edit shifts every later index, so an
/// index taken from the rewritten string and reported as a source offset is
/// wrong by the accumulated delta. That was R-2, live on Windows.
///
/// Backed by one int per rewritten character, holding that character's source
/// index. Chosen over the ordered (start, lengthDelta) edit list the plan
/// sketched because rules apply <em>in sequence</em> — each rule rewrites the
/// output of the previous one — and composing delta lists across passes is
/// where that representation gets subtle. Re-indexing an array per pass is
/// obviously correct, and correctness is the entire point of a fix for silently
/// wrong arithmetic.
///
/// The array is allocated only when an edit actually happens. Text that no rule
/// touches — the overwhelmingly common case, and every case when pronunciations
/// are switched off — gets <see cref="Identity"/>, which stores nothing and
/// answers from the index itself.
/// </summary>
public sealed class TextOffsetMap
{
    /// <summary>Source index per rewritten character; null means identity.</summary>
    private readonly int[]? _origin;
    private readonly int _sourceLength;

    private TextOffsetMap(int[]? origin, int sourceLength)
    {
        _origin = origin;
        _sourceLength = sourceLength < 0 ? 0 : sourceLength;
    }

    /// <summary>Nothing was rewritten: index <c>i</c> came from index <c>i</c>.</summary>
    public static TextOffsetMap Identity(int length) => new(null, length);

    /// <summary>
    /// Build from an explicit per-character origin array. <paramref name="origins"/>
    /// is taken by reference and must not be mutated afterwards.
    /// </summary>
    public static TextOffsetMap FromOrigins(int[] origins, int sourceLength) =>
        new(origins ?? throw new ArgumentNullException(nameof(origins)), sourceLength);

    public bool IsIdentity => _origin is null;

    /// <summary>Length of the original text this map resolves back into.</summary>
    public int SourceLength => _sourceLength;

    /// <summary>Length of the rewritten text this map resolves from.</summary>
    public int RewrittenLength => _origin?.Length ?? _sourceLength;

    /// <summary>
    /// Source index for a rewritten index. Indices inside a substitution all
    /// resolve to the <em>start</em> of the source span that produced them — a
    /// boundary landing anywhere inside "kilograms" highlights the "kg" the user
    /// actually wrote, which is the only answer that makes sense on screen.
    /// Out-of-range indices clamp, so a caller asking about one-past-the-end
    /// (the normal way to measure a span) gets the source length rather than an
    /// exception.
    /// </summary>
    public int ToSource(int rewrittenIndex)
    {
        // Clamp negatives to the first character — NOT to source index 0. When
        // the very first character was itself displaced (a rule that fires at
        // index 0, or a stripped leading zero-width space) its origin is not 0,
        // and short-circuiting to 0 here silently reports the wrong offset for
        // the one index every caller asks about first.
        if (rewrittenIndex < 0) rewrittenIndex = 0;
        if (_origin is null) return rewrittenIndex < _sourceLength ? rewrittenIndex : _sourceLength;
        return rewrittenIndex < _origin.Length ? _origin[rewrittenIndex] : _sourceLength;
    }

    /// <summary>
    /// How many source characters a rewritten span covers. This is what a
    /// highlight length should be: "kilograms" is nine rewritten characters but
    /// covers the two the user typed, so the highlight is two wide, not nine.
    /// </summary>
    public int SourceSpanLength(int rewrittenStart, int rewrittenLength)
    {
        if (rewrittenLength <= 0) return 0;
        int from = ToSource(rewrittenStart);
        int to = ToSource(rewrittenStart + rewrittenLength);
        return to > from ? to - from : 0;
    }

    /// <summary>
    /// Compose two passes. <paramref name="first"/> maps X→Y,
    /// <paramref name="second"/> maps Y→Z, and the result maps X→Z.
    ///
    /// Used to fold the sanitizer's map (spoken→rewritten) into the
    /// pronunciation map (rewritten→source) so callers hold one map from what is
    /// actually spoken all the way back to what the client sent.
    /// </summary>
    public static TextOffsetMap Chain(TextOffsetMap first, TextOffsetMap second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.IsIdentity && second.IsIdentity) return Identity(second._sourceLength);

        int n = first.RewrittenLength;
        var origins = new int[n];
        for (int i = 0; i < n; i++) origins[i] = second.ToSource(first.ToSource(i));
        return new TextOffsetMap(origins, second._sourceLength);
    }
}

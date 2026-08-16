using System.Text;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.Core.Text;

/// <summary>
/// One user-defined text substitution applied to every text fragment before it
/// reaches the model. Designed for fixing mispronunciations that the model's
/// front-end can't help with — e.g. all-caps short tokens that get spelled as
/// letters ("NOT" → "N O T") or non-pronounceable glyphs ("Bᵠ" → "B phi").
/// </summary>
public sealed class PronunciationRule
{
    public bool   Enabled       { get; set; } = true;
    public string Match         { get; set; } = "";
    public string Replace       { get; set; } = "";
    public bool   WholeWord     { get; set; } = true;
    public bool   CaseSensitive { get; set; } = true;
    public string Notes         { get; set; } = "";

    public PronunciationRule Clone() => (PronunciationRule)MemberwiseClone();
}

/// <summary>
/// Persisted as JSON at <c>&lt;DataDir&gt;/pronunciations.json</c>. Read by the
/// engine on every Speak (mtime-cached by the host); written by the launcher's
/// Pronunciations tab.
///
/// This type used to exist twice — once in the engine and once in the launcher,
/// with a comment on each telling the reader to keep them in sync by hand. Both
/// now use this one, which is the point of Core: the launcher's "Test" pane and
/// the engine cannot disagree about what a rule does, because they run the same
/// code.
/// </summary>
public sealed class PronunciationsConfig
{
    public bool Enabled { get; set; } = true;
    public List<PronunciationRule> Rules { get; set; } = new();

    /// <summary>
    /// Apply every enabled rule in order, using regexes the caller has already
    /// compiled and cached. Returns the original string if the master switch is
    /// off, the list is empty, or no rule fires.
    /// </summary>
    public string Apply(string text, IReadOnlyList<Regex?> compiled) =>
        Apply(text, compiled, out _);

    /// <summary>
    /// Apply every enabled rule in order, compiling regexes on demand. For
    /// callers that run this rarely enough not to want a cache — the launcher's
    /// Test pane — and want to be certain they see exactly what the engine will.
    /// </summary>
    public string Apply(string text) => Apply(text, null, out _);

    /// <summary>
    /// Apply every enabled rule in order and report where the result came from.
    ///
    /// <paramref name="map"/> resolves an index in the returned string back to
    /// an index in <paramref name="text"/>. Without it, a length-changing rule
    /// silently shifts every later word-boundary offset by the accumulated
    /// delta — R-2, live on Windows since pronunciation rules shipped.
    ///
    /// Pass null for <paramref name="compiled"/> to compile on demand.
    /// </summary>
    public string Apply(string text, IReadOnlyList<Regex?>? compiled, out TextOffsetMap map)
    {
        if (string.IsNullOrEmpty(text))
        {
            map = TextOffsetMap.Identity(0);
            return text;
        }

        int sourceLength = text.Length;
        map = TextOffsetMap.Identity(sourceLength);
        if (!Enabled || Rules.Count == 0) return text;

        string current = text;
        // origin[i] = index in the ORIGINAL text of current[i]. Stays null until
        // a rule actually fires, so the no-op case allocates nothing at all.
        int[]? origin = null;

        for (int i = 0; i < Rules.Count; i++)
        {
            var rule = Rules[i];
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Match)) continue;

            Regex? re = compiled is null
                ? Compile(rule)
                : (i < compiled.Count ? compiled[i] : null);
            if (re is null) continue; // rule failed to compile — skip silently

            var matches = re.Matches(current);
            if (matches.Count == 0) continue;

            var sb = new StringBuilder(current.Length);
            var next = new List<int>(current.Length);
            int last = 0;

            foreach (Match m in matches)
            {
                for (int k = last; k < m.Index; k++)
                {
                    sb.Append(current[k]);
                    next.Add(OriginOf(origin, k, sourceLength));
                }

                // Match.Result is what Regex.Replace calls per match, so "$&" and
                // friends in a replacement behave exactly as they did before this
                // method started tracking offsets. Behaviour-preserving on purpose:
                // this is a fix for wrong arithmetic, not a change to what is said.
                string replacement = m.Result(rule.Replace ?? "");
                int anchor = OriginOf(origin, m.Index, sourceLength);
                sb.Append(replacement);
                for (int k = 0; k < replacement.Length; k++) next.Add(anchor);

                last = m.Index + m.Length;
            }

            for (int k = last; k < current.Length; k++)
            {
                sb.Append(current[k]);
                next.Add(OriginOf(origin, k, sourceLength));
            }

            current = sb.ToString();
            origin = next.ToArray();
        }

        if (origin is not null) map = TextOffsetMap.FromOrigins(origin, sourceLength);
        return current;
    }

    private static int OriginOf(int[]? origin, int index, int sourceLength)
    {
        if (origin is null) return index < sourceLength ? index : sourceLength;
        return index < origin.Length ? origin[index] : sourceLength;
    }

    public static Regex? Compile(PronunciationRule r)
    {
        if (string.IsNullOrEmpty(r.Match)) return null;
        try
        {
            string pattern = Regex.Escape(r.Match);

            // \b is a transition between a word and a non-word character, so it
            // can only anchor a side that actually starts or ends with a word
            // character. Wrapping a symbol in \b...\b produces a rule that can
            // never fire: "\b=\b" does not match "3 = 4", because the spaces on
            // either side are already non-word and there is no transition to
            // find. It matches only "3=4", where the digits supply the
            // boundaries — which looks like the rule working intermittently.
            //
            // This mattered: the Pronunciations tab defaults WholeWord to true,
            // so every symbol rule a user adds is born dead. Found from Linux
            // (a "=" -> " equal " rule that did nothing) but the defect is
            // shared Core code and was live on Windows for as long as the
            // feature has existed.
            //
            // Anchoring each side independently is the whole fix. Word matches
            // are completely unaffected -- "kg" still compiles to "\bkg\b".
            if (r.WholeWord)
            {
                if (IsWordChar(r.Match[0])) pattern = @"\b" + pattern;
                if (IsWordChar(r.Match[^1])) pattern += @"\b";
            }

            var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (!r.CaseSensitive) opts |= RegexOptions.IgnoreCase;
            return new Regex(pattern, opts);
        }
        catch { return null; }
    }

    /// <summary>
    /// What <c>\b</c> considers a word character: .NET's definition is
    /// <c>[\p{L}\p{Mn}\p{Nd}\p{Pc}]</c>, which underscore falls into and which
    /// <see cref="char.IsLetterOrDigit"/> otherwise matches closely enough for
    /// deciding whether an anchor can bind at all.
    /// </summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

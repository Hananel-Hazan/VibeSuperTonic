using System.Globalization;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Core.Text;

/// <summary>What an SSML document asks the engine to do, one step at a time.</summary>
public enum SsmlFragmentKind
{
    /// <summary>Words to speak, with the language and rate in force around them.</summary>
    Text,

    /// <summary><c>&lt;break&gt;</c> — a stretch of silence.</summary>
    Silence,

    /// <summary><c>&lt;mark name="..."/&gt;</c> — a bookmark at this point in the audio.</summary>
    Mark,
}

/// <summary>
/// One step of a parsed SSML document. The same three shapes the Windows engine
/// builds from SAPI's fragment list (<c>SpeakTextItem</c>, <c>SpeakSilenceItem</c>,
/// <c>SpeakBookmarkItem</c> in <c>SapiEngine.BuildSpeakPlan</c>), so the two
/// platforms mean the same thing by the same markup.
/// </summary>
/// <param name="Text">Text fragments only: the decoded, whitespace-collapsed words.</param>
/// <param name="Lang">
/// Text fragments only: the Supertonic language code <c>xml:lang</c> resolved to,
/// or null when the fragment carries none <b>or one Supertonic does not speak</b>
/// — which is exactly what <c>SupertonicLanguages.FromLcid</c> returns on Windows,
/// where null means "use the configured language".
/// </param>
/// <param name="RateAdj">
/// Text fragments only: SAPI's −10…+10 per-fragment rate adjustment
/// (<c>SPVSTATE.RateAdj</c>), already clamped. The engine adds the client's own
/// rate to it and applies <c>1.5^(n/10)</c>.
/// </param>
/// <param name="SilenceMs">Silence fragments only. Always positive.</param>
/// <param name="Mark">Mark fragments only. Never empty.</param>
public sealed record SsmlFragment(
    SsmlFragmentKind Kind,
    string Text = "",
    string? Lang = null,
    int RateAdj = 0,
    int SilenceMs = 0,
    string? Mark = null)
{
    public static SsmlFragment Speak(string text, string? lang, int rateAdj) =>
        new(SsmlFragmentKind.Text, Text: text, Lang: lang, RateAdj: rateAdj);

    public static SsmlFragment Pause(int ms) => new(SsmlFragmentKind.Silence, SilenceMs: ms);

    public static SsmlFragment Bookmark(string name) => new(SsmlFragmentKind.Mark, Mark: name);
}

/// <summary>
/// Parses an SSML document into the fragments the engine acts on.
///
/// <para><b>On Windows this job is SAPI's, not ours</b> — SAPI parses the SSML
/// and hands the engine an already-built <c>SPVTEXTFRAG</c> list, so there was no
/// parser to move here. This is the Linux equivalent, written to produce the same
/// list: <c>xml:lang</c> becomes a per-fragment language, <c>prosody rate</c> a
/// per-fragment rate adjustment, <c>break</c> a silence and <c>mark</c> a
/// bookmark. What SAPI does that this does not is documented below, because every
/// gap is a place the two platforms could disagree.</para>
///
/// <para><b>Anything it cannot parse returns false, and the caller strips
/// instead</b> — <see cref="Ssml.Strip"/>, today's behaviour. The contract is
/// "never throw to a client": a screen reader sends whatever an application
/// wrote, and a render that fails over an unclosed tag is a sentence the user
/// never hears. Malformed means a closing tag that does not match, an element
/// left open, text after the root closed, or a comment that never ends. A bare
/// <c>&lt;</c> that cannot start a tag is text, exactly as in
/// <see cref="Ssml.Strip"/>.</para>
///
/// <para>Hand-rolled over <c>System.Xml</c> on purpose. Strictness is the wrong
/// default here (see above), and XmlReader would drag its whole parser into the
/// native-AOT <c>vst-speechd</c>, which links Core.</para>
///
/// <para><b>Invariant the tests hold it to:</b> for a document with no marks,
/// breaks, prosody or language changes, the concatenation of its text fragments
/// equals <see cref="Ssml.Strip"/> of the same text. Parsing must not change
/// what is said.</para>
/// </summary>
public static class SsmlDocument
{
    /// <summary>
    /// The longest single <c>&lt;break&gt;</c>. Windows passes SAPI's value
    /// through with no cap (<c>SapiEngine</c> writes <c>SilenceMSecs</c> as given;
    /// the 10 s and 60 s limits are Linux-only guards, a known and deliberate
    /// divergence); this is a guard, not a policy — a client can name any number, and
    /// the render verb would otherwise write that many seconds of zeros.
    /// </summary>
    public const int MaxBreakMs = 10_000;

    /// <summary>SAPI's per-fragment rate range, which the engine clamps to.</summary>
    public const int MaxRateAdj = 10;

    /// <summary>
    /// <c>strength</c> without <c>time</c>, in milliseconds. <b>CHOSEN, NOT
    /// VERIFIED:</b> SAPI converts strength to <c>SPVSTATE.SilenceMSecs</c> before
    /// the engine sees it and the repo only ever sees the result
    /// (<c>SapiEngine.BuildSpeakPlan</c> plays <c>SilenceMSecs</c> verbatim). The
    /// Microsoft documentation could not be fetched when this was checked
    /// (2026-10-03, egress blocked), so no source backs this table; the values
    /// are in the order of magnitude of a comma (weak) and a sentence end
    /// (strong). Verify on Windows by logging <c>SilenceMSecs</c> for each
    /// strength (see docs/SPEECHD-PLAN.md, SSML provenance).
    /// </summary>
    private static int StrengthMs(string strength) => strength.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "x-weak" => 100,
        "weak" => 200,
        "medium" => 400,
        "strong" => 700,
        "x-strong" => 1000,
        _ => 400,
    };

    /// <summary>
    /// Parse <paramref name="text"/>. False when it is not an SSML document or is
    /// malformed — the caller then strips, exactly as before.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<SsmlFragment> fragments)
    {
        fragments = Array.Empty<SsmlFragment>();
        if (text is null || !Ssml.IsDocument(text)) return false;

        try
        {
            var parsed = new Parser(text).Run();
            if (parsed is null) return false;
            fragments = parsed;
            return true;
        }
        catch (Exception)
        {
            // Defence in depth for the contract above. The parser is written not
            // to throw; a client must not be able to find the input that makes it.
            return false;
        }
    }

    /// <summary>
    /// The text a document says out loud, fragments joined by a space — what
    /// <see cref="Ssml.Strip"/> would return, for callers that already hold a
    /// parse and need only the words (the espeak fallback's pre-check).
    /// </summary>
    public static string SpokenText(IReadOnlyList<SsmlFragment> fragments) =>
        string.Join(' ', fragments.Where(f => f.Kind == SsmlFragmentKind.Text).Select(f => f.Text));

    // ---------------------------------------------------------------- rate

    /// <summary>
    /// A <c>prosody rate</c> value as SAPI's −10…+10 adjustment, or null when it
    /// says nothing usable (the fragment keeps the rate around it).
    ///
    /// <para>Keywords map straight onto the scale (x-slow −6 … x-fast +6). A
    /// relative change (<c>+20%</c>, <c>-30%</c>) or a multiplier (<c>150%</c>,
    /// <c>2</c>) is converted through the SAPI scale's own definition, a factor of
    /// 3 across the full range: <c>adj = 10 · log₃(factor)</c>. <b>CHOSEN, NOT
    /// VERIFIED:</b> SAPI converts the keyword or percentage to
    /// <c>SPVSTATE.RateAdj</c> before the engine sees it; nothing in the repo
    /// records that conversion (the harness's Step 6 only checks that x-fast and
    /// x-slow do not fail) and the Microsoft docs were unreachable on 2026-10-03.
    /// These are the least certain values in this file. What IS verified is the
    /// consuming side: the engine clamps site rate + RateAdj to ±10 and applies
    /// <c>1.5^(n/10)</c> (<c>SapiEngine.ComputeSpeed</c>).</para>
    /// </summary>
    public static int? RateAdjFrom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim().ToLowerInvariant();

        switch (v)
        {
            case "x-slow": return -6;
            case "slow": return -3;
            case "medium":
            case "default": return 0;
            case "fast": return 3;
            case "x-fast": return 6;
        }

        bool signed = v[0] is '+' or '-';
        bool percent = v.EndsWith('%');
        string number = percent ? v[..^1] : v;

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            || double.IsNaN(n) || double.IsInfinity(n))
            return null;

        double factor;
        if (percent) factor = signed ? 1 + n / 100.0 : n / 100.0;
        else if (!signed) factor = n;                 // SSML 1.0: a multiplier
        else return null;                             // a signed bare number has no defined unit

        // Zero or negative speed is not a speed. Ignored rather than clamped to
        // the floor, because "-100%" is a request for silence, not for slow.
        if (factor <= 0.0001) return null;

        int adj = (int)Math.Round(10.0 * Math.Log(factor) / Math.Log(3.0));
        return Math.Clamp(adj, -MaxRateAdj, MaxRateAdj);
    }

    /// <summary>
    /// A <c>break</c>'s duration in milliseconds, clamped to
    /// <see cref="MaxBreakMs"/>. <c>time</c> wins over <c>strength</c>; neither
    /// gives a medium break, which is what SSML specifies for a bare
    /// <c>&lt;break/&gt;</c>.
    /// </summary>
    public static int BreakMs(string? time, string? strength)
    {
        if (!string.IsNullOrWhiteSpace(time) && ParseTimeMs(time) is { } ms)
            return Math.Clamp(ms, 0, MaxBreakMs);

        return string.IsNullOrWhiteSpace(strength) ? StrengthMs("medium") : StrengthMs(strength);
    }

    private static int? ParseTimeMs(string time)
    {
        string t = time.Trim().ToLowerInvariant();
        double scale = 1.0;                           // a bare number is milliseconds
        if (t.EndsWith("ms")) { t = t[..^2]; }
        else if (t.EndsWith('s')) { t = t[..^1]; scale = 1000.0; }

        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            || double.IsNaN(n) || double.IsInfinity(n) || n < 0)
            return null;

        double ms = n * scale;
        return ms >= int.MaxValue ? int.MaxValue : (int)Math.Round(ms);
    }

    // -------------------------------------------------------------- parser

    private sealed class Frame
    {
        internal required string Name;
        internal string? Lang;
        internal int Adj;
        internal bool Silent;     // text inside is not spoken (metadata, desc, a sub's content)
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly List<Frame> _stack = new();
        private readonly List<SsmlFragment> _out = new();
        private readonly System.Text.StringBuilder _text = new();
        private string? _textLang;
        private int _textAdj;
        private bool _gap;                 // a tag boundary since the last text
        private bool _rootClosed;

        private readonly Ssml.TagScanner _tags;

        internal Parser(string s)
        {
            _s = s;
            _tags = new Ssml.TagScanner(s);
        }

        private Frame Top => _stack.Count > 0 ? _stack[^1] : _root;
        private static readonly Frame _root = new() { Name = "" };

        internal List<SsmlFragment>? Run()
        {
            int i = 0;
            while (i < _s.Length)
            {
                if (_s[i] == '<')
                {
                    if (string.CompareOrdinal(_s, i, "<!--", 0, 4) == 0)
                    {
                        int close = _s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                        if (close < 0) return null;
                        i = close + 3;
                        _gap = true;
                        continue;
                    }

                    if (string.CompareOrdinal(_s, i, "<![CDATA[", 0, 9) == 0)
                    {
                        int close = _s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                        if (close < 0) return null;
                        if (!Append(_s[(i + 9)..close], decode: false)) return null;
                        i = close + 3;
                        continue;
                    }

                    if (_tags.TagEnd(i) is { } end)
                    {
                        if (!Tag(_s.AsSpan(i + 1, end - i - 1).ToString())) return null;
                        i = end + 1;
                        continue;
                    }
                }

                // A run of text up to the next '<' that opens something. A '<'
                // that opens nothing is part of the text, same as Ssml.Strip.
                int next = i + 1;
                while (next < _s.Length && !(_s[next] == '<' && OpensTag(next))) next++;
                if (!Append(_s[i..next], decode: true)) return null;
                i = next;
            }

            if (_stack.Count != 0) return null;       // an element left open
            Flush();
            return _out;
        }

        private bool OpensTag(int at) =>
            string.CompareOrdinal(_s, at, "<!--", 0, 4) == 0
            || string.CompareOrdinal(_s, at, "<![CDATA[", 0, 9) == 0
            || _tags.TagEnd(at) is not null;

        private bool Append(string raw, bool decode)
        {
            if (_rootClosed)
                return string.IsNullOrWhiteSpace(raw);   // anything after </speak> is malformed

            if (Top.Silent) return true;

            string piece = decode ? Ssml.Entities.Decode(raw) : raw;
            if (string.IsNullOrWhiteSpace(piece)) { _gap = true; return true; }

            AppendText(piece, Top.Lang, Top.Adj);
            return true;
        }

        private void AppendText(string piece, string? lang, int adj)
        {
            if (_text.Length > 0 && (lang != _textLang || adj != _textAdj)) Flush();

            if (_text.Length == 0) { _textLang = lang; _textAdj = adj; }
            else if (_gap) _text.Append(' ');

            _text.Append(piece);
            _gap = false;
        }

        private void Flush()
        {
            if (_text.Length == 0) return;
            string spoken = Ssml.CollapseWhitespace(_text.ToString());
            _text.Clear();
            if (spoken.Length > 0) _out.Add(SsmlFragment.Speak(spoken, _textLang, _textAdj));
        }

        /// <summary>Handle one tag's inner text, between '&lt;' and '&gt;'. False = malformed.</summary>
        private bool Tag(string inner)
        {
            _gap = true;

            if (inner.Length == 0) return false;
            if (inner[0] is '?' or '!') return true;          // prolog, DOCTYPE: nothing to say

            if (inner[0] == '/')
            {
                string closing = LocalName(inner[1..].Trim());
                if (_stack.Count == 0 || !string.Equals(_stack[^1].Name, closing, StringComparison.Ordinal))
                    return false;
                _stack.RemoveAt(_stack.Count - 1);
                if (_stack.Count == 0) _rootClosed = true;
                return true;
            }

            bool selfClosing = inner[^1] == '/';
            string body = selfClosing ? inner[..^1] : inner;

            int nameEnd = 0;
            while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd])) nameEnd++;
            string name = LocalName(body[..nameEnd]);
            if (name.Length == 0) return false;

            if (_rootClosed) return false;
            if (_stack.Count == 0 && name != "speak") return false;

            var attrs = Attributes(body[nameEnd..]);
            var parent = Top;
            var frame = new Frame { Name = name, Lang = parent.Lang, Adj = parent.Adj, Silent = parent.Silent };

            // xml:lang wins for its own subtree, and an unsupported language
            // CLEARS the inherited one rather than keeping it: that is what a
            // fragment tagged with an LCID Supertonic lacks does on Windows
            // (FromLcid returns null, the configured language applies).
            if (attrs.TryGetValue("xml:lang", out string? lang))
                frame.Lang = SupertonicLanguages.FromTag(lang);

            switch (name)
            {
                case "prosody":
                    if (attrs.TryGetValue("rate", out string? rate) && RateAdjFrom(rate) is { } adj)
                        frame.Adj = Math.Clamp(parent.Adj + adj, -MaxRateAdj, MaxRateAdj);
                    break;

                case "break":
                {
                    attrs.TryGetValue("time", out string? time);
                    attrs.TryGetValue("strength", out string? strength);
                    int ms = BreakMs(time, strength);
                    if (!parent.Silent && ms > 0)
                    {
                        Flush();
                        _out.Add(SsmlFragment.Pause(ms));
                    }
                    break;
                }

                case "mark":
                    // An empty name is dropped, as the Windows engine drops an
                    // empty bookmark.
                    if (!parent.Silent && attrs.TryGetValue("name", out string? mark) && !string.IsNullOrEmpty(mark))
                    {
                        Flush();
                        _out.Add(SsmlFragment.Bookmark(mark));
                    }
                    break;

                case "sub":
                    // The alias is what is said; what it replaces is not.
                    if (attrs.TryGetValue("alias", out string? alias) && !parent.Silent)
                    {
                        string said = Ssml.Entities.Decode(alias);
                        if (!string.IsNullOrWhiteSpace(said)) AppendText(said, frame.Lang, frame.Adj);
                        frame.Silent = true;
                    }
                    break;

                case "metadata":
                case "meta":
                case "desc":
                case "lexicon":
                    frame.Silent = true;
                    break;
            }

            if (!selfClosing) _stack.Add(frame);
            else if (_stack.Count == 0) _rootClosed = true;       // <speak/>

            return true;
        }

        private static string LocalName(string name)
        {
            int colon = name.IndexOf(':');
            return (colon >= 0 ? name[(colon + 1)..] : name).ToLowerInvariant();
        }

        /// <summary>
        /// name="value" pairs, quotes either kind. Lenient about everything else:
        /// a bare word is skipped, an unterminated quote ends the list.
        /// </summary>
        private static Dictionary<string, string> Attributes(string s)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                int start = i;
                while (i < s.Length && s[i] != '=' && !char.IsWhiteSpace(s[i])) i++;
                string key = s[start..i];

                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length || s[i] != '=') continue;
                i++;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;

                string value;
                if (s[i] is '"' or '\'')
                {
                    char q = s[i++];
                    int close = s.IndexOf(q, i);
                    if (close < 0) break;
                    value = s[i..close];
                    i = close + 1;
                }
                else
                {
                    int vs = i;
                    while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                    value = s[vs..i];
                }

                if (key.Length > 0) result[key] = Ssml.Entities.Decode(value);
            }

            return result;
        }
    }
}

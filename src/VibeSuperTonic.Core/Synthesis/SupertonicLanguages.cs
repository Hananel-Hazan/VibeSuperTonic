namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// The 31 languages Supertonic 3 speaks, plus their Windows LCIDs.
///
/// This table is the bridge between three vocabularies that all name the same
/// thing differently:
///   • Supertonic's two-letter code — what <c>TextToSpeech.Call</c> wants, and
///     what gets wrapped around the text as <c>&lt;de&gt;…&lt;/de&gt;</c>.
///   • The Windows LCID — what SAPI hands us per text fragment in
///     <c>SPVSTATE.LangID</c> when the client used SSML <c>xml:lang</c>, and
///     what a voice token advertises in its <c>Language</c> attribute.
///   • A human-readable name — what the Control Panel shows in a dropdown.
///
/// IMPORTANT: the code set here must stay a subset of
/// <see cref="Supertonic.Languages.Available"/> (the upstream-matching list in
/// SupertonicSdk.cs). That list also contains <c>"na"</c> — upstream's
/// language-agnostic tag — which is deliberately NOT offered here: it exists
/// for callers who don't know the language, and picking it from a dropdown
/// labelled "language" makes no sense. <see cref="Normalize"/> still lets it
/// through so a hand-edited settings.json can use it.
public sealed record SupertonicLanguage(string Code, string HexLcid, string DisplayName);

public static class SupertonicLanguages
{
    public const string Default = "en";

    /// <summary>
    /// Ordered for a dropdown: English first (the default and the best-supported),
    /// then the rest alphabetically by display name.
    ///
    /// HexLcid is the *primary* language id — the sublanguage bits are left at
    /// the "default sublanguage" value Windows uses for a bare language (eg. 409
    /// = en-US, 407 = de-DE). Supertonic has no regional variants, so a fragment
    /// tagged en-GB (809) and one tagged en-US (409) both resolve to "en" via
    /// <see cref="FromLcid"/>, which matches on the low 10 bits only.
    /// </summary>
    public static readonly SupertonicLanguage[] All =
    {
        new("en", "409", "English"),
        new("ar", "401", "Arabic"),
        new("bg", "402", "Bulgarian"),
        new("hr", "41A", "Croatian"),
        new("cs", "405", "Czech"),
        new("da", "406", "Danish"),
        new("nl", "413", "Dutch"),
        new("et", "425", "Estonian"),
        new("fi", "40B", "Finnish"),
        new("fr", "40C", "French"),
        new("de", "407", "German"),
        new("el", "408", "Greek"),
        new("hi", "439", "Hindi"),
        new("hu", "40E", "Hungarian"),
        new("id", "421", "Indonesian"),
        new("it", "410", "Italian"),
        new("ja", "411", "Japanese"),
        new("ko", "412", "Korean"),
        new("lv", "426", "Latvian"),
        new("lt", "427", "Lithuanian"),
        new("pl", "415", "Polish"),
        new("pt", "416", "Portuguese"),
        new("ro", "418", "Romanian"),
        new("ru", "419", "Russian"),
        new("sk", "41B", "Slovak"),
        new("sl", "424", "Slovenian"),
        new("es", "40A", "Spanish"),
        new("sv", "41D", "Swedish"),
        new("tr", "41F", "Turkish"),
        new("uk", "422", "Ukrainian"),
        new("vi", "42A", "Vietnamese"),
    };

    /// <summary>
    /// Every advertised LCID, semicolon-separated — the format SAPI expects in a
    /// voice token's <c>Language</c> attribute when an engine speaks more than
    /// one language. Clients that filter the voice list by language (NVDA's
    /// voice picker, <c>SpVoice.GetVoices("Language=407")</c>) only see the
    /// voice for a language that appears here.
    /// </summary>
    public static string AllHexLcidsSemicolonSeparated { get; } =
        string.Join(";", All.Select(l => l.HexLcid));

    /// <summary>
    /// Map a SAPI <c>SPVSTATE.LangID</c> to a Supertonic code, or null when we
    /// don't speak it (caller falls back to the configured language rather than
    /// failing the utterance — a German voice reading one Thai word badly beats
    /// throwing).
    ///
    /// Matches on the primary language id (low 10 bits) so every regional
    /// variant collapses onto the one model we have: 409/809/0C09 (US/UK/AU
    /// English) all → "en".
    ///
    /// Caveat inherited from Windows: primary id 0x1A covers Croatian, Serbian
    /// AND Bosnian, separated only by sublanguage. We map the whole primary id
    /// to "hr" — Supertonic has no Serbian model, and Croatian is the closest
    /// thing in the set.
    /// </summary>
    public static string? FromLcid(ushort langId)
    {
        if (langId == 0) return null;             // fragment carries no language
        int primary = langId & PrimaryLanguageMask;
        foreach (var l in All)
        {
            // Mask BOTH sides. The table stores full LCIDs (409, not 9), so
            // comparing a masked incoming id against an unmasked table entry
            // never matches and every tagged fragment silently falls back to
            // English — which is exactly what shipped-looking-fine looks like.
            if ((Convert.ToInt32(l.HexLcid, 16) & PrimaryLanguageMask) == primary) return l.Code;
        }
        return null;
    }

    /// <summary>Low 10 bits of an LCID: the language, without the region.</summary>
    private const int PrimaryLanguageMask = 0x3FF;

    /// <summary>
    /// Coerce a persisted / hand-edited language string to something the SDK
    /// will accept. Unknown values fall back to <see cref="Default"/> rather
    /// than throwing: settings.json is user-editable, and a typo there should
    /// degrade to English, not brick every Speak call.
    /// </summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Default;
        string c = code.Trim().ToLowerInvariant();
        if (c == "na") return c;                  // upstream's language-agnostic tag
        foreach (var l in All)
        {
            if (string.Equals(l.Code, c, StringComparison.Ordinal)) return l.Code;
        }
        return Default;
    }

    public static string DisplayNameFor(string code)
    {
        string c = Normalize(code);
        foreach (var l in All)
        {
            if (l.Code == c) return l.DisplayName;
        }
        return c;
    }
}

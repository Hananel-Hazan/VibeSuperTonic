using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.SpeechD;

/// <summary>What we know about a voice's gender, including that we do not.</summary>
internal enum VoiceGender
{
    /// <summary>
    /// Piper. Nothing upstream states it — the catalog carries a name, a
    /// language, a quality and a licence, and no gender — and this module does
    /// not guess one from a speaker's name. See <see cref="VoiceList.Resolve"/>
    /// for what a symbolic request does when this is all there is.
    /// </summary>
    Unknown,
    Male,
    Female,
}

/// <summary>
/// One row of <c>LIST VOICES</c>, and everything needed to speak it.
/// </summary>
/// <param name="Name">
/// What the list publishes and what comes back as <c>synthesis_voice</c>. It is
/// the module's own vocabulary rather than the daemon's, because a Supertonic
/// row names a style <em>and</em> a language and no single id in this product
/// carries both.
/// </param>
/// <param name="Language">speechd's language column, normalised the way speechd normalises it.</param>
/// <param name="Variant">Informational — the style name, or Piper's quality tier.</param>
/// <param name="RenderVoice">The qualified id for <c>vst-ctl render --voice</c>.</param>
/// <param name="RenderLanguage">
/// The language for <c>--language</c>, for Supertonic only. Null for Piper, where
/// the model <em>is</em> the language and passing one would state a second,
/// possibly conflicting, opinion about the same utterance.
/// </param>
/// <param name="Rank">
/// 1-based position among the installed voices of the same language and gender,
/// which is what turns speechd's <c>MALE1..3</c> into a voice. Position among
/// what is INSTALLED rather than the digit in the style's name: with only
/// <c>M3</c> present, <c>male1</c> has to find it or the request fails for a
/// machine that has a perfectly good male voice on it.
/// </param>
internal sealed record SpeechdVoice(
    string Name,
    string Language,
    string Variant,
    string RenderVoice,
    string? RenderLanguage,
    VoiceGender Gender,
    int Rank);

/// <summary>
/// The voice list, and the mapping between speechd's vocabulary and ours —
/// docs/SPEECHD-PLAN.md, S3 and trap 8.
///
/// <para><b>Pure, and computed per request.</b> Route A would have generated a
/// config file at install time and had to regenerate it whenever a voice was
/// added or removed; a native module answers <c>LIST VOICES</c> from whatever is
/// on disk at the moment it is asked, so nothing is written down and nothing can
/// be stale. That is the second thing route B bought.</para>
///
/// <para><b>Both of speechd's selection paths arrive here</b>, and they are not
/// alternatives — measured 2026-08-28 against speech-dispatcher 0.12.1:
/// selecting a voice by name sends <c>synthesis_voice=&lt;name&gt;</c> with no
/// <c>voice</c>, and every other client sends <c>voice=male1</c> (or another of
/// the eight symbolic names) with <c>synthesis_voice=NULL</c>. The symbolic form
/// is the DEFAULT, not the exception, so a module that read only
/// <c>synthesis_voice</c> would ignore the voice nearly every client asks
/// for.</para>
///
/// <para><b>speechd does not validate <c>synthesis_voice</c> against the list it
/// was given</b> — also measured; <c>-y no-such-voice</c> arrives verbatim. So an
/// unknown name has to be handled here rather than trusted.</para>
/// </summary>
internal static class VoiceList
{
    /// <summary>
    /// speechd's eight symbolic voice names, lowercase, as they arrive on the
    /// wire. <c>child_*</c> resolves to its gender at rank 1: this product ships
    /// no child voice, and answering a child request with an adult voice of the
    /// right gender is the "never go silent" rule applied to voice selection.
    /// </summary>
    internal static (VoiceGender Gender, int Rank)? ParseSymbolic(string? symbolic) =>
        symbolic?.Trim().ToLowerInvariant() switch
        {
            "male1" => (VoiceGender.Male, 1),
            "male2" => (VoiceGender.Male, 2),
            "male3" => (VoiceGender.Male, 3),
            "female1" => (VoiceGender.Female, 1),
            "female2" => (VoiceGender.Female, 2),
            "female3" => (VoiceGender.Female, 3),
            "child_male" => (VoiceGender.Male, 1),
            "child_female" => (VoiceGender.Female, 1),
            _ => null,
        };

    /// <summary>
    /// Lowercase, hyphenated. speechd sends <c>en-us</c> and <c>pt-br</c>; our
    /// own ids spell the same thing <c>en_US</c>, and the catalog spells it
    /// <c>en_US</c> too. Comparing them without this is how a machine with a
    /// German voice installed reports that it has none.
    /// </summary>
    internal static string NormaliseLanguage(string? language) =>
        (language ?? "").Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>The part before any region — <c>en-us</c> and <c>en-gb</c> are both <c>en</c>.</summary>
    private static string Primary(string normalised)
    {
        int dash = normalised.IndexOf('-');
        return dash < 0 ? normalised : normalised[..dash];
    }

    /// <summary>
    /// The prefix a Supertonic row's name carries, so the name says which engine
    /// it came from in a picker that shows nothing else about it.
    /// </summary>
    private const string SupertonicNamePrefix = "supertonic-";

    /// <summary>
    /// Every installed voice, as speechd rows.
    ///
    /// <para><b>Supertonic appears once per style per language</b>, which is a
    /// deliberate multiplication and the one design choice in here worth
    /// defending. Ten styles across 31 languages is 310 rows, and the instinct is
    /// that no voice list should be that long — but the distro's own
    /// <c>espeak-ng</c> publishes <b>14,805</b> (measured on this machine), so
    /// this is unremarkable by the standards of the thing consuming it. Listing
    /// the styles once under English instead would halve nothing that matters and
    /// would hide the product entirely from a user whose screen reader is set to
    /// German — which is the audience this whole feature exists for, since Orca
    /// groups the picker by language.</para>
    ///
    /// <para><b>A Piper voice's language comes from its own id</b>, not from the
    /// catalog. Every catalog id is prefixed by its language code — checked
    /// across all 43 — and the id is the only source that also covers a voice
    /// installed by hand, which the catalog deliberately does not describe
    /// (<c>en_US-lessac</c> is the live example). One source that always works
    /// beats two that usually agree.</para>
    /// </summary>
    internal static IReadOnlyList<SpeechdVoice> Build(
        IReadOnlyList<string> supertonicStyles,
        IReadOnlyList<string> piperVoiceIds,
        IReadOnlyList<string> supertonicLanguages)
    {
        var rows = new List<SpeechdVoice>();

        // Ranked once, over the styles, so a style's rank is the same in every
        // language rather than being recomputed 31 times.
        var males = 0;
        var females = 0;
        var ranked = new List<(string Style, VoiceGender Gender, int Rank)>();
        foreach (string style in supertonicStyles)
        {
            var gender = StyleGender(style);
            int rank = gender switch
            {
                VoiceGender.Male => ++males,
                VoiceGender.Female => ++females,
                _ => 0,
            };
            ranked.Add((style, gender, rank));
        }

        foreach (string language in supertonicLanguages)
        {
            string lang = NormaliseLanguage(language);
            foreach (var (style, gender, rank) in ranked)
            {
                rows.Add(new SpeechdVoice(
                    Name: SupertonicNamePrefix + style + "-" + lang,
                    Language: lang,
                    Variant: style,
                    RenderVoice: VoiceId.ForSupertonic(style).ToString(),
                    RenderLanguage: lang,
                    Gender: gender,
                    Rank: rank));
            }
        }

        // Ranked per language, because that is the set a symbolic request is
        // resolved within.
        var perLanguage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in piperVoiceIds)
        {
            string lang = PiperLanguage(id);
            perLanguage.TryGetValue(lang, out int seen);
            perLanguage[lang] = ++seen;

            rows.Add(new SpeechdVoice(
                Name: id,
                Language: lang,
                Variant: PiperQuality(id),
                RenderVoice: VoiceId.ForPiper(id).ToString(),
                RenderLanguage: null,
                Gender: VoiceGender.Unknown,
                Rank: seen));
        }

        return rows;
    }

    /// <summary>
    /// <c>M1</c>..<c>M5</c> and <c>F1</c>..<c>F5</c> — Supertonic's own names for
    /// its styles, which is the only place in this product where a voice's gender
    /// is stated by anyone but us. Anything else is <see cref="VoiceGender.Unknown"/>
    /// rather than a guess, because a style file is a file on disk and a user can
    /// put another one there.
    /// </summary>
    private static VoiceGender StyleGender(string style) =>
        style.Length >= 2 && char.IsAsciiDigit(style[1])
            ? char.ToUpperInvariant(style[0]) switch
            {
                'M' => VoiceGender.Male,
                'F' => VoiceGender.Female,
                _ => VoiceGender.Unknown,
            }
            : VoiceGender.Unknown;

    /// <summary>
    /// <c>en_US-lessac-medium</c> → <c>en-us</c>. The id's first segment is its
    /// language code by construction upstream.
    /// </summary>
    private static string PiperLanguage(string id)
    {
        int dash = id.IndexOf('-');
        return NormaliseLanguage(dash <= 0 ? id : id[..dash]);
    }

    /// <summary><c>en_US-lessac-medium</c> → <c>medium</c>, for the variant column.</summary>
    private static string PiperQuality(string id)
    {
        int dash = id.LastIndexOf('-');
        return dash < 0 || dash == id.Length - 1 ? "piper" : id[(dash + 1)..];
    }

    /// <summary>
    /// Which installed voice a client asked for, or null to leave the choice to
    /// the daemon's configured default.
    ///
    /// <para><b>Null is not a failure and must not become one.</b> An unresolvable
    /// request — a <c>synthesis_voice</c> naming something uninstalled, a language
    /// nothing covers — means "speak this with whatever you have", which produces
    /// the user's own default voice. Refusing instead would drop the utterance
    /// into the espeak fallback, so a single stale setting in a screen reader
    /// would replace the neural voice everywhere with no error anyone sees.</para>
    /// </summary>
    internal static SpeechdVoice? Resolve(
        IReadOnlyList<SpeechdVoice> voices, string? synthesisVoice, string? symbolic, string? language)
    {
        if (voices.Count == 0) return null;

        // An exact name wins over everything. It is the only request that names a
        // voice rather than describing one, and speechd sends it only when a
        // human picked that row.
        if (!string.IsNullOrWhiteSpace(synthesisVoice))
        {
            var named = voices.FirstOrDefault(
                v => string.Equals(v.Name, synthesisVoice.Trim(), StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named;
        }

        // A LANGUAGE ON ITS OWN SELECTS NOTHING, and that is the correction the
        // measurement forced. speechd sends `language=en-us` on EVERY SET whether
        // or not anybody chose it — it is the session default, not a request — so
        // treating it as one would have every client that never picked a voice
        // silently replace the user's configured default with whichever row
        // happened to sort first. A voice is asked for by name or by symbolic
        // name; the language only narrows that.
        if (ParseSymbolic(symbolic) is not { } request) return null;

        var candidates = ByLanguage(voices, NormaliseLanguage(language));
        if (candidates.Count == 0) return null;

        // The gender is a filter only where it is KNOWN. A Piper voice states no
        // gender, so excluding it from a `male1` request would answer "no voice"
        // on a machine whose only installed voice is a perfectly good one — and
        // including it under a label it may contradict is the lesser wrong, since
        // the alternative the caller falls back to is the espeak buzz.
        var gendered = candidates.Where(v => v.Gender == request.Gender).ToList();
        var pool = gendered.Count > 0 ? gendered : candidates;

        // The requested rank, or the closest one below it: `male3` on a machine
        // with two male styles is the second, not nothing.
        return pool.FirstOrDefault(v => v.Rank == request.Rank)
               ?? pool.OrderByDescending(v => v.Rank).First();
    }

    /// <summary>
    /// The voices for a language, best match first: an exact <c>en-us</c> before a
    /// bare <c>en</c>, and a bare <c>en</c> before nothing at all. With no
    /// language asked for, everything is a candidate.
    /// </summary>
    private static List<SpeechdVoice> ByLanguage(IReadOnlyList<SpeechdVoice> voices, string want)
    {
        if (want.Length == 0) return voices.ToList();

        var exact = voices.Where(v => v.Language == want).ToList();
        if (exact.Count > 0) return exact;

        string primary = Primary(want);
        var related = voices.Where(v => Primary(v.Language) == primary).ToList();
        return related;
    }
}

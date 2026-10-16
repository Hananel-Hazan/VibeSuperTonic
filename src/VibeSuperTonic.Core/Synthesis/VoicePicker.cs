using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>One row of a picker level: what is stored, and what is shown.</summary>
/// <param name="Value">The stable key — an engine name, a language code, a voice id, a speaker index.</param>
/// <param name="Label">What the control shows.</param>
public sealed record PickerRow(string Value, string Label)
{
    /// <summary>
    /// The label, because <b>this is what a control displays when nothing tells
    /// it otherwise</b> — and a record's compiler-generated ToString does not
    /// display a label, it displays its own source code.
    ///
    /// <para>Reported 2026-08-30: the Tune tab's dropdowns read
    /// <c>PickerRow { Value = All, Label = All voices }</c>. Avalonia's ComboBox
    /// renders an item through ToString unless it is given a template, so every
    /// picker on that tab — scope, engine, language, voice, speaker — showed the
    /// debugger's view of the object to the user. The Label was correct the
    /// whole time and nothing was reading it.</para>
    ///
    /// <para>Fixed here rather than with a template on each control, because a
    /// template fixes the five pickers that exist and this fixes the sixth.</para>
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// Where the picker stands: the engine, language, voice and speaker chosen.
/// </summary>
public sealed record VoiceSelection(string Engine, string Language, string Voice, int? Speaker);

/// <summary>
/// The voice control, as a CASCADE rather than a list.
///
/// <para><b>A flat list of every installed voice does not survive contact with
/// the catalog.</b> The Voices tab lists what is on disk and what could be
/// downloaded, so the daemon reports Supertonic as one row — ten styles over one
/// shared 383 MB model set is one download and one licence. But the Tune tab's
/// control CHOOSES, and there a single row means nine styles that cannot be
/// selected at all. Expanding everything is no better: with every English Piper
/// voice installed, <c>en_US-libritts</c> and <c>libritts_r</c> contribute
/// <b>904 speakers each</b> and <c>en_GB-vctk</c> another 109, so the flat list
/// runs past eighteen hundred rows and the ten Supertonic styles are lost inside
/// it.</para>
///
/// <para>So the choice is made in steps — <b>engine, then language, then voice,
/// then speaker</b> — and each step only offers what the one before it left
/// standing. Nothing shows a list of everything, at any point.</para>
///
/// <para>Two asymmetries are deliberate and neither can be smoothed away.
/// Supertonic has no language level: it is one model set that takes its language
/// per utterance, which is the separate Language field on this same tab, and its
/// "speakers" are style NAMES that are part of the voice id. A Piper voice IS a
/// language, and its speakers are INDICES whose names are labels.</para>
/// </summary>
public static class VoicePicker
{
    /// <summary>The engines with something installed, in a stable order.</summary>
    public static IReadOnlyList<PickerRow> Engines(IReadOnlyList<VoiceEntry>? installed)
    {
        var seen = new List<PickerRow>();
        foreach (var entry in installed ?? [])
        {
            string engine = EngineOf(entry);
            if (seen.All(r => r.Value != engine)) seen.Add(new PickerRow(engine, Pretty(engine)));
        }
        return seen;
    }

    /// <summary>
    /// The languages that engine offers. Empty for Supertonic, which is one
    /// model for all of them — an emptiness the UI reads as "no language level
    /// here" rather than "no languages", and the two must not be confused.
    /// </summary>
    public static IReadOnlyList<PickerRow> Languages(IReadOnlyList<VoiceEntry>? installed, string engine)
    {
        if (IsSupertonic(engine)) return [];

        var rows = new List<PickerRow>();
        foreach (var entry in installed ?? [])
        {
            if (EngineOf(entry) != engine) continue;
            string code = entry.LanguageCode ?? LanguageFromId(entry.Id);
            if (code.Length == 0 || rows.Any(r => r.Value == code)) continue;
            rows.Add(new PickerRow(code, entry.Language is { Length: > 0 } l ? l : code));
        }
        rows.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        return rows;
    }

    /// <summary>
    /// The voices inside one engine and language. For Supertonic these are its
    /// styles, which is what makes all ten selectable; <paramref name="language"/>
    /// is ignored there because the styles do not have one.
    /// </summary>
    public static IReadOnlyList<PickerRow> Voices(
        IReadOnlyList<VoiceEntry>? installed, string engine, string language)
    {
        var rows = new List<PickerRow>();

        foreach (var entry in installed ?? [])
        {
            if (EngineOf(entry) != engine) continue;

            if (IsSupertonic(engine))
            {
                foreach (string style in entry.SpeakerNames ?? [])
                    rows.Add(new PickerRow(VoiceId.ForSupertonic(style).ToString(), style));
                continue;
            }

            string code = entry.LanguageCode ?? LanguageFromId(entry.Id);
            if (language.Length > 0 && code != language) continue;

            // The id WITHOUT a speaker: the speaker is the next level down, and a
            // voice that carries one here would be listed once per speaker again.
            rows.Add(new PickerRow(
                VoiceId.Parse(entry.Id).WithSpeaker(null).ToString(),
                Describe(entry)));
        }

        return rows;
    }

    /// <summary>
    /// The speakers of one voice, or empty when it has one — which is most of
    /// them. Index N is speaker id N; the name is a label and may repeat.
    /// </summary>
    public static IReadOnlyList<PickerRow> Speakers(IReadOnlyList<VoiceEntry>? installed, string voice)
    {
        if (voice.Length == 0) return [];

        var wanted = VoiceId.Parse(voice).WithSpeaker(null).ToString();
        foreach (var entry in installed ?? [])
        {
            if (IsSupertonic(EngineOf(entry))) continue;
            if (VoiceId.Parse(entry.Id).WithSpeaker(null).ToString() != wanted) continue;
            if (entry.SpeakerNames is not { Count: > 1 } names) return [];

            var rows = new List<PickerRow>(names.Count);
            for (int sid = 0; sid < names.Count; sid++)
                rows.Add(new PickerRow(sid.ToString(), $"{sid} — {names[sid]}"));
            return rows;
        }
        return [];
    }

    /// <summary>
    /// Where the cascade should stand for the voice currently in force.
    ///
    /// <para>A settings file written before engine-qualified ids holds a bare
    /// <c>M4</c>, which parses to an id with no engine and matches nothing called
    /// <c>supertonic:M4</c>. Resolving it against what is installed is what stops
    /// the picker offering the same voice twice, once under each spelling.</para>
    /// </summary>
    public static VoiceSelection Locate(IReadOnlyList<VoiceEntry>? installed, string inForce)
    {
        var wanted = VoiceId.Parse(inForce ?? "");
        string engine = wanted.Engine switch
        {
            VoiceEngine.Piper => "piper",
            VoiceEngine.Supertonic => "supertonic",
            _ => GuessEngine(installed, wanted),
        };

        string voice = IsSupertonic(engine)
            ? VoiceId.ForSupertonic(wanted.Bare).ToString()
            : wanted with { Engine = VoiceEngine.Piper, Speaker = null } is var id ? id.ToString() : "";

        string language = "";
        if (!IsSupertonic(engine))
        {
            foreach (var entry in installed ?? [])
            {
                if (VoiceId.Parse(entry.Id).WithSpeaker(null).ToString() != voice) continue;
                language = entry.LanguageCode ?? LanguageFromId(entry.Id);
                break;
            }
            if (language.Length == 0) language = LanguageFromId(voice);
        }

        return new VoiceSelection(engine, language, voice, wanted.Speaker);
    }

    /// <summary>
    /// Which speaker a row should show: the one its id carries, clamped to what
    /// the voice actually has.
    ///
    /// <para><b>The id is the only honest source.</b> The Voices tab builds a
    /// speaker dropdown per row and used to seed it from the row's id — which
    /// carried no speaker, so a voice configured with speaker 6 displayed 0, and
    /// pressing Use appeared to reset the choice. The daemon now puts the
    /// configured speaker on the row it is configured for; this is the reading
    /// half of that, and the clamp is what stops a settings file naming speaker
    /// 950 of a 904-speaker voice from selecting nothing at all.</para>
    /// </summary>
    public static int SpeakerIndex(VoiceEntry? entry)
    {
        if (entry is null) return 0;
        int count = entry.SpeakerNames?.Count ?? entry.Speakers ?? 1;
        int wanted = VoiceId.Parse(entry.Id).Speaker ?? 0;
        return count <= 0 ? 0 : Math.Clamp(wanted, 0, count - 1);
    }

    /// <summary>The id to save, from where the cascade stands.</summary>
    public static string Compose(string voice, int? speaker)
    {
        if (voice.Length == 0) return "";
        var id = VoiceId.Parse(voice);
        return id.Engine == VoiceEngine.Supertonic
            ? id.WithSpeaker(null).ToString()
            : id.WithSpeaker(speaker).ToString();
    }

    private static string EngineOf(VoiceEntry entry) =>
        entry.Engine is { Length: > 0 } e ? e : VoiceId.Parse(entry.Id).Engine switch
        {
            VoiceEngine.Piper => "piper",
            VoiceEngine.Supertonic => "supertonic",
            _ => "supertonic",
        };

    private static bool IsSupertonic(string engine) =>
        string.Equals(engine, "supertonic", StringComparison.OrdinalIgnoreCase);

    private static string Pretty(string engine) =>
        IsSupertonic(engine) ? "Supertonic" : engine.Length == 0 ? engine
            : char.ToUpperInvariant(engine[0]) + engine[1..];

    /// <summary>
    /// A voice's language from its id — <c>piper:en_US-ljspeech-high</c> is
    /// <c>en_US</c>. Only a fallback: the daemon states the code, and this is for
    /// a reply that did not.
    /// </summary>
    private static string LanguageFromId(string id)
    {
        string bare = VoiceId.Parse(id).Bare;
        int dash = bare.IndexOf('-');
        return dash <= 0 ? "" : bare[..dash];
    }

    private static string Describe(VoiceEntry entry)
    {
        string bare = VoiceId.Parse(entry.Id).Bare;

        // "en_US-ljspeech-high" reads as "ljspeech · high" once the language is
        // the level above it, and the licence belongs here because it is the
        // thing a user chooses ON: five of the English voices are NonCommercial.
        var parts = bare.Split('-');
        string name = parts.Length > 1 ? string.Join('-', parts[1..]) : bare;
        string licence = LicenceLabel(entry) is { Length: > 0 } l ? $"  ·  {l}" : "";
        string speakers = entry.SpeakerNames is { Count: > 1 } s ? $"  ·  {s.Count} speakers" : "";
        return $"{name}{speakers}{licence}";
    }

    /// <summary>
    /// The licence, short enough for a dropdown row.
    ///
    /// <para>The CLASS rather than the name, because a third of the catalog's
    /// licence names are bare URLs — "https://creativecommons.org/licenses/by/4.0/"
    /// is not something to put in a picker. The full terms and their link belong
    /// on the download gate in the Voices tab, which is where the user agrees to
    /// them; here it only has to be enough to choose on, and the distinction that
    /// matters when choosing is whether a voice is NonCommercial.</para>
    /// </summary>
    private static string LicenceLabel(VoiceEntry entry) => entry.LicenceClass switch
    {
        "public" => "public domain",
        "by" => "CC BY",
        "by-sa" => "CC BY-SA",
        "nc" => "NonCommercial",
        "apache" => "Apache-2.0",
        "agpl" => "AGPL",
        _ => entry.Licence ?? "",
    };

    private static string GuessEngine(IReadOnlyList<VoiceEntry>? installed, VoiceId wanted)
    {
        foreach (var entry in installed ?? [])
        {
            var id = VoiceId.Parse(entry.Id);
            if (IsSupertonic(EngineOf(entry))
                && (entry.SpeakerNames ?? []).Contains(wanted.Bare, StringComparer.OrdinalIgnoreCase))
            {
                return "supertonic";
            }
            if (string.Equals(id.Bare, wanted.Bare, StringComparison.OrdinalIgnoreCase))
                return EngineOf(entry);
        }
        return "supertonic";
    }
}

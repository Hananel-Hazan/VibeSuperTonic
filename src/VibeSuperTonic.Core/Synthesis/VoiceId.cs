namespace VibeSuperTonic.Core.Synthesis;

/// <summary>Which engine renders a voice, when the id says so.</summary>
public enum VoiceEngine
{
    /// <summary>
    /// A bare id, carrying no engine. Resolved by the rule that has governed
    /// since P3 — if it names an installed Piper voice it is one, otherwise it is
    /// a Supertonic style — which is what keeps every <c>DefaultVoice</c> written
    /// before P4 working untouched.
    /// </summary>
    Unspecified,
    Supertonic,
    Piper,
}

/// <summary>
/// A voice id, optionally qualified by the engine that renders it:
/// <c>supertonic:M1</c>, <c>piper:en_US-lessac-high</c>, or a bare <c>M1</c>.
///
/// <para><b>Why a qualified form exists at all, when the voice already selects
/// the engine.</b> The routing rule is unchanged and is not up for
/// re-litigation: a directory under <c>models/piper/</c> is a Piper voice and
/// everything else is a Supertonic style. What the prefix buys is a settings file
/// and a catalog that say what they mean <em>without consulting the filesystem</em>.
/// A bare <c>M1</c> in <c>settings.json</c> is only a Supertonic style because no
/// Piper voice happens to be installed under that name; the same string means
/// something different on a machine where one is. That ambiguity is harmless for
/// the styles Supertonic ships and stops being harmless the moment a catalog of
/// 43 downloadable ids exists.</para>
///
/// <para><b>An unknown prefix is not an engine.</b> <c>pipper:x</c> parses as the
/// bare id <c>pipper:x</c>, not as a failure and not as Piper. The alternative —
/// rejecting it — turns a typo in a settings file into a daemon that will not
/// speak, where this turns it into a voice that is not found, which is a sentence
/// the user can act on. Colons do not occur in Supertonic styles or upstream
/// Piper keys, so nothing legitimate is caught by this.</para>
///
/// <para>In Core because the string crosses the settings file, the protocol and
/// two platforms, and because Windows will eventually read the same
/// <c>settings.json</c> this writes.</para>
/// </summary>
/// <param name="Engine">The engine named by the prefix, or <see cref="VoiceEngine.Unspecified"/>.</param>
/// <param name="Bare">The id with any prefix and speaker removed — what names the model directory.</param>
/// <param name="Speaker">
/// Which speaker of a multi-speaker voice, from a <c>#N</c> suffix, or null.
///
/// <para><b>Part of the id rather than a setting of its own</b>, decided in P4.
/// A speaker only means anything relative to one voice —
/// <c>en_GB-vctk-medium</c> has 109 and <c>de_DE-thorsten-high</c> has none — so
/// a separate <c>SpeakerId</c> key would go on describing the previous voice the
/// moment someone changed voices, and would do it silently, since speaker 12 of
/// a single-speaker voice is not an error but a clamp. Carrying it here means
/// switching voices cannot leave it behind, and
/// <c>vst-ctl speak --voice piper:en_GB-vctk-medium#12</c> works for free.</para>
///
/// <para><see cref="Bare"/> deliberately excludes it: the bare id names a
/// directory and a file, and <c>en_GB-vctk-medium#12.onnx</c> is not one.</para>
/// </param>
public readonly record struct VoiceId(VoiceEngine Engine, string Bare, int? Speaker = null)
{
    public const string SupertonicPrefix = "supertonic:";
    public const string PiperPrefix = "piper:";

    /// <summary>Separates a voice from the speaker inside it.</summary>
    public const char SpeakerSeparator = '#';

    /// <summary>True when the id named its engine rather than leaving it to be inferred.</summary>
    public bool IsQualified => Engine != VoiceEngine.Unspecified;

    /// <summary>
    /// Parse a voice id. Never throws on shape — an unrecognised prefix is part
    /// of the bare id. Throws only on null or blank, which is a caller bug.
    /// </summary>
    public static VoiceId Parse(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string s = id.Trim();

        if (s.StartsWith(PiperPrefix, StringComparison.OrdinalIgnoreCase))
            return Qualified(VoiceEngine.Piper, s[PiperPrefix.Length..], s);
        if (s.StartsWith(SupertonicPrefix, StringComparison.OrdinalIgnoreCase))
            return Qualified(VoiceEngine.Supertonic, s[SupertonicPrefix.Length..], s);

        var (bare, speaker) = SplitSpeaker(s);
        return new VoiceId(VoiceEngine.Unspecified, bare, speaker);

        // "piper:" with nothing after it names no voice. Treating it as the bare
        // id keeps the "never throws on shape" promise, and the lookup that
        // follows fails with the string the user actually wrote.
        static VoiceId Qualified(VoiceEngine engine, string rest, string whole)
        {
            rest = rest.Trim();
            if (rest.Length == 0) return new VoiceId(VoiceEngine.Unspecified, whole);

            var (bare, speaker) = SplitSpeaker(rest);
            return new VoiceId(engine, bare, speaker);
        }
    }

    /// <summary>
    /// Split a trailing <c>#N</c>. Anything that is not a non-negative integer
    /// stays part of the id — a voice called <c>foo#bar</c> is a voice that will
    /// not be found, which is a better failure than one silently renamed to
    /// <c>foo</c>.
    /// </summary>
    private static (string Bare, int? Speaker) SplitSpeaker(string s)
    {
        int at = s.LastIndexOf(SpeakerSeparator);
        if (at <= 0 || at == s.Length - 1) return (s, null);

        return int.TryParse(s[(at + 1)..], out int speaker) && speaker >= 0
            ? (s[..at], speaker)
            : (s, null);
    }

    /// <summary>Parse, or false on null/blank. For settings files, where absent is normal.</summary>
    public static bool TryParse(string? id, out VoiceId voice)
    {
        if (string.IsNullOrWhiteSpace(id)) { voice = default; return false; }
        voice = Parse(id);
        return true;
    }

    /// <summary>A qualified id for an engine that is known.</summary>
    public static VoiceId ForPiper(string bare, int? speaker = null) =>
        new(VoiceEngine.Piper, Require(bare), speaker);

    /// <inheritdoc cref="ForPiper"/>
    public static VoiceId ForSupertonic(string bare) => new(VoiceEngine.Supertonic, Require(bare));

    private static string Require(string bare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bare);
        return bare.Trim();
    }

    /// <summary>
    /// Whether this id could name a Piper voice at all — false only when it
    /// explicitly says Supertonic. The store still decides; this is what saves
    /// asking it about an id that has already ruled itself out.
    /// </summary>
    public bool MayBePiper => Engine != VoiceEngine.Supertonic;

    /// <summary>
    /// The canonical string: qualified when the engine is known, bare when it is
    /// not. Round-trips through <see cref="Parse"/>.
    /// </summary>
    public override string ToString()
    {
        string tail = Speaker is { } n ? Bare + SpeakerSeparator + n : Bare;
        return Engine switch
        {
            VoiceEngine.Piper => PiperPrefix + tail,
            VoiceEngine.Supertonic => SupertonicPrefix + tail,
            _ => tail,
        };
    }

    /// <summary>The same voice with a speaker chosen, or with none.</summary>
    public VoiceId WithSpeaker(int? speaker) => this with { Speaker = speaker };
}

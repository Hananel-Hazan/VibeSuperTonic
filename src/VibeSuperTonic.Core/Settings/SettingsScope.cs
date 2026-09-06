using System.Text.Json;
using System.Text.Json.Nodes;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Core.Settings;

/// <summary>Who a setting applies to.</summary>
public enum SettingsScopeKind
{
    /// <summary>Every voice, both engines — the file's own values.</summary>
    All,

    /// <summary>Every voice of one engine.</summary>
    Engine,

    /// <summary>One voice.</summary>
    Voice,
}

/// <summary>
/// Settings that apply to all voices, to one engine's voices, or to one voice.
///
/// <para><b>Overrides are merged as raw JSON, and that is the whole design.</b>
/// A typed override has to express "not set" with a sentinel — the Windows
/// engine uses <c>0</c> — and a sentinel cannot say <c>VolumeTrimDb: 0</c>,
/// which is exactly the value someone picks when they want no trim at all. On
/// the raw object an absent key is absent and a present one is a value, with
/// nothing to confuse the two.</para>
///
/// <para>Both sides of the product use this: the daemon resolves what a voice
/// speaks with, and the Tune tab decides where a save lands. One implementation,
/// because a settings file whose reader and writer disagree is a file that loses
/// people's settings.</para>
/// </summary>
public static class SettingsScope
{
    /// <summary>The section holding per-voice overrides. Shared with the Windows engine.</summary>
    public const string PerVoice = "PerVoice";

    /// <summary>The section holding per-engine overrides.</summary>
    public const string PerEngine = "PerEngine";

    /// <summary>
    /// The keys that describe HOW A VOICE SOUNDS, and may therefore be scoped.
    ///
    /// <para>Everything else in the file is about this MACHINE rather than this
    /// voice — the execution provider, the thread budget, whether to read the
    /// clipboard, which voice is default. Scoping those would let "all Piper
    /// voices" change which voice speaks, or what the daemon does to the
    /// computer.</para>
    /// </summary>
    public static readonly string[] Keys =
    [
        "Language", "TotalStep", "EngineSpeed", "DspRate", "VolumeTrimDb",
        "RateClampCeiling", "SynthesisSilenceSec", "MaxChunkChars", "MinChunkChars",
        "InterChunkSilenceMs",
    ];

    public static bool IsScoped(string key) => Array.IndexOf(Keys, key) >= 0;

    /// <summary>The engine name a Piper scope goes under.</summary>
    public const string PiperEngine = "piper";

    /// <summary>
    /// Scoped keys that describe the SUPERTONIC MODEL and mean nothing to a
    /// Piper voice.
    ///
    /// <para><c>TotalStep</c> is a diffusion step count and Piper has no
    /// diffusion; <c>Language</c> selects one of Supertonic's 31 languages and a
    /// Piper voice IS its language, baked into the model. The Tune tab greys both
    /// out when a Piper voice is selected, for exactly this reason.</para>
    ///
    /// <para><b>Reported 2026-09-06, and it made a warning that could not be
    /// cleared.</b> The tab greyed the field and saved it anyway, so
    /// <c>PerEngine.piper.TotalStep 6</c> sat under a global 12 — and the
    /// benchmark's staleness guard, which asks the voice in force for its step
    /// count, compared a Supertonic profile's 12 against a number no Piper voice
    /// has ever run at. The banner offered a re-measure, and the sweep it offered
    /// could not run either (see <c>BenchmarkVoiceTests</c>).</para>
    ///
    /// <para><b>Filtered on the read, not only on the write.</b> Doing it here
    /// means an install that already carries the key stops reporting a number
    /// nothing uses, without anybody having to re-save the file — and a key that
    /// an engine cannot act on is not a setting whose loss anyone can hear.</para>
    /// </summary>
    public static readonly string[] SupertonicOnlyKeys = ["TotalStep", "Language"];

    /// <summary>Whether <paramref name="key"/> can do anything for an engine's voices.</summary>
    public static bool AppliesTo(string key, string engine) =>
        !string.Equals(engine, PiperEngine, StringComparison.Ordinal)
        || Array.IndexOf(SupertonicOnlyKeys, key) < 0;

    /// <summary>
    /// How a section names a voice: the bare style for Supertonic, the qualified
    /// id for Piper, and never the speaker.
    ///
    /// <para>The style form is what the Windows engine's own <c>PerVoice</c>
    /// already uses, so one settings.json keeps working on both platforms. The
    /// speaker is left out because it selects who speaks rather than how, and
    /// 904 sections for one voice is not a file anybody can read.</para>
    /// </summary>
    public static string KeyFor(VoiceId voice) =>
        voice.Engine == VoiceEngine.Piper
            ? new VoiceId(VoiceEngine.Piper, voice.Bare).ToString()
            : voice.Bare;

    /// <summary>The engine name a scope section uses.</summary>
    public static string EngineNameFor(VoiceId voice, bool isPiper) =>
        isPiper || voice.Engine == VoiceEngine.Piper ? "piper" : "supertonic";

    /// <summary>One override section, or null when it is not there.</summary>
    public static JsonObject? Section(JsonObject? root, string section, string name) =>
        root?[section] is JsonObject sections && sections[name] is JsonObject values ? values : null;

    /// <summary>Whether a scope has any override of its own.</summary>
    public static bool Has(JsonObject? root, SettingsScopeKind kind, string engine, string voiceKey) =>
        kind switch
        {
            SettingsScopeKind.Engine => Section(root, PerEngine, engine)?.Count > 0,
            SettingsScopeKind.Voice => Section(root, PerVoice, voiceKey)?.Count > 0,
            _ => false,
        };

    /// <summary>
    /// The file's values with this engine's overrides on top, and this voice's
    /// on top of those. Sections themselves are dropped from the result: what
    /// comes back is one flat object describing one voice.
    /// </summary>
    public static JsonObject Merge(JsonObject root, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);

        var merged = new JsonObject();
        foreach (var (name, value) in root)
        {
            if (name is PerVoice or PerEngine) continue;
            merged[name] = value?.DeepClone();
        }

        Overlay(merged, Section(root, PerEngine, engine), engine);
        Overlay(merged, Section(root, PerVoice, voiceKey), engine);
        return merged;

        static void Overlay(JsonObject target, JsonObject? source, string engine)
        {
            if (source is null) return;
            foreach (var (name, value) in source)
            {
                // Only what may be scoped. An override section that carried a
                // DefaultVoice or a Provider would be a voice quietly changing
                // which voice speaks, or what the machine does.
                if (!IsScoped(name)) continue;

                // And only what this engine can act on — see SupertonicOnlyKeys
                // for the warning a Piper scope's TotalStep made unclearable.
                if (!AppliesTo(name, engine)) continue;

                target[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>
    /// The object the Tune tab's boxes are FILLED FROM — the file itself for
    /// <see cref="SettingsScopeKind.All"/>, the merged view for the others.
    ///
    /// <para><b>This exists so that no caller chooses a source.</b> The tab used
    /// to fill its fields from two places — the file, on every refresh, and the
    /// merged view, on every scope change — and the refresh ran second, so a
    /// scoped value was replaced by the file's within the same repaint. Reported
    /// 2026-08-30 as "the volume will not stick": the file held
    /// <c>PerEngine.supertonic.VolumeTrimDb 5</c> and the box showed the top
    /// level's 0, and the next save wrote that 0 back over the 5. One function
    /// answering "which object" is what makes that unrepresentable.</para>
    /// </summary>
    public static JsonObject Resolve(JsonObject root, SettingsScopeKind kind, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);
        return kind == SettingsScopeKind.All ? root : Merge(root, engine, voiceKey);
    }

    /// <summary>
    /// The scoped keys this voice does NOT take from the file, because its own
    /// engine or voice section sets them.
    ///
    /// <para>What it is for: a person editing "All voices" is editing values
    /// this voice may not use, and nothing on screen would otherwise say so. It
    /// is the same file's own doing — writing a scope saves every field into it,
    /// so one deliberate override leaves the whole tab shadowed for that voice
    /// — and the symptom is a setting that saves correctly, reads back
    /// correctly, and changes nothing anybody can hear.</para>
    /// </summary>
    public static IReadOnlyList<string> Shadowed(JsonObject? root, string engine, string voiceKey)
    {
        var shadowed = new List<string>();
        foreach (string key in Keys)
        {
            // A key the engine cannot act on shadows nothing, because the merge
            // above now ignores it. Listing it would tell a user that changing
            // the global step count will not reach their Piper voice — true of
            // every Piper voice, and nothing to do with their override.
            if (!AppliesTo(key, engine)) continue;

            if (Section(root, PerEngine, engine)?.ContainsKey(key) == true
                || Section(root, PerVoice, voiceKey)?.ContainsKey(key) == true)
                shadowed.Add(key);
        }
        return shadowed;
    }

    /// <summary>
    /// The object a save should write into, created if it is not there.
    /// <see cref="SettingsScopeKind.All"/> is the file itself.
    /// </summary>
    public static JsonObject Target(JsonObject root, SettingsScopeKind kind, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (kind == SettingsScopeKind.All) return root;

        string section = kind == SettingsScopeKind.Engine ? PerEngine : PerVoice;
        string name = kind == SettingsScopeKind.Engine ? engine : voiceKey;

        if (root[section] is not JsonObject sections)
        {
            sections = new JsonObject();
            root[section] = sections;
        }
        if (sections[name] is not JsonObject values)
        {
            values = new JsonObject();
            sections[name] = values;
        }
        return values;
    }

    /// <summary>
    /// What a scope resolves to with nothing of its own — the file for an engine
    /// scope, and the file with that engine's overlay for a voice scope.
    ///
    /// <para>This is the thing a saved value is compared against, and the reason
    /// a voice scope is compared against its ENGINE rather than against the file:
    /// a voice sitting at its engine's value has nothing of its own to say, and
    /// recording it there would pin it against a later change to the engine.</para>
    /// </summary>
    public static JsonObject Inherited(
        JsonObject root, SettingsScopeKind kind, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);

        var inherited = new JsonObject();
        foreach (var (name, value) in root)
        {
            if (name is PerVoice or PerEngine) continue;
            inherited[name] = value?.DeepClone();
        }

        if (kind == SettingsScopeKind.Voice && Section(root, PerEngine, engine) is { } section)
        {
            foreach (var (name, value) in section)
            {
                if (!IsScoped(name) || !AppliesTo(name, engine)) continue;
                inherited[name] = value?.DeepClone();
            }
        }

        return inherited;
    }

    /// <summary>
    /// Write a scope's values, keeping <b>only what the scope does not already
    /// inherit</b>.
    ///
    /// <para><b>Reported 2026-09-06 as "piper does not obey the speed change."</b>
    /// The editor used to write every field it showed into whichever scope was
    /// selected, so one deliberate override pinned all nine — the reporting
    /// user's <c>PerEngine.piper</c> was a complete snapshot of the tab. After
    /// that, editing "All voices" saved correctly, read back correctly, and
    /// changed nothing anybody could hear, because every value the global boxes
    /// set was shadowed by a copy of itself.</para>
    ///
    /// <para>Two consequences beyond the fix. A scope becomes undoable by setting
    /// the value back to the inherited one — the override disappears rather than
    /// pinning the old number. And a key the engine cannot act on is never taken
    /// at all, which is what stops a Piper scope acquiring the <c>TotalStep</c>
    /// that made the benchmark warn about a step count nothing runs.</para>
    /// </summary>
    /// <param name="values">
    /// Key to value, or key to null to clear it. Keys that are not
    /// <see cref="IsScoped"/> belong to the file rather than to a scope and are
    /// rejected, so a caller cannot put a Provider inside a voice.
    /// </param>
    public static void Save(
        JsonObject root, SettingsScopeKind kind, string engine, string voiceKey,
        IReadOnlyDictionary<string, JsonNode?> values)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
        {
            if (!IsScoped(key))
                throw new ArgumentException(
                    $"'{key}' is not a scoped key — it describes this machine rather than " +
                    "this voice, and belongs at the file's top level. See SettingsScope.Keys.",
                    nameof(values));
        }

        // "All voices" is the file itself. Nothing is above it, so there is
        // nothing to compare against and every value is written.
        if (kind == SettingsScopeKind.All)
        {
            foreach (var (key, value) in values)
            {
                if (value is null) root.Remove(key);
                else root[key] = value;
            }
            return;
        }

        var inherited = Inherited(root, kind, engine, voiceKey);
        var target = Target(root, kind, engine, voiceKey);

        foreach (var (key, value) in values)
        {
            if (value is null || !AppliesTo(key, engine) || SameValue(inherited[key], value))
                target.Remove(key);
            else
                target[key] = value;
        }

        // An override section that ended up empty is deleted rather than left as
        // an empty object: "this voice has settings" must mean it has some.
        if (target.Count == 0) Clear(root, kind, engine, voiceKey);
    }

    /// <summary>
    /// Whether two settings values are the same value.
    ///
    /// <para>Numerically for numbers, because a file people edit by hand holds
    /// <c>1.2</c> and <c>"1.2"</c> and <c>1</c> and <c>1.0</c> for the same key —
    /// and an editor showing all of them as the same box must not read one back
    /// as an override of another.</para>
    /// </summary>
    private static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;

        if (AsNumber(a) is { } x && AsNumber(b) is { } y) return x == y;
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);

        // Through the JSON text rather than GetValue<double>(): a node parsed
        // from the file is JsonElement-backed and converts, while one this
        // process just built with JsonValue.Create is backed by the CLR type it
        // was given — and an Int32 or Int64 THROWS on a request for a double.
        // Both kinds meet here on every save.
        static double? AsNumber(JsonNode node) => node.GetValueKind() switch
        {
            JsonValueKind.Number => Parse(node.ToJsonString()),
            JsonValueKind.String => Parse(node.GetValue<string>()),
            _ => null,
        };

        static double? Parse(string? text) =>
            double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed) ? parsed : null;
    }

    /// <summary>
    /// Remove a scope's overrides entirely, and the section with them when it
    /// empties — a settings file should not accumulate the shape of choices
    /// somebody has undone.
    /// </summary>
    public static void Clear(JsonObject root, SettingsScopeKind kind, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (kind == SettingsScopeKind.All) return;

        string section = kind == SettingsScopeKind.Engine ? PerEngine : PerVoice;
        string name = kind == SettingsScopeKind.Engine ? engine : voiceKey;

        if (root[section] is not JsonObject sections) return;
        sections.Remove(name);
        if (sections.Count == 0) root.Remove(section);
    }
}

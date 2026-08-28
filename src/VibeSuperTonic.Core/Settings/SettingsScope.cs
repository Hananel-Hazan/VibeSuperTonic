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

        Overlay(merged, Section(root, PerEngine, engine));
        Overlay(merged, Section(root, PerVoice, voiceKey));
        return merged;

        static void Overlay(JsonObject target, JsonObject? source)
        {
            if (source is null) return;
            foreach (var (name, value) in source)
            {
                // Only what may be scoped. An override section that carried a
                // DefaultVoice or a Provider would be a voice quietly changing
                // which voice speaks, or what the machine does.
                if (!IsScoped(name)) continue;
                target[name] = value?.DeepClone();
            }
        }
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

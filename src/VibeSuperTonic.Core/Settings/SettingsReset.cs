using System.Text.Json.Nodes;

namespace VibeSuperTonic.Core.Settings;

/// <summary>
/// "Reset to defaults" for the Tune tab, which has to mean something different
/// at each level of <see cref="SettingsScopeKind"/>.
///
/// <para><b>Why not Windows' version.</b> The Windows Advanced tab's reset saves
/// a blank <c>EngineSettings</c>, which wipes every per-voice override along with
/// the knobs ("nuke everything", its own comment says). That is tolerable when
/// there is one list of knobs; here a voice's overrides are the product of a
/// deliberate choice made on another day, and a button labelled for one voice
/// must not erase another's. So the scope decides what goes:</para>
/// <list type="bullet">
///   <item><b>Only this voice</b> — that voice's section, and nothing else.</item>
///   <item><b>All voices of an engine</b> — that engine's section, and nothing else.</item>
///   <item><b>All voices</b> — the file's own tuning values. The per-engine and
///   per-voice sections are left alone: they have their own "Clear these
///   overrides" button, and the confirmation says so.</item>
/// </list>
///
/// <para><b>"Reset" means remove the key.</b> An absent key IS the default, in the
/// daemon and in the Windows engine alike, so nothing here needs to know what
/// the defaults are and none can drift from <c>LinuxSettings</c>. A file that
/// accumulates <c>"TotalStep": 8</c> is a file nobody can tell from a tuned one.
/// Keys this build has never heard of, and the voice choice, are never touched.</para>
/// </summary>
public static class SettingsReset
{
    /// <summary>
    /// The keys that describe this MACHINE rather than a voice, and that the Tune
    /// tab edits at the file's top level whatever scope is showing. Reset with
    /// the global scope only — a voice cannot have its own.
    /// </summary>
    public static readonly string[] MachineKeys =
    [
        "MaxCpuPercent", "OnnxInterOpThreads", "Provider", "ClipboardFallback", "GpuOnBattery",
    ];

    /// <summary>Reset one scope.</summary>
    /// <returns>The keys that were actually removed — empty when nothing was set.</returns>
    public static IReadOnlyList<string> Apply(
        JsonObject root, SettingsScopeKind kind, string engine, string voiceKey)
    {
        ArgumentNullException.ThrowIfNull(root);

        var removed = new List<string>();

        if (kind != SettingsScopeKind.All)
        {
            var section = SettingsScope.Section(
                root,
                kind == SettingsScopeKind.Engine ? SettingsScope.PerEngine : SettingsScope.PerVoice,
                kind == SettingsScopeKind.Engine ? engine : voiceKey);

            if (section is not null) removed.AddRange(section.Select(p => p.Key));
            SettingsScope.Clear(root, kind, engine, voiceKey);
            return removed;
        }

        foreach (string key in SettingsScope.Keys.Concat(MachineKeys))
        {
            if (root.Remove(key)) removed.Add(key);
        }
        return removed;
    }

    /// <summary>What the confirmation should say will happen, for the scope on screen.</summary>
    public static string Describe(SettingsScopeKind kind, string engineLabel, string voiceLabel) => kind switch
    {
        SettingsScopeKind.Voice =>
            $"Remove every setting {voiceLabel} has of its own, so it follows {engineLabel} voices "
            + "and All voices again? Other voices are not touched.",
        SettingsScopeKind.Engine =>
            $"Remove every setting {engineLabel} voices have of their own, so they follow All "
            + "voices again? Overrides on a single voice are not touched.",
        _ =>
            "Put every value in All voices back to its default, including the CPU share, inter-op "
            + "threads, provider and the two checkboxes? Your voice choice is kept, and so are the "
            + "overrides on an engine or a single voice — use \"Clear these overrides\" on those.",
    };
}

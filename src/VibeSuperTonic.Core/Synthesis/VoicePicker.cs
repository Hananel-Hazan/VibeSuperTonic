using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// The rows a "which voice speaks" control offers, built from the daemon's
/// installed list.
///
/// <para><b>The voice LIST and the voice CHOICE are different questions, and
/// answering both from one shape is what broke.</b> The Voices tab lists what
/// is on disk and what could be downloaded, so Supertonic is correctly ONE row:
/// ten styles over one shared 383 MB model set is one download and one licence,
/// and ten rows with a size each would state something false about the disk.
/// But the Tune tab's picker is the control that chooses which voice speaks —
/// and there the same single row means nine of the ten styles cannot be
/// selected at all. A user with every style installed saw exactly one.</para>
///
/// <para>So a picker expands any entry that carries speakers into one row per
/// speaker, and leaves everything else alone.</para>
///
/// <para><b>And it qualifies the voice in force.</b> A settings file written
/// before engine-qualified ids holds a bare <c>M4</c>, which parses to an
/// id with no engine and therefore matches nothing in a list of
/// <c>supertonic:M4</c> — so the picker showed BOTH, one of them a phantom that
/// named the same voice twice.</para>
/// </summary>
public static class VoicePicker
{
    /// <summary>
    /// The ids to offer, in the daemon's order, and which one is selected.
    /// </summary>
    /// <param name="installed">The daemon's installed voices.</param>
    /// <param name="inForce">
    /// The configured voice, in whatever form the settings hold — bare or
    /// qualified.
    /// </param>
    public static (IReadOnlyList<string> Ids, string Selected) Rows(
        IReadOnlyList<VoiceEntry>? installed, string inForce)
    {
        var ids = new List<string>();

        foreach (var entry in installed ?? [])
        {
            var id = VoiceId.Parse(entry.Id);

            // Supertonic's speakers are STYLE NAMES and its id carries one of
            // them; Piper's are indices, where index N is sid N and the name is
            // a label. Two different meanings behind one field, so they cannot
            // share a formatting rule.
            if (entry.SpeakerNames is { Count: > 1 } names)
            {
                if (id.Engine == VoiceEngine.Supertonic)
                {
                    foreach (string style in names) ids.Add(VoiceId.ForSupertonic(style).ToString());
                    continue;
                }

                for (int sid = 0; sid < names.Count; sid++) ids.Add(id.WithSpeaker(sid).ToString());
                continue;
            }

            ids.Add(entry.Id);
        }

        string selected = Qualify(inForce, ids);

        // A configured voice that is not installed is still listed, and first:
        // the setting names something that is not there, and the one control
        // whose job is to show what is chosen is where that has to be visible.
        if (!ids.Contains(selected, StringComparer.Ordinal)) ids.Insert(0, selected);

        return (ids, selected);
    }

    /// <summary>
    /// Put <paramref name="inForce"/> into the same form the rows use.
    ///
    /// <para>A bare id is matched against the rows by its bare part, which is
    /// what turns a legacy <c>M4</c> into the <c>supertonic:M4</c> that is
    /// actually offered. If nothing matches, it is left exactly as written —
    /// inventing an engine for a voice nobody can find would replace "this
    /// voice is not installed" with a different, wronger sentence.</para>
    /// </summary>
    private static string Qualify(string inForce, IReadOnlyList<string> ids)
    {
        if (string.IsNullOrWhiteSpace(inForce)) return "";

        var wanted = VoiceId.Parse(inForce);
        string qualified = wanted.ToString();
        if (ids.Contains(qualified, StringComparer.Ordinal)) return qualified;

        if (wanted.Engine == VoiceEngine.Unspecified)
        {
            foreach (string id in ids)
            {
                var row = VoiceId.Parse(id);
                if (string.Equals(row.Bare, wanted.Bare, StringComparison.OrdinalIgnoreCase)
                    && row.Speaker == wanted.Speaker)
                {
                    return id;
                }
            }
        }

        return qualified;
    }
}

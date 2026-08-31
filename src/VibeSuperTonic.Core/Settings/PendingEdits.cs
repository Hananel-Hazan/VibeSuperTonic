namespace VibeSuperTonic.Core.Settings;

/// <summary>
/// Whether a reload is allowed to replace what somebody is editing.
///
/// <para><b>Four bugs in three days had one shape</b>, and this is the rule all
/// four broke: a speaker that snapped back to 0, a rate that reverted when Save
/// landed on the same repaint as a refresh, a volume trim that read from the
/// wrong scope, and a pronunciation rule list that a tab switch quietly threw
/// away. Every one was a refresh overwriting an edit that had not been saved
/// yet, and every one was reported by a person rather than caught by a
/// test.</para>
///
/// <para><see cref="ScopedFields"/> enforces this for a form of boxes;
/// this is the same rule for a document a tab holds in memory — a list of
/// rules, a tree — where the caller owns the filling and only needs to be told
/// whether it may.</para>
///
/// <para><b>Why a refresh happens at all when nobody asked for one:</b> these
/// tabs re-read on every visit, and a toolkit re-attaches a tab's content each
/// time it is selected. So switching to another tab and back is a reload, which
/// is not something a user would ever describe as an action they took.</para>
/// </summary>
public sealed class PendingEdits
{
    /// <summary>Whether something has been edited since the last load or save.</summary>
    public bool Touched { get; private set; }

    /// <summary>
    /// Fill from storage — unless that would discard an edit.
    /// </summary>
    /// <returns>
    /// True when <paramref name="fill"/> ran. False means the caller is showing
    /// unsaved work, and <b>should say so on screen</b>: a tab that silently
    /// ignores the file is as confusing as one that silently discards the edit.
    /// </returns>
    public bool Load(Action fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (Touched) return false;
        fill();
        return true;
    }

    /// <summary>Somebody changed something.</summary>
    public void Edited() => Touched = true;

    /// <summary>A save landed: what is on screen is what is in the file.</summary>
    public void Saved() => Touched = false;

    /// <summary>
    /// Throw the edits away on purpose, and fill. The one path that is allowed
    /// to lose work, because it is the one the user pressed a button to reach.
    /// </summary>
    public void Discard(Action fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        Touched = false;
        fill();
    }
}

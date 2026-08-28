namespace VibeSuperTonic.Core.Install;

/// <summary>
/// What a change to the install invalidates. Flags, because one move usually
/// invalidates several things and the user wants one sentence about each.
/// </summary>
[Flags]
public enum InstallImpact
{
    None = 0,

    /// <summary>
    /// Anything outside this folder that names a path inside it: the desktop
    /// entry, the hotkey configuration that launches through it, and the Speech
    /// Dispatcher module config. <b>These cannot be repaired from in here</b> —
    /// see <see cref="InstallCheck"/>.
    /// </summary>
    Bindings = 1,

    /// <summary>The measured thread count and provider — <c>benchmark.json</c>.</summary>
    Benchmark = 2,

    /// <summary>Per-voice <c>length_scale</c> calibration, measured on this machine.</summary>
    Calibration = 4,

    /// <summary>Which voices are installed, and where they are.</summary>
    Voices = 8,
}

/// <summary>
/// Where this install is and what it is running on — the facts that, when one of
/// them changes, mean something outside this folder is now pointing at the wrong
/// place.
/// </summary>
/// <param name="BaseDir">The directory holding the binaries.</param>
/// <param name="StoreRoot">Where <c>models/</c> and <c>data/</c> live. Differs from
/// <paramref name="BaseDir"/> only for an AppImage.</param>
/// <param name="ModelsRoot">The models directory, which a <c>--models</c> override can move.</param>
/// <param name="AppImageFile">The <c>.AppImage</c> this ran from, or empty.</param>
/// <param name="Home">
/// The user's home. An AppImage's portable <c>.home</c> is one, so this changing
/// means the desktop configuration being read is a different one.
/// </param>
/// <param name="MachineId">The derived per-machine id — see the daemon's <c>MachineFacts</c>.</param>
/// <param name="LogicalProcessors">Core count, which is what a thread pick is about.</param>
/// <param name="Version">
/// Recorded for diagnostics and <b>deliberately not compared</b>. An upgrade
/// replaces the binaries in place: every path that pointed at this folder still
/// does, so nothing outside it needs rebinding, and treating an upgrade as a move
/// would put a banner in front of every user on every release for no action they
/// could take. What an upgrade can invalidate — a changed model set, a changed
/// <c>TotalStep</c> — the benchmark's own staleness check already covers.
/// </param>
public sealed record InstallIdentity(
    string BaseDir,
    string StoreRoot,
    string ModelsRoot,
    string AppImageFile,
    string Home,
    string MachineId,
    int LogicalProcessors,
    string Version);

/// <summary>One thing that is not what it was, said in a sentence a user can act on.</summary>
/// <param name="What">The sentence.</param>
/// <param name="Impact">What it invalidates.</param>
public sealed record InstallChange(string What, InstallImpact Impact);

/// <summary>
/// The install as it was when it last checked out, with the date it was recorded.
///
/// <para><b>The point of storing this is the fast path, not the slow one.</b> The
/// daemon is frequently started BY a hotkey press, and the press must not wait:
/// comparing eight strings and an integer against a small JSON file is the whole
/// cost when nothing has moved, which is every start but a handful in the life of
/// an install.</para>
/// </summary>
public sealed record InstallFingerprint(InstallIdentity Identity, string RecordedUtc)
{
    /// <summary>
    /// What has changed since this was recorded, or empty when nothing has.
    ///
    /// <para><b>Sentences rather than a bool</b>, the same choice
    /// <c>BenchmarkProfile.StalenessAgainst</c> made and for the same reason: the
    /// causes want different things from the user. A moved folder means re-run the
    /// bind script; a different machine means re-measure; a moved store means
    /// neither, and the voice list simply follows it.</para>
    /// </summary>
    public IReadOnlyList<InstallChange> ChangesAgainst(InstallIdentity now)
    {
        ArgumentNullException.ThrowIfNull(now);
        var changes = new List<InstallChange>();

        // A DIFFERENT MACHINE IS REPORTED ALONE. A portable folder carried to
        // another machine changes its paths too, and listing "the program moved"
        // beside it would describe a move that never happened — the folder is
        // where its owner put it, on a machine that has never bound a hotkey to
        // it or measured anything about it. One true sentence beats three.
        if (!string.Equals(Identity.MachineId, now.MachineId, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                "this folder was last used on a different machine",
                InstallImpact.Bindings | InstallImpact.Benchmark | InstallImpact.Calibration));
            return changes;
        }

        if (!string.Equals(Identity.BaseDir, now.BaseDir, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                $"the program moved — it was in {Identity.BaseDir}",
                InstallImpact.Bindings));
        }

        // Separately from BaseDir, because an AppImage's BaseDir is a temporary
        // mount that is SUPPOSED to change every run: the file is the durable
        // thing, and renaming it is the move that breaks a shortcut.
        if (!string.Equals(Identity.AppImageFile, now.AppImageFile, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                Identity.AppImageFile.Length == 0
                    ? "this is now running as an AppImage"
                    : $"the AppImage moved or was renamed — it was {Identity.AppImageFile}",
                InstallImpact.Bindings));
        }

        if (!string.Equals(Identity.Home, now.Home, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                $"the home directory changed — it was {Identity.Home}",
                InstallImpact.Bindings));
        }

        if (!string.Equals(Identity.StoreRoot, now.StoreRoot, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                $"the models and settings are in a different place — they were in {Identity.StoreRoot}",
                InstallImpact.Voices));
        }

        // Separately again: --models can move this without the store moving.
        else if (!string.Equals(Identity.ModelsRoot, now.ModelsRoot, StringComparison.Ordinal))
        {
            changes.Add(new InstallChange(
                $"the models directory changed — it was {Identity.ModelsRoot}",
                InstallImpact.Voices));
        }

        // Reached only on the same machine, so this is a CPU that gained or lost
        // cores rather than a different box — a VM resized, or a core disabled.
        if (Identity.LogicalProcessors != now.LogicalProcessors)
        {
            changes.Add(new InstallChange(
                $"this machine had {Identity.LogicalProcessors} logical processors and now has {now.LogicalProcessors}",
                InstallImpact.Benchmark));
        }

        return changes;
    }
}

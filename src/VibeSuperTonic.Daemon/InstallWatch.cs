using VibeSuperTonic.Core.Install;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The startup check: is this install where it was last time, and if not, what
/// stopped being true?
///
/// <para><b>The fast path is the design.</b> The daemon is frequently started by
/// a hotkey press and R-5 says a press must never be silent, so a start where
/// nothing moved must cost approximately nothing: one small file read, eight
/// string comparisons, and no probing of anything. Everything expensive is
/// reached only once the comparison has already said something changed.</para>
///
/// <para><b>It heals nothing, and that is a decision rather than a gap.</b> The
/// things a move breaks all live OUTSIDE this folder — the desktop entry, the
/// desktop's hotkey configuration, a Speech Dispatcher module config — and the
/// project already knows what happens when this program writes those on its own:
/// setting a KDE shortcut over D-Bus SIGABRT'd <c>kwin_wayland</c> and killed the
/// session, and writing <c>kglobalshortcutsrc</c> by hand does not reach a
/// <c>kglobalaccel</c> that is already running. So this reports precisely, names
/// the script that fixes it, and touches nothing it does not own.</para>
///
/// <para>In the daemon rather than Core for the usual reason: every fact here
/// comes from a path policy — R-12.</para>
/// </summary>
internal static class InstallWatch
{
    /// <summary>Where we are now.</summary>
    internal static InstallIdentity Current(string modelsRoot, string version) =>
        new(
            BaseDir: LinuxDataPaths.BaseDir,
            StoreRoot: LinuxDataPaths.StoreRoot,
            ModelsRoot: modelsRoot,
            AppImageFile: LinuxDataPaths.AppImageFile ?? "",
            Home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "",
            MachineId: MachineFacts.MachineId(),
            LogicalProcessors: Environment.ProcessorCount,
            Version: version);

    /// <summary>
    /// Compare against what was recorded, then record where we are now.
    ///
    /// <para><b>The fingerprint is rewritten even when it changed</b>, so a move
    /// is reported once and not on every start for the rest of the install's
    /// life. The user is told, the advice stands until they act on it, and the
    /// banner does not become wallpaper. What keeps the advice honest afterwards
    /// is <see cref="BrokenLaunchers"/>, which is measured rather than
    /// remembered: a shortcut that is still broken tomorrow still says so.</para>
    /// </summary>
    internal static InstallCheck Run(string dataDir, string modelsRoot, string version,
        BenchmarkProfile? profile, BenchmarkMachine machineNow)
    {
        var now = Current(modelsRoot, version);
        string path = Path.Combine(dataDir, InstallStore.FileName);

        InstallFingerprint? recorded = null;
        try { recorded = InstallStore.Load(path); }
        catch (ArgumentException) { /* a caller bug in a check that must not stop a start */ }

        var changes = recorded?.ChangesAgainst(now) ?? Array.Empty<InstallChange>();
        var benchmarkStale = profile?.StalenessAgainst(machineNow) ?? Array.Empty<string>();

        // ONLY WHEN SOMETHING MOVED. Reading the desktop entry on every start
        // would put a file read on the hotkey path to answer a question whose
        // answer cannot have changed — nothing rewrites a .desktop but us and the
        // user, and a user who rewrote it moved something.
        var broken = changes.Count > 0 ? BrokenLaunchers() : Array.Empty<string>();

        InstallStore.Save(path, new InstallFingerprint(now, DateTime.UtcNow.ToString("O")));

        return new InstallCheck(changes, benchmarkStale, broken, FirstRun: recorded is null);
    }

    /// <summary>
    /// Desktop-entry commands naming a program that is not there.
    ///
    /// <para>This is the one thing in the check that is <b>measured</b>. A moved
    /// folder means the hotkeys are probably dead; a launcher whose <c>Exec</c>
    /// names a missing file means they certainly are, and a user who is told
    /// "certainly" will go and fix it.</para>
    ///
    /// <para>Never throws and never blocks: an unreadable desktop file is no
    /// finding at all, because the alternative is a daemon that fails to start
    /// over the contents of a file in someone else's directory.</para>
    /// </summary>
    private static IReadOnlyList<string> BrokenLaunchers()
    {
        var broken = new List<string>();

        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "";
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(home, ".local", "share");

            string desktop = Path.Combine(dataHome, "applications", "vibesupertonic.desktop");
            if (!File.Exists(desktop)) return broken;

            foreach (string line in File.ReadAllLines(desktop))
            {
                if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;

                // The program is the first word. Everything after it is the verb
                // the shortcut invokes, which is not a path and must not be
                // stat-ed — `Exec=/opt/vst/vst-ctl read` names one file.
                string command = line["Exec=".Length..].Trim();
                string program = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";

                // Only absolute paths. A bare name is resolved through $PATH by
                // the desktop environment, and deciding it is missing from in
                // here would mean re-implementing that lookup to produce a
                // warning we are not sure about.
                if (program.Length == 0 || program[0] != '/') continue;

                if (!File.Exists(program) && !broken.Contains(program))
                    broken.Add(program);
            }
        }
        catch (Exception) { /* not our file, and not worth a failed start */ }

        return broken;
    }
}

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The copy of <c>vst-ctl</c> that lives outside the AppImage, and the file that
/// tells it where the AppImage went.
///
/// <para><b>Why a copy at all.</b> <c>vst-ctl</c> is NativeAOT for one reason,
/// and the packer asserts it in as many words: a managed apphost works perfectly
/// and costs ~100 ms of runtime startup on every hotkey press, against a 150 ms
/// press-to-feedback budget. Binding the key at the AppImage instead costs a
/// squashfs mount per press — measured at +14.7 ms, which passes, but is still
/// 14.7 ms paid forever for a 4 MB file copy. The copy is a complete client
/// rather than a shim, because a static binary has nothing to be separated
/// from.</para>
///
/// <para><b>Why it is re-checked on every start.</b> A copy is a thing that goes
/// stale. Upgrading means replacing one <c>.AppImage</c> file, and nothing in
/// that gesture touches <c>~/.local/bin</c> — so without this, a user who
/// upgrades keeps talking to their new daemon through last month's client, which
/// is exactly the mismatch the packer's version-agreement assertion exists to
/// prevent, arriving through the one door the packer cannot see. Both the daemon
/// and the window call this at startup, so any way of waking the product up
/// repairs it.</para>
///
/// <para><b>The sidecar.</b> A copied <c>vst-ctl</c> has no <c>vibesupertonicd</c>
/// beside it, and autostarting the daemon on the first press is
/// [R-5](docs/LINUX-PORT-ARCHIVE.md#r-5) — the rule that keeps a hotkey from
/// silently doing nothing. So the copy is accompanied by one line of text naming
/// the AppImage it came from, which the client runs as
/// <c>&lt;image&gt; daemon</c>.</para>
/// </summary>
internal static class AppImageClient
{
    /// <summary>
    /// <c>~/.local/bin</c> — on the default <c>PATH</c> of every desktop
    /// distribution this targets, and writable without asking for a password.
    /// </summary>
    public static string BinDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");

    public static string ClientPath => Path.Combine(BinDir, "vst-ctl");

    /// <summary>One absolute path, one newline. Read by <c>vst-ctl</c> itself.</summary>
    public static string SidecarPath => Path.Combine(BinDir, "vst-ctl.appimage");

    /// <summary>
    /// Install or refresh the client, and say what happened — or null when
    /// nothing needed doing, which is every start after the first.
    /// </summary>
    /// <param name="appImageFile">The <c>.AppImage</c> this process is running from.</param>
    /// <param name="payloadDir">Where <c>vst-ctl</c> sits inside the mounted image.</param>
    /// <param name="version">What this build reports, to compare against the copy.</param>
    public static string? EnsureInstalled(string appImageFile, string payloadDir, string version)
    {
        string source = Path.Combine(payloadDir, "vst-ctl");
        if (!File.Exists(source)) return null;              // not an install we composed

        string? installed = TryVersionOf(ClientPath);
        bool sidecarCurrent = ReadSidecar() == appImageFile;

        if (installed == version && sidecarCurrent) return null;

        try
        {
            Directory.CreateDirectory(BinDir);

            // Temp-and-rename, in the same directory so the rename cannot cross
            // a filesystem and degrade into a copy. A half-written client is a
            // hotkey that does nothing, and the press that finds it is by
            // definition one the user is making right now.
            string temp = Path.Combine(BinDir, $".vst-ctl.{Environment.ProcessId}");
            File.Copy(source, temp, overwrite: true);
            File.SetUnixFileMode(temp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temp, ClientPath, overwrite: true);

            File.WriteAllText(SidecarPath, appImageFile + "\n");

            // Three reasons to have done work, and they are not the same event.
            // Saying "replaced: it reported 0.2.9, this build is 0.2.9" — which
            // is what one sentence for all three produced — reads as a bug in
            // the sentence, and invites the reader to distrust the next one.
            return installed is null
                ? $"installed {ClientPath} ({version}) for the hotkeys"
                : installed != version
                    ? $"replaced {ClientPath}: it reported {installed}, this build is {version}"
                    : $"repointed {ClientPath} at {appImageFile}";
        }
        catch (Exception ex)
        {
            // Never fatal. The product still works through the AppImage itself,
            // one squashfs mount slower, and a daemon that refused to start over
            // a convenience copy would be the very failure this exists to avoid.
            return $"could not install {ClientPath}: {ex.GetType().Name}: {ex.Message}. " +
                   "The hotkeys still work if they are bound to the AppImage itself.";
        }
    }

    private static string? ReadSidecar()
    {
        try { return File.Exists(SidecarPath) ? File.ReadAllText(SidecarPath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>
    /// What the installed copy says it is, or null when there is not one or it
    /// cannot answer. Asked of the binary rather than inferred from its size or
    /// its timestamp, because the question is exactly "would this client agree
    /// with this daemon".
    /// </summary>
    private static string? TryVersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;

            string output = p.StandardOutput.ReadToEnd().Trim();
            // Bounded: a wedged binary must not hold up a daemon start.
            if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }
}

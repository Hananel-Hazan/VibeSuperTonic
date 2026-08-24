namespace VibeSuperTonic.Daemon;

/// <summary>
/// Where this install reads and writes. **Portable by default** — everything
/// lives under the directory of the executable, so the folder can be copied to
/// a USB stick or another machine and resume with every setting intact.
///
/// <para>This is the <c>IHostConfig</c> row of the seam table, and the Linux
/// counterpart of the engine's
/// <c>VibeSuperTonic.Engine.Settings.DataPaths</c>. It lives in the daemon
/// rather than in Core for the same reason the registry reader does not:
/// [R-12] keeps platform path policy out of Core, and Core is not allowed to
/// express a platform at all.</para>
///
/// <para><b>The layout mirrors the shipped Windows one exactly</b>, because it
/// is the same product and a user moving between them should not have to learn
/// a second one. Taken from a real portable install rather than from the
/// packer:</para>
///
/// <code>
///   VibeSuperTonic/
///     vibesupertonicd            &lt;- BaseDir is this directory
///     vst-ctl
///     models/onnx/               &lt;- ModelsDir
///     models/voice_styles/
///     data/settings.json         &lt;- DataDir
///     data/pronunciations.json
///     data/logs/
/// </code>
///
/// <para><b>The plan was wrong about this and is corrected here.</b> Phase 4b
/// as written specified <c>$XDG_CONFIG_HOME/vibesupertonic</c> and
/// <c>$XDG_DATA_HOME/vibesupertonic</c>. Those are the right answer for a
/// distro-packaged application and the wrong answer for this one: state under
/// <c>$HOME</c> does not travel with the folder, so a portable install would
/// silently lose every setting the moment it moved to another machine — which
/// is the whole point of the product. XDG paths are not used anywhere.</para>
///
/// <para>The daemon's previous default was worse still and is also gone: models
/// defaulted to
/// <c>Environment.SpecialFolder.LocalApplicationData/vibesupertonic/models</c>,
/// i.e. <c>~/.local/share</c>, so a portable folder carrying 383 MB of models
/// beside the binary would have ignored them and looked in the home directory.</para>
/// </summary>
internal static class LinuxDataPaths
{
    public const string DefaultFolderName = "data";

    /// <summary>
    /// The folder that holds <c>models/</c> and <c>data/</c> when the product is
    /// an AppImage and cannot put them beside its executable. Named for the
    /// product rather than for the file, so renaming
    /// <c>VibeSuperTonic-0.2.9-x86_64.AppImage</c> to <c>tts.AppImage</c> does not
    /// orphan 383 MB of models.
    /// </summary>
    public const string AppImageStoreFolderName = "VibeSuperTonic";

    /// <summary>Where the store went, and the sentence <c>config</c> reports.</summary>
    public sealed record StorePick(string Root, string Reason);

    private static StorePick? _store;

    /// <summary>
    /// The <c>.AppImage</c> file this process was started from, or null for an
    /// ordinary install.
    ///
    /// <para>The AppImage runtime exports <c>$APPIMAGE</c>. It is validated
    /// rather than trusted — an absolute path to a file that exists — because
    /// this variable decides where 383 MB of models are written, it is inherited
    /// by every child process, and a daemon started by <c>vst-ctl</c> from a
    /// shell that once ran an AppImage would otherwise adopt a store belonging
    /// to something else.</para>
    /// </summary>
    public static string? AppImageFile { get; } = ReadAppImageFile();

    /// <summary>True when <see cref="BaseDir"/> is a read-only squashfs mount.</summary>
    public static bool IsAppImage => AppImageFile is not null;

    private static string? ReadAppImageFile()
    {
        string? raw = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            string full = Path.GetFullPath(raw.Trim());
            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Where <c>models/</c> and <c>data/</c> live, with the reason it is there.
    ///
    /// <para>For an ordinary install this is <see cref="BaseDir"/> and always was:
    /// everything beside the executable, which is the whole portability
    /// story.</para>
    /// </summary>
    public static StorePick Store => _store ??= ResolveStore(
        AppImageFile,
        BaseDir,
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Directory.Exists,
        CanCreateIn);

    public static string StoreRoot => Store.Root;

    /// <summary>
    /// The rule, as a pure function so it can be tested without an AppImage, a
    /// home directory or a read-only mount.
    ///
    /// <para><b>An existing store beats a preferred location, and that is the
    /// whole subtlety.</b> The obvious implementation picks "beside the AppImage
    /// if writable, else XDG" and is wrong in a way nobody would report as a
    /// bug: move the file from <c>~/Downloads</c> to <c>~/Apps</c> — which is
    /// exactly what a person does after trying it — and the models, the settings,
    /// the pronunciation rules and the benchmark profile all vanish, the
    /// first-run screen returns, and 383 MB downloads again into the new
    /// location. Looking for a store that already exists before choosing where a
    /// new one goes costs two <c>stat</c> calls and removes that failure
    /// entirely.</para>
    ///
    /// <para>Beside the file comes first when neither exists, because that is the
    /// portable promise the tarball makes and the reason this product resolves
    /// everything from its own directory: an AppImage on a USB stick with its
    /// store beside it still travels. XDG is the fallback for the case that
    /// cannot work — a read-only medium, or a directory owned by someone
    /// else.</para>
    /// </summary>
    public static StorePick ResolveStore(
        string? appImageFile,
        string baseDir,
        string? xdgDataHome,
        string? home,
        Func<string, bool> dirExists,
        Func<string, bool> canCreateIn)
    {
        if (appImageFile is null)
            return new StorePick(baseDir, "beside the executable");

        string beside = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(appImageFile)) ?? "/",
            AppImageStoreFolderName);

        string xdgRoot = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome!
            : Path.Combine(string.IsNullOrWhiteSpace(home) ? "/tmp" : home!, ".local", "share");
        string xdg = Path.Combine(xdgRoot, "vibesupertonic");

        // Existing first, in both places, before either is created.
        if (HasStore(beside, dirExists)) return new StorePick(beside, "beside the AppImage");
        if (HasStore(xdg, dirExists))    return new StorePick(xdg, "under XDG data — nothing beside the AppImage");

        return canCreateIn(beside)
            ? new StorePick(beside, "new, beside the AppImage")
            : new StorePick(xdg, "new, under XDG data — the AppImage's own directory is not writable");
    }

    /// <summary>
    /// A directory counts as a store when it holds either half of one. Models
    /// alone happens on a fresh install whose first run was interrupted before
    /// anything was saved; data alone happens whenever someone changes a setting
    /// before downloading. Requiring both would discard a store in exactly the
    /// two states a person is most likely to be in.
    /// </summary>
    private static bool HasStore(string root, Func<string, bool> dirExists) =>
        dirExists(Path.Combine(root, "models")) || dirExists(Path.Combine(root, DefaultFolderName));

    /// <summary>
    /// Whether <paramref name="dir"/> could be created and written. Probes the
    /// nearest existing ancestor, because the store itself usually does not exist
    /// yet — asking whether a non-existent directory is writable always answers
    /// no, which would send every fresh AppImage to XDG.
    /// </summary>
    private static bool CanCreateIn(string dir)
    {
        try
        {
            string? probe = Directory.Exists(dir) ? dir : Path.GetDirectoryName(dir);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe)) return false;

            string file = Path.Combine(probe, $".vst-write-probe-{Environment.ProcessId}");
            File.WriteAllText(file, "");
            File.Delete(file);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Directory holding the daemon executable — the anchor for everything
    /// else, and the reason this install is portable.
    ///
    /// <para><see cref="AppContext.BaseDirectory"/> rather than
    /// <c>Environment.CurrentDirectory</c>: the daemon is started from a
    /// hotkey, from <c>vst-ctl</c>'s autostart, or from a shell sitting in an
    /// unrelated directory, so the working directory is whatever it happened to
    /// inherit and is never a safe anchor.</para>
    /// </summary>
    public static string BaseDir => AppContext.BaseDirectory.TrimEnd('/');

    /// <summary>
    /// Pure resolution, so a caller can show the user where a setting will land
    /// before committing to it. Same rules as the Windows side: empty means
    /// <c>&lt;BaseDir&gt;/data</c>, a rooted path is taken at face value, and a
    /// relative one anchors against <see cref="BaseDir"/> so portable moves
    /// stay portable. <c>$VAR</c> and <c>~</c> are expanded.
    /// </summary>
    public static string ResolveDataDir(string? raw, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Path.Combine(baseDir, DefaultFolderName);

        string expanded = Expand(raw.Trim());
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Expands <c>~</c> and <c>$VAR</c> / <c>${VAR}</c>. .NET has no Unix
    /// equivalent of <c>ExpandEnvironmentVariables</c>' behaviour here, and a
    /// path handed to us by a user who typed <c>~/somewhere</c> in a config
    /// file would otherwise be created as a literal directory named "~".
    /// </summary>
    private static string Expand(string s)
    {
        if (s == "~" || s.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                s = s.Length == 1 ? home : Path.Combine(home, s[2..]);
        }

        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '$') { sb.Append(s[i]); continue; }

            int start = i + 1;
            bool braced = start < s.Length && s[start] == '{';
            if (braced) start++;

            int end = start;
            while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_')) end++;

            if (end == start) { sb.Append('$'); continue; }

            string name = s[start..end];
            sb.Append(Environment.GetEnvironmentVariable(name) ?? "");
            i = (braced && end < s.Length && s[end] == '}') ? end : end - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>models/</c> under <see cref="StoreRoot"/> — which is
    /// <see cref="BaseDir"/> for every install that is not an AppImage, so this
    /// is unchanged for the tarball and for a portable folder on a stick.
    /// </summary>
    public static string DefaultModelsDir => Path.Combine(StoreRoot, "models");

    public static string SettingsFile(string dataDir) => Path.Combine(dataDir, "settings.json");
    public static string PronunciationsFile(string dataDir) => Path.Combine(dataDir, "pronunciations.json");
    public static string LogsDir(string dataDir) => Path.Combine(dataDir, "logs");

    /// <summary>
    /// What <c>vst-ctl benchmark</c> measured about this machine. Beside the
    /// other two rather than under <c>logs/</c>: it is state the daemon reads
    /// back on every start, not a record of something that happened.
    ///
    /// <para>The filename comes from <see cref="BenchmarkStore"/> as of
    /// 2026-08-19 — the Windows Control Panel writes the same file into the same
    /// portable data directory, and two literals is how one platform ends up
    /// reading a file the other never wrote.</para>
    /// </summary>
    public static string BenchmarkFile(string dataDir) =>
        Path.Combine(dataDir, VibeSuperTonic.Core.Synthesis.BenchmarkStore.FileName);

    /// <summary>
    /// Best effort, and deliberately not fatal. A portable folder can sit on a
    /// read-only mount, a CD, or a directory owned by root — in which case the
    /// settings that are already there still load and still apply, and only
    /// saving is unavailable. Refusing to start would turn a read-only install
    /// into a hotkey that does nothing, which is the failure
    /// <see cref="Program"/> already refuses to have for missing models.
    /// </summary>
    /// <returns>True if the data directory exists and is writable.</returns>
    public static bool TryEnsureWritable(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            string probe = Path.Combine(dataDir, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

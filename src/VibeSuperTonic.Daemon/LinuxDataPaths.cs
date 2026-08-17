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

    public static string DefaultModelsDir => Path.Combine(BaseDir, "models");

    public static string SettingsFile(string dataDir) => Path.Combine(dataDir, "settings.json");
    public static string PronunciationsFile(string dataDir) => Path.Combine(dataDir, "pronunciations.json");
    public static string LogsDir(string dataDir) => Path.Combine(dataDir, "logs");

    /// <summary>
    /// What <c>vst-ctl benchmark</c> measured about this machine. Beside the
    /// other two rather than under <c>logs/</c>: it is state the daemon reads
    /// back on every start, not a record of something that happened.
    /// </summary>
    public static string BenchmarkFile(string dataDir) => Path.Combine(dataDir, "benchmark.json");

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

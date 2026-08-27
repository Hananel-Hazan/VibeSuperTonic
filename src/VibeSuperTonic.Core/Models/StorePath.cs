namespace VibeSuperTonic.Core.Models;

/// <summary>
/// Where a manifest entry or a voice id is allowed to land on disk.
///
/// <para><b>A manifest and a catalog are data, and they decide where bytes are
/// written and which directories are deleted.</b> Both of the ones this product
/// reads are files we generate and ship — but they sit beside the binaries in a
/// portable install, where anything can edit them, and a voice catalog is exactly
/// the kind of file somebody copies from a forum post because it lists more
/// voices than ours does. A voice id also arrives straight from a command line:
/// <c>vst-ctl voice remove &lt;id&gt;</c>.</para>
///
/// <para><b>The hash pin does not help.</b> It says the bytes are the ones the
/// manifest named; it says nothing about whether the manifest named a sane place
/// to put them. Nothing else in the download path looks at the path at all.</para>
///
/// <para>Two ways out of a directory, and the second is the one that surprises
/// people:</para>
///
/// <list type="bullet">
///   <item><c>../../.bashrc</c> — traversal, which is what anyone would think to
///   check for.</item>
///   <item><c>/etc/cron.d/anything</c> — <see cref="Path.Combine(string, string)"/>
///   <b>discards the base entirely</b> when the second argument is rooted. An
///   absolute manifest path is not a traversal being caught or missed; it is
///   simply obeyed, and a check written against <c>".."</c> never sees it.</item>
/// </list>
///
/// <para>So the test is on the <em>resolved</em> path rather than on its text:
/// <c>a/../b</c> is fine and <c>a/../../b</c> is not, and only
/// <see cref="Path.GetFullPath(string)"/> can tell those apart.</para>
/// </summary>
/// <remarks>
/// Public rather than internal because this project forbids
/// <c>InternalsVisibleTo</c> — see the note in
/// <c>VibeSuperTonic.Core.csproj</c>: Way 3 compiles these sources into each
/// host, where a cross-assembly grant stops meaning anything, so everything the
/// tests need is public on purpose.
/// </remarks>
public static class StorePath
{
    /// <summary>
    /// <paramref name="relative"/> resolved under <paramref name="root"/>, or
    /// null when it lands anywhere else. Callers report; they do not write.
    /// </summary>
    public static string? Under(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (string.IsNullOrWhiteSpace(relative)) return null;

        string full, baseDir;
        try
        {
            baseDir = Path.GetFullPath(root);
            // Forward slashes: a manifest path is a relative locator, not a
            // native path, and it is written with '/' on both platforms.
            full = Path.GetFullPath(Path.Combine(
                baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            // A path the platform will not even parse — a NUL byte, or longer
            // than the OS allows. Refused rather than thrown: this sits inside a
            // loop over a file somebody else wrote.
            return null;
        }

        // The trailing separator is not decoration. Without it "/x/models" is a
        // prefix of "/x/models-backup/evil", and the check passes.
        string prefix = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// Whether a voice id may be used as a directory name.
    ///
    /// <para>Stricter than <see cref="Under"/> on purpose, because the id does
    /// not merely locate a file — <c>voice remove</c> hands its directory to
    /// <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c>.
    /// An id of <c>..</c> is a resolved path that stays inside the models root
    /// and deletes the whole store, so "inside the root" is not a strong enough
    /// question to ask of it.</para>
    ///
    /// <para>A real id is <c>en_US-ljspeech-high</c>. Letters, digits, and the
    /// three separators upstream uses. Anything else — a path separator, a drive
    /// letter, a leading dot, a control character — is not an id that could have
    /// come from the catalog.</para>
    ///
    /// <para><b>Letters, not ASCII letters.</b> One shipped id is
    /// <c>pt_PT-tugão-medium</c>, and an ASCII rule would refuse a voice that is
    /// in the catalog right now — which is how a security guard becomes a bug
    /// report about Portuguese. The property being defended is "contains no path
    /// separator and is not a relative-path token", and Unicode letters do not
    /// threaten it.</para>
    /// </summary>
    public static bool IsSafeVoiceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        string s = id.Trim();
        if (s.Length > 128) return false;
        if (s[0] == '.') return false;          // ".", "..", and hidden directories

        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '_' or '-' or '.') continue;
            return false;                        // '/', '\\', ':', NUL, spaces, anything else
        }

        return true;
    }
}

namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// How a snap's daemon opens the window: the way snapd itself would start the
/// <c>vibesupertonic</c> app, read from the snap's own <c>meta/snap.yaml</c>.
///
/// <para><b>Why not just run <c>$SNAP/vibesupertonic-ui</c>.</b> In a snap the
/// window app is wrapped by the GNOME extension's command chain
/// (<c>desktop-launch</c>, the GPU wrapper), which is what puts the GNOME
/// runtime's libraries on the path. The daemon is usually started by the hotkey
/// client, which is deliberately bare, so a window it runs directly has none of
/// that: on revision 3 every click on the tray icon aborted the window with
/// <c>libfontconfig.so.1: cannot open shared object file</c> (2026-09-25).</para>
///
/// <para><b>Read, not hard-coded</b>, because the chain belongs to the
/// extension and changes with it; a copy here would go stale with no error
/// anywhere. snapd writes <c>meta/snap.yaml</c> in one block style, and this
/// reads only what it needs from it: the app's <c>command</c>, its
/// <c>command-chain</c> and any <c>environment</c> of its own.</para>
///
/// <para>The window still runs under the confinement of whatever started the
/// daemon, which is why every app that can start the daemon carries the
/// window's plugs too (snapcraft.yaml.in, decision 2).</para>
/// </summary>
/// <param name="Argv">What to execute, absolute paths, chain first.</param>
/// <param name="Environment">Variables the app sets itself, already expanded.</param>
public sealed record SnapWindow(IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The snap app that is the window.</summary>
    public const string WindowApp = "vibesupertonic";

    /// <summary>
    /// The window's command in the snap at <paramref name="snapRoot"/>, or null
    /// with <paramref name="why"/> saying what is wrong.
    /// </summary>
    public static SnapWindow? Resolve(string snapRoot, Func<string, string?> env, out string? why)
    {
        string meta = Path.Combine(snapRoot, "meta", "snap.yaml");
        string yaml;
        try
        {
            yaml = File.ReadAllText(meta);
        }
        catch (Exception ex)
        {
            why = $"cannot read {meta}: {ex.Message}";
            return null;
        }
        return Parse(yaml, snapRoot, env, out why);
    }

    /// <summary>The rule, pure, for the tests.</summary>
    public static SnapWindow? Parse(string yaml, string snapRoot, Func<string, string?> env, out string? why)
    {
        string? command = null;
        var chain = new List<string>();
        var vars = new List<KeyValuePair<string, string>>();

        bool inApps = false, inApp = false, found = false;
        string section = "";

        foreach (string raw in yaml.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            int indent = raw.Length - raw.TrimStart(' ').Length;
            string line = raw.Trim();

            if (indent == 0)
            {
                inApps = line == "apps:";
                inApp = false;
                continue;
            }
            if (!inApps) continue;

            if (indent == 2)
            {
                inApp = line == WindowApp + ":";
                found |= inApp;
                section = "";
                continue;
            }
            if (!inApp) continue;

            // A key of the app, at four spaces. A block list may sit at the
            // key's own indentation ("    - x"), which is how snapd writes it.
            if (indent == 4 && !line.StartsWith("- ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line[..colon];
                string value = Unquote(line[(colon + 1)..].Trim());
                section = key;
                if (key == "command") command = value;
                else if (key == "command-chain" && value.StartsWith('['))
                    chain.AddRange(FlowList(value));
                continue;
            }

            if (section == "command-chain" && line.StartsWith("- ", StringComparison.Ordinal))
                chain.Add(Unquote(line[2..].Trim()));
            else if (section == "environment" && indent >= 6 && line.IndexOf(':') is > 0 and var c)
                vars.Add(new(line[..c], Unquote(line[(c + 1)..].Trim())));
        }

        if (!found)
        {
            why = $"meta/snap.yaml has no '{WindowApp}' app";
            return null;
        }
        if (string.IsNullOrEmpty(command))
        {
            why = $"meta/snap.yaml gives the '{WindowApp}' app no command";
            return null;
        }

        // snapd expands variables in the app's environment against the snap's
        // own, in order, so a later one may use an earlier one.
        var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Lookup(string name) =>
            expanded.TryGetValue(name, out var v) ? v : name == "SNAP" ? snapRoot : env(name);
        foreach (var (key, value) in vars) expanded[key] = Expand(value, Lookup);

        var argv = new List<string>();
        foreach (string link in chain) argv.Add(InSnap(Expand(link, Lookup), snapRoot));
        string[] words = Expand(command, Lookup).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        argv.Add(InSnap(words[0], snapRoot));
        argv.AddRange(words.Skip(1));

        why = null;
        return new SnapWindow(argv, expanded);
    }

    private static string InSnap(string path, string snapRoot) =>
        path.StartsWith('/') ? path : Path.Combine(snapRoot, path);

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0]
            ? value[1..^1]
            : value;

    private static IEnumerable<string> FlowList(string value) =>
        value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(Unquote);

    /// <summary><c>$NAME</c> and <c>${NAME}</c>, as snapd expands them; unknown names become empty.</summary>
    internal static string Expand(string value, Func<string, string?> lookup)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '$' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
            int start = i + 1, end;
            string name;
            if (value[start] == '{')
            {
                end = value.IndexOf('}', start);
                if (end < 0) { sb.Append(value[i]); continue; }
                name = value[(start + 1)..end];
                i = end;
            }
            else
            {
                end = start;
                while (end < value.Length && (char.IsAsciiLetterOrDigit(value[end]) || value[end] == '_')) end++;
                if (end == start) { sb.Append(value[i]); continue; }
                name = value[start..end];
                i = end - 1;
            }
            sb.Append(lookup(name) ?? "");
        }
        return sb.ToString();
    }
}

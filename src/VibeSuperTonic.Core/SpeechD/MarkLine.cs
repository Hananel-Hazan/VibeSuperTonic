using System.Globalization;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Core.SpeechD;

/// <summary>
/// How an SSML bookmark travels from <c>vst-ctl render --marks</c> to the Speech
/// Dispatcher module: one line on the renderer's stderr.
///
/// <para><b>Stderr, because the other two channels are taken.</b> Stdout is a WAV
/// and cannot carry anything else; the exit code is one number. A line per mark
/// on stderr needs no new descriptor — .NET cannot hand a child an extra one —
/// and it degrades gracefully: a reader that does not look for the lines sees
/// them as diagnostics, which is what stderr has always been.</para>
///
/// <para>The name is percent-encoded so a name carrying a newline (the client
/// controls it) cannot end the line early and have the rest read as something
/// else.</para>
/// </summary>
public static class MarkLine
{
    /// <summary>Distinguishes a mark from the free-text diagnostics sharing stderr.</summary>
    public const string Prefix = "@vst-mark ";

    public static string Format(RenderMark mark) =>
        Prefix + mark.Sample.ToString(CultureInfo.InvariantCulture)
        + " " + Uri.EscapeDataString(mark.Name);

    /// <summary>False for any line that is not a well-formed mark.</summary>
    public static bool TryParse(string? line, out RenderMark mark)
    {
        mark = new RenderMark("", 0);
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        string rest = line[Prefix.Length..];
        int space = rest.IndexOf(' ');
        if (space <= 0) return false;

        if (!long.TryParse(rest.AsSpan(0, space), NumberStyles.None,
                CultureInfo.InvariantCulture, out long sample))
            return false;

        string name;
        try { name = Uri.UnescapeDataString(rest[(space + 1)..]); }
        catch (UriFormatException) { return false; }

        mark = new RenderMark(name, sample);
        return true;
    }

    /// <summary>
    /// A mark name made safe to put on a protocol line of its own: control
    /// characters (newline above all) become spaces. speech-dispatcher reads
    /// <c>700-&lt;name&gt;</c> up to the newline, so one inside a name would end
    /// the event early and leave the tail to be parsed as a reply.
    /// </summary>
    public static string ForProtocolLine(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i])) chars[i] = ' ';
        return new string(chars);
    }
}

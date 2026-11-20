using System.Globalization;
using VibeSuperTonic.Core.Export;

namespace VibeSuperTonic.Core.Ipc;

/// <summary>What <see cref="CtlArgs.Parse"/> decided the process should do.</summary>
public enum CtlOutcome
{
    /// <summary>Send <see cref="CtlArgs.Verb"/> to the daemon.</summary>
    Run,
    /// <summary>Print usage and exit 0 (<c>--help</c>).</summary>
    Help,
    /// <summary>Print usage and exit 2 (no arguments at all).</summary>
    Usage,
    /// <summary>Print this binary's version.</summary>
    Version,
    /// <summary>Report which export formats are available here.</summary>
    ExportFormats,
    /// <summary>Print <see cref="CtlArgs.Error"/> to stderr and exit 2.</summary>
    Error,
}

/// <summary>
/// vst-ctl's command line, parsed. It lives in Core rather than in the Ctl
/// program so the tests can reach it (review item 77): this is the first thing
/// the hotkey runs, and until now its parser could only be exercised by
/// starting the binary.
///
/// <para>Written for NativeAOT and a startup budget: no reflection, no regex,
/// one pass over the arguments. Verbs are matched by walking the enum's values
/// rather than <c>Enum.TryParse</c>, which would also accept "7" and
/// "Speak,Read" (item 68).</para>
///
/// <para>Every <c>--option</c> that is not listed here is an error (item 21);
/// before, <c>--voice=M4</c> was silently dropped and the default voice spoke.
/// An option that takes a value accepts <c>--opt value</c> and
/// <c>--opt=value</c>. A single-dash argument other than <c>-y</c> is text,
/// so a negative number or "-" is still readable.</para>
/// </summary>
public sealed class CtlArgs
{
    public CtlOutcome Outcome { get; private set; } = CtlOutcome.Run;
    public string? Error { get; private set; }
    /// <summary>With <see cref="CtlOutcome.Error"/>: also print the usage text.</summary>
    public bool ShowUsage { get; private set; }

    public bool NoStart { get; private set; }
    public bool AsJson { get; private set; }
    public bool WithText { get; private set; }
    public bool Force { get; private set; }
    public bool AcceptLicence { get; private set; }
    public bool Marks { get; private set; }
    /// <summary><c>--text-stdin</c> or its alias <c>--stdin</c>.</summary>
    public bool TextFromStdin { get; private set; }

    public string? Voice { get; private set; }
    public string? Language { get; private set; }
    public int? Rate { get; private set; }
    public int? Volume { get; private set; }
    public string OutPath { get; private set; } = "-";
    public ExportFormat? ExportFormat { get; private set; }

    public RequestVerb Verb { get; private set; }
    /// <summary>The subject of <c>voice install|remove</c>.</summary>
    public string? VoiceSubject { get; private set; }
    public int? SeekOffset { get; private set; }
    /// <summary>The arguments that are not options; element 0 is the verb as typed.</summary>
    public string[] Positional { get; private set; } = [];

    private CtlArgs() { }

    private static CtlArgs Fail(string message, bool usage = false) =>
        new() { Outcome = CtlOutcome.Error, Error = message, ShowUsage = usage };

    public static CtlArgs Parse(string[] args)
    {
        if (args.Length == 0) return new CtlArgs { Outcome = CtlOutcome.Usage };
        if (args[0] is "--help" or "-h") return new CtlArgs { Outcome = CtlOutcome.Help };

        // `--` ends the options: everything after it is text, whatever it looks
        // like (text comes from whatever window has focus).
        int end = Array.IndexOf(args, "--");
        int optionCount = end >= 0 ? end : args.Length;

        // Questions about this binary, answered before anything else is judged.
        for (int i = 0; i < optionCount; i++)
        {
            if (args[i] == "--version") return new CtlArgs { Outcome = CtlOutcome.Version };
        }
        for (int i = 0; i < optionCount; i++)
        {
            if (args[i] == "--export-formats") return new CtlArgs { Outcome = CtlOutcome.ExportFormats };
        }

        var r = new CtlArgs();
        var positional = new List<string>(args.Length);
        string? formatText = null;

        for (int i = 0; i < optionCount; i++)
        {
            string a = args[i];
            if (a == "-y") { r.AcceptLicence = true; continue; }
            if (!a.StartsWith("--", StringComparison.Ordinal)) { positional.Add(a); continue; }

            int eq = a.IndexOf('=');
            string name = eq >= 0 ? a[..eq] : a;
            string? inline = eq >= 0 ? a[(eq + 1)..] : null;

            bool isFlag = true;
            switch (name)
            {
                case "--no-start": r.NoStart = true; break;
                case "--json": r.AsJson = true; break;
                case "--text": r.WithText = true; break;
                case "--force": r.Force = true; break;
                case "--marks": r.Marks = true; break;
                case "--yes" or "--accept-licence" or "--accept-license": r.AcceptLicence = true; break;
                case "--text-stdin" or "--stdin": r.TextFromStdin = true; break;
                case "--voice" or "--language" or "--rate" or "--volume" or "--out" or "--format":
                    isFlag = false;
                    break;
                default:
                    return Fail($"unknown option: {a} (vst-ctl --help lists them)");
            }

            if (isFlag)
            {
                if (inline is not null) return Fail($"{name} takes no value");
                continue;
            }

            // A value option: `--opt=value` or the next argument. The next
            // argument may not itself be an option; a negative number is not
            // one (it starts with a single dash).
            string? value = inline;
            if (value is null && i + 1 < optionCount && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++i];

            switch (name)
            {
                case "--voice":
                    if (string.IsNullOrEmpty(value)) return Fail("--voice needs a voice id, e.g. `--voice en_US-lessac-medium`");
                    r.Voice = value; break;
                case "--language":
                    if (string.IsNullOrEmpty(value)) return Fail("--language needs a code, e.g. `--language de`");
                    r.Language = value; break;
                case "--rate":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate))
                        return Fail("--rate needs a whole number from -100 to 100, e.g. `--rate 30`");
                    r.Rate = rate; break;
                case "--volume":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vol))
                        return Fail("--volume needs a whole number from -100 to 100, e.g. `--volume -20`");
                    r.Volume = vol; break;
                case "--out":
                    if (string.IsNullOrEmpty(value)) return Fail("--out needs a path, or - for stdout");
                    r.OutPath = value; break;
                case "--format":
                    if (ExportFormats.Parse(value) is not { } f) return Fail("--format needs one of: wav, mp3, aac, flac");
                    r.ExportFormat = f; formatText = value; break;
            }
        }

        if (r.ExportFormat is { } chosen)
        {
            if (r.OutPath == "-" && chosen != Export.ExportFormat.Wav)
                return Fail($"--format {formatText} needs --out FILE; only WAV can go to stdout");
        }
        else if (r.OutPath != "-")
        {
            r.ExportFormat = ExportFormats.FromPath(r.OutPath);
            // FromPath no longer maps .aac (our AAC is an MP4 container, so the name
            // would lie), and a null format means WAV: without this, `--out x.aac`
            // writes a WAV file called .aac with no word to anyone.
            if (r.ExportFormat is null && r.OutPath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
                return Fail("--out *.aac is not written: AAC is exported in an MP4 container, name it .m4a");
        }

        for (int i = end >= 0 ? end + 1 : args.Length; i < args.Length; i++) positional.Add(args[i]);

        if (positional.Count == 0)
            return Fail($"no verb in: {string.Join(' ', args)}", usage: true);

        // `voice install <id>` and `voice remove <id>`: two words, one wire verb.
        string verbWord = positional[0];
        if (string.Equals(verbWord, "voice", StringComparison.OrdinalIgnoreCase))
        {
            if (positional.Count < 2)
                return Fail("voice needs an action: `voice install <id>` or `voice remove <id>`");

            string action = positional[1].ToLowerInvariant();
            if (action is not ("install" or "remove"))
                return Fail($"unknown voice action: {positional[1]} (install, remove)");

            r.VoiceSubject = positional.Count > 2 ? positional[2] : r.Voice;
            if (string.IsNullOrWhiteSpace(r.VoiceSubject))
                return Fail($"voice {action} needs a voice id — `vst-ctl voices` lists them");

            positional = [action == "install" ? "voiceinstall" : "voiceremove"];
            verbWord = positional[0];
        }

        if (!TryParseVerb(verbWord, out var verb))
            return Fail($"unknown verb: {verbWord}");
        r.Verb = verb;
        r.Positional = positional.ToArray();

        if (verb == RequestVerb.Seek)
        {
            if (positional.Count < 2 || !int.TryParse(positional[1], out int offset))
                return Fail("seek needs a character offset, e.g. `vst-ctl seek 42`");
            r.SeekOffset = offset;
        }

        if (r.TextFromStdin)
        {
            if (verb is not (RequestVerb.Render or RequestVerb.Speak))
                return Fail("--text-stdin is for `render` and `speak`");
            if (positional.Count > 1)
                return Fail("--text-stdin and text on the command line: give one or the other");
        }

        return r;
    }

    /// <summary>
    /// A verb by name, case-insensitively, and only by name: <c>Enum.TryParse</c>
    /// also takes "7" and "Speak,Read". Walks the values, which the NativeAOT
    /// binary can do without reflection over names.
    /// </summary>
    public static bool TryParseVerb(string word, out RequestVerb verb)
    {
        foreach (var v in Enum.GetValues<RequestVerb>())
        {
            if (string.Equals(v.ToString(), word, StringComparison.OrdinalIgnoreCase))
            {
                verb = v;
                return true;
            }
        }
        verb = default;
        return false;
    }
}

using System.Diagnostics;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.SpeechD;

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// Where the two renderers live, and how to start one.
///
/// <para><b>Both write a WAV to stdout</b>, which is what lets the module have a
/// single audio path: <c>vst-ctl render --out -</c> and the bundled
/// <c>espeak-ng --stdout</c> differ in latency and quality and in nothing this
/// process has to care about.</para>
///
/// <para><b>Both read the text on stdin, never from the command line</b>
/// (2026-10-04). Linux caps one argument at 128 KiB (MAX_ARG_STRLEN), and with
/// the message as one argv string a long document made Process.Start fail with
/// E2BIG for the neural voice and then for the fallback, which is silence: the
/// one outcome trap 16 exists to rule out. It also settles what an argument
/// beginning with a dash means. Text that is not on the command line can never
/// be read as an option, by vst-ctl or by espeak-ng.</para>
/// </summary>
internal sealed class Voices
{
    /// <summary>The directory the archive was extracted into.</summary>
    private readonly string _root;

    internal string CtlPath { get; }
    internal string EspeakPath { get; }
    internal string EspeakDataPath { get; }

    /// <summary>
    /// Resolve everything relative to this executable.
    ///
    /// <para><b>Not from $PATH, and not from a configured path.</b> The archive is
    /// portable — the user chooses where it lives and may move it — so the only
    /// durable statement about where <c>vst-ctl</c> is, is "beside me". A module
    /// that searched $PATH would find a different installation's client, or the
    /// wrong version of it, and the failure would be a voice that is subtly not
    /// the one the user configured.</para>
    /// </summary>
    /// <param name="root">
    /// Where to look. Defaults to this executable's own directory, which is the
    /// only durable answer for a portable archive the user may move — see the
    /// remarks. Overridden by the tests, which need an install that is not the
    /// one they are running inside.
    /// </param>
    /// <param name="store">
    /// Where <c>models/</c> lives, which is <b>not</b> the same question.
    ///
    /// <para>For a tarball, a portable folder or a USB stick it is the same
    /// directory as <paramref name="root"/> and always has been. For an AppImage
    /// the two genuinely differ: the binaries are inside a read-only squashfs
    /// mount at a path that changes every run, and the models cannot be, so they
    /// live in a store beside the .AppImage file. Resolving models from
    /// <paramref name="root"/> would report that an AppImage user has no voices
    /// at all.</para>
    /// </param>
    internal Voices(string? root = null, string? store = null)
    {
        _root = (root ?? AppContext.BaseDirectory).TrimEnd('/');
        CtlPath = Path.Combine(_root, "vst-ctl");
        EspeakPath = Path.Combine(_root, "espeak", "espeak-ng");
        EspeakDataPath = Path.Combine(_root, "espeak");

        // LinuxDataPaths, compiled in from the daemon rather than re-spelled —
        // see the csproj. `store` is the tests' override and stands in for the
        // whole rule, since a test install is never an AppImage.
        ModelsRoot = store is not null
            ? Path.Combine(store.TrimEnd('/'), "models")
            : Daemon.LinuxDataPaths.DefaultModelsDir;
    }

    /// <summary>Where <c>voice_styles/</c> and <c>piper/</c> are, for S3's voice list.</summary>
    internal string ModelsRoot { get; }

    internal bool EspeakPresent => File.Exists(EspeakPath);

    /// <summary>
    /// Start the neural voice. Streams, so the first samples arrive long before
    /// the last are synthesised.
    /// </summary>
    /// <param name="language">
    /// Supertonic's language for this utterance, or null to leave the daemon's
    /// configured one alone. Supertonic is one model set over 31 languages and
    /// takes the language per utterance, so this is what carries speechd's
    /// <c>language=de</c> through to the synthesiser. Null for a Piper voice,
    /// where the model IS the language.
    /// </param>
    /// <param name="rate">
    /// speech-dispatcher's -100..100, forwarded so the neural voice answers the
    /// screen reader's speed control.
    ///
    /// <para><b>Reported 2026-09-06 as "piper does not obey the speed change."</b>
    /// This method took no rate at all, so <c>SET RATE</c> reached only the
    /// espeak fallback below — the voice a user is not listening to — and Orca's
    /// slider did nothing to the one they were. It is passed as the speechd
    /// number rather than a factor because the daemon owns what a rate means and
    /// the range is documented and bounded.</para>
    /// </param>
    /// <param name="volume">
    /// speech-dispatcher's −100..100, applied by the daemon as a gain. Zero sends
    /// nothing, for the compatibility reason the rate documents.
    /// </param>
    /// <param name="marks">
    /// True when the text is an SSML document with <c>&lt;mark&gt;</c>s in it: the
    /// renderer then reports each one on stderr (<see cref="MarkLine"/>).
    /// </param>
    internal Process StartNeural(
        string text, string? voice, string? language = null, int rate = 0,
        int volume = 0, bool marks = false)
    {
        var psi = Base(CtlPath);
        foreach (string argument in NeuralArguments(voice, language, rate, volume, marks))
            psi.ArgumentList.Add(argument);
        return Start(psi, text);
    }

    /// <summary>
    /// The command line <see cref="StartNeural"/> runs, separated out so it can
    /// be asserted without starting a process — which is the only way the rate
    /// reaching <c>vst-ctl</c> is testable at all.
    /// </summary>
    internal static IReadOnlyList<string> NeuralArguments(
        string? voice, string? language, int rate,
        int volume = 0, bool marks = false)
    {
        var arguments = new List<string> { "render", "--out", "-" };

        // Sent only when it says something. A daemon too old to know --rate
        // refuses an unknown flag, and every utterance would then fall through to
        // espeak — so the ordinary case, a client that never set a rate, keeps
        // the command line it has always had.
        if (rate != 0)
        {
            arguments.Add("--rate");
            arguments.Add(rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (volume != 0)
        {
            arguments.Add("--volume");
            arguments.Add(volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (marks) arguments.Add("--marks");

        if (!string.IsNullOrWhiteSpace(voice))
        {
            arguments.Add("--voice");
            arguments.Add(voice);
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            arguments.Add("--language");
            arguments.Add(language);
        }

        // The text comes on stdin (see the class remarks), so it is not here at
        // all. It arrives from whatever window has focus and is not ours to
        // trust, and until 2026-10-04 it was the last argument here: vst-ctl
        // dropped anything beginning with "--" from its positionals and answered
        // "--version" wherever it stood, so a line reading "--version" printed a
        // version where a WAV belonged and fell through to espeak.
        arguments.Add("--text-stdin");
        return arguments;
    }

    /// <summary>
    /// Start the fast voice — the one that answers a keystroke, and the one
    /// [trap 16] says must still answer when everything else has failed.
    /// </summary>
    /// <param name="rate">
    /// speech-dispatcher's -100..100. espeak-ng takes words per minute, and 175
    /// is its own default; the mapping is the linear one its own module uses.
    /// </param>
    /// <param name="volume">speech-dispatcher's −100..100, as espeak's amplitude.</param>
    /// <param name="rateAdj">An SSML fragment's own rate adjustment, −10..10.</param>
    internal Process StartEspeak(string text, string? language, int rate, int volume = 0, int rateAdj = 0)
    {
        var psi = Base(EspeakPath);
        foreach (string argument in EspeakArguments(language, rate, volume, rateAdj))
            psi.ArgumentList.Add(argument);
        return Start(psi, text);
    }

    /// <summary>
    /// The command line <see cref="StartEspeak"/> runs, separated out for the same
    /// reason <see cref="NeuralArguments"/> is.
    /// </summary>
    internal IReadOnlyList<string> EspeakArguments(
        string? language, int rate, int volume = 0, int rateAdj = 0)
    {
        var args = new List<string>
        {
            "--path=" + EspeakDataPath,
            "--stdout",
            "-s",
            EspeakWordsPerMinute(rate, rateAdj).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // Sent only when it says something: espeak's own default is 100, which is
        // what a volume of 0 means, and a command line that never changed for a
        // client that never touched the volume is one nothing can regress.
        if (volume != 0)
        {
            args.Add("-a");
            args.Add(EspeakAmplitude(volume).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            args.Add("-v");
            args.Add(language);
        }

        // The text on stdin, read WHOLE. Without --stdin espeak-ng also reads
        // stdin when it has no text argument, but line by line through a
        // 1000-byte buffer, one synthesis per line: a long line would be cut
        // mid-word and every line break would become a sentence end. --stdin
        // reads to end of input and synthesises once, as the argument did.
        // (Checked against espeak-ng.c at build-espeak.sh's pin.)
        args.Add("--stdin");
        return args;
    }

    /// <summary>
    /// speech-dispatcher's -100..100 onto espeak's words per minute, the way
    /// speech-dispatcher's own espeak module does it: 175 at 0, and the ends of
    /// the range at 80 and 450.
    ///
    /// <para><b>Delegated to Core rather than kept here, and the sharing is the
    /// point.</b> The neural voice reads the same map as a multiplier — see
    /// <see cref="SpeechRate.SpeechdRateScale"/> — so that a [trap 16] fallback,
    /// which happens between one utterance and the next, changes the timbre and
    /// not the pace. Two copies of these three numbers would let that property
    /// rot silently the first time one of them was edited.</para>
    /// </summary>
    internal static int EspeakWordsPerMinute(int rate) => SpeechRate.SpeechdWordsPerMinute(rate);

    /// <summary>
    /// The same, with an SSML fragment's own adjustment on top: the engine's
    /// <c>1.5^(n/10)</c> applied to the words per minute, held inside the range
    /// the speechd map itself spans so a stack of nested rates cannot ask espeak
    /// for a speed it garbles.
    /// </summary>
    internal static int EspeakWordsPerMinute(int rate, int rateAdj)
    {
        int wpm = SpeechRate.SpeechdWordsPerMinute(rate);
        if (rateAdj == 0) return wpm;
        return Math.Clamp(
            (int)Math.Round(wpm * SpeechRate.RateAdjScale(rateAdj)),
            SpeechRate.SpeechdMinWpm, SpeechRate.SpeechdMaxWpm);
    }

    /// <summary>
    /// speech-dispatcher's volume as espeak-ng's amplitude (0..200, 100 default):
    /// the same gain the neural voice gets, so a fallback does not change the
    /// loudness along with the timbre.
    /// </summary>
    internal static int EspeakAmplitude(int volume) =>
        Math.Clamp((int)Math.Round(100 * SpeechRate.SpeechdVolumeScale(volume)), 0, 200);

    private static ProcessStartInfo Base(string file) => new()
    {
        FileName = file,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,

        // Explicit, because the default follows the console's encoding and the
        // text is read as UTF-8 at the other end. No BOM: it would be spoken.
        StandardInputEncoding = new System.Text.UTF8Encoding(false),
        UseShellExecute = false,
    };

    /// <summary>
    /// Start the renderer and hand it <paramref name="text"/> on stdin.
    ///
    /// <para><b>Written aside, not inline.</b> A message larger than the pipe's
    /// 64 KB buffer blocks the writer until the renderer reads it, and a renderer
    /// that writes before it has read everything, or never reads at all, would
    /// then deadlock with this process, which is not yet reading its stdout. A
    /// renderer that exits without reading breaks the pipe. That is its own
    /// failure, reported by the missing WAV, so the write's error is not.</para>
    /// </summary>
    private static Process Start(ProcessStartInfo psi, string text)
    {
        var p = new Process { StartInfo = psi };
        p.Start();

        StreamWriter stdin = p.StandardInput;
        _ = Task.Run(() =>
        {
            try
            {
                stdin.Write(text);
                stdin.Close();
            }
            catch (Exception) { /* the renderer went away; its stdout says so */ }
        });

        return p;
    }
}

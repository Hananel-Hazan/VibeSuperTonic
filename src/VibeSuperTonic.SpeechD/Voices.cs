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
    internal Process StartNeural(string text, string? voice, string? language = null, int rate = 0)
    {
        var psi = Base(CtlPath);
        foreach (string argument in NeuralArguments(text, voice, language, rate))
            psi.ArgumentList.Add(argument);
        return Start(psi);
    }

    /// <summary>
    /// The command line <see cref="StartNeural"/> runs, separated out so it can
    /// be asserted without starting a process — which is the only way the rate
    /// reaching <c>vst-ctl</c> is testable at all.
    /// </summary>
    internal static IReadOnlyList<string> NeuralArguments(
        string text, string? voice, string? language, int rate)
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

        // Last, and never a flag: the text arrives from whatever window has
        // focus, so it is not ours to trust. ArgumentList means it is never
        // parsed by a shell — the same reasoning as the `--` on the espeak line.
        arguments.Add(text);
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
    internal Process StartEspeak(string text, string? language, int rate)
    {
        var psi = Base(EspeakPath);
        psi.ArgumentList.Add("--path=" + EspeakDataPath);
        psi.ArgumentList.Add("--stdout");
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(EspeakWordsPerMinute(rate).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(language))
        {
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add(language);
        }

        // `--` so a message beginning with a dash is text and not a flag. The
        // text arrives from whatever window has focus, so it is not ours to
        // trust: this is the same reasoning as quoting $DATA on route A, and the
        // reason route B never builds a shell command at all.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(text);
        return Start(psi);
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

    private static ProcessStartInfo Base(string file) => new()
    {
        FileName = file,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        UseShellExecute = false,
    };

    private static Process Start(ProcessStartInfo psi)
    {
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
    }
}

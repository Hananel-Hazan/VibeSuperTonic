using System.Diagnostics;
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
    internal Process StartNeural(string text, string? voice, string? language = null)
    {
        var psi = Base(CtlPath);
        psi.ArgumentList.Add("render");
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add("-");
        if (!string.IsNullOrWhiteSpace(voice))
        {
            psi.ArgumentList.Add("--voice");
            psi.ArgumentList.Add(voice);
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            psi.ArgumentList.Add("--language");
            psi.ArgumentList.Add(language);
        }

        psi.ArgumentList.Add(text);
        return Start(psi);
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
    /// </summary>
    internal static int EspeakWordsPerMinute(int rate)
    {
        rate = Math.Clamp(rate, -100, 100);
        return rate < 0
            ? 175 + rate * (175 - 80) / 100
            : 175 + rate * (450 - 175) / 100;
    }

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

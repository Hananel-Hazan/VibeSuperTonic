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
    internal Voices(string? root = null)
    {
        _root = (root ?? AppContext.BaseDirectory).TrimEnd('/');
        CtlPath = Path.Combine(_root, "vst-ctl");
        EspeakPath = Path.Combine(_root, "espeak", "espeak-ng");
        EspeakDataPath = Path.Combine(_root, "espeak");
    }

    internal bool EspeakPresent => File.Exists(EspeakPath);

    /// <summary>
    /// Start the neural voice. Streams, so the first samples arrive long before
    /// the last are synthesised.
    /// </summary>
    internal Process StartNeural(string text, string? voice)
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

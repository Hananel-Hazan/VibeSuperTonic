using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// The rule this whole feature is built on: <b>never report success having
/// produced no audio</b>.
///
/// <para>It is trap 14, it is the same failure a missing espeak dictionary has —
/// exit 0, one line to a stderr nobody reads, and no phonemes — and here the
/// person on the other end of it is navigating by ear. A screen reader that goes
/// quiet gives its user nothing to search for: no error, no dialog, no log they
/// can reach. <see cref="RenderWav"/> enforces the rule on the <c>vst-ctl</c>
/// side and is tested there; this is the module's side of the same seam, where a
/// renderer that produced nothing has to become a FALLBACK rather than a
/// silence.</para>
///
/// <para><b>Both renderers are shell scripts here</b>, which is what makes the
/// interesting states reachable at all: a real vst-ctl cannot easily be made to
/// emit a header and then die, and that is precisely the case that must not read
/// as success.</para>
/// </summary>
public class NoAudioTests
{
    /// <summary>
    /// Whatever the module wrote to stdout, split into protocol lines.
    ///
    /// <para>No trailing QUIT in these scripts, deliberately: a QUIT arriving
    /// while audio is in flight is a shutdown, so the module stops the utterance
    /// and answers 703 rather than 702 — correct, and it would hide every
    /// assertion below. Ending the stream is what a test wants; speech-dispatcher
    /// closing the pipe is the same thing.</para>
    /// </summary>
    private static (string[] Replies, List<string> Log) Run(string root, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();
        var log = new List<string>();

        new SpeechdModule(input, output, new Voices(root, root), log.Add, inputFd: -1).Run();

        return (Encoding.UTF8.GetString(output.ToArray())
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries),
                log);
    }

    private static bool Spoke(string[] replies) => replies.Contains("705 AUDIO");

    /// <summary>
    /// An install whose two renderers do exactly what the case needs.
    /// </summary>
    /// <param name="ctl">The body of the fake <c>vst-ctl</c>, after the shebang.</param>
    /// <param name="espeak">The body of the fake <c>espeak-ng</c>.</param>
    private static string Install(string ctl, string espeak)
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-noaudio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "espeak"));

        // A real header, written by the code the product writes headers with, so
        // "a header and no samples" is genuinely a well-formed WAV that carries
        // nothing — not a malformed one, which is a different failure.
        string header = Path.Combine(root, "header-only.wav");
        using (var f = File.Create(header)) RenderWav.WriteHeader(f, 22050, 1);

        string wav = Path.Combine(root, "audio.wav");
        using (var f = File.Create(wav))
        {
            RenderWav.WriteHeader(f, 22050, 1);
            f.Write(new byte[] { 0x0A, 0x00, 0x7D, 0x00, 0x01, 0x02, 0x0A, 0x7D });
        }

        Script(Path.Combine(root, "vst-ctl"), ctl, root);
        Script(Path.Combine(root, "espeak", "espeak-ng"), espeak, root);
        return root;
    }

    private static void Script(string path, string body, string root)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("$ROOT", root) + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private const string Silence = "exit 0";
    private const string HeaderOnly = "cat '$ROOT/header-only.wav'";
    private const string Audio = "cat '$ROOT/audio.wav'";
    private const string Marker = "touch '$ROOT/it-ran'; cat '$ROOT/audio.wav'";

    // ------------------------------------------------------ the neural voice

    /// <summary>
    /// A WAV header and then nothing. Everything about this looks like success —
    /// the process starts, the header parses, the exit code is 0 — and the user
    /// hears nothing. It has to become a fallback.
    /// </summary>
    [Fact]
    public void A_header_with_no_samples_is_not_success()
    {
        string root = Install(ctl: HeaderOnly, espeak: Audio);

        var (replies, log) = Run(root, "SPEAK", "read this to me", ".");

        Assert.Contains(log, l => l.Contains("a header and no samples"));
        Assert.True(Spoke(replies), "the fallback voice did not speak");
        Assert.Contains("702 END", replies);
    }

    /// <summary>The renderer that exits 0 having printed nothing at all.</summary>
    [Fact]
    public void An_exit_zero_with_no_output_is_not_success()
    {
        string root = Install(ctl: Silence, espeak: Audio);

        var (replies, log) = Run(root, "SPEAK", "read this to me", ".");

        Assert.Contains(log, l => l.Contains("no audio"));
        Assert.True(Spoke(replies), "the fallback voice did not speak");
    }

    /// <summary>
    /// And the renderer that refuses properly — non-zero with a reason, which is
    /// what <see cref="RenderWav"/> guarantees vst-ctl actually does.
    /// </summary>
    [Fact]
    public void A_refusal_with_a_reason_is_logged_with_that_reason()
    {
        string root = Install(ctl: "echo 'no voice is installed' >&2; exit 1", espeak: Audio);

        var (replies, log) = Run(root, "SPEAK", "read this to me", ".");

        Assert.Contains(log, l => l.Contains("no voice is installed"));
        Assert.True(Spoke(replies), "the fallback voice did not speak");
    }

    // ---------------------------------------------------------- both of them

    /// <summary>
    /// Both silent. The install is broken rather than busy, and the two things
    /// that must still happen are the log line — the only trace anyone can
    /// follow — and the 702 END, because an unterminated utterance wedges the
    /// server's queue for every module, including the user's working espeak-ng.
    /// </summary>
    [Fact]
    public void When_neither_voice_produces_audio_it_says_so_and_still_ends_the_utterance()
    {
        string root = Install(ctl: HeaderOnly, espeak: HeaderOnly);

        var (replies, log) = Run(root, "SPEAK", "read this to me", ".");

        Assert.Contains(log, l => l.Contains("both voices failed"));
        Assert.False(Spoke(replies), "it claimed to have sent audio");
        Assert.Contains("702 END", replies);
    }

    // ------------------------------------------------------- the other half

    /// <summary>
    /// The rule has a second edge that is easy to lose: a voice that DID speak
    /// must not be followed by the fallback speaking it again. The user hears
    /// the sentence twice, which is a worse bug than hearing it once flatly.
    /// </summary>
    [Fact]
    public void A_voice_that_worked_is_not_spoken_again_by_the_fallback()
    {
        string root = Install(ctl: Audio, espeak: Marker);

        var (replies, _) = Run(root, "SPEAK", "read this to me", ".");

        Assert.True(Spoke(replies));
        Assert.False(File.Exists(Path.Combine(root, "it-ran")), "the fallback spoke as well");
    }
}

using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// What a screen reader gets from the module beyond the words: pause, volume,
/// index marks and the BEGIN/END/PAUSE framing around them.
///
/// <para>Both renderers are shell scripts that record how they were called, which
/// is the only way to assert what reached them — and a fake <c>vst-ctl</c> that
/// can print bookmark lines on stderr, as the real one does under
/// <c>--marks</c>.</para>
///
/// <para>Event names and shapes (<c>700-&lt;name&gt;</c> + <c>700 INDEX MARK</c>,
/// <c>704 PAUSE</c>) are from speech-dispatcher's module protocol as the plan
/// records it; <b>none of this has been run against a real speech-dispatcher
/// or Orca</b>, only against a module-side model of them.</para>
/// </summary>
public class FidelityTests
{
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

    /// <summary>
    /// An install whose renderers record every call. espeak's calls go one per
    /// line to <c>espeak-calls.txt</c>, arguments joined with a bell character
    /// that cannot appear in the text.
    /// </summary>
    private static string Install(string ctl = "exit 1")
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-fidelity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "espeak"));

        string wav = Path.Combine(root, "audio.wav");
        using (var f = File.Create(wav))
        {
            RenderWav.WriteHeader(f, 22050, 1);
            f.Write(new byte[] { 0x0A, 0x00, 0x7D, 0x00, 0x01, 0x02, 0x0A, 0x7D });   // 4 samples
        }

        Script(Path.Combine(root, "vst-ctl"),
            "printf '%s\\n' \"$*\" > '$ROOT/ctl-args.txt'\n" + ctl, root);
        Script(Path.Combine(root, "espeak", "espeak-ng"),
            "args=''\nfor a in \"$@\"; do args=\"$args$a\u0007\"; done\n" +
            "printf '%s\\n' \"$args\" >> '$ROOT/espeak-calls.txt'\n" +
            "cat '$ROOT/audio.wav'", root);
        return root;
    }

    private static void Script(string path, string body, string root)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("$ROOT", root) + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string[] EspeakCalls(string root)
    {
        string file = Path.Combine(root, "espeak-calls.txt");
        return File.Exists(file)
            ? File.ReadAllLines(file).Select(l => l.TrimEnd('\u0007')).ToArray()
            : [];
    }

    private static string[] EspeakArgs(string call) => call.Split('\u0007');

    private static string TextOf(string call) => EspeakArgs(call)[^1];

    private static int Index(string[] replies, string line) => Array.IndexOf(replies, line);

    private static int LastIndex(string[] replies, string line) => Array.LastIndexOf(replies, line);

    // CHAR routes to espeak without a daemon, which is what makes the fast voice's
    // behaviour reachable hermetically.
    private static string[] Speak(string root, string text, params string[] before) =>
        Run(root, [.. before, "CHAR", text, "."]).Replies;

    // ----------------------------------------------------------------- pause

    /// <summary>
    /// A PAUSE during an utterance stops it. It used to be put back with the other
    /// commands, so the audio carried on to the end, an END was sent, and the 704
    /// arrived afterwards — a pause key that did nothing until it no longer
    /// mattered.
    /// </summary>
    [Fact]
    public void A_pause_during_an_utterance_stops_the_audio_and_reports_a_pause_not_an_end()
    {
        string root = Install();

        var replies = Run(root, "CHAR", "hello there", ".", "PAUSE").Replies;

        Assert.Contains("701 BEGIN", replies);
        Assert.DoesNotContain("705 AUDIO", replies);
        Assert.DoesNotContain("702 END", replies);
        Assert.Equal(1, replies.Count(r => r == "704 PAUSE"));
        Assert.True(Index(replies, "704 PAUSE") > Index(replies, "701 BEGIN"));
    }

    [Fact]
    public void A_pause_ends_the_utterance_without_falling_back_to_another_voice()
    {
        // Pausing the neural voice must not start the espeak fallback: a user who
        // pressed pause would hear the flat voice start up.
        string root = Install(ctl: "cat '$ROOT/audio.wav'");

        Run(root, "SPEAK", "hello there", ".", "PAUSE");

        Assert.Empty(EspeakCalls(root));
    }

    [Fact]
    public void A_pause_when_nothing_is_speaking_is_still_answered()
    {
        var replies = Run(Install(), "PAUSE", "QUIT").Replies;
        Assert.Equal("704 PAUSE", replies[0]);
    }

    [Fact]
    public void The_next_utterance_after_a_pause_speaks_normally()
    {
        // speechd resumes by sending the rest as a new SPEAK. The pause must not
        // leave the module believing it is still paused.
        string root = Install();

        var replies = Run(root, "CHAR", "first", ".", "PAUSE", "CHAR", "second", ".").Replies;

        Assert.Equal(1, replies.Count(r => r == "704 PAUSE"));
        Assert.Contains("705 AUDIO", replies);
        Assert.Equal(1, replies.Count(r => r == "702 END"));
        // "first" was started before the pause arrived and killed; "second" is the
        // resumed utterance and is the last thing the fast voice was asked for.
        Assert.Equal("second", TextOf(EspeakCalls(root)[^1]));
    }

    // ---------------------------------------------------------------- volume

    [Fact]
    public void Volume_reaches_the_neural_voice_on_the_command_line()
    {
        string root = Install(ctl: "cat '$ROOT/audio.wav'");

        Run(root, "SET", "volume=-40", ".", "SPEAK", "hello", ".");

        string args = File.ReadAllText(Path.Combine(root, "ctl-args.txt"));
        Assert.Contains("--volume -40", args);
    }

    [Fact]
    public void Volume_reaches_the_fast_voice_as_the_same_gain()
    {
        string root = Install();

        Speak(root, "a", "SET", "volume=-50", ".");

        var args = EspeakArgs(Assert.Single(EspeakCalls(root)));
        int at = Array.IndexOf(args, "-a");
        Assert.True(at >= 0, "no -a reached espeak-ng");
        Assert.Equal("50", args[at + 1]);
    }

    [Fact]
    public void A_volume_of_zero_leaves_both_command_lines_as_they_always_were()
    {
        string root = Install(ctl: "cat '$ROOT/audio.wav'");

        Run(root, "SET", "volume=0", ".", "SPEAK", "hello", ".", "CHAR", "a", ".");

        Assert.DoesNotContain("--volume", File.ReadAllText(Path.Combine(root, "ctl-args.txt")));
        Assert.DoesNotContain("-a", EspeakArgs(Assert.Single(EspeakCalls(root))));
    }

    [Fact]
    public void Volume_survives_across_utterances_and_a_hostile_value_is_clamped()
    {
        string root = Install();

        Speak(root, "a", "SET", "volume=9999", ".");

        var args = EspeakArgs(Assert.Single(EspeakCalls(root)));
        Assert.Equal("200", args[Array.IndexOf(args, "-a") + 1]);      // 2.0x, espeak's ceiling
    }

    [Fact]
    public void Pitch_is_accepted_and_changes_nothing_on_either_voice()
    {
        string root = Install(ctl: "cat '$ROOT/audio.wav'");

        var replies = Run(root, "SET", "pitch=80", "pitch_range=50", ".", "SPEAK", "hello", ".", "CHAR", "a", ".").Replies;

        Assert.Equal("203 OK SETTINGS RECEIVED", replies[1]);
        Assert.DoesNotContain("pitch", File.ReadAllText(Path.Combine(root, "ctl-args.txt")));
        Assert.DoesNotContain("-p", EspeakArgs(Assert.Single(EspeakCalls(root))));
    }

    // ----------------------------------------------------------------- marks

    private const string TwoMarks =
        "<speak><mark name=\"__spd_id_1\"/>First part. <mark name=\"__spd_id_2\"/>Second part.</speak>";

    /// <summary>
    /// THE FALLBACK IS DETERMINISTIC, SO IT CARRIES THE EXACT-POSITION PROOF: the
    /// audio of the words before a mark, then the mark, then the words after.
    /// </summary>
    [Fact]
    public void An_index_mark_is_reported_between_the_audio_either_side_of_it()
    {
        string root = Install();

        var replies = Speak(root, "<speak>one <mark name=\"m1\"/>two</speak>");

        int begin = Index(replies, "701 BEGIN");
        int firstAudio = Index(replies, "705 AUDIO");
        int mark = Index(replies, "700 INDEX MARK");
        int lastAudio = LastIndex(replies, "705 AUDIO");
        int end = Index(replies, "702 END");

        Assert.True(begin >= 0 && firstAudio > begin, "no audio for the words before the mark");
        Assert.True(mark > firstAudio, "the mark came before the words that precede it");
        Assert.True(lastAudio > mark, "no audio for the words after the mark");
        Assert.True(end > lastAudio);
        Assert.Equal("700-m1", replies[mark - 1]);

        Assert.Equal(["one", "two"], EspeakCalls(root).Select(TextOf).ToArray());
    }

    [Fact]
    public void Several_marks_come_out_in_document_order_each_once()
    {
        var replies = Speak(Install(), TwoMarks);

        var names = replies.Where(r => r.StartsWith("700-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["700-__spd_id_1", "700-__spd_id_2"], names);
        Assert.Equal(2, replies.Count(r => r == "700 INDEX MARK"));
    }

    [Fact]
    public void A_mark_after_the_last_word_still_arrives_before_the_end()
    {
        var replies = Speak(Install(), "<speak>done<mark name=\"last\"/></speak>");

        Assert.True(Index(replies, "700-last") > Index(replies, "705 AUDIO"));
        Assert.True(Index(replies, "700-last") < Index(replies, "702 END"));
    }

    /// <summary>
    /// A document that is nothing but a mark has nothing to say and still has to
    /// announce it: a client waiting on the event is waiting for exactly this.
    /// </summary>
    [Fact]
    public void A_document_of_only_marks_reports_them_between_begin_and_end_with_no_audio()
    {
        var replies = Speak(Install(), "<speak><mark name=\"a\"/><mark name=\"b\"/></speak>");

        Assert.Equal(
            ["701 BEGIN", "700-a", "700 INDEX MARK", "700-b", "700 INDEX MARK", "702 END"],
            replies.SkipWhile(r => r != "701 BEGIN").TakeWhile((_, i) => i < 6).ToArray());
        Assert.DoesNotContain("705 AUDIO", replies);
    }

    [Fact]
    public void A_mark_name_cannot_break_the_protocol_line()
    {
        var replies = Speak(Install(), "<speak>x<mark name=\"evil&#10;702 END\"/>y</speak>");

        // One END, at the end: the newline in the name did not forge another.
        Assert.Equal(1, replies.Count(r => r == "702 END"));
        Assert.Contains("700-evil 702 END", replies);
    }

    [Fact]
    public void The_neural_voice_is_asked_for_marks_only_when_the_text_has_some()
    {
        string root = Install(ctl: "cat '$ROOT/audio.wav'");

        Run(root, "SPEAK", "<speak>no marks here</speak>", ".");
        Assert.DoesNotContain("--marks", File.ReadAllText(Path.Combine(root, "ctl-args.txt")));

        Run(root, "SPEAK", TwoMarks, ".");
        Assert.Contains("--marks", File.ReadAllText(Path.Combine(root, "ctl-args.txt")));
    }

    [Fact]
    public void Marks_the_neural_renderer_prints_become_events_in_order_before_the_end()
    {
        string root = Install(ctl:
            "echo '@vst-mark 0 __spd_id_1' >&2\n" +
            "cat '$ROOT/audio.wav'\n" +
            "echo '@vst-mark 4 __spd_id_2' >&2");

        var replies = Run(root, "SPEAK", TwoMarks, ".").Replies;

        Assert.Contains("705 AUDIO", replies);
        var names = replies.Where(r => r.StartsWith("700-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["700-__spd_id_1", "700-__spd_id_2"], names);

        int end = Index(replies, "702 END");
        Assert.True(end > LastIndex(replies, "700 INDEX MARK"), "a mark arrived after the END");
        Assert.Empty(EspeakCalls(root));
    }

    [Fact]
    public void Ordinary_stderr_from_the_renderer_is_not_mistaken_for_a_mark()
    {
        string root = Install(ctl: "echo 'warming up the model' >&2\ncat '$ROOT/audio.wav'");

        var replies = Run(root, "SPEAK", TwoMarks, ".").Replies;

        Assert.DoesNotContain(replies, r => r.StartsWith("700", StringComparison.Ordinal));
    }

    // -------------------------------------------------- ssml, on the fallback

    [Fact]
    public void The_fallback_is_never_handed_markup_to_read_aloud()
    {
        string root = Install();

        Speak(root, "<speak>plain <emphasis>words</emphasis></speak>");

        Assert.Equal("plain words", TextOf(Assert.Single(EspeakCalls(root))));
    }

    [Fact]
    public void A_malformed_document_is_stripped_not_refused()
    {
        string root = Install();

        var replies = Speak(root, "<speak>unclosed <s>words");

        Assert.Contains("705 AUDIO", replies);
        Assert.Equal("unclosed words", TextOf(Assert.Single(EspeakCalls(root))));
    }

    [Fact]
    public void The_fallback_speaks_each_language_in_its_own_voice()
    {
        string root = Install();

        Speak(root, "<speak>Hello <voice xml:lang=\"de-DE\">Guten Tag</voice></speak>", "SET", "language=en", ".");

        var calls = EspeakCalls(root).Select(EspeakArgs).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("en", calls[0][Array.IndexOf(calls[0], "-v") + 1]);
        Assert.Equal("de", calls[1][Array.IndexOf(calls[1], "-v") + 1]);
    }

    [Fact]
    public void The_fallback_speeds_up_a_prosody_fragment_by_the_engines_factor()
    {
        string root = Install();

        Speak(root, "<speak>steady <prosody rate=\"x-fast\">quick</prosody></speak>");

        var calls = EspeakCalls(root).Select(EspeakArgs).ToArray();
        int steady = int.Parse(calls[0][Array.IndexOf(calls[0], "-s") + 1]);
        int quick = int.Parse(calls[1][Array.IndexOf(calls[1], "-s") + 1]);

        Assert.Equal(175, steady);
        Assert.Equal((int)Math.Round(175 * Math.Pow(1.5, 0.6)), quick);
    }

    [Fact]
    public void A_break_becomes_silence_between_the_words_on_the_fallback()
    {
        string root = Install();

        var replies = Speak(root, "<speak>before<break time=\"500ms\"/>after</speak>");

        // 22050 Hz x 0.5 s x 2 bytes = 22050 bytes of silence, in blocks of 10000.
        // Count the blocks: two audio blocks for the words plus three for the pause.
        Assert.Equal(2 + 3, replies.Count(r => r == "705 AUDIO"));
        Assert.Equal(["before", "after"], EspeakCalls(root).Select(TextOf).ToArray());
    }

    [Fact]
    public void A_pause_in_the_middle_of_a_break_stops_the_silence_too()
    {
        string root = Install();

        var replies = Run(root, "CHAR", "<speak>x<break time=\"9s\"/>y</speak>", ".", "PAUSE").Replies;

        Assert.Equal(1, replies.Count(r => r == "704 PAUSE"));
        Assert.DoesNotContain("702 END", replies);
    }

    // --------------------------------------------------- BEGIN / END framing

    [Fact]
    public void Every_utterance_is_bracketed_by_exactly_one_begin_and_one_terminator()
    {
        string root = Install();

        foreach (string text in new[] { "plain", TwoMarks, "<speak>a<break time=\"100ms\"/>b</speak>", "   ", "<speak/>" })
        {
            var replies = Speak(root, text);

            Assert.Equal(1, replies.Count(r => r == "701 BEGIN"));
            Assert.Equal(1, replies.Count(r => r is "702 END" or "703 STOP" or "704 PAUSE"));
            Assert.True(Index(replies, "701 BEGIN") < Array.FindIndex(replies, r => r is "702 END" or "703 STOP" or "704 PAUSE"),
                $"END before BEGIN for: {text}");
        }
    }
}

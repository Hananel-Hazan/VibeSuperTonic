using System.Text;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.SpeechD;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// Items 1, 2, 27 and 56 of docs/CODE-REVIEW-2026-11-09.md: the fallback voice
/// that was silent in a language its payload lacks, key echo that said nothing
/// for punctuation and spaces, a stop that killed the renderer's children, and a
/// module that died on one command's exception. Each test failed against the
/// code before its fix except three guards, which pass on both and stop the fix
/// from overreaching: A_language_espeak_speaks_is_not_retried,
/// Capitals_are_not_announced_unless_asked and A_renderer_that_ignores_sigterm_is_killed.
///
/// <para>The renderers are shell scripts, as in <see cref="FidelityTests"/>. The
/// fake espeak-ng records each call (its arguments and then its stdin, joined by
/// a bell character) and imitates the real one's two ways of lacking a
/// language, both measured against the shipped payload on 2026-11-09:
/// <c>Can't read dictionary file</c> on stderr with exit 0 and a WAV of silence,
/// and <c>The specified espeak-ng voice does not exist</c> with exit 1 and
/// nothing.</para>
/// </summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly List<string> _roots = new();

    public void Dispose()
    {
        foreach (string root in _roots)
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Samples the fake espeak "speaks", chosen to need no escaping.</summary>
    private static readonly byte[] Speech = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x01];

    private string Install(string ctl = "exit 1")
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-review", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "espeak"));

        Wav(Path.Combine(root, "speech.wav"), Speech);
        Wav(Path.Combine(root, "silence.wav"), new byte[8]);
        File.WriteAllBytes(Path.Combine(root, "raw.pcm"), Speech);

        Script(Path.Combine(root, "vst-ctl"), ctl, root);
        Script(Path.Combine(root, "espeak", "espeak-ng"),
            "args=''\nfor a in \"$@\"; do args=\"$args$a\u0007\"; done\nargs=\"$args$(cat)\"\n" +
            "printf '%s\\n' \"$args\" >> '$ROOT/espeak-calls.txt'\n" +
            "case \"$*\" in\n" +
            "  *'-v ja'*) echo \"Can't read dictionary file: '$ROOT/espeak/espeak-ng-data/ja_dict'\" >&2; cat '$ROOT/silence.wav'; exit 0 ;;\n" +
            "  *'-v zz'*) echo 'Error: The specified espeak-ng voice does not exist.' >&2; exit 1 ;;\n" +
            // Item 105: a complaint about some other file, with real speech.
            "  *'-v nl'*) echo \"Can't read dictionary file: '$ROOT/espeak/espeak-ng-data/nl_extra'\" >&2; cat '$ROOT/speech.wav'; exit 0 ;;\n" +
            // Item 106: half the speech, then a wait for the test to see it arrive.
            "  *'-v fi'*) cat '$ROOT/speech.wav'; i=0; while [ ! -e '$ROOT/go' ] && [ $i -lt 50 ]; do sleep 0.1; i=$((i+1)); done\n" +
            "    if [ -e '$ROOT/go' ]; then echo streamed > '$ROOT/result'; else echo held > '$ROOT/result'; fi\n" +
            "    cat '$ROOT/raw.pcm'; exit 0 ;;\n" +
            "esac\n" +
            "cat '$ROOT/speech.wav'", root);
        return root;
    }

    private static void Wav(string path, byte[] samples)
    {
        using var f = File.Create(path);
        RenderWav.WriteHeader(f, 22050, 1);
        f.Write(samples);
    }

    private static void Script(string path, string body, string root)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("$ROOT", root) + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static (string[] Replies, byte[] Wire) Run(string root, Action<string> log, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();

        new SpeechdModule(input, output, new Voices(root, root), log, inputFd: -1).Run();

        byte[] wire = output.ToArray();
        return (Encoding.UTF8.GetString(wire).Split('\n', StringSplitOptions.RemoveEmptyEntries), wire);
    }

    private static string[][] EspeakCalls(string root)
    {
        string file = Path.Combine(root, "espeak-calls.txt");
        return File.Exists(file)
            ? File.ReadAllLines(file).Select(l => l.Split('\u0007')).ToArray()
            : [];
    }

    private static bool HasVoiceFlag(string[] call) => call.Contains("-v");

    /// <summary>The samples of every audio block on the wire, unescaped.</summary>
    private static List<byte[]> AudioBlocks(byte[] wire)
    {
        string text = Encoding.Latin1.GetString(wire);
        var blocks = new List<byte[]>();
        int from = 0;
        while (text.IndexOf("705-AUDIO\0", from, StringComparison.Ordinal) is var at and >= 0)
        {
            int start = at + "705-AUDIO\0".Length;
            int end = text.IndexOf("\n705 AUDIO\n", start, StringComparison.Ordinal);
            blocks.Add(Hdlc.Unescape(wire.AsSpan(start, end - start)));
            from = end;
        }

        return blocks;
    }

    // ------------------------------------------------------------- item 1

    /// <summary>
    /// THE HEBREW USER'S SILENCE, with <c>ja</c> standing in for any language the
    /// payload has no dictionary for. Before the fix there was one espeak call,
    /// with <c>-v ja</c>, and its silent WAV went to the server as success.
    /// </summary>
    [Fact]
    public void A_language_espeak_has_no_dictionary_for_is_said_again_in_its_default_voice()
    {
        string root = Install();
        var log = new List<string>();

        var (replies, wire) = Run(root, log.Add, "SET", "language=ja", ".", "CHAR", "a", ".");

        var calls = EspeakCalls(root);
        Assert.Equal(2, calls.Length);
        Assert.Equal("ja", calls[0][Array.IndexOf(calls[0], "-v") + 1]);
        Assert.False(HasVoiceFlag(calls[1]), "the retry still named the missing language");

        // The silence was held back; only the retry's speech reached the server.
        Assert.Equal(Speech, Assert.Single(AudioBlocks(wire)));
        Assert.Contains("702 END", replies);

        string line = Assert.Single(log);
        Assert.Contains("'ja'", line);
        Assert.Contains("Can't read dictionary file", line);
    }

    /// <summary>
    /// Logged once per language, and never asked of espeak again: a screen reader
    /// echoes every key, and a retry per key would double the fast voice's work
    /// and fill speechd's log.
    /// </summary>
    [Fact]
    public void A_missing_language_is_logged_once_and_not_tried_again()
    {
        string root = Install();
        var log = new List<string>();

        Run(root, log.Add, "SET", "language=ja", ".", "CHAR", "a", ".", "CHAR", "b", ".", "CHAR", "c", ".");

        var calls = EspeakCalls(root);
        Assert.Equal(4, calls.Length);                    // a, a again, b, c
        Assert.Single(calls, HasVoiceFlag);
        Assert.Single(log);
    }

    /// <summary>A voice espeak-ng has no file for at all fails loudly, and is retried the same way.</summary>
    [Fact]
    public void A_voice_espeak_does_not_have_is_said_in_its_default_voice()
    {
        string root = Install();
        var log = new List<string>();

        var (_, wire) = Run(root, log.Add, "SET", "language=zz", ".", "CHAR", "a", ".");

        Assert.Equal(Speech, Assert.Single(AudioBlocks(wire)));
        Assert.Contains(log, l => l.Contains("'zz'") && l.Contains("does not exist"));
    }

    /// <summary>A language the install does speak keeps its own voice.</summary>
    [Fact]
    public void A_language_espeak_speaks_is_not_retried()
    {
        string root = Install();
        var log = new List<string>();

        var (_, wire) = Run(root, log.Add, "SET", "language=de", ".", "CHAR", "a", ".", "CHAR", "b", ".");

        var calls = EspeakCalls(root);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal("de", c[Array.IndexOf(c, "-v") + 1]));
        Assert.Equal(2, AudioBlocks(wire).Count);
        Assert.Empty(log);
    }

    // ------------------------------------------------------------- item 2

    [Fact]
    public void A_char_is_said_with_its_punctuation()
    {
        string root = Install();

        Run(root, _ => { }, "CHAR", ",", ".");

        var call = Assert.Single(EspeakCalls(root));
        Assert.Contains("--punct", call);
        Assert.Equal(",", call[^1]);
    }

    /// <summary>
    /// SSIP's <c>space</c> and a bare space both reach espeak as the character
    /// itself inside a <c>tts:char</c> say-as, which espeak names in the voice's
    /// language. A bare space used to be an empty message: BEGIN, END, no audio.
    /// </summary>
    [Theory]
    [InlineData("space", 32)]
    [InlineData(" ", 32)]
    [InlineData("\t", 9)]
    public void A_space_is_named_rather_than_silent(string sent, int code)
    {
        string root = Install();

        var (_, wire) = Run(root, _ => { }, "CHAR", sent, ".");

        var call = Assert.Single(EspeakCalls(root));
        Assert.Contains("-m", call);
        Assert.Equal($"<say-as interpret-as=\"tts:char\">&#{code};</say-as>", call[^1]);
        Assert.Single(AudioBlocks(wire));
    }

    [Theory]
    [InlineData("spell", "2")]
    [InlineData("icon", "1")]
    public void A_capital_is_announced_when_the_client_asks(string mode, string k)
    {
        string root = Install();

        Run(root, _ => { }, "SET", "cap_let_recogn=" + mode, ".", "CHAR", "A", ".");

        var call = Assert.Single(EspeakCalls(root));
        Assert.Equal(k, call[Array.IndexOf(call, "-k") + 1]);
    }

    [Fact]
    public void Capitals_are_not_announced_unless_asked_and_reading_is_unchanged()
    {
        string root = Install();

        Run(root, _ => { }, "SET", "cap_let_recogn=none", ".", "CHAR", "A", ".", "SPEAK", "Hello, world.", ".");

        var calls = EspeakCalls(root);
        Assert.Equal(2, calls.Length);
        Assert.DoesNotContain("-k", calls[0]);

        // A SPEAK that fell back to espeak reads as it always did.
        Assert.DoesNotContain("--punct", calls[1]);
        Assert.DoesNotContain("-m", calls[1]);
    }

    // ------------------------------------------------------------ item 27

    /// <summary>
    /// vst-ctl starts the daemon as its own child when none is running. A stop
    /// that kills vst-ctl's whole process tree kills that daemon too; this fake
    /// starts a stand-in "daemon", sends its audio, closes its output and then
    /// lingers, so the module has to stop it.
    /// </summary>
    [Fact]
    public void Stopping_the_renderer_leaves_the_daemon_it_started_running()
    {
        string root = Install(ctl:
            "cat > /dev/null\n" +
            "sleep 30 </dev/null >/dev/null 2>&1 &\n" +
            "echo $! > '$ROOT/daemon.pid'\n" +
            "cat '$ROOT/speech.wav'\n" +
            "exec >&- 2>&-\n" +
            "exec sleep 20");

        int daemon = 0;
        try
        {
            var (replies, _) = Run(root, _ => { }, "SPEAK", "hello", ".");
            Assert.Contains("702 END", replies);

            daemon = int.Parse(File.ReadAllText(Path.Combine(root, "daemon.pid")).Trim());
            Thread.Sleep(100);
            Assert.True(IsAlive(daemon), "the daemon vst-ctl started was killed with it");
            Assert.Empty(EspeakCalls(root));                    // the neural voice spoke
        }
        finally
        {
            if (daemon > 0) try { System.Diagnostics.Process.GetProcessById(daemon).Kill(); } catch (ArgumentException) { }
        }
    }

    /// <summary>A renderer that ignores SIGTERM is still gone, after a short grace.</summary>
    [Fact]
    public void A_renderer_that_ignores_sigterm_is_killed()
    {
        string root = Install(ctl:
            "trap '' TERM\n" +
            "echo $$ > '$ROOT/ctl.pid'\n" +
            "cat > /dev/null\n" +
            "cat '$ROOT/speech.wav'\n" +
            "exec >&- 2>&-\n" +
            "exec sleep 20");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (replies, _) = Run(root, _ => { }, "SPEAK", "hello", ".");
        clock.Stop();

        Assert.Contains("702 END", replies);
        int ctl = int.Parse(File.ReadAllText(Path.Combine(root, "ctl.pid")).Trim());
        Assert.False(IsAlive(ctl), "a renderer that ignored SIGTERM outlived its utterance");
        Assert.True(clock.ElapsedMilliseconds < 5000, $"took {clock.ElapsedMilliseconds} ms");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException) { return false; }
    }

    // ------------------------------------------------------------ item 56

    /// <summary>
    /// An exception nobody expected, injected through the log: the first line
    /// logged throws. Mid-utterance, the utterance must still end, and the next
    /// command must still be answered. Before the fix it escaped Run and ended
    /// the module, which speechd does not restart until the next login.
    /// </summary>
    [Fact]
    public void An_unexpected_failure_mid_utterance_still_ends_it_and_the_module_carries_on()
    {
        // No vst-ctl, so the first line logged is "could not start", which is
        // logged outside the renderer's own try.
        string root = Install();
        File.Delete(Path.Combine(root, "vst-ctl"));
        int logged = 0;

        var (replies, _) = Run(root, _ => { if (logged++ == 0) throw new IOException("injected"); },
            "SPEAK", "hello", ".", "INIT");

        Assert.Equal(
            ["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "702 END"],
            replies[..4]);
        Assert.Equal("299 OK LOADED SUCCESSFULLY", replies[^1]);
    }

    /// <summary>
    /// The same in the middle of a parameter block: its remaining lines are read
    /// as the block and not as commands, and the reply the block owes is sent.
    /// </summary>
    [Fact]
    public void An_unexpected_failure_inside_a_parameter_block_does_not_desynchronise()
    {
        string root = Install();
        int logged = 0;

        var (replies, _) = Run(root, _ => { if (logged++ == 0) throw new IOException("injected"); },
            "AUDIO", "audio_output_method=alsa", "LIST VOICES", ".", "INIT");

        Assert.Equal(
            ["207 OK RECEIVING AUDIO SETTINGS", "203 OK AUDIO INITIALIZED"],
            replies[..2]);
        Assert.DoesNotContain(replies, r => r.StartsWith("200 OK VOICE LIST", StringComparison.Ordinal));
        Assert.Equal("299 OK LOADED SUCCESSFULLY", replies[^1]);
    }
    // ------------------------------------------------------------ item 104

    /// <summary>
    /// SSIP <c>KEY</c> names reach espeak-ng inside a <c>tts:key</c> say-as, the
    /// format speech-dispatcher's own sd_espeak-ng uses. espeak-ng then names
    /// space, underscore, double-quote, tab and single characters in the voice's
    /// language: measured with <c>-x -v de</c> against the shipped payload,
    /// <c>space</c> as text is <c>(en)sp'eIs(de)</c> and inside tts:key
    /// <c>l'e:rts'aIC@n</c> ("Leerzeichen"). Before the fix the name went as
    /// plain text, with no <c>-m</c>.
    /// </summary>
    [Theory]
    [InlineData("space", "space")]
    [InlineData("shift_a", "shift_a")]
    [InlineData("<", "&lt;")]
    [InlineData("double-quote", "double-quote")]
    public void A_key_is_named_in_the_voice_s_language(string key, string escaped)
    {
        string root = Install();

        var (replies, wire) = Run(root, _ => { }, "SET", "language=de", ".", "KEY", key, ".");

        var call = Assert.Single(EspeakCalls(root));
        Assert.Contains("-m", call);
        Assert.Equal("de", call[Array.IndexOf(call, "-v") + 1]);
        Assert.Equal($"<say-as interpret-as=\"tts:key\">{escaped}</say-as>", call[^1]);
        Assert.Single(AudioBlocks(wire));
        Assert.Contains("702 END", replies);
    }

    // ------------------------------------------------------------ item 105

    /// <summary>
    /// A "Can't read dictionary file" line beside real speech is not a missing
    /// language: the speech is sent, nothing is said twice, and the language
    /// keeps its voice. Before the fix the speech was dropped, the text said
    /// again in the default voice and the language demoted for the module's life.
    /// </summary>
    [Fact]
    public void A_dictionary_complaint_beside_real_speech_does_not_demote_the_language()
    {
        string root = Install();
        var log = new List<string>();

        var (_, wire) = Run(root, log.Add, "SET", "language=nl", ".", "CHAR", "a", ".", "CHAR", "b", ".");

        var calls = EspeakCalls(root);
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.Equal("nl", c[Array.IndexOf(c, "-v") + 1]));
        Assert.Equal(new[] { Speech, Speech }, AudioBlocks(wire));
        Assert.Empty(log);
    }

    // ------------------------------------------------------------ item 106

    /// <summary>
    /// The first run in a language streams: the fake espeak sends its first
    /// samples and then waits, up to 5 s, for them to reach the server before it
    /// sends the rest. Before the fix the module held the whole WAV, so the fake
    /// waited out its 5 s and wrote "held".
    /// </summary>
    [Fact]
    public void The_first_run_in_a_language_streams_its_audio()
    {
        string root = Install();
        var input = new MemoryStream(Encoding.UTF8.GetBytes("SET\nlanguage=fi\n.\nCHAR\na\n.\n"));
        var output = new SignalOnAudio(Path.Combine(root, "go"));

        new SpeechdModule(input, output, new Voices(root, root), _ => { }, inputFd: -1).Run();

        Assert.Equal("streamed", File.ReadAllText(Path.Combine(root, "result")).Trim());
        Assert.Equal(new[] { Speech, Speech }, AudioBlocks(output.ToArray()));
    }

    /// <summary>Creates a file once the first complete audio block has been written.</summary>
    private sealed class SignalOnAudio(string signal) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            Signal();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer);
            Signal();
        }

        private void Signal()
        {
            if (!File.Exists(signal) &&
                Encoding.Latin1.GetString(ToArray()).Contains("\n705 AUDIO\n", StringComparison.Ordinal))
                File.WriteAllText(signal, "");
        }
    }

    // ------------------------------------------------------------ item 107

    /// <summary>
    /// A failure inside INIT still answers INIT, with a reply speechd reads as a
    /// refusal rather than waiting for one that never comes. The failure is
    /// injected as the first write of INIT's reply throwing. Before the fix INIT
    /// owed nothing, so nothing at all was written.
    /// </summary>
    [Fact]
    public void A_failure_inside_init_still_answers_it()
    {
        string root = Install();
        var log = new List<string>();
        var input = new MemoryStream(Encoding.UTF8.GetBytes("INIT\n"));
        var output = new ThrowOnFirst("299-");

        new SpeechdModule(input, output, new Voices(root, root), log.Add, inputFd: -1).Run();

        string[] replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["399 ERR CANT INIT MODULE"], replies);
        Assert.Contains(log, l => l.StartsWith("INIT:", StringComparison.Ordinal));
    }

    /// <summary>Throws once, on the first write that begins with <c>prefix</c>.</summary>
    private sealed class ThrowOnFirst(string prefix) : MemoryStream
    {
        private bool _thrown;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_thrown && Encoding.UTF8.GetString(buffer).StartsWith(prefix, StringComparison.Ordinal))
            {
                _thrown = true;
                throw new IOException("injected");
            }

            base.Write(buffer);
        }
    }
}

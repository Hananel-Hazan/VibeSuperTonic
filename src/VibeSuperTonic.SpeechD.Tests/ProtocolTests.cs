using System.Text;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// The module protocol, as speech-dispatcher actually speaks it — read out of
/// <c>src/modules/module_process.c</c> and confirmed against a running server.
///
/// <para><b>Order is the thing being tested, not politeness.</b> A command is
/// answered <em>before</em> its parameters arrive: the server does not send the
/// block until the first reply lands, so a module that reads the block first
/// deadlocks. And one that replies out of order desynchronises the stream, at
/// which point speech-dispatcher <b>refuses to start at all</b> rather than run
/// without that module — taking the user's working espeak-ng with it. The gate
/// probe did exactly this once. docs/SPEECHD-PLAN.md, trap 17.</para>
/// </summary>
public class ProtocolTests
{
    /// <summary>
    /// Drive the module with a canned session and collect what it wrote.
    /// The input stream cannot block, so the polled descriptor is disabled.
    /// </summary>
    private static string[] Run(params string[] lines) => RunIn(FakeInstall.Value, lines);

    private static string[] RunIn(string root, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();

        new SpeechdModule(input, output, new Voices(root, root), _ => { }, inputFd: -1).Run();

        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// An install whose espeak-ng exists but cannot run.
    ///
    /// <para>Enough for INIT, which asks only whether a fallback voice is
    /// PRESENT — because a module with no fallback turns every later failure into
    /// silence, and being absent is better than that. Whether the real one
    /// renders is the packer's behavioural assertion against the real payload; it
    /// is not something a unit test can honestly claim.</para>
    /// </summary>
    private static readonly Lazy<string> FakeInstall = new(() => Install(working: false));

    /// <summary>
    /// An install whose espeak-ng renders, so the paths after a SUCCESSFUL voice
    /// can be reached at all.
    ///
    /// <para><b>This exists because sabotage found the tests could not see
    /// them.</b> With both voices failing, every utterance ended through the
    /// both-failed branch — so deleting the 702 END from the success branch
    /// changed nothing any test could observe, and an unterminated utterance is
    /// the failure that wedges the server's queue for every module. The fake also
    /// records its arguments, which is the only way to assert what text actually
    /// reached a renderer.</para>
    /// </summary>
    private static readonly Lazy<string> RenderingInstall = new(() => Install(working: true));

    private static string TextHandedToTheVoice(string root) =>
        File.ReadAllText(Path.Combine(root, "last-text.txt")).TrimEnd('\n');

    private static string Install(bool working)
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "espeak"));
        string bin = Path.Combine(root, "espeak", "espeak-ng");

        if (!working)
        {
            File.WriteAllText(bin, "not really espeak");
            return root;
        }

        // A canned WAV beside the script, written by the same code the product
        // writes headers with — so this fake speaks the format the real one does.
        string wav = Path.Combine(root, "probe.wav");
        using (var f = File.Create(wav))
        {
            VibeSuperTonic.Core.Ipc.RenderWav.WriteHeader(f, 22050, 1);

            // Samples chosen to contain both bytes that cannot travel unescaped,
            // so a block written from this exercises the escaping end to end.
            f.Write(new byte[] { 0x0A, 0x00, 0x7D, 0x00, 0x01, 0x02, 0x0A, 0x7D });
        }

        File.WriteAllText(bin,
            "#!/bin/sh\n" +
            "# The last argument is the text; everything before it is flags.\n" +
            "for a in \"$@\"; do last=\"$a\"; done\n" +
            $"printf '%s' \"$last\" > '{Path.Combine(root, "last-text.txt")}'\n" +
            $"cat '{wav}'\n");
        File.SetUnixFileMode(bin,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return root;
    }

    // ------------------------------------------------------------------ INIT

    /// <summary>
    /// INIT is the only reply speech-dispatcher acts on: a module that fails it
    /// is dropped and never appears in <c>spd-say -O</c> at all.
    /// </summary>
    [Fact]
    public void Init_reports_success_and_says_what_the_module_is()
    {
        var replies = Run("INIT", "QUIT");

        Assert.StartsWith("299-", replies[0]);
        Assert.Equal("299 OK LOADED SUCCESSFULLY", replies[1]);
        Assert.Equal("210 OK QUIT", replies[2]);
    }

    /// <summary>
    /// TRAP 14, AND THE ONLY THING INIT IS ALLOWED TO REFUSE FOR. No espeak
    /// beside the module means no fallback, and no fallback means every later
    /// failure becomes silence. Better to be absent from spd-say -O — where a
    /// user can see the module is missing — than to be present and sometimes
    /// mute.
    ///
    /// <para>It does NOT refuse for a missing daemon or an undownloaded model:
    /// espeak still answers, and declining to load over the neural voice would
    /// take the working voice down with it.</para>
    /// </summary>
    [Fact]
    public void Init_refuses_when_there_is_no_fallback_voice_at_all()
    {
        string empty = Path.Combine(Path.GetTempPath(), "vst-speechd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        var replies = RunIn(empty, "INIT", "QUIT");

        Assert.Contains(replies, r => r.StartsWith("399", StringComparison.Ordinal));
        Assert.DoesNotContain(replies, r => r.StartsWith("299 OK", StringComparison.Ordinal));

        // And it names the path it looked at, because the person reading this is
        // looking at speechd's log wondering why the module vanished.
        Assert.Contains(replies, r => r.Contains("espeak", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------- two replies

    /// <summary>
    /// THE COMMAND IS ANSWERED BEFORE ITS PARAMETERS ARRIVE. This is the shape a
    /// module that read the block first would deadlock on, and the ordering the
    /// server desynchronises against.
    /// </summary>
    [Fact]
    public void Audio_is_answered_twice_and_in_the_right_order()
    {
        var replies = Run("AUDIO", "audio_output_method=server", ".", "QUIT");

        Assert.Equal("207 OK RECEIVING AUDIO SETTINGS", replies[0]);
        Assert.Equal("203 OK AUDIO INITIALIZED", replies[1]);
        Assert.Equal("210 OK QUIT", replies[2]);
    }

    [Fact]
    public void Set_is_answered_twice_and_in_the_right_order()
    {
        var replies = Run("SET", "rate=0", "language=en", ".", "QUIT");

        Assert.Equal("203 OK RECEIVING SETTINGS", replies[0]);
        Assert.Equal("203 OK SETTINGS RECEIVED", replies[1]);
    }

    [Fact]
    public void Loglevel_is_answered_twice_and_in_the_right_order()
    {
        var replies = Run("LOGLEVEL", "log_level=0", ".", "QUIT");

        Assert.Equal("207 OK RECEIVING LOGLEVEL SETTINGS", replies[0]);
        Assert.Equal("203 OK LOGLEVEL SET", replies[1]);
    }

    /// <summary>
    /// The real opening exchange, in the order a server sends it — taken from
    /// the gate probe's log rather than from the source, so it is what a server
    /// does and not only what it says it does.
    /// </summary>
    [Fact]
    public void A_whole_session_opening_stays_in_step()
    {
        var replies = Run(
            "INIT",
            "AUDIO", "audio_output_method=server", ".",
            "LOGLEVEL", "log_level=0", ".",
            "SET", "rate=0", "pitch=0", "volume=0", "punctuation_mode=none",
                   "spelling_mode=off", "cap_let_recogn=none", "voice=male1",
                   "language=c", "synthesis_voice=NULL", ".",
            "QUIT");

        Assert.Equal(
            new[]
            {
                "299 OK LOADED SUCCESSFULLY",
                "207 OK RECEIVING AUDIO SETTINGS",
                "203 OK AUDIO INITIALIZED",
                "207 OK RECEIVING LOGLEVEL SETTINGS",
                "203 OK LOGLEVEL SET",
                "203 OK RECEIVING SETTINGS",
                "203 OK SETTINGS RECEIVED",
                "210 OK QUIT",
            },
            replies.Where(r => !r.StartsWith("299-", StringComparison.Ordinal)).ToArray());
    }

    /// <summary>
    /// Parameters this product has no lever on are ACCEPTED and ignored — a
    /// decision, not an omission. Answering an error to a parameter every client
    /// sends would make the module look broken to someone not using the feature.
    /// docs/SPEECHD-PLAN.md, trap 11.
    /// </summary>
    [Fact]
    public void Settings_with_no_meaning_here_are_accepted_rather_than_refused()
    {
        var replies = Run(
            "SET", "punctuation_mode=all", "spelling_mode=on", "cap_let_recogn=spell",
                   "pitch=50", "volume=-20", "nonsense=xyz", "malformed-no-equals", ".",
            "QUIT");

        Assert.Equal("203 OK SETTINGS RECEIVED", replies[1]);
        Assert.DoesNotContain(replies, r => r.StartsWith("30", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------- speak

    /// <summary>
    /// An empty label is a real thing that happens dozens of times a session. It
    /// produces no audio and is not an error — but BEGIN and END still have to
    /// arrive, or the server waits for an utterance that never ends and the
    /// queue wedges for every module.
    /// </summary>
    [Fact]
    public void An_empty_message_still_begins_and_ends()
    {
        var replies = Run("SPEAK", "", ".", "QUIT");

        Assert.Equal("202 OK RECEIVING MESSAGE", replies[0]);
        Assert.Equal("200 OK SPEAKING", replies[1]);
        Assert.Equal("701 BEGIN", replies[2]);
        Assert.Equal("702 END", replies[3]);
    }

    /// <summary>
    /// EVERY UTTERANCE MUST END. Whatever happened to the renderers — and in this
    /// test both are absent, because nothing is installed beside the test binary
    /// — the module owes the server a 702. An utterance left open is a screen
    /// reader that never speaks again.
    /// </summary>
    [Theory]
    [InlineData("SPEAK")]
    [InlineData("CHAR")]
    [InlineData("KEY")]
    [InlineData("SOUND_ICON")]
    public void Every_utterance_is_closed_even_when_both_voices_fail(string command)
    {
        var replies = Run(command, "hello", ".", "QUIT");

        Assert.Equal("202 OK RECEIVING MESSAGE", replies[0]);
        Assert.Equal("200 OK SPEAKING", replies[1]);
        Assert.Equal("701 BEGIN", replies[2]);
        Assert.Contains("702 END", replies);
        Assert.Equal("210 OK QUIT", replies[^1]);
    }

    /// <summary>
    /// A line beginning with <c>..</c> is how SSIP carries one that really begins
    /// with a dot — and a lone dot ends the message. A module that skipped the
    /// unescaping would silently drop a character from any text that did.
    /// </summary>
    [Fact]
    public void A_leading_double_dot_is_unescaped_rather_than_dropped()
    {
        // Nothing asserts the text directly — it goes to a renderer that is not
        // installed here — but the message must still terminate on the LONE dot
        // and not on the escaped one, which this proves by reaching QUIT.
        var replies = Run("SPEAK", "..hidden", "..", ".", "QUIT");

        Assert.Equal("210 OK QUIT", replies[^1]);
        Assert.Contains("702 END", replies);
    }

    // ------------------------------------------------- when a voice does work

    /// <summary>
    /// THE SUCCESS PATH, AND IT WAS UNREACHABLE UNTIL A VOICE COULD RENDER.
    /// Sabotage showed that deleting the 702 END from this branch broke no test,
    /// because every utterance in the suite was ending through the both-failed
    /// branch instead. An unterminated utterance wedges the server's queue for
    /// every module, not just this one.
    /// </summary>
    [Fact]
    public void An_utterance_that_renders_begins_ends_and_carries_audio()
    {
        // NO TRAILING QUIT, AND THAT IS THE POINT OF THE COMMENT. The audio loop
        // reads whatever the server has already sent between blocks, which is how
        // a STOP interrupts an utterance in flight. A whole session queued up
        // front is not what a server does — it sends nothing until the utterance
        // ends — and feeding one here made the module read its own QUIT as a stop
        // and produce no audio at all.
        var replies = RunIn(RenderingInstall.Value, "CHAR", "a", ".");

        Assert.Equal("202 OK RECEIVING MESSAGE", replies[0]);
        Assert.Equal("200 OK SPEAKING", replies[1]);
        Assert.Equal("701 BEGIN", replies[2]);
        Assert.Contains(replies, r => r.StartsWith("705-", StringComparison.Ordinal));
        Assert.Contains("702 END", replies);
    }

    /// <summary>
    /// The audio actually reaches the server as a block, with its samples escaped
    /// — the whole of route B, exercised through the module rather than through
    /// the writer on its own.
    /// </summary>
    [Fact]
    public void The_samples_reach_the_server_escaped_and_terminated()
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes("KEY\nControl_L\n.\n"));
        var output = new MemoryStream();
        new SpeechdModule(input, output, new Voices(RenderingInstall.Value, RenderingInstall.Value), _ => { }, inputFd: -1).Run();

        byte[] wire = output.ToArray();
        string text = Encoding.Latin1.GetString(wire);

        Assert.Contains("705-sample_rate=22050\n", text);
        Assert.Contains("705-num_samples=4\n", text);          // 8 bytes of 16-bit mono
        Assert.Contains("\n705 AUDIO\n", text);

        // The NUL separator, and the escaping of the two bytes the fake voice
        // deliberately puts in its samples.
        int marker = text.IndexOf("705-AUDIO", StringComparison.Ordinal);
        Assert.Equal(0, wire[marker + "705-AUDIO".Length]);

        int start = marker + "705-AUDIO".Length + 1;
        int end = text.IndexOf("\n705 AUDIO\n", start, StringComparison.Ordinal);
        Assert.Equal(
            new byte[] { 0x0A, 0x00, 0x7D, 0x00, 0x01, 0x02, 0x0A, 0x7D },
            VibeSuperTonic.Core.SpeechD.Hdlc.Unescape(wire[start..end]));
    }

    /// <summary>
    /// What the renderer was actually handed. Until this existed, nothing checked
    /// the message body at all — so a module that dropped the SSIP dot-unescaping,
    /// and with it the first character of any line beginning with a dot, passed
    /// every test in this file.
    /// </summary>
    [Fact]
    public void A_leading_double_dot_reaches_the_voice_as_a_single_dot()
    {
        string root = Install(working: true);
        RunIn(root, "SPEAK", "..a line that starts with a dot", ".", "QUIT");

        Assert.Equal(".a line that starts with a dot", TextHandedToTheVoice(root));
    }

    [Fact]
    public void A_multi_line_message_reaches_the_voice_whole()
    {
        string root = Install(working: true);
        RunIn(root, "CHAR", "first", "second", ".", "QUIT");

        Assert.Equal("first\nsecond", TextHandedToTheVoice(root));
    }

    /// <summary>
    /// A STOP ARRIVING MID-UTTERANCE IS THE OPERATION ORCA PERFORMS MOST, and
    /// it is why the audio loop reads between blocks at all rather than writing
    /// everything it has. The utterance ends with 703, not 702: the server needs
    /// to know it was cancelled rather than finished.
    /// </summary>
    [Fact]
    public void A_stop_that_arrives_during_audio_cuts_the_utterance_short()
    {
        var replies = RunIn(RenderingInstall.Value, "SPEAK", "some words", ".", "STOP");

        Assert.Contains("703 STOP", replies);
        Assert.DoesNotContain("702 END", replies);
    }

    /// <summary>
    /// QUIT during audio is a shutdown, so the utterance stops too — and the
    /// QUIT is put back rather than swallowed, or the module would go on running
    /// after the server had asked it to leave.
    /// </summary>
    [Fact]
    public void A_quit_during_audio_stops_the_utterance_and_is_still_obeyed()
    {
        var replies = RunIn(RenderingInstall.Value, "SPEAK", "some words", ".", "QUIT");

        Assert.Contains("703 STOP", replies);
        Assert.Equal("210 OK QUIT", replies[^1]);
    }

    // -------------------------------------------------------------- controls

    [Fact]
    public void Stop_and_pause_are_answered()
    {
        var replies = Run("STOP", "PAUSE", "QUIT");

        Assert.Equal("703 STOP", replies[0]);
        Assert.Equal("704 PAUSE", replies[1]);
    }

    [Fact]
    public void The_voice_list_is_never_empty()
    {
        var replies = Run("LIST VOICES", "QUIT");

        // A module that lists nothing is one a user cannot select at all.
        Assert.Contains(replies, r => r.StartsWith("200-", StringComparison.Ordinal));
        Assert.Contains("200 OK VOICE LIST SENT", replies);

        // Name, language, variant — tab-separated, which is the format the
        // server parses. A space here and the voice silently does not appear.
        foreach (string row in replies.Where(r => r.StartsWith("200-", StringComparison.Ordinal)))
            Assert.Equal(3, row["200-".Length..].Split('\t').Length);
    }

    /// <summary>
    /// An unknown command is ANSWERED. Silence would leave the server waiting on
    /// a module that is perfectly healthy, and a future speech-dispatcher adding
    /// a command is a thing that happens.
    /// </summary>
    [Fact]
    public void An_unknown_command_gets_a_reply_rather_than_silence()
    {
        var replies = Run("SOMETHING_NEW", "QUIT");

        Assert.Single(replies.Where(r => r.StartsWith("300", StringComparison.Ordinal)));
        Assert.Equal("210 OK QUIT", replies[^1]);
    }

    /// <summary>
    /// speech-dispatcher closing stdin is how a module is asked to go away when
    /// the server shuts down. It must exit rather than spin on end-of-file.
    /// </summary>
    [Fact]
    public void Closed_input_ends_the_module()
    {
        var replies = Run("INIT");
        Assert.Equal("299 OK LOADED SUCCESSFULLY", replies[^1]);
    }
}

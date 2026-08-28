using System.Text;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// S3 end to end through the protocol: what a real store produces in
/// <c>LIST VOICES</c>, and what a client's selection turns into on the
/// renderer's command line.
///
/// <para><b>The renderer's arguments are the assertion that matters here.</b>
/// Everything between speechd's <c>voice=female1</c> and a German F1 actually
/// speaking is invisible from outside — a module that resolved the voice
/// perfectly and then dropped it on the way to <c>vst-ctl</c> would pass every
/// reply-shaped test in this project while always speaking the default voice. So
/// the fake <c>vst-ctl</c> records its whole argv.</para>
/// </summary>
public class VoiceSelectionTests
{
    // ------------------------------------------------------------- the install

    /// <summary>
    /// An install with a store in it: a fake <c>vst-ctl</c> that renders and
    /// records its arguments, and a fake espeak so INIT does not refuse.
    /// </summary>
    private static string Install(IEnumerable<string> styles, IEnumerable<string> piperVoices)
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-s3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "espeak"));

        string stylesDir = Path.Combine(root, "models", "voice_styles");
        Directory.CreateDirectory(stylesDir);
        foreach (string style in styles)
            File.WriteAllText(Path.Combine(stylesDir, style + ".json"), "{}");

        // BOTH FILES OR NEITHER is the store's own rule for what counts as an
        // installed Piper voice, so a fixture that wrote one would be testing a
        // half-finished download.
        foreach (string id in piperVoices)
        {
            string dir = Path.Combine(root, "models", "piper", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".onnx"), "weights");
            File.WriteAllText(Path.Combine(dir, id + ".onnx.json"), "{}");
        }

        string wav = Path.Combine(root, "probe.wav");
        using (var f = File.Create(wav))
        {
            VibeSuperTonic.Core.Ipc.RenderWav.WriteHeader(f, 22050, 1);
            f.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        }

        Script(Path.Combine(root, "vst-ctl"),
            $"printf '%s\\n' \"$@\" > '{Path.Combine(root, "ctl-argv.txt")}'\n" +
            $"cat '{wav}'\n");

        // Present but not runnable: INIT only asks whether a fallback EXISTS, and
        // a fallback that ran would mask a neural path that silently failed.
        File.WriteAllText(Path.Combine(root, "espeak", "espeak-ng"), "not really espeak");

        return root;
    }

    private static void Script(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string[] Run(string root, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();

        new SpeechdModule(input, output, new Voices(root, root), _ => { }, inputFd: -1).Run();

        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>What the fake vst-ctl was actually invoked with.</summary>
    private static string[] Argv(string root)
    {
        string path = Path.Combine(root, "ctl-argv.txt");
        return File.Exists(path)
            ? File.ReadAllLines(path)
            : Array.Empty<string>();
    }

    /// <summary>The value after a flag, or null when the flag was not passed at all.</summary>
    private static string? ValueOf(string[] argv, string flag)
    {
        int at = Array.IndexOf(argv, flag);
        return at >= 0 && at + 1 < argv.Length ? argv[at + 1] : null;
    }

    /// <summary>
    /// One SET and one SPEAK, and <b>no trailing QUIT</b>. The audio loop reads
    /// stdin between blocks so that a STOP can interrupt an utterance in flight,
    /// and a QUIT sitting there is one — it is answered with 703, correctly, and
    /// it would mean no test here could see how an utterance normally ends.
    /// Closing stdin instead is what speech-dispatcher does at shutdown anyway.
    /// </summary>
    private static string[] Speak(string root, params string[] settings)
    {
        var lines = new List<string> { "SET" };
        lines.AddRange(settings);
        lines.Add(".");
        lines.AddRange(new[] { "SPEAK", "hello", "." });
        return Run(root, lines.ToArray());
    }

    private static readonly string[] TenStyles =
        { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" };

    // ------------------------------------------------------------ LIST VOICES

    /// <summary>
    /// Only what is installed appears — trap 8. A list offering voices that are
    /// not downloaded is one where most selections produce nothing.
    /// </summary>
    [Fact]
    public void The_list_is_read_from_the_store()
    {
        string root = Install(new[] { "M1", "F1" }, new[] { "de_DE-thorsten-low" });

        var rows = Rows(Run(root, "LIST VOICES", "QUIT"));

        Assert.Contains(rows, r => r.StartsWith("supertonic-M1-en\t", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("de_DE-thorsten-low\tde-de\t", StringComparison.Ordinal));

        // Two styles over the 31 languages Supertonic speaks, plus the one Piper
        // voice, plus the echo row.
        Assert.Equal(2 * 31 + 1 + 1, rows.Count);
    }

    /// <summary>
    /// A voice that is not installed must not be listed, or a user selects it and
    /// hears nothing.
    /// </summary>
    [Fact]
    public void An_uninstalled_voice_is_not_offered()
    {
        string root = Install(new[] { "M1" }, Array.Empty<string>());

        var rows = Rows(Run(root, "LIST VOICES", "QUIT"));

        Assert.DoesNotContain(rows, r => r.StartsWith("supertonic-F1-", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("lessac", StringComparison.Ordinal));
    }

    /// <summary>
    /// TRAP 14, AND THE REASON THE ECHO ROW IS UNCONDITIONAL. On a fresh install
    /// nothing has been downloaded, and a module that lists nothing is one a user
    /// cannot select from — it looks broken while being perfectly healthy, and
    /// the espeak beside it needs no model at all.
    /// </summary>
    [Fact]
    public void A_store_with_no_models_still_offers_the_voice_that_cannot_fail()
    {
        string root = Install(Array.Empty<string>(), Array.Empty<string>());

        var rows = Rows(Run(root, "LIST VOICES", "QUIT"));

        Assert.Equal("vibesupertonic-echo\ten\techo", Assert.Single(rows));
    }

    /// <summary>
    /// Re-read per request, not cached — and the voice is installed WHILE ONE
    /// MODULE IS RUNNING, which is the only version of this test that means
    /// anything.
    ///
    /// <para><b>The first way of writing it did not test this.</b> It asked two
    /// separate module instances, so a cache held in a field would have been
    /// re-read anyway and every mutation of the rule passed. That matters here
    /// more than it looks: speech-dispatcher starts this process at login and it
    /// lives as long as the session does, so anything cached at startup is stale
    /// for the rest of the day — a user downloads a voice in the Voices tab,
    /// opens their screen reader's picker, and it is not there until they log
    /// out.</para>
    /// </summary>
    [Fact]
    public void A_voice_installed_while_the_module_runs_appears_in_the_next_list()
    {
        string root = Install(new[] { "M1" }, Array.Empty<string>());

        var input = new ScriptedInput(
            "LIST VOICES\n",
            () => File.WriteAllText(Path.Combine(root, "models", "voice_styles", "F1.json"), "{}"),
            "LIST VOICES\n");
        var output = new MemoryStream();

        new SpeechdModule(input, output, new Voices(root, root), _ => { }, inputFd: -1).Run();

        string all = Encoding.UTF8.GetString(output.ToArray());
        string[] chunks = all.Split("200 OK VOICE LIST SENT");

        // Two lists were asked for and two were sent; the text after the last
        // sentinel is not one of them.
        Assert.Equal(2, chunks.Length - 1);

        var lists = chunks
            .Take(2)
            .Select(chunk => Rows(chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
            .ToList();

        Assert.DoesNotContain(lists[0], r => r.StartsWith("supertonic-F1-", StringComparison.Ordinal));
        Assert.Contains(lists[1], r => r.StartsWith("supertonic-F1-", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stdin whose second command only exists once something has happened in
    /// between. The reader takes 4 KB chunks, so a single buffer holding both
    /// commands would be consumed before the module answered the first — and the
    /// install would happen after both had already been read.
    /// </summary>
    private sealed class ScriptedInput : Stream
    {
        private readonly byte[] _first;
        private readonly byte[] _rest;
        private readonly Action _between;
        private int _at;
        private bool _ran;

        internal ScriptedInput(string first, Action between, string rest)
        {
            _first = Encoding.UTF8.GetBytes(first);
            _rest = Encoding.UTF8.GetBytes(rest);
            _between = between;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < _first.Length)
            {
                int n = Math.Min(count, _first.Length - _at);
                Array.Copy(_first, _at, buffer, offset, n);
                _at += n;
                return n;
            }

            if (!_ran)
            {
                _between();
                _ran = true;
            }

            int from = _at - _first.Length;
            if (from >= _rest.Length) return 0;

            int m = Math.Min(count, _rest.Length - from);
            Array.Copy(_rest, from, buffer, offset, m);
            _at += m;
            return m;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static List<string> Rows(IEnumerable<string> replies) => replies
        .Where(r => r.StartsWith("200-", StringComparison.Ordinal))
        .Select(r => r["200-".Length..])
        .ToList();

    /// <summary>
    /// Tab-separated, three fields. A space instead and the server parses the row
    /// wrongly and the voice silently does not appear.
    /// </summary>
    [Fact]
    public void Every_row_is_three_tab_separated_fields()
    {
        string root = Install(TenStyles, new[] { "en_US-lessac-medium" });

        foreach (string row in Rows(Run(root, "LIST VOICES", "QUIT")))
            Assert.Equal(3, row.Split('\t').Length);
    }

    // -------------------------------------------------------------- selection

    /// <summary>
    /// The symbolic form is what nearly every client sends — measured — so this
    /// is the path that decides whether voice selection works at all.
    /// </summary>
    [Fact]
    public void A_symbolic_voice_and_a_language_reach_the_renderer()
    {
        string root = Install(TenStyles, Array.Empty<string>());

        Speak(root, "voice=female1", "language=de", "synthesis_voice=NULL");

        var argv = Argv(root);
        Assert.Equal("supertonic:F1", ValueOf(argv, "--voice"));
        Assert.Equal("de", ValueOf(argv, "--language"));
    }

    /// <summary>
    /// Selecting a row by name sends synthesis_voice and no voice at all, and the
    /// language it sends beside it is the CLIENT's, not the row's — so the row's
    /// own language has to come from the name or a German voice speaks English.
    /// </summary>
    [Fact]
    public void A_named_row_carries_its_own_language_not_the_clients()
    {
        string root = Install(TenStyles, Array.Empty<string>());

        Speak(root, "voice=male1", "language=en-us", "synthesis_voice=supertonic-F3-ja");

        var argv = Argv(root);
        Assert.Equal("supertonic:F3", ValueOf(argv, "--voice"));
        Assert.Equal("ja", ValueOf(argv, "--language"));
    }

    /// <summary>
    /// A Piper model IS its language, so sending one would state a second opinion
    /// about the same utterance.
    /// </summary>
    [Fact]
    public void A_piper_voice_is_rendered_without_a_language()
    {
        string root = Install(Array.Empty<string>(), new[] { "de_DE-thorsten-low" });

        Speak(root, "voice=male1", "language=de", "synthesis_voice=NULL");

        var argv = Argv(root);
        Assert.Equal("piper:de_DE-thorsten-low", ValueOf(argv, "--voice"));
        Assert.Null(ValueOf(argv, "--language"));
    }

    /// <summary>
    /// speechd does not validate synthesis_voice against the list it was given —
    /// measured. A stale name must still speak, with the daemon's default, rather
    /// than falling through to the espeak buzz.
    /// </summary>
    [Fact]
    public void A_stale_voice_name_still_renders_with_the_daemons_default()
    {
        string root = Install(TenStyles, Array.Empty<string>());

        var replies = Speak(root, "language=en", "synthesis_voice=no-such-voice");

        // No --voice at all: the daemon picks, which is a working voice.
        Assert.Null(ValueOf(Argv(root), "--voice"));
        Assert.Contains("702 END", replies);
    }

    /// <summary>
    /// TRAP 10's "perfectly good outcome" has to be reachable as a deliberate
    /// choice: the neural voice for a document, espeak for everything else. That
    /// is only true if picking the echo row means it for a SPEAK too.
    /// </summary>
    [Fact]
    public void Choosing_the_echo_row_sends_a_sentence_to_espeak_rather_than_the_daemon()
    {
        string root = Install(TenStyles, Array.Empty<string>());

        Speak(root, "language=en", "synthesis_voice=vibesupertonic-echo");

        // The neural renderer was never started, so nothing recorded an argv.
        Assert.Empty(Argv(root));
    }

    /// <summary>
    /// A client that sets no voice at all gets the daemon's configured default,
    /// which is the setting the window writes and the hotkey uses.
    /// </summary>
    [Fact]
    public void No_voice_setting_leaves_the_choice_to_the_daemon()
    {
        string root = Install(TenStyles, Array.Empty<string>());

        Speak(root, "rate=0");

        Assert.Null(ValueOf(Argv(root), "--voice"));
        Assert.Null(ValueOf(Argv(root), "--language"));
    }

    /// <summary>
    /// CHAR and KEY are echo and go to espeak whatever voice is selected — the
    /// routing decision route B exists for. A neural render of one keystroke is
    /// the worst case for its fixed cost.
    /// </summary>
    [Theory]
    [InlineData("CHAR", "a")]
    [InlineData("KEY", "Control_L")]
    public void Echo_never_reaches_the_neural_renderer(string command, string body)
    {
        string root = Install(TenStyles, Array.Empty<string>());

        Run(root, "SET", "voice=female1", "language=de", ".", command, body, ".", "QUIT");

        Assert.Empty(Argv(root));
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// The module under input nobody wrote by hand: seeded random sessions, plus the
/// specific shapes a reviewer found wrong on 2026-10-04.
///
/// <para><b>Why a model rather than "it did not crash".</b> A module that answers
/// every command with a well-formed line can still answer them in the wrong
/// ORDER, and trap 17 is that speech-dispatcher then refuses to start at all. So
/// each random session is checked against the reply sequence it must produce,
/// computed from the session itself — not only for shape.</para>
///
/// <para><b>Deterministic.</b> Every seed is fixed, so a failure names a seed that
/// reproduces it, and the file stays well under the 15 s it is budgeted.</para>
/// </summary>
public class StressTests
{
    // ---------------------------------------------------------------- fixture

    /// <summary>
    /// An install whose two renderers record how they were called and then speak
    /// a canned WAV. Each records its argv one per line, its last argument, and —
    /// only when it was told to read it, as the real programs are — its stdin.
    ///
    /// <para><b>Reading stdin only on the flag is deliberate.</b> A fake that always
    /// read it would, under a module that never redirected it, block on the test
    /// runner's own stdin.</para>
    /// </summary>
    private static string Install(string ctlPrefix = "", bool working = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-speechd-stress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "espeak"));

        if (!working)
        {
            // Present for INIT, unrunnable for SPEAK: both voices fail before any
            // audio exists, so no audio loop ever reads ahead — which makes the
            // reply sequence a pure function of the input.
            File.WriteAllText(Path.Combine(root, "espeak", "espeak-ng"), "not really espeak");
            return root;
        }

        string wav = Path.Combine(root, "audio.wav");
        using (var f = File.Create(wav))
        {
            RenderWav.WriteHeader(f, 22050, 1);
            f.Write(new byte[] { 0x0A, 0x00, 0x7D, 0x00, 0x01, 0x02, 0x0A, 0x7D });
        }

        Script(Path.Combine(root, "vst-ctl"), Recorder("ctl", "--text-stdin", root) + ctlPrefix + $"cat '{wav}'\n");
        Script(Path.Combine(root, "espeak", "espeak-ng"), Recorder("espeak", "--stdin", root) + $"cat '{wav}'\n");
        return root;
    }

    private static string Recorder(string name, string stdinFlag, string root) =>
        $"printf '%s\\n' \"$@\" > '{root}/{name}-argv.txt'\n" +
        "last=''; for a in \"$@\"; do last=\"$a\"; done\n" +
        $"printf '%s' \"$last\" > '{root}/{name}-last.txt'\n" +
        $"rm -f '{root}/{name}-stdin.txt'\n" +
        $"for a in \"$@\"; do [ \"$a\" = '{stdinFlag}' ] && cat > '{root}/{name}-stdin.txt'; done\n";

    private static void Script(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string[] Argv(string root, string name)
    {
        string path = Path.Combine(root, name + "-argv.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <summary>The arguments a getopt-style parser would treat as options: those before a "--".</summary>
    private static string[] OptionsPart(string[] argv)
    {
        int end = Array.IndexOf(argv, "--");
        return end < 0 ? argv : argv[..end];
    }

    /// <summary>The text the renderer was given, by whichever channel it came.</summary>
    private static string? TextReaching(string root, string name)
    {
        string stdin = Path.Combine(root, name + "-stdin.txt");
        if (File.Exists(stdin)) return File.ReadAllText(stdin);
        string last = Path.Combine(root, name + "-last.txt");
        return File.Exists(last) ? File.ReadAllText(last) : null;
    }

    /// <summary>
    /// Run a session on its own thread and give up if it does not finish: a module
    /// that hangs is a screen reader that hangs, and an xunit test that hangs is a
    /// CI job that times out an hour later saying nothing useful.
    /// </summary>
    private static (string[] Lines, List<string> Log) RunBounded(string root, byte[] input, int seconds = 10)
    {
        var output = new MemoryStream();
        var log = new List<string>();

        var task = Task.Run(() =>
            new SpeechdModule(new MemoryStream(input), output, new Voices(root, root), log.Add, inputFd: -1).Run());

        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "the module did not finish: hung");
        Assert.Equal(0, task.Result);

        // Latin-1, because an audio block's payload is bytes and not UTF-8; its
        // escaping guarantees it holds no raw newline, so splitting is exact.
        return (Encoding.Latin1.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries), log);
    }

    private static (string[] Lines, List<string> Log) RunBounded(string root, params string[] lines) =>
        RunBounded(root, Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    /// <summary>The lines that END a reply — a code and a space — less the audio blocks.</summary>
    private static string[] Finals(string[] lines) =>
        lines.Where(l => l.Length >= 4 && l[3] == ' ' && l != "705 AUDIO").ToArray();

    private static readonly Regex WellFormed = new(@"^\d{3}[- ]", RegexOptions.Compiled);

    // ------------------------------------------------- STOP behind a deferral

    /// <summary>
    /// THE REVIEWER'S FIRST FINDING. A SET mid-utterance is put back for later,
    /// and the reader handed the put-back line out first on every read — so each
    /// later look at stdin found that same SET, put it back again and reported
    /// "no stop". A STOP behind it was never seen until the audio ran out, and its
    /// 703 arrived after a 702 END: a stop key that did nothing on any utterance
    /// during which the screen reader had touched a setting.
    /// </summary>
    [Fact]
    public void A_stop_behind_a_deferred_set_still_stops_the_utterance()
    {
        var (lines, _) = RunBounded(Install(), "SPEAK", "some words", ".", "SET", "rate=10", ".", "STOP");

        Assert.Equal(
            ["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "703 STOP",
             "203 OK RECEIVING SETTINGS", "203 OK SETTINGS RECEIVED"],
            Finals(lines));
    }

    [Fact]
    public void A_pause_behind_a_deferred_voice_list_still_pauses_the_utterance()
    {
        var (lines, _) = RunBounded(Install(), "CHAR", "a", ".", "LIST VOICES", "PAUSE");

        Assert.Equal(
            ["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "704 PAUSE",
             "200 OK VOICE LIST SENT"],
            Finals(lines));
    }

    /// <summary>
    /// Reading past a deferred command must not reorder what was deferred: the
    /// server sent a SET before a LIST, and answering them the other way round is
    /// trap 17's desynchronised stream.
    /// </summary>
    [Fact]
    public void Deferred_commands_are_answered_in_the_order_they_arrived()
    {
        var (lines, _) = RunBounded(Install(),
            "CHAR", "a", ".", "SET", "rate=1", ".", "LIST VOICES", "DEBUG ON /tmp", "LOGLEVEL", "log_level=3", ".", "STOP");

        Assert.Equal(
            ["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "703 STOP",
             "203 OK RECEIVING SETTINGS", "203 OK SETTINGS RECEIVED",
             "200 OK VOICE LIST SENT", "200 OK DEBUGGING SET",
             "207 OK RECEIVING LOGLEVEL SETTINGS", "203 OK LOGLEVEL SET"],
            Finals(lines));
    }

    /// <summary>
    /// A line reading STOP inside a deferred message is TEXT. Reading ahead to find
    /// a stop means reading other commands' blocks too, and a reader that did not
    /// know where a block ends would cancel an utterance because the next one
    /// contained the word.
    /// </summary>
    [Fact]
    public void A_line_saying_stop_inside_a_deferred_message_is_text_not_a_stop()
    {
        string root = Install();
        var (lines, _) = RunBounded(root, "CHAR", "a", ".", "CHAR", "STOP", ".", "SET", "PAUSE=1", ".");

        Assert.Equal(
            ["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "702 END",
             "202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "702 END",
             "203 OK RECEIVING SETTINGS", "203 OK SETTINGS RECEIVED"],
            Finals(lines));
        Assert.Equal("STOP", TextReaching(root, "espeak"));
    }

    /// <summary>
    /// The reviewer's lower-priority finding. The first read of the renderer's WAV
    /// header blocks for as long as a cold model load takes — seconds — and no
    /// look at stdin happened until it returned, so a STOP pressed during the load
    /// was obeyed only once audio was ready to play.
    /// </summary>
    [Fact]
    public void A_stop_during_a_slow_first_header_is_obeyed_without_waiting_for_it()
    {
        string root = Install(ctlPrefix: "sleep 4\n");

        var clock = Stopwatch.StartNew();
        var (lines, _) = RunBounded(root, "SPEAK", "a cold start", ".", "STOP");
        clock.Stop();

        Assert.Contains("703 STOP", lines);
        Assert.DoesNotContain("702 END", lines);
        Assert.True(clock.ElapsedMilliseconds < 2500,
            $"the stop waited for the renderer's header: {clock.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------ hostile text

    /// <summary>
    /// TEXT THAT LOOKS LIKE AN OPTION. vst-ctl dropped every argument beginning
    /// with "--" from its positionals and answered "--version" wherever it stood,
    /// so a line that was exactly "--version" printed a version where a WAV
    /// belonged, and "-- signed, Bob" lost its first word — each falling through
    /// to espeak with nothing anywhere saying why. The text must reach the voice
    /// whole, and never where a parser would read it as a flag.
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("--version")]
    [InlineData("--help")]
    [InlineData("-- signed, Bob")]
    [InlineData("--no-start")]
    [InlineData("-v en")]
    public void Text_that_looks_like_an_option_reaches_both_voices_as_text(string text)
    {
        string root = Install();
        RunBounded(root, "SPEAK", text, ".");

        // Past "render --out -", whose "-" is the destination and not the text.
        Assert.Equal(text, TextReaching(root, "ctl"));
        Assert.DoesNotContain(text, OptionsPart(Argv(root, "ctl")).Skip(3));

        RunBounded(root, "CHAR", text, ".");

        Assert.Equal(text, TextReaching(root, "espeak"));
        Assert.DoesNotContain(text, OptionsPart(Argv(root, "espeak")));
    }

    /// <summary>
    /// ONE ARGUMENT MAY NOT EXCEED 128 KiB ON LINUX (MAX_ARG_STRLEN), and both
    /// renderers were handed the whole message as one. Process.Start failed with
    /// E2BIG for each, so a long document — exactly what the neural voice is for —
    /// produced silence and a log line.
    /// </summary>
    [Theory]
    [InlineData("SPEAK", "ctl")]
    [InlineData("CHAR", "espeak")]
    public void A_message_over_128_KiB_is_spoken_rather_than_dropped(string command, string renderer)
    {
        string root = Install();
        string text = string.Concat(Enumerable.Repeat("All work and no play. ", 7000));   // ~154 KB
        Assert.True(Encoding.UTF8.GetByteCount(text) > 128 * 1024);

        var (lines, log) = RunBounded(root, command, text, ".");

        Assert.True(lines.Contains("705 AUDIO"), "no audio: " + string.Join(" | ", log));
        Assert.Contains("702 END", lines);
        Assert.Equal(text, TextReaching(root, renderer));
    }

    // ------------------------------------------------------------ the fuzzer

    private enum Kind { Init, Audio, LogLevel, Set, Speak, ListVoices, Debug, Stop, Pause, Quit, Junk }

    /// <summary>One command and its block, as bytes, plus what the model needs to know.</summary>
    private sealed record Command(Kind Kind, byte[] Bytes, bool BlankText);

    private static readonly string[] Words =
    [
        "hello", "STOP", "PAUSE", "QUIT", "SET", "LIST VOICES", "rate=5", "..lead", "..", "-", "--",
        "--version", "-x", "", " ", "été", "שלום", "a.b", "702 END", "\t",
    ];

    private static readonly string[] SpeakCommands = ["SPEAK", "CHAR", "KEY", "SOUND_ICON"];

    private static void Line(List<byte> into, Random r, string text, bool allowCr = true)
    {
        into.AddRange(Encoding.UTF8.GetBytes(text));
        if (allowCr && r.Next(5) == 0) into.Add((byte)'\r');
        into.Add((byte)'\n');
    }

    private static void Terminator(List<byte> into, Random r) =>
        into.AddRange(r.Next(4) == 0 ? ".\r\n"u8.ToArray() : ".\n"u8.ToArray());

    /// <summary>A body line: words, sometimes a byte sequence that is not UTF-8.</summary>
    private static (byte[] Bytes, string Decoded) BodyLine(Random r)
    {
        if (r.Next(8) == 0)
        {
            byte[] bad = [0xC3, 0x28, 0xFF, (byte)'z', 0x80];
            return (bad, Encoding.UTF8.GetString(bad));
        }

        string s = string.Join(' ', Enumerable.Range(0, r.Next(1, 4)).Select(_ => Words[r.Next(Words.Length)]));
        return (Encoding.UTF8.GetBytes(s), s);
    }

    /// <summary>
    /// A random session. <paramref name="truncate"/> ends the input in the middle
    /// of the last command's block, sometimes in the middle of its last line —
    /// which is speech-dispatcher dying mid-send, and still has to end cleanly.
    /// </summary>
    private static List<Command> Session(Random r, bool truncate)
    {
        var commands = new List<Command>();
        int n = r.Next(1, 25);

        for (int i = 0; i < n; i++)
        {
            bool last = i == n - 1;
            bool open = last && truncate;          // no terminator on this one
            var b = new List<byte>();
            Kind kind = (Kind)r.Next(Enum.GetValues<Kind>().Length);
            bool blank = false;

            switch (kind)
            {
                case Kind.Init: Line(b, r, "INIT"); break;
                case Kind.Stop: Line(b, r, "STOP"); break;
                case Kind.Pause: Line(b, r, "PAUSE"); break;
                case Kind.Quit: Line(b, r, "QUIT"); break;
                case Kind.ListVoices: Line(b, r, "LIST VOICES"); break;
                case Kind.Debug: Line(b, r, "DEBUG ON /tmp/vst-" + r.Next(100)); break;

                case Kind.Junk:
                    if (r.Next(3) == 0) { b.AddRange(new byte[] { 0xFF, 0xFE, (byte)'X', (byte)'\n' }); }
                    else Line(b, r, r.Next(4) == 0 ? "" : "XNOPE" + r.Next(1000));
                    break;

                case Kind.Audio:
                case Kind.LogLevel:
                case Kind.Set:
                    Line(b, r, kind switch { Kind.Audio => "AUDIO", Kind.LogLevel => "LOGLEVEL", _ => "SET" });
                    string[] keys = ["rate", "volume", "language", "synthesis_voice", "voice", "pitch", "audio_output_method", "log_level"];
                    for (int k = r.Next(0, 6); k > 0; k--)
                    {
                        string p = r.Next(6) switch
                        {
                            0 => "malformed-no-equals",
                            1 => "=novalue",
                            2 => keys[r.Next(keys.Length)] + "=" + Words[r.Next(Words.Length)],
                            _ => keys[r.Next(keys.Length)] + "=" + r.Next(-150, 150),
                        };
                        Line(b, r, p);
                    }
                    if (!open) Terminator(b, r);
                    break;

                case Kind.Speak:
                    Line(b, r, SpeakCommands[r.Next(SpeakCommands.Length)]);
                    var decoded = new List<string>();
                    for (int k = r.Next(0, 5); k > 0; k--)
                    {
                        var (bytes, text) = BodyLine(r);
                        b.AddRange(bytes);
                        if (r.Next(5) == 0) b.Add((byte)'\r');
                        b.Add((byte)'\n');
                        decoded.Add(text.StartsWith("..", StringComparison.Ordinal) ? text[1..] : text);
                    }
                    blank = string.IsNullOrWhiteSpace(string.Join('\n', decoded));
                    if (!open) Terminator(b, r);
                    break;
            }

            // The very last byte of the input missing its newline: a final line
            // with no terminator is still a line. Not done to an EMPTY last line,
            // which would then be no line at all — and no command to answer.
            if (open && r.Next(2) == 0 && b.Count > 0 && b[^1] == (byte)'\n')
            {
                int cut = b.Count - 1;
                if (cut > 0 && b[cut - 1] == (byte)'\r') cut--;
                if (cut > 0 && b[cut - 1] != (byte)'\n') b.RemoveRange(cut, b.Count - cut);
            }

            commands.Add(new Command(kind, b.ToArray(), blank));
            if (kind == Kind.Quit) break;
        }

        return commands;
    }

    /// <summary>
    /// The reply sequence a session must produce. With <paramref name="working"/>
    /// voices, every non-blank utterance reaches its audio loop, and that loop
    /// reads ahead — in memory, everything is already there — until it meets a
    /// STOP, a PAUSE or a QUIT: the first two end the utterance and are consumed,
    /// QUIT ends it and is answered later, and everything in between is answered
    /// after the utterance, in order. Without working voices nothing reads ahead,
    /// so each command is answered strictly in turn.
    /// </summary>
    private static List<string> Expected(List<Command> session, bool working)
    {
        var replies = new List<string>();
        var consumed = new HashSet<int>();
        int readUpTo = 0;           // commands at or beyond this have not been read

        for (int i = 0; i < session.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            readUpTo = Math.Max(readUpTo, i + 1);

            switch (session[i].Kind)
            {
                case Kind.Init: replies.Add("299 OK LOADED SUCCESSFULLY"); break;
                case Kind.Audio: replies.AddRange(["207 OK RECEIVING AUDIO SETTINGS", "203 OK AUDIO INITIALIZED"]); break;
                case Kind.LogLevel: replies.AddRange(["207 OK RECEIVING LOGLEVEL SETTINGS", "203 OK LOGLEVEL SET"]); break;
                case Kind.Set: replies.AddRange(["203 OK RECEIVING SETTINGS", "203 OK SETTINGS RECEIVED"]); break;
                case Kind.ListVoices: replies.Add("200 OK VOICE LIST SENT"); break;
                case Kind.Debug: replies.Add("200 OK DEBUGGING SET"); break;
                case Kind.Stop: replies.Add("703 STOP"); break;
                case Kind.Pause: replies.Add("704 PAUSE"); break;
                case Kind.Junk: replies.Add("300 ERROR UNKNOWN COMMAND"); break;
                case Kind.Quit: replies.Add("210 OK QUIT"); return replies;

                case Kind.Speak:
                    replies.AddRange(["202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN"]);
                    string end = "702 END";

                    if (working && !session[i].BlankText)
                    {
                        for (int j = readUpTo; j < session.Count; j++)
                        {
                            readUpTo = j + 1;
                            Kind k = session[j].Kind;
                            if (k is Kind.Stop or Kind.Pause) { consumed.Add(j); end = k == Kind.Stop ? "703 STOP" : "704 PAUSE"; break; }
                            if (k is Kind.Quit) { end = "703 STOP"; break; }
                        }
                    }

                    replies.Add(end);
                    break;
            }
        }

        return replies;
    }

    private static void Fuzz(bool working, int sessions, int seedBase)
    {
        string root = Install(working: working);

        for (int s = 0; s < sessions; s++)
        {
            int seed = seedBase + s;
            var r = new Random(seed);
            var session = Session(r, truncate: r.Next(3) == 0);
            byte[] input = session.SelectMany(c => c.Bytes).ToArray();

            var (lines, _) = RunBounded(root, input);

            foreach (string line in lines)
                Assert.True(WellFormed.IsMatch(line), $"seed {seed}: malformed reply line '{line}'");

            Assert.True(
                Expected(session, working).SequenceEqual(Finals(lines)),
                $"seed {seed}:\n  sent     {Encoding.UTF8.GetString(input).Replace("\n", "\\n").Replace("\r", "\\r")}\n" +
                $"  expected {string.Join(" | ", Expected(session, working))}\n" +
                $"  actual   {string.Join(" | ", Finals(lines))}");
        }
    }

    /// <summary>Every command answered, in order, when no audio loop is involved.</summary>
    [Fact]
    public void Random_sessions_are_answered_in_order_when_no_voice_can_speak() =>
        Fuzz(working: false, sessions: 400, seedBase: 1000);

    /// <summary>
    /// The same with voices that speak, so the audio loop reads ahead: deferred
    /// commands keep their order, and a STOP or PAUSE anywhere behind them still
    /// ends the utterance it arrived during.
    /// </summary>
    [Fact]
    public void Random_sessions_are_answered_in_order_while_audio_reads_ahead() =>
        Fuzz(working: true, sessions: 120, seedBase: 5000);
}

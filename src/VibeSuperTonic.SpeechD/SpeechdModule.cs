using System.Diagnostics;
using System.Text;
using VibeSuperTonic.Core.SpeechD;

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// The Speech Dispatcher output module: a state machine over stdin and stdout.
///
/// <para><b>Single-threaded, deliberately, the way upstream's own modules are.</b>
/// The audio loop writes a block and then reads whatever has arrived on stdin
/// before writing the next one, which is how a <c>STOP</c> interrupts an
/// utterance already in flight. A worker thread producing audio while the main
/// thread answered commands would need a lock around stdout, and every reply
/// would race the block being written beside it — for no gain, because the thing
/// that has to be responsive is the *stop*, and the stop is already checked
/// between blocks.</para>
///
/// <para><b>The one rule underneath all of it: never go silent.</b> A screen
/// reader that says nothing is, to someone navigating by ear, indistinguishable
/// from a machine that has died. So every failure of the neural voice falls back
/// to the espeak this archive ships — docs/SPEECHD-PLAN.md, trap 16.</para>
/// </summary>
internal sealed class SpeechdModule
{
    private readonly Stream _out;
    private readonly LineReader _in;
    private readonly Voices _voices;
    private readonly Action<string> _log;

    /// <summary>Set by a STOP that arrives while audio is being written.</summary>
    private bool _stopRequested;

    // speech-dispatcher's SET parameters that this module acts on. Everything
    // else is accepted and ignored — see HandleSet.
    private int _rate;
    private string? _language;
    private string? _synthesisVoice;

    /// <param name="inputFd">
    /// The descriptor <paramref name="input"/> reads from, so readiness is asked
    /// about the right file. Negative for a stream that cannot block, which is
    /// how the tests drive this.
    /// </param>
    internal SpeechdModule(
        Stream input, Stream output, Voices voices, Action<string> log,
        int inputFd = LineReader.StdinFd)
    {
        _in = new LineReader(input, inputFd);
        _out = output;
        _voices = voices;
        _log = log;
    }

    // ------------------------------------------------------------------ loop

    internal int Run()
    {
        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null) return 0;              // speech-dispatcher closed stdin

            switch (line)
            {
                case "INIT": HandleInit(); break;
                case "AUDIO": HandleAudio(); break;
                case "SET": HandleSet(); break;
                case "LOGLEVEL": HandleLogLevel(); break;
                case "STOP": Reply("703 STOP"); break;
                case "PAUSE": Reply("704 PAUSE"); break;
                case "QUIT": Reply("210 OK QUIT"); return 0;

                default:
                    if (ModuleRouting.Parse(line) is { } type) HandleSpeak(type);
                    else if (line.StartsWith("LIST VOICES", StringComparison.Ordinal)) HandleListVoices();
                    else if (line.StartsWith("DEBUG", StringComparison.Ordinal)) Reply("200 OK DEBUGGING SET");
                    else Reply("300 ERROR UNKNOWN COMMAND");
                    break;
            }
        }
    }

    // -------------------------------------------------------------- commands

    /// <summary>
    /// <b>INIT is where a broken install has to announce itself</b>, because it
    /// is the only reply speech-dispatcher acts on: a module that fails here is
    /// dropped and simply never appears in <c>spd-say -O</c>. Trap 14.
    ///
    /// <para>It refuses only when there is <em>no</em> voice at all. A missing
    /// daemon or an undownloaded model is not a refusal — espeak still answers,
    /// and a module that declined to load because the neural voice was not ready
    /// would take the working voice down with it.</para>
    /// </summary>
    private void HandleInit()
    {
        if (!_voices.EspeakPresent)
        {
            // No fast voice means no fallback, and no fallback means every
            // failure below becomes silence. Better to be absent than to be a
            // module that sometimes says nothing.
            Reply("399-no espeak-ng beside this module at " + _voices.EspeakPath);
            Reply("399 ERR CANT INIT MODULE");
            return;
        }

        Reply("299-VibeSuperTonic: neural voice for reading, espeak for keystroke echo");
        Reply("299 OK LOADED SUCCESSFULLY");
    }

    /// <summary>
    /// Server-side audio, which is the whole of route B: we describe the samples
    /// and hand them back, and speech-dispatcher plays them. This module opens no
    /// audio device on any distro — the gate proved 0.11.1 supports it too.
    /// </summary>
    private void HandleAudio()
    {
        Reply("207 OK RECEIVING AUDIO SETTINGS");

        foreach (var (key, value) in ReadParameters())
        {
            if (key == "audio_output_method" && value != "server")
            {
                // Only reachable on a server that does not offer server-side
                // audio. Nothing good happens next, and saying so in the log is
                // the only way anyone finds out why the module went quiet.
                _log($"the server asked for audio_output_method={value}; this module only does server");
            }
        }

        Reply("203 OK AUDIO INITIALIZED");
    }

    private void HandleSet()
    {
        Reply("203 OK RECEIVING SETTINGS");

        foreach (var (key, value) in ReadParameters())
        {
            switch (key)
            {
                case "rate": _rate = ParseInt(value, 0); break;
                case "language": _language = Normalise(value); break;
                case "synthesis_voice": _synthesisVoice = Normalise(value); break;

                // ACCEPTED AND IGNORED, WHICH IS A DECISION AND NOT AN OMISSION.
                // punctuation_mode, spelling_mode and cap_let_recogn all mean
                // "say something extra out loud", and this product has no concept
                // of it; pitch and volume have no lever on either voice. Answering
                // 303 to a parameter every client sends would make the module look
                // broken to a user who is not using the feature at all.
                // docs/SPEECHD-PLAN.md, trap 11.
                default: break;
            }
        }

        Reply("203 OK SETTINGS RECEIVED");
    }

    private void HandleLogLevel()
    {
        Reply("207 OK RECEIVING LOGLEVEL SETTINGS");
        foreach (var _ in ReadParameters()) { }
        Reply("203 OK LOGLEVEL SET");
    }

    /// <summary>
    /// The voice list. S3 replaces this with the installed set; until then it
    /// names the two voices that always exist, which is honest rather than empty
    /// — a module that lists nothing is one a user cannot select. Trap 8's
    /// mapping onto speechd's vocabulary lives here.
    /// </summary>
    private void HandleListVoices()
    {
        Reply("200-VibeSuperTonic\ten\tMALE1");
        Reply("200-VibeSuperTonic-echo\ten\tMALE2");
        Reply("200 OK VOICE LIST SENT");
    }

    // ----------------------------------------------------------------- speak

    private void HandleSpeak(SpeechdMessageType type)
    {
        Reply("202 OK RECEIVING MESSAGE");
        string text = ReadMessage();

        _stopRequested = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            // An empty label is a real thing that happens dozens of times a
            // session. It is an utterance that produces no audio, not an error —
            // but the begin/end pair still has to arrive or the server waits for
            // an utterance that never ends.
            Reply("200 OK SPEAKING");
            Reply("701 BEGIN");
            Reply("702 END");
            return;
        }

        Reply("200 OK SPEAKING");
        Reply("701 BEGIN");

        var chose = ModuleRouting.For(type);
        bool spoke = chose == RenderVoice.Neural
            ? SpeakWith(() => _voices.StartNeural(text, _synthesisVoice), "neural")
            : false;

        // TRAP 16. Anything that went wrong with the neural voice — no daemon, a
        // model still loading, no voice installed, a refusal because the hotkey
        // is speaking — lands here, and here is the espeak that cannot fail. The
        // user hears a flat voice rather than nothing, and "my screen reader
        // sounds different" is a different kind of day from "my screen reader
        // stopped".
        if (!spoke && !_stopRequested)
            spoke = SpeakWith(() => _voices.StartEspeak(text, _language, _rate), "espeak");

        if (_stopRequested) Reply("703 STOP");
        else if (spoke) Reply("702 END");
        else
        {
            // Both voices failed, which means the install is broken rather than
            // busy. Say so where speechd logs it, and still end the utterance:
            // an unterminated one wedges the server's queue for every module.
            _log("both voices failed; the utterance produced no audio");
            Reply("702 END");
        }
    }

    /// <summary>
    /// Run one renderer and forward its WAV as audio blocks. Returns false if it
    /// produced no audio, which is the caller's cue to fall back.
    /// </summary>
    private bool SpeakWith(Func<Process> start, string what)
    {
        Process process;
        try
        {
            process = start();
        }
        catch (Exception ex)
        {
            _log($"{what}: could not start — {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        try
        {
            var stdout = process.StandardOutput.BaseStream;

            if (WavHeader.Read(stdout) is not { } wav)
            {
                // Not a WAV. Either the renderer refused — vst-ctl exits non-zero
                // with a reason on stderr, which is exactly what "never exit 0
                // with no audio" bought us — or it is not the program we think.
                _log($"{what}: no audio ({Drain(process)})");
                return false;
            }

            long sent = SendAudio(stdout, wav);

            // NEVER REPORT SUCCESS HAVING PRODUCED NO AUDIO. A renderer can emit
            // a header and then nothing at all, and that is the failure this
            // whole feature keeps finding.
            if (sent == 0 && !_stopRequested)
            {
                _log($"{what}: a header and no samples ({Drain(process)})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log($"{what}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            StopProcess(process);
        }
    }

    /// <summary>
    /// The audio loop, and its shape is the reason a stop works: one block, then
    /// a look at stdin, then the next block.
    /// </summary>
    private long SendAudio(Stream pcmSource, WavHeader wav)
    {
        var buffer = new byte[AudioBlock.MaxChunkBytes];
        long total = 0;
        int held = 0;

        while (true)
        {
            if (DrainCommandsAndCheckStop()) return total;

            int n = pcmSource.Read(buffer, held, buffer.Length - held);
            if (n <= 0) break;

            held += n;

            // A block must contain whole frames, so a partial one at the end of a
            // read is carried into the next. Splitting a 16-bit sample across two
            // blocks would put a click in the audio at every chunk boundary.
            int whole = held - held % wav.FrameBytes;
            if (whole == 0) continue;

            AudioBlock.Write(_out, buffer.AsSpan(0, whole), wav.SampleRate, wav.Channels, wav.Bits);
            total += whole;

            held -= whole;
            if (held > 0) Array.Copy(buffer, whole, buffer, 0, held);
        }

        if (held >= wav.FrameBytes)
        {
            int whole = held - held % wav.FrameBytes;
            AudioBlock.Write(_out, buffer.AsSpan(0, whole), wav.SampleRate, wav.Channels, wav.Bits);
            total += whole;
        }

        return total;
    }

    /// <summary>
    /// Read anything speech-dispatcher has sent without waiting for it, and
    /// report whether it told us to stop.
    ///
    /// <para>A command that is not a stop is put back: the server does not send
    /// a second SPEAK while one is in flight, so anything else here is a SET or a
    /// LIST for after this utterance, and losing it would be worse than
    /// deferring it.</para>
    /// </summary>
    private bool DrainCommandsAndCheckStop()
    {
        while (_in.ReadLine(block: false) is { } line)
        {
            switch (line)
            {
                case "STOP":
                    _stopRequested = true;
                    return true;

                case "QUIT":
                    _stopRequested = true;
                    _in.PushBack("QUIT");
                    return true;

                default:
                    _in.PushBack(line);
                    return false;
            }
        }

        return false;
    }

    // ----------------------------------------------------------------- input

    /// <summary>Key/value lines until a lone dot.</summary>
    private IEnumerable<(string Key, string Value)> ReadParameters()
    {
        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null || line == ".") yield break;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            yield return (line[..eq], line[(eq + 1)..]);
        }
    }

    /// <summary>
    /// The message body: lines until a lone dot, with a leading dot unescaped.
    /// <c>..</c> at the start of a line is how SSIP carries a line that really
    /// begins with a dot, and a module that skipped this would silently drop a
    /// character from any text that did.
    /// </summary>
    private string ReadMessage()
    {
        var sb = new StringBuilder();

        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null || line == ".") break;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- output

    private void Reply(string line)
    {
        _out.Write(Encoding.UTF8.GetBytes(line + "\n"));
        _out.Flush();
    }

    // ----------------------------------------------------------------- utils

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : fallback;

    /// <summary>
    /// speech-dispatcher sends "NULL" for an unset string, and a literal voice
    /// named NULL is not what anyone meant.
    /// </summary>
    private static string? Normalise(string value) =>
        string.IsNullOrWhiteSpace(value) || value == "NULL" ? null : value;

    /// <summary>The renderer's own explanation, for the log.</summary>
    private static string Drain(Process process)
    {
        try
        {
            string err = process.StandardError.ReadToEnd().Trim();
            return string.IsNullOrEmpty(err) ? "no message" : err.Split('\n')[0];
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    /// <summary>
    /// SIGTERM, then make sure it is gone. vst-ctl handles SIGTERM as a stop and
    /// exits 0 (S1); espeak-ng dies on it. Either way the point is that a
    /// cancelled utterance leaves no process still synthesising it, because Orca
    /// cancels constantly and they would otherwise accumulate for the session.
    /// </summary>
    private void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception) { /* it exited between the check and the kill */ }
        finally { process.Dispose(); }
    }
}

using System.Diagnostics;
using System.Text;
using VibeSuperTonic.Core.SpeechD;
using VibeSuperTonic.Core.Text;

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
    private readonly InstalledVoices _installed;
    private readonly Action<string> _log;

    /// <summary>Set by a STOP that arrives while audio is being written.</summary>
    private bool _stopRequested;

    /// <summary>
    /// Set by a PAUSE that arrives while audio is being written. Like a stop it
    /// ends the audio loop, but the utterance is reported as paused and not as
    /// ended or stopped — see <see cref="HandleSpeak"/>.
    /// </summary>
    private bool _pauseRequested;

    /// <summary>Either interruption: the audio loop's one question.</summary>
    private bool Interrupted => _stopRequested || _pauseRequested;

    // speech-dispatcher's SET parameters that this module acts on. Everything
    // else is accepted and ignored — see HandleSet.
    private int _rate;

    /// <summary>
    /// speechd's −100..100, applied as a gain by the daemon and as amplitude by
    /// the espeak fallback. Pitch has no counterpart here — see HandleSet.
    /// </summary>
    private int _volume;

    // ---- one utterance's bookkeeping, reset by HandleSpeak ----

    /// <summary>Samples handed to the server so far, silence included.</summary>
    private long _samplesSent;

    /// <summary>Sample rate of the audio last sent, for writing silence at it.</summary>
    private int _streamRate = FallbackRate;

    /// <summary>Marks the renderer reported that the audio has not yet reached.</summary>
    private readonly List<VibeSuperTonic.Core.Ipc.RenderMark> _dueMarks = new();

    /// <summary>
    /// How often stdin is looked at while a renderer has not yet sent its header.
    /// About a third of an audio block, so a STOP during a model load is obeyed
    /// no later than one during audio would be.
    /// </summary>
    private const int HeaderPollMs = 30;

    /// <summary>espeak-ng's own output rate, for silence written before any audio exists.</summary>
    private const int FallbackRate = 22050;

    /// <summary>
    /// The longest silence one utterance may carry, summed over its breaks. The
    /// daemon bounds the neural path's the same way; this is the fallback's.
    /// </summary>
    private const int MaxSilenceMs = 60_000;
    private string? _language;
    private string? _synthesisVoice;

    /// <summary>speechd's <c>cap_let_recogn</c>, as espeak-ng's <c>-k</c>; 0 is off.</summary>
    private int _capitals;

    /// <summary>
    /// espeak-ng voices this install has been seen to speak, and ones it has been
    /// seen to lack — item 1 of docs/CODE-REVIEW-2026-11-09.md. The payload ships
    /// about 31 dictionaries; for any other language espeak-ng prints
    /// <c>Can't read dictionary file</c>, exits 0 and writes a WAV of silence.
    /// </summary>
    private readonly HashSet<string> _espeakSpeaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _espeakLacks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// speechd's <c>voice=male1</c> and friends. Read because it is the DEFAULT
    /// selection path rather than an exotic one: measured against 0.12.1, a
    /// client that has not picked a specific voice sends this on every SET with
    /// <c>synthesis_voice=NULL</c> beside it. S3.
    /// </summary>
    private string? _symbolicVoice;

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
        _installed = new InstalledVoices(voices.ModelsRoot);
    }

    // ------------------------------------------------------------------ loop

    internal int Run()
    {
        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null) return 0;              // speech-dispatcher closed stdin

            _owed.Clear();
            _blockOpen = false;

            // ONE COMMAND'S FAILURE IS NOT THE MODULE'S (item 56, 2026-11-09).
            // An exception escaping here used to end the process mid-utterance,
            // and speechd then has no module for this voice until the next login.
            // So it is logged, whatever the command still owed the server is sent
            // (for an utterance, its END), and the loop goes on. Only a failure to
            // write that reply escapes: then the server is gone and so is the point.
            try
            {
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
            catch (Exception ex)
            {
                try { _log($"{line}: {ex.GetType().Name}: {ex.Message}; answering what it owed and carrying on"); }
                catch (Exception) { /* the log is what failed; the reply still matters */ }

                // The rest of a parameter block would otherwise be read as commands.
                if (_blockOpen)
                    while (_in.ReadLine(block: true) is { } rest && rest != ".") { }
                _blockOpen = false;

                foreach (string owed in _owed.ToArray()) Reply(owed);
            }
        }
    }

    /// <summary>
    /// The replies the command in hand still owes the server, in order, for
    /// <see cref="Run"/>'s catch to send if it fails. Each handler states its debt
    /// with <see cref="Owe"/>; <see cref="Reply"/> pays it down.
    /// </summary>
    private readonly List<string> _owed = new();

    /// <summary>True while a parameter block's lines are still unread.</summary>
    private bool _blockOpen;

    private void Owe(params string[] lines)
    {
        _owed.Clear();
        _owed.AddRange(lines);
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
        // INIT owes a reply like every other command (item 107): one that goes
        // unanswered is a module speechd drops, and a refusal at least says why
        // in speechd's log, where Run's catch writes the exception.
        Owe("399 ERR CANT INIT MODULE");

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
        Owe("207 OK RECEIVING AUDIO SETTINGS", "203 OK AUDIO INITIALIZED");
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
        Owe("203 OK RECEIVING SETTINGS", "203 OK SETTINGS RECEIVED");
        Reply("203 OK RECEIVING SETTINGS");

        foreach (var (key, value) in ReadParameters())
        {
            switch (key)
            {
                case "rate": _rate = ParseInt(value, 0); break;
                case "volume": _volume = Math.Clamp(ParseInt(value, 0), -100, 100); break;
                case "language": _language = Normalise(value); break;
                case "synthesis_voice": _synthesisVoice = Normalise(value); break;
                case "voice": _symbolicVoice = Normalise(value); break;

                // Read for key echo only: a CHAR that is a capital letter is
                // announced as one when the client asks (item 2, 2026-11-09).
                case "cap_let_recogn": _capitals = CharEcho.Capitals(value); break;

                // ACCEPTED AND IGNORED, WHICH IS A DECISION AND NOT AN OMISSION.
                // punctuation_mode and spelling_mode mean "say something extra
                // out loud" while reading, and this product has no concept of it.
                // (A CHAR always says its punctuation, as speechd's own espeak
                // module does.) Answering 303 to a parameter every client sends
                // would make the module look broken to a user who is not using the
                // feature at all. docs/SPEECHD-PLAN.md, trap 11.
                //
                // PITCH AND PITCH_RANGE ARE IGNORED FOR A DIFFERENT REASON, and it
                // is measured rather than assumed: Supertonic has no pitch input
                // (its style vector is the only voice control), and the one lever
                // there is — resampling — moves pitch and speed together, which is
                // trap 5's "do not fake it". The espeak fallback does have -p, but
                // applying pitch to the voice a user is NOT listening to would make
                // a fallback change the timbre by more than it should.
                //
                // ssml_mode is not read either, because SSML is detected from the
                // text: trap 11 measured a plain `spd-say "hello"` arriving as
                // <speak>hello</speak> with the mode off.
                default: break;
            }
        }

        Reply("203 OK SETTINGS RECEIVED");
    }

    private void HandleLogLevel()
    {
        Owe("207 OK RECEIVING LOGLEVEL SETTINGS", "203 OK LOGLEVEL SET");
        Reply("207 OK RECEIVING LOGLEVEL SETTINGS");
        foreach (var _ in ReadParameters()) { }
        Reply("203 OK LOGLEVEL SET");
    }

    /// <summary>
    /// The voice list, read from the store every time it is asked for — S3.
    ///
    /// <para><b>Re-read per request rather than cached</b>, which is the whole
    /// advantage route B has here. A user who downloads a voice in the Voices tab
    /// expects it in their screen reader's picker, and this module is a
    /// long-lived process started at login: anything cached would go stale the
    /// moment they installed one and stay stale until they logged out. A client
    /// asks for the list when a human opens a voice picker, so the cost is a
    /// directory listing at human speed.</para>
    ///
    /// <para><b>The espeak row is always last and always present.</b> It is the
    /// voice that cannot fail (trap 16), it needs no model downloaded, and on a
    /// fresh install where nothing else exists it is the only thing standing
    /// between this module and trap 14's empty list — which a user cannot select
    /// from and which makes a working install look broken.</para>
    /// </summary>
    private void HandleListVoices()
    {
        Owe("200 OK VOICE LIST SENT");
        foreach (var v in _installed.Read())
            Reply($"200-{v.Name}\t{v.Language}\t{v.Variant}");

        Reply($"200-{EchoVoiceName}\ten\techo");
        Reply("200 OK VOICE LIST SENT");
    }

    /// <summary>
    /// The bundled espeak, named so a user can choose it deliberately. Selecting
    /// it is the configuration [trap 10] says is a perfectly good outcome: the
    /// neural voice for reading a document, the fast one for keystroke echo.
    /// </summary>
    private const string EchoVoiceName = "vibesupertonic-echo";

    // ----------------------------------------------------------------- speak

    private void HandleSpeak(SpeechdMessageType type)
    {
        // An utterance, once begun, must end — even through Run's catch.
        Owe("202 OK RECEIVING MESSAGE", "200 OK SPEAKING", "701 BEGIN", "702 END");
        Reply("202 OK RECEIVING MESSAGE");
        string text = ReadMessage();

        _stopRequested = false;
        _pauseRequested = false;
        _samplesSent = 0;
        _streamRate = FallbackRate;
        _dueMarks.Clear();

        // SSML, PARSED ONCE HERE FOR THE TWO THINGS ONLY THIS PROCESS CAN DO: tell
        // an utterance with nothing to say from one with something, and drive the
        // espeak fallback fragment by fragment. The neural voice is handed the
        // text as it came and the daemon parses it again — one parser (Core's),
        // two callers, so they cannot disagree about what a document means. A
        // document that does not parse is null here and is STRIPPED, which is
        // what this module did for every document before 2026-10-03.
        //
        // KEY ECHO GETS ITS OWN SWITCHES (item 2). A CHAR is said with --punct
        // and, when the client asked, espeak's capital announcement; a space,
        // which as text is nothing at all, is named instead — see CharEcho. That
        // SSML is the module's own and is not parsed as the client's. A KEY is
        // a key name, handed to espeak as tts:key so that it is named in the
        // voice's language rather than read as an English word (item 104).
        var echo = type == SpeechdMessageType.Char
            ? new EspeakEcho(Punctuation: true, Capitals: _capitals, Ssml: false)
            : default;
        IReadOnlyList<SsmlFragment>? fragments;
        string plain;
        if (type == SpeechdMessageType.Char && CharEcho.WhitespaceSsml(text) is { } named)
        {
            fragments = null;
            plain = named;
            echo = echo with { Ssml = true };
        }
        else if (type == SpeechdMessageType.Key && CharEcho.KeySsml(text) is { } key)
        {
            fragments = null;
            plain = key;
            echo = new EspeakEcho(Punctuation: false, Capitals: 0, Ssml: true);
        }
        else
        {
            fragments = SsmlDocument.TryParse(text, out var parsed) ? parsed : null;
            plain = fragments is null ? Ssml.Strip(text) : SsmlDocument.SpokenText(fragments);
        }

        if (string.IsNullOrWhiteSpace(plain))
        {
            // An empty label is a real thing that happens dozens of times a
            // session. It is an utterance that produces no audio, not an error —
            // but the begin/end pair still has to arrive or the server waits for
            // an utterance that never ends. Any bookmarks it carried still fire:
            // a client waiting on `<speak><mark name="done"/></speak>` is waiting
            // for exactly that event.
            Reply("200 OK SPEAKING");
            Reply("701 BEGIN");
            if (fragments is not null)
                foreach (var f in fragments)
                    if (f.Kind == SsmlFragmentKind.Mark) ReportMark(f.Mark!);
            Reply("702 END");
            return;
        }

        Reply("200 OK SPEAKING");
        Reply("701 BEGIN");

        // THE USER CAN CHOOSE THE FAST VOICE DELIBERATELY, and choosing it has to
        // beat the message type. Trap 10 calls "excellent for a document, espeak
        // for echo" a perfectly good outcome for this release rather than a
        // failure; that is only true if picking the echo row in a voice list
        // actually means it, including for a SPEAK.
        bool echoChosen = string.Equals(
            _synthesisVoice, EchoVoiceName, StringComparison.OrdinalIgnoreCase);

        var chose = echoChosen ? RenderVoice.Espeak : ModuleRouting.For(type);

        bool wantsMarks = fragments?.Any(f => f.Kind == SsmlFragmentKind.Mark) == true;

        bool spoke = false;
        if (chose == RenderVoice.Neural)
        {
            // Resolved per utterance, off the same live read the voice list
            // answers from, because a client sets a voice once at connect and a
            // voice can be installed at any point in a session that lasts as long
            // as the login does.
            var pick = VoiceList.Resolve(
                _installed.Read(), _synthesisVoice, _symbolicVoice, _language);

            if (pick is null && !string.IsNullOrWhiteSpace(_synthesisVoice))
            {
                // speechd does not check synthesis_voice against the list it was
                // given — measured — so a stale setting in a screen reader
                // arrives here verbatim. Speaking with the default is the right
                // answer and the log line is the only way anyone finds out why
                // they are not hearing the voice they chose.
                _log($"no installed voice matches '{_synthesisVoice}'; using the daemon's default");
            }

            spoke = SpeakWith(
                () => _voices.StartNeural(
                    text, pick?.RenderVoice, pick?.RenderLanguage, _rate, _volume, wantsMarks),
                "neural");
        }

        // TRAP 16. Anything that went wrong with the neural voice — no daemon, a
        // model still loading, no voice installed, a refusal because the hotkey
        // is speaking — lands here, and here is the espeak that cannot fail. The
        // user hears a flat voice rather than nothing, and "my screen reader
        // sounds different" is a different kind of day from "my screen reader
        // stopped".
        if (!spoke && !Interrupted)
            spoke = SpeakEspeak(fragments, plain, echo);

        if (_stopRequested) Reply("703 STOP");

        // A PAUSE ENDS THE UTTERANCE WITHOUT AN END. speechd resumes by sending
        // the remainder as a new SPEAK, starting from the last index mark it saw
        // — which is why the marks above matter for more than highlighting — and
        // an END here would tell it the whole message had been spoken.
        else if (_pauseRequested) Reply("704 PAUSE");
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
    /// The fast voice for a whole utterance. A plain message is one espeak run; an
    /// SSML document is one run per text fragment, so that each fragment gets its
    /// own language and pace, a <c>&lt;break&gt;</c> becomes silence and a
    /// <c>&lt;mark&gt;</c> is reported where the audio before it ends.
    /// </summary>
    private bool SpeakEspeak(IReadOnlyList<SsmlFragment>? fragments, string plain, EspeakEcho echo)
    {
        // Not a document, or one that did not parse: the stripped text, in one
        // run. espeak-ng would otherwise be handed "<speak>" and read it.
        if (fragments is null)
            return RunEspeak(plain, _language, 0, echo);

        bool any = false;
        int silenceLeft = MaxSilenceMs;

        foreach (var fragment in fragments)
        {
            if (Interrupted) break;

            switch (fragment.Kind)
            {
                case SsmlFragmentKind.Mark:
                    ReportMark(fragment.Mark!);
                    break;

                case SsmlFragmentKind.Silence:
                    int ms = Math.Min(fragment.SilenceMs, silenceLeft);
                    silenceLeft -= ms;
                    if (ms > 0) any |= SendSilence(ms);
                    break;

                default:
                    any |= RunEspeak(fragment.Text, fragment.Lang ?? _language, fragment.RateAdj, echo);
                    break;
            }
        }

        return any;
    }

    /// <summary>
    /// One espeak-ng run, in <paramref name="language"/> when this install can
    /// speak it and in espeak's default voice when it cannot.
    ///
    /// <para><b>The fallback has a fallback (item 1, 2026-11-09).</b> With
    /// <c>-v he</c> the shipped espeak-ng prints <c>Can't read dictionary
    /// file</c>, exits 0 and writes a WAV of silence, which looked like success:
    /// a Hebrew user heard nothing for every key echo and for every sentence while
    /// the daemon was down, and nothing was logged. So the first run in a language
    /// holds back its leading silence, and if the run ends with nothing but
    /// silence <em>and</em> espeak-ng said it could not read a dictionary, that
    /// silence is dropped and the text is said again without <c>-v</c>. That is
    /// logged once, and every later run in that language goes straight to the
    /// default voice. The default voice reads foreign script letter by letter or
    /// by name, which is poor, but it is not silence.</para>
    ///
    /// <para><b>Both signals, not the stderr line alone (item 105).</b> The line
    /// alone once demoted a language for the module's life, and the line names a
    /// file rather than a language. A missing dictionary writes samples that are
    /// all exactly zero (measured on the shipped payload, <c>-v ja</c> and
    /// <c>-v ko</c>: 13 274 and 33 162 bytes, none non-zero), while speech has a
    /// non-zero byte within its first 1.2 KB (<c>de</c>, <c>en</c>, <c>he</c>).
    /// So a complaint beside real speech is logged nowhere and changes nothing.
    /// A demotion still lasts the module's life: it now needs a run that said
    /// nothing at all, which is what a missing dictionary is.</para>
    ///
    /// <para><b>It streams (item 106).</b> It used to hold the whole WAV until
    /// stderr ended; now only the leading zeros wait, and the first non-zero
    /// sample releases them and everything after it as it arrives.</para>
    /// </summary>
    private bool RunEspeak(string text, string? language, int rateAdj, EspeakEcho echo)
    {
        string? voice = string.IsNullOrWhiteSpace(language) || _espeakLacks.Contains(language) ? null : language;
        bool check = voice is not null && !_espeakSpeaks.Contains(voice);

        var outcome = Render(
            () => _voices.StartEspeak(text, voice, _rate, _volume, rateAdj, echo), "espeak", check, out string? why);

        if (outcome == RenderOutcome.LanguageMissing)
        {
            _espeakLacks.Add(voice!);
            _log($"espeak-ng cannot speak '{voice}' in this install ({why}); using its default voice for it from now on");
            outcome = Render(
                () => _voices.StartEspeak(text, null, _rate, _volume, rateAdj, echo), "espeak", false, out _);
        }
        else if (outcome == RenderOutcome.Spoke && voice is not null)
        {
            _espeakSpeaks.Add(voice);
        }

        return outcome == RenderOutcome.Spoke;
    }

    private enum RenderOutcome { Spoke, Silent, LanguageMissing }

    /// <summary>
    /// What espeak-ng prints when it cannot speak the voice it was given: the
    /// first with exit code 0 and a WAV of silence, the second with exit code 1
    /// and nothing. Both measured against the shipped payload.
    /// </summary>
    private static readonly string[] EspeakLacksLanguage =
    [
        "Can't read dictionary file",
        "voice does not exist",
    ];

    /// <summary>
    /// Write <paramref name="ms"/> of silence as audio blocks. Returns false if
    /// there was nothing to send, and stops at once on a STOP or PAUSE like the
    /// audio loop does.
    /// </summary>
    private bool SendSilence(int ms)
    {
        long remaining = (long)_streamRate * ms / 1000 * 2;       // 16-bit mono
        remaining -= remaining % 2;
        if (remaining <= 0) return false;

        var block = new byte[AudioBlock.MaxChunkBytes];
        bool sent = false;

        while (remaining > 0)
        {
            if (DrainCommandsAndCheckInterrupt()) return sent;

            int n = (int)Math.Min(remaining, block.Length);
            AudioBlock.Write(_out, block.AsSpan(0, n), _streamRate, 1, 16);
            _samplesSent += n / 2;
            remaining -= n;
            sent = true;
        }

        return sent;
    }

    /// <summary>
    /// Run the neural renderer and forward its WAV as audio blocks. Returns false
    /// if it produced no audio, which is the caller's cue to fall back.
    /// </summary>
    private bool SpeakWith(Func<Process> start, string what) =>
        Render(start, what, checkLanguage: false, out _) == RenderOutcome.Spoke;

    /// <summary>
    /// Run one renderer and forward its WAV as audio blocks.
    /// </summary>
    /// <param name="checkLanguage">
    /// Hold back the WAV's leading silence, and send none of it if the run was
    /// silence throughout and espeak-ng said it cannot speak the voice it was
    /// given. See <see cref="RunEspeak"/>.
    /// </param>
    /// <param name="why">espeak-ng's own line, when the outcome is LanguageMissing.</param>
    private RenderOutcome Render(Func<Process> start, string what, bool checkLanguage, out string? why)
    {
        why = null;
        Process process;
        try
        {
            process = start();
        }
        catch (Exception ex)
        {
            _log($"{what}: could not start — {ex.GetType().Name}: {ex.Message}");
            return RenderOutcome.Silent;
        }

        // EVERY RENDERER'S STDERR IS READ AS IT ARRIVES (2026-11-09). The neural
        // renderer reports bookmarks there; espeak-ng reports a missing
        // dictionary there and nowhere else, with exit code 0. It used to be read
        // only after a failure, so that report was never seen, and a renderer
        // that filled the pipe buffer would have blocked mid-utterance.
        var pump = new StderrPump(process.StandardError);
        StderrPump? marks = what == "neural" ? pump : null;

        string? Lacks()
        {
            pump.WaitForEnd();
            foreach (string said in EspeakLacksLanguage)
                if (pump.Find(said) is { } line) return line;
            return null;
        }

        try
        {
            var stdout = process.StandardOutput.BaseStream;

            // THE HEADER IS WAITED FOR WITH ONE EAR ON STDIN. Its first bytes
            // arrive only when the first audio exists, which after a cold model
            // load is seconds away, and a blocking read here meant a STOP pressed
            // during the load was obeyed only once there was audio to play. So the
            // read runs aside and the loop below keeps asking the question the
            // audio loop asks. On an interruption the finally kills the renderer,
            // which ends the read.
            var headerRead = Task.Run(() => WavHeader.Read(stdout));
            if (!AwaitAside(headerRead)) return RenderOutcome.Silent;

            // GetResult rather than Result so a failed read is logged as itself
            // and not as an AggregateException wrapping it.
            if (headerRead.GetAwaiter().GetResult() is not { } wav)
            {
                // Not a WAV. Either the renderer refused — vst-ctl exits non-zero
                // with a reason on stderr, which is exactly what "never exit 0
                // with no audio" bought us — or it is not the program we think.
                if (checkLanguage && Lacks() is { } missing) { why = missing; return RenderOutcome.LanguageMissing; }
                pump.WaitForEnd();
                _log($"{what}: no audio ({pump.Message})");
                return RenderOutcome.Silent;
            }

            Stream pcm = stdout;
            if (checkLanguage)
            {
                if (HoldLeadingSilence(stdout) is not { } gate) return RenderOutcome.Silent;
                if (!gate.Heard && Lacks() is { } missing) { why = missing; return RenderOutcome.LanguageMissing; }
                pcm = gate.Pcm;
            }

            _streamRate = wav.SampleRate;
            long sent = SendAudio(pcm, wav, marks);

            // NEVER REPORT SUCCESS HAVING PRODUCED NO AUDIO. A renderer can emit
            // a header and then nothing at all, and that is the failure this
            // whole feature keeps finding.
            if (sent == 0 && !Interrupted)
            {
                pump.WaitForEnd();
                _log($"{what}: a header and no samples ({pump.Message})");
                return RenderOutcome.Silent;
            }

            // The audio is all handed over, so every bookmark the renderer
            // printed is now due, in order — before the END that follows.
            if (!Interrupted && marks is not null)
            {
                marks.WaitForEnd();
                CollectMarks(marks);
                ReportMarks(before: long.MaxValue);
            }

            return RenderOutcome.Spoke;
        }
        catch (Exception ex)
        {
            _log($"{what}: {ex.GetType().Name}: {ex.Message}");
            return RenderOutcome.Silent;
        }
        finally
        {
            StopProcess(process);
        }
    }

    /// <summary>
    /// Read <paramref name="stdout"/> up to its first non-zero byte, counting the
    /// zeros before it rather than keeping them. Returns the stream to send from
    /// (those zeros, then the rest), and whether a non-zero byte was found before
    /// the end; null if a STOP or PAUSE arrived first. stdin is looked at between
    /// reads, as the audio loop does.
    /// </summary>
    private (Stream Pcm, bool Heard)? HoldLeadingSilence(Stream stdout)
    {
        var chunk = new byte[AudioBlock.MaxChunkBytes];
        long zeros = 0;

        while (true)
        {
            if (DrainCommandsAndCheckInterrupt()) return null;

            int n = stdout.Read(chunk, 0, chunk.Length);
            if (n <= 0) return (new HeldSilence(zeros, [], stdout), false);

            if (chunk.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0)
            {
                zeros += n;
                continue;
            }

            return (new HeldSilence(zeros, chunk[..n], stdout), true);
        }
    }

    /// <summary>
    /// <c>zeros</c> zero bytes, then <c>head</c>, then whatever <c>rest</c> still
    /// has: what <see cref="HoldLeadingSilence"/> read, put back in front.
    /// </summary>
    private sealed class HeldSilence(long zeros, byte[] head, Stream rest) : Stream
    {
        private long _zeros = zeros;
        private int _headAt;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;

            if (_zeros > 0)
            {
                int k = (int)Math.Min(_zeros, count);
                Array.Clear(buffer, offset, k);
                _zeros -= k;
                return k;
            }

            if (_headAt < head.Length)
            {
                int k = Math.Min(head.Length - _headAt, count);
                Array.Copy(head, _headAt, buffer, offset, k);
                _headAt += k;
                return k;
            }

            return rest.Read(buffer, offset, count);
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

    /// <summary>
    /// Wait for a read running aside while answering stdin the way the audio loop
    /// does. False if a STOP or PAUSE arrived first.
    /// </summary>
    private bool AwaitAside(Task read)
    {
        _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        // The wait handle rather than Wait(ms), which throws for a faulted read
        // instead of reporting that it finished.
        var finished = ((IAsyncResult)read).AsyncWaitHandle;
        while (!finished.WaitOne(HeaderPollMs))
        {
            if (DrainCommandsAndCheckInterrupt()) return false;
        }

        return true;
    }

    /// <summary>
    /// The audio loop, and its shape is the reason a stop works: one block, then
    /// a look at stdin, then the next block.
    /// </summary>
    private long SendAudio(Stream pcmSource, WavHeader wav, StderrPump? pump)
    {
        var buffer = new byte[AudioBlock.MaxChunkBytes];
        long total = 0;
        int held = 0;

        while (true)
        {
            if (DrainCommandsAndCheckInterrupt()) return total;

            int n = pcmSource.Read(buffer, held, buffer.Length - held);
            if (n <= 0) break;

            held += n;

            // A block must contain whole frames, so a partial one at the end of a
            // read is carried into the next. Splitting a 16-bit sample across two
            // blocks would put a click in the audio at every chunk boundary.
            int whole = held - held % wav.FrameBytes;
            if (whole == 0) continue;

            // INDEX MARKS, announced just before the block that contains them.
            // speechd with server-side audio does not synchronise a module's
            // events to its own playback — upstream's espeak module reports them
            // at synthesis time too — so "as the audio holding it is handed over"
            // is the honest position: at most one block (about 0.1 s) early, and
            // a mark at the very start precedes the first sample, when the
            // renderer's line has arrived by then. A line that arrives late is
            // reported with the next block, or at the end at the latest.
            long blockEnd = _samplesSent + whole / wav.FrameBytes;
            if (pump is not null)
            {
                CollectMarks(pump);
                ReportMarks(before: blockEnd);
            }

            AudioBlock.Write(_out, buffer.AsSpan(0, whole), wav.SampleRate, wav.Channels, wav.Bits);
            total += whole;
            _samplesSent = blockEnd;

            held -= whole;
            if (held > 0) Array.Copy(buffer, whole, buffer, 0, held);
        }

        if (held >= wav.FrameBytes)
        {
            int whole = held - held % wav.FrameBytes;
            AudioBlock.Write(_out, buffer.AsSpan(0, whole), wav.SampleRate, wav.Channels, wav.Bits);
            total += whole;
            _samplesSent += whole / wav.FrameBytes;
        }

        return total;
    }

    // ------------------------------------------------------------------ marks

    /// <summary>Move whatever the renderer has reported so far into the due list.</summary>
    private void CollectMarks(StderrPump pump)
    {
        while (pump.TryTake(out var mark)) _dueMarks.Add(mark);
    }

    /// <summary>
    /// Report the marks that fall before sample <paramref name="before"/> —
    /// <see cref="long.MaxValue"/> for all of them. In order, each exactly once.
    /// </summary>
    private void ReportMarks(long before)
    {
        int reported = 0;
        foreach (var mark in _dueMarks)
        {
            if (mark.Sample >= before) break;
            ReportMark(mark.Name);
            reported++;
        }

        _dueMarks.RemoveRange(0, reported);
    }

    /// <summary>
    /// <c>700-&lt;name&gt;</c> then <c>700 INDEX MARK</c>: speech-dispatcher's
    /// index-mark event, which is what lets a client highlight the word being
    /// read and lets the server resume a paused message from the right place. The
    /// name goes on one line, so control characters in it are neutralised — it
    /// came from a client.
    /// </summary>
    private void ReportMark(string name)
    {
        Reply("700-" + MarkLine.ForProtocolLine(name));
        Reply("700 INDEX MARK");
    }

    /// <summary>
    /// Read anything speech-dispatcher has sent without waiting for it, and
    /// report whether it told us to stop or to pause.
    ///
    /// <para>A command that is neither is put back: the server does not send
    /// a second SPEAK while one is in flight, so anything else here is a SET or a
    /// LIST for after this utterance, and losing it would be worth more than
    /// deferring it.</para>
    ///
    /// <para><b>And the look goes on past it.</b> It used to stop at the first
    /// command it put back, and, because it read through the put-back queue, find
    /// that same command on every later look. So a STOP behind a SET was not seen
    /// until the audio ran out. Now every line that has arrived is read, lines of a
    /// deferred command's block are deferred as text (a message may well say
    /// "STOP"), and only lines nobody has read yet are looked at.</para>
    ///
    /// <para><b>A PAUSE is an interruption, not a note for later.</b> It used to
    /// be put back with the others, so the audio carried on and the 704 arrived
    /// after the utterance had finished — a pause key that did nothing until it
    /// no longer mattered.</para>
    /// </summary>
    private bool DrainCommandsAndCheckInterrupt()
    {
        while (_in.ReadUnseen(out bool data) is { } line)
        {
            if (!data)
            {
                switch (line)
                {
                    case "STOP":
                        _stopRequested = true;
                        return true;

                    case "PAUSE":
                        _pauseRequested = true;
                        return true;

                    case "QUIT":
                        // Behind anything already deferred, so the commands the
                        // server sent before it are still answered before it.
                        _stopRequested = true;
                        _in.PushBack("QUIT");
                        return true;
                }
            }

            _in.PushBack(line);
        }

        return false;
    }

    // ----------------------------------------------------------------- input

    /// <summary>Key/value lines until a lone dot.</summary>
    private IEnumerable<(string Key, string Value)> ReadParameters()
    {
        _blockOpen = true;
        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null || line == ".") { _blockOpen = false; yield break; }

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
        _blockOpen = true;

        while (true)
        {
            string? line = _in.ReadLine(block: true);
            if (line is null || line == ".") { _blockOpen = false; break; }

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

        // Pay down the debt Run's catch would otherwise settle. Any terminator
        // settles an utterance, so a STOP or a PAUSE is not followed by an END;
        // INIT's success settles the refusal it owed.
        int paid = _owed.IndexOf(line);
        if (paid >= 0) _owed.RemoveRange(0, paid + 1);
        else if (line is "702 END" or "703 STOP" or "704 PAUSE" or "299 OK LOADED SUCCESSFULLY") _owed.Clear();
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

    /// <summary>
    /// SIGTERM, a short grace, then SIGKILL — to the renderer alone, never its
    /// children. vst-ctl handles SIGTERM as a stop and exits 0 (S1); espeak-ng
    /// dies on it. Either way the point is that a cancelled utterance leaves no
    /// process still synthesising it, because Orca cancels constantly and they
    /// would otherwise accumulate for the session.
    ///
    /// <para><b>Until 2026-11-09 this said SIGTERM and sent SIGKILL to the whole
    /// process tree</b> (item 27). The tree is the reason it changed, not the
    /// signal. vst-ctl starts the daemon itself when none is running (R-5), with a
    /// plain Process.Start, so the daemon is that vst-ctl's child for as long as
    /// it runs, cold model load included. A STOP during that first utterance
    /// killed the daemon it had just started, and the next utterance began
    /// another cold start. ProtocolTests now shows such a child surviving.</para>
    /// </summary>
    private void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                if (kill(process.Id, Sigterm) == 0) process.WaitForExit(TermGraceMs);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(2000);
                }
            }
        }
        catch (Exception) { /* it exited between the check and the kill */ }
        finally { process.Dispose(); }
    }

    /// <summary>
    /// How long a renderer has to obey SIGTERM. Both exit within milliseconds of
    /// it; this bounds only a hung one, and the module answers nothing meanwhile.
    /// </summary>
    private const int TermGraceMs = 250;

    private const int Sigterm = 15;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}

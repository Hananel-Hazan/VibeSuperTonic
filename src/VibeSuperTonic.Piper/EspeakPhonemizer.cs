using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Synthesis.Piper;

namespace VibeSuperTonic.Piper;

/// <summary>
/// espeak-ng phonemisation, P/Invoked, reproducing exactly what piper does.
///
/// <para><b>THE SPECIFICATION IS UPSTREAM'S <c>espeakbridge.c</c> PLUS
/// <c>phonemize_espeak.py</c></b>, and this is a reimplementation of both rather
/// than an interpretation of them. Every step below exists because dropping it
/// changes the id sequence, and a changed id sequence is audio that is wrong
/// rather than audio that fails. Promoted from
/// <see href="../../spike/piper-phonemes">the P1 spike</see> — which now
/// references this class instead of holding a copy, so its corpus run measures
/// what ships.</para>
///
/// <para><b>The function is <c>espeak_TextToPhonemesWithTerminator</c>, not
/// <c>espeak_TextToPhonemes</c></b>, which is what the plan said until P1
/// measured it. The plain one throws the clause terminator away — so there is no
/// way to know whether a clause ended a sentence, every input collapses to one
/// sentence, and the trailing punctuation piper feeds the model as a phoneme is
/// simply absent. It is also newer than the 1.52.0 release: it exists at the
/// commit piper pins and not at the tag, which is
/// <see cref="EspeakLibrary"/>'s whole reason for existing — and why the
/// constructor asks whether the library it just bound actually exports it.</para>
///
/// <para><b>Not thread-safe, and it cannot be made so.</b> espeak-ng keeps the
/// selected voice and the clause cursor in process-global state, so two
/// concurrent <see cref="Phonemize"/> calls would interleave inside the native
/// library. Calls are serialised on an instance lock; the library is also
/// initialised once per process, because <c>espeak_Initialize</c> is global and
/// a second call with a different data directory silently rebinds the first
/// caller's.</para>
/// </summary>
public sealed partial class EspeakPhonemizer : IPhonemizer, IDisposable
{
    private const string Lib = EspeakLibrary.Name;

    // espeak_AUDIO_OUTPUT: PLAYBACK, RETRIEVAL, SYNCHRONOUS, SYNCH_PLAYBACK.
    // Piper passes SYNCHRONOUS. Nothing is synthesised — it is the mode that
    // does not open an audio device, which matters because the library we ship
    // is built without any audio backend at all.
    private const int AudioOutputSynchronous = 2;
    private const int EspeakCharsAuto = 0;      // 8-bit or UTF-8, autodetected
    private const int EspeakPhonemesIpa = 0x02;

    // Clause flags, from espeakbridge.c. The low 20 bits carry the pause length
    // and the intonation; the type bits say whether the clause ended a sentence.
    private const int ClauseIntonationFullStop = 0x00000000;
    private const int ClauseIntonationComma = 0x00001000;
    private const int ClauseIntonationQuestion = 0x00002000;
    private const int ClauseIntonationExclamation = 0x00003000;
    private const int ClauseTypeClause = 0x00040000;
    private const int ClauseTypeSentence = 0x00080000;

    private const int ClausePeriod = 40 | ClauseIntonationFullStop | ClauseTypeSentence;
    private const int ClauseComma = 20 | ClauseIntonationComma | ClauseTypeClause;
    private const int ClauseQuestion = 40 | ClauseIntonationQuestion | ClauseTypeSentence;
    private const int ClauseExclamation = 45 | ClauseIntonationExclamation | ClauseTypeSentence;
    private const int ClauseColon = 30 | ClauseIntonationFullStop | ClauseTypeClause;
    private const int ClauseSemicolon = 30 | ClauseIntonationComma | ClauseTypeClause;

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int espeak_Initialize(int output, int bufLength, string? path, int options);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int espeak_SetVoiceByName(string name);

    [LibraryImport(Lib)]
    private static partial IntPtr espeak_TextToPhonemesWithTerminator(
        ref IntPtr textPtr, int textMode, int phonemeMode, out int terminator);

    [LibraryImport(Lib)]
    private static partial int espeak_Terminate();

    private static readonly object InitGate = new();
    private static bool _initialised;
    private static string? _initialisedWith;

    private readonly object _gate = new();
    private string? _voice;
    private bool _disposed;

    /// <summary>
    /// Bind and initialise, once per process.
    /// </summary>
    /// <param name="resolution">
    /// From <see cref="EspeakLibrary.Probe"/>. The caller does the probing so it
    /// can log which library answered — the difference between our build and a
    /// distro one is inaudible right up until it is a report about prosody.
    /// </param>
    public EspeakPhonemizer(EspeakLibrary.Resolution resolution)
    {
        EspeakLibrary.Bind(resolution);

        // Before anything is initialised, because the alternative is an
        // EntryPointNotFoundException out of a P/Invoke on the render thread —
        // which reaches the user as a press that produced no sound.
        if (EspeakLibrary.MissingTerminatorExport(resolution) is { } why)
            throw new InvalidOperationException(why);

        lock (InitGate)
        {
            if (_initialised)
            {
                // A second data directory would silently rebind the first
                // caller's — espeak_Initialize is process-global — so this is
                // refused rather than allowed to half work.
                if (_initialisedWith != resolution.DataDir)
                    throw new InvalidOperationException(
                        $"espeak-ng is already initialised with data directory " +
                        $"'{_initialisedWith ?? "(default)"}'; it is process-global and " +
                        $"cannot be re-initialised with '{resolution.DataDir ?? "(default)"}'");
                return;
            }

            int rc = espeak_Initialize(AudioOutputSynchronous, 0, resolution.DataDir, 0);
            if (rc < 0)
                throw new InvalidOperationException(
                    $"espeak_Initialize failed ({rc}) with data directory " +
                    $"'{resolution.DataDir ?? "(espeak-ng's default)"}'");

            _initialised = true;
            _initialisedWith = resolution.DataDir;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IReadOnlyList<string>> Phonemize(string voice, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voice);
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetVoiceLocked(voice);
            return PhonemizeLocked(text);
        }
    }

    private void SetVoiceLocked(string voice)
    {
        if (voice == _voice) return;
        int rc = espeak_SetVoiceByName(voice);
        if (rc != 0)
            throw new InvalidOperationException($"espeak_SetVoiceByName(\"{voice}\") failed ({rc})");
        _voice = voice;
    }

    private List<IReadOnlyList<string>> PhonemizeLocked(string text)
    {
        var all = new List<IReadOnlyList<string>>();
        var sentence = new List<string>();

        // espeak advances this pointer through the buffer and nulls it when the
        // text is exhausted. The buffer has to outlive the loop, so the original
        // is held separately for the free.
        IntPtr buffer = Marshal.StringToCoTaskMemUTF8(text);
        IntPtr cursor = buffer;
        try
        {
            while (cursor != IntPtr.Zero)
            {
                IntPtr result = espeak_TextToPhonemesWithTerminator(
                    ref cursor, EspeakCharsAuto, EspeakPhonemesIpa, out int terminator);

                string phonemes = Marshal.PtrToStringUTF8(result) ?? "";

                // Only the low 20 bits are the clause; the rest is espeak's
                // internal bookkeeping and would break every comparison below.
                terminator &= 0x000FFFFF;

                string terminatorStr = terminator switch
                {
                    ClausePeriod => ".",
                    ClauseQuestion => "?",
                    ClauseExclamation => "!",
                    ClauseComma => ",",
                    ClauseColon => ":",
                    ClauseSemicolon => ";",
                    _ => "",
                };

                // (lang) markers surround words espeak switched language for.
                phonemes = LanguageSwitch().Replace(phonemes, "");

                // The terminator is fed to the model as a phoneme. A comma,
                // colon or semicolon also gets a trailing space and does NOT end
                // the sentence — that distinction is the whole reason the
                // WithTerminator variant is required.
                phonemes += terminatorStr;
                if (terminatorStr is "," or ":" or ";")
                    phonemes += " ";

                // NFD, then one "phoneme" per Unicode code point — so an accented
                // character becomes its base plus its combining mark, each looked
                // up separately. Runes rather than chars: a char is a UTF-16 code
                // unit and would split anything above the BMP where Python never
                // splits it.
                foreach (var rune in phonemes.Normalize(NormalizationForm.FormD).EnumerateRunes())
                    sentence.Add(rune.ToString());

                if ((terminator & ClauseTypeSentence) == ClauseTypeSentence)
                {
                    all.Add(sentence);
                    sentence = [];
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }

        // Text with no final terminator is still one complete sentence.
        if (sentence.Count > 0) all.Add(sentence);

        return all;
    }

    [GeneratedRegex(@"\([^)]+\)")]
    private static partial Regex LanguageSwitch();

    /// <summary>
    /// Releases the voice selection, not the library.
    ///
    /// <para><c>espeak_Terminate</c> is deliberately NOT called. It tears down
    /// process-global state that <see cref="_initialised"/> would still claim to
    /// hold, so a daemon that disposed one phonemiser and built another — which
    /// is exactly what an engine switch does — would P/Invoke into a terminated
    /// library. The library stays loaded for the life of the process, which is
    /// what it costs: a few MB of dictionaries.</para>
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _voice = null;
        }
    }
}

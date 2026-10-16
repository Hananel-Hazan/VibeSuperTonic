using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Piper;

namespace VibeSuperTonic.Spike.PiperPhonemes;

/// <summary>
/// P1's harness around the phonemiser — and, since P3, around the phonemiser
/// that SHIPS.
///
/// <para>This class used to be the implementation. It is now the negative
/// control and nothing else: <see cref="EspeakPhonemizer"/> holds the
/// reimplementation of upstream's <c>espeakbridge.c</c> and
/// <c>phonemize_espeak.py</c>, and this wraps it so the corpus can be run
/// against a deliberately broken variant. The reason for the move is the whole
/// value of P1: a parity run against a COPY of the phonemiser measures the copy,
/// and the copy is free to drift from the one the product uses.</para>
///
/// <para><see cref="Sabotage.None"/> — the case the parity number comes from —
/// calls the production class directly. Every other case re-implements the one
/// step it is removing, because a sabotage has to be able to break something
/// production would not let it break.</para>
/// </summary>
public sealed partial class Espeak : IDisposable
{
    private const string Lib = "espeak-ng";

    // espeak_AUDIO_OUTPUT: PLAYBACK, RETRIEVAL, SYNCHRONOUS, SYNCH_PLAYBACK.
    // Piper passes SYNCHRONOUS. Nothing is synthesised — it is the mode that
    // does not open an audio device, which matters because this library is
    // built without any audio backend at all.
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

    private readonly EspeakPhonemizer _production;
    private string? _voice;

    /// <summary>
    /// Deliberate sabotage, for the negative control. A parity run that passes
    /// proves nothing unless the same harness can be made to fail — this
    /// project has now recorded the vacuous guard five times, and a corpus
    /// diff that would agree no matter what it compared is exactly that shape.
    /// Each value removes ONE step upstream performs.
    /// </summary>
    public enum Sabotage
    {
        None,
        SkipNfd,             // leave accented characters composed
        SkipLanguageStrip,   // leave espeak's (lang) markers in
        SkipTerminator,      // drop the punctuation phoneme
        SkipClauseSpace,     // no trailing space after , : ;
        SplitEveryClause,    // treat every clause as a sentence
    }

    public Sabotage Broken { get; set; } = Sabotage.None;

    /// <param name="dataDir">The espeak-ng-data directory.</param>
    /// <param name="libraryPath">
    /// The shared library. Passed explicitly rather than found on the loader
    /// path, because the whole point of building our own is that the one we
    /// test is the one we ship — resolving to whatever the distro installed
    /// would be a check that reads something other than the artifact.
    /// </param>
    public Espeak(string dataDir, string libraryPath)
    {
        // The production class binds and initialises the library; this one's
        // own P/Invokes then resolve through the same resolver, because
        // SetDllImportResolver is per-assembly and both assemblies name the
        // same soname. The sabotage paths need direct calls, so the imports
        // below stay.
        _production = new EspeakPhonemizer(
            new EspeakLibrary.Resolution(libraryPath, "the spike", dataDir));

        NativeLibrary.SetDllImportResolver(typeof(Espeak).Assembly, (name, _, _) =>
            name == Lib ? NativeLibrary.Load(libraryPath) : IntPtr.Zero);
    }

    public void SetVoice(string voice)
    {
        if (voice == _voice) return;
        var rc = espeak_SetVoiceByName(voice);
        if (rc != 0)
            throw new InvalidOperationException($"espeak_SetVoiceByName(\"{voice}\") failed ({rc})");
        _voice = voice;
    }

    /// <summary>Text to phonemes, grouped by sentence, as piper groups them.</summary>
    public List<List<string>> Phonemize(string voice, string text)
    {
        // THE PARITY NUMBER COMES FROM HERE. Everything below this line exists
        // to be broken on purpose.
        if (Broken == Sabotage.None)
            return _production.Phonemize(voice, text).Select(s => s.ToList()).ToList();

        SetVoice(voice);

        var all = new List<List<string>>();
        var sentence = new List<string>();

        // espeak advances this pointer through the buffer and nulls it when the
        // text is exhausted. The buffer has to outlive the loop, so the original
        // is held separately for the free.
        var buffer = Marshal.StringToCoTaskMemUTF8(text);
        var cursor = buffer;
        try
        {
            while (cursor != IntPtr.Zero)
            {
                var result = espeak_TextToPhonemesWithTerminator(
                    ref cursor, EspeakCharsAuto, EspeakPhonemesIpa, out var terminator);

                var phonemes = Marshal.PtrToStringUTF8(result) ?? "";

                // Only the low 20 bits are the clause; the rest is espeak's
                // internal bookkeeping and would break every comparison below.
                terminator &= 0x000FFFFF;

                var terminatorStr = terminator switch
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
                if (Broken != Sabotage.SkipLanguageStrip)
                    phonemes = LanguageSwitch().Replace(phonemes, "");

                // The terminator is fed to the model as a phoneme. A comma,
                // colon or semicolon also gets a trailing space and does NOT
                // end the sentence — that distinction is the whole reason the
                // WithTerminator variant is required.
                if (Broken != Sabotage.SkipTerminator)
                    phonemes += terminatorStr;
                if (terminatorStr is "," or ":" or ";" && Broken != Sabotage.SkipClauseSpace)
                    phonemes += " ";

                // NFD, then one "phoneme" per Unicode code point — so an
                // accented character becomes its base plus its combining mark,
                // each looked up separately. Runes rather than chars: a char is
                // a UTF-16 code unit and would split anything above the BMP in
                // a place Python never splits it.
                var decomposed = Broken == Sabotage.SkipNfd
                    ? phonemes
                    : phonemes.Normalize(NormalizationForm.FormD);
                foreach (var rune in decomposed.EnumerateRunes())
                    sentence.Add(rune.ToString());

                if (Broken == Sabotage.SplitEveryClause
                    || (terminator & ClauseTypeSentence) == ClauseTypeSentence)
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
        if (sentence.Count > 0)
            all.Add(sentence);

        return all;
    }

    /// <summary>
    /// Phonemes to ids: BOS, PAD, then every phoneme followed by PAD, then EOS.
    /// A phoneme the map does not know is SKIPPED, not substituted and not an
    /// error — upstream logs a warning and carries on, and matching that is the
    /// difference between agreeing with piper and being more correct than it.
    /// </summary>
    public static List<int> ToIds(IEnumerable<string> phonemes, IReadOnlyDictionary<string, int[]> idMap)
        => Core.Synthesis.Piper.PiperPhonemes.ToIds(phonemes, idMap).Select(id => (int)id).ToList();

    [GeneratedRegex(@"\([^)]+\)")]
    private static partial Regex LanguageSwitch();

    /// <summary>
    /// espeak_Terminate is NOT called — see EspeakPhonemizer.Dispose for why the
    /// library outlives its wrappers.
    /// </summary>
    public void Dispose() => _production.Dispose();
}

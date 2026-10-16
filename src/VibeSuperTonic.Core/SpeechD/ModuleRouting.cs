namespace VibeSuperTonic.Core.SpeechD;

/// <summary>
/// The message types speech-dispatcher distinguishes, which route A could not
/// see at all.
/// </summary>
public enum SpeechdMessageType
{
    /// <summary><c>SPEAK</c> — a sentence, a paragraph, a document.</summary>
    Text,

    /// <summary><c>CHAR</c> — one character, typically an echoed keystroke.</summary>
    Char,

    /// <summary><c>KEY</c> — a key name such as <c>Control_L</c>.</summary>
    Key,

    /// <summary><c>SOUND_ICON</c> — a symbolic event name such as <c>message-new</c>.</summary>
    SoundIcon,
}

/// <summary>Which voice renders an utterance.</summary>
public enum RenderVoice
{
    /// <summary>The neural voice, through <c>vst-ctl render</c>. 718 ms to first word.</summary>
    Neural,

    /// <summary>The bundled espeak-ng. 3.3 ms to first audio.</summary>
    Espeak,
}

/// <summary>
/// Neural for reading, espeak for echo — decided by the message type, which is
/// <b>the whole reason this is a native module and not a generic one</b>.
///
/// <para>S0 measured the numbers this rests on: 718 ms to the first word of a
/// sentence and 383 ms for a single character, against 3.3 ms for the espeak-ng
/// this archive already ships. 718 ms before a paragraph is the same order as the
/// hotkey path people already use by choice; 383 ms per keystroke is not an echo,
/// and a user typing at speed would fall behind within a sentence and never catch
/// up.</para>
///
/// <para><b>Route A could not do this.</b> A generic module receives
/// <c>SPEAK</c>, <c>CHAR</c> and <c>KEY</c> as bare <c>$DATA</c> with nothing
/// marking which is which — measured with a probe, not assumed — so it could only
/// route by guessing from the shape of the text. Correct echo routing is what
/// turned route B from "saves six milliseconds" into the design.
/// docs/SPEECHD-PLAN.md, traps 10 and 15.</para>
/// </summary>
public static class ModuleRouting
{
    /// <summary>Which voice should render a message of this type.</summary>
    public static RenderVoice For(SpeechdMessageType type) => type switch
    {
        // The dominant traffic of a screen reader, and the case the neural voice
        // is 100x too slow for.
        SpeechdMessageType.Char => RenderVoice.Espeak,
        SpeechdMessageType.Key => RenderVoice.Espeak,

        // A symbolic event name — "message-new-instant". It is punctuation in the
        // interaction, not prose, and it wants to be instant rather than lovely.
        SpeechdMessageType.SoundIcon => RenderVoice.Espeak,

        // Everything else is reading, which is what this product is for.
        _ => RenderVoice.Neural,
    };

    /// <summary>
    /// Parse the command word speech-dispatcher sent. Returns null for anything
    /// that is not a speak command, so the caller's dispatch stays one lookup.
    /// </summary>
    public static SpeechdMessageType? Parse(string command) => command switch
    {
        "SPEAK" => SpeechdMessageType.Text,
        "CHAR" => SpeechdMessageType.Char,
        "KEY" => SpeechdMessageType.Key,
        "SOUND_ICON" => SpeechdMessageType.SoundIcon,
        _ => null,
    };
}

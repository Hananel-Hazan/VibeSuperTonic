namespace VibeSuperTonic.Engine.Interop;

internal static class SapiConstants
{
    public const int S_OK = 0;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    public static readonly Guid SPDFID_Text = new("7CEEF9F9-3D13-11D2-9EE7-00C04F797396");
    public static readonly Guid SPDFID_WaveFormatEx = new("C31ADBAE-527F-4FF5-A230-F62BB61FF70C");

    public const ushort WAVE_FORMAT_PCM = 1;

    public const uint SPVES_CONTINUE = 0;
    public const uint SPVES_ABORT = 1 << 0;
    public const uint SPVES_SKIP = 1 << 1;
    public const uint SPVES_RATE = 1 << 2;
    public const uint SPVES_VOLUME = 1 << 3;

    // SPEVENTENUM values from sapi.h
    public const ushort SPEI_START_INPUT_STREAM = 1;
    public const ushort SPEI_END_INPUT_STREAM = 2;
    public const ushort SPEI_VOICE_CHANGE = 3;
    public const ushort SPEI_TTS_BOOKMARK = 4;
    public const ushort SPEI_WORD_BOUNDARY = 5;
    public const ushort SPEI_PHONEME = 6;
    public const ushort SPEI_SENTENCE_BOUNDARY = 7;
    public const ushort SPEI_VISEME = 8;

    // SPEVENTLPARAMTYPE values for SPEVENT.elParamType
    public const ushort SPET_LPARAM_IS_UNDEFINED = 0;
    public const ushort SPET_LPARAM_IS_TOKEN = 1;
    public const ushort SPET_LPARAM_IS_OBJECT = 2;
    public const ushort SPET_LPARAM_IS_POINTER = 3;
    public const ushort SPET_LPARAM_IS_STRING = 4;
}

internal enum SPVACTIONS
{
    SPVA_Speak = 0,
    SPVA_Silence,
    SPVA_Pronounce,
    SPVA_Bookmark,
    SPVA_SpellOut,
    SPVA_Section,
    SPVA_ParseUnknownTag,
}

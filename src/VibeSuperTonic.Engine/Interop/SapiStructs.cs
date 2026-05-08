using System.Runtime.InteropServices;

namespace VibeSuperTonic.Engine.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SPVPITCH
{
    public int MiddleAdj;
    public int RangeAdj;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SPVSTATE
{
    public SPVACTIONS eAction;
    public ushort LangID;
    public ushort wReserved;
    public int EmphAdj;
    public int RateAdj;
    public uint Volume;
    public SPVPITCH PitchAdj;
    public uint SilenceMSecs;
    public IntPtr pPhoneIds;
    public byte ePartOfSpeech;
    public SPVCONTEXT Context;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SPVCONTEXT
{
    public IntPtr pCategory;
    public IntPtr pBefore;
    public IntPtr pAfter;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SPVTEXTFRAG
{
    public IntPtr pNext;
    public SPVSTATE State;
    public IntPtr pTextStart;
    public uint ulTextLen;
    public uint ulTextSrcOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SPEVENT
{
    public ushort eEventId;
    public ushort elParamType;
    public uint ulStreamNum;
    public ulong ullAudioStreamOffset;
    public IntPtr wParam;
    public IntPtr lParam;
}

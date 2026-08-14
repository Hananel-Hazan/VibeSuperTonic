using System.Runtime.InteropServices;

namespace VibeSuperTonic.Launcher.Export;

/// <summary>
/// Minimal Media Foundation interop surface — only the entry points and
/// interface methods <see cref="MfAudioEncoder"/> actually invokes.
///
/// Interface declarations must list every vtable slot up to the ones we call,
/// in correct order, because the marshaler dispatches by slot index, not by
/// method name. Unused slots are declared as <c>UnusedNN</c> stubs returning
/// HRESULT with no parameters — they never execute, but their presence keeps
/// the vtable indexing correct.
///
/// GUIDs are pulled from <c>mfapi.h</c> and <c>mfidl.h</c> (Windows SDK).
/// </summary>
internal static class MfApi
{
    public const uint MF_VERSION = 0x00020070; // MF_SDK_VERSION 2 << 16 | MF_API_VERSION 0x70

    public const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
        IntPtr pAttributes,
        out IMFSourceReader ppSourceReader);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
        IntPtr pByteStream,
        IntPtr pAttributes,
        out IMFSinkWriter ppSinkWriter);

    // Enumerates COMPLETE output media types the installed encoder advertises for
    // a subtype. The MP3 encoder MFT rejects a hand-built output type
    // (MF_E_INVALIDMEDIATYPE on SetInputMediaType) — it needs one of these,
    // which carry the MPEG layer-3 user-data blob. AAC tolerates a hand-built type.
    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFTranscodeGetAudioOutputAvailableTypes(
        in Guid guidSubType, uint dwMFTFlags, IntPtr pCodecConfig, out IMFCollection ppAvailableTypes);

    public const uint MFT_ENUM_FLAG_ALL = 0x0000003F;

    // -- Major / subtype GUIDs --
    public static readonly Guid MFMediaType_Audio   = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM   = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_MP3   = new("00000055-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC   = new("00001610-0000-0010-8000-00AA00389B71");

    // -- Attribute GUIDs --
    public static readonly Guid MF_MT_MAJOR_TYPE                 = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE                    = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS         = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND   = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE      = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT      = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE                  = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
}

// =========================================================================
// IMFMediaType (extends IMFAttributes). 30 IMFAttributes slots + 5 own.
// We only call SetUINT32 (slot 18) and SetGUID (slot 21); the rest are stubs.
// =========================================================================
[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // IMFAttributes vtable
    [PreserveSig] int Unused00();
    [PreserveSig] int Unused01();
    [PreserveSig] int Unused02();
    [PreserveSig] int Unused03();
    // 4: GetUINT32(REFGUID guidKey, UINT32 *punValue)
    [PreserveSig] int GetUINT32([In] in Guid guidKey, out uint punValue);
    [PreserveSig] int Unused05();
    [PreserveSig] int Unused06();
    [PreserveSig] int Unused07();
    [PreserveSig] int Unused08();
    [PreserveSig] int Unused09();
    [PreserveSig] int Unused10();
    [PreserveSig] int Unused11();
    [PreserveSig] int Unused12();
    [PreserveSig] int Unused13();
    [PreserveSig] int Unused14();
    [PreserveSig] int Unused15();
    [PreserveSig] int Unused16();
    [PreserveSig] int Unused17();
    // 18: SetUINT32(REFGUID guidKey, UINT32 unValue)
    [PreserveSig] int SetUINT32([In] in Guid guidKey, uint unValue);
    [PreserveSig] int Unused19();
    [PreserveSig] int Unused20();
    // 21: SetGUID(REFGUID guidKey, REFGUID guidValue)
    [PreserveSig] int SetGUID([In] in Guid guidKey, [In] in Guid guidValue);
    [PreserveSig] int Unused22();
    [PreserveSig] int Unused23();
    [PreserveSig] int Unused24();
    [PreserveSig] int Unused25();
    [PreserveSig] int Unused26();
    [PreserveSig] int Unused27();
    [PreserveSig] int Unused28();
    [PreserveSig] int Unused29();
    // IMFMediaType own slots — unused.
    [PreserveSig] int Unused30();
    [PreserveSig] int Unused31();
    [PreserveSig] int Unused32();
    [PreserveSig] int Unused33();
    [PreserveSig] int Unused34();
}

// =========================================================================
// IMFSourceReader
// We use: SetStreamSelection (1), SetCurrentMediaType (4), ReadSample (6).
// =========================================================================
[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int Unused0_GetStreamSelection();
    // 1: SetStreamSelection(DWORD dwStreamIndex, BOOL fSelected)
    [PreserveSig] int SetStreamSelection(int dwStreamIndex, [MarshalAs(UnmanagedType.Bool)] bool fSelected);
    [PreserveSig] int Unused2_GetNativeMediaType();
    [PreserveSig] int Unused3_GetCurrentMediaType();
    // 4: SetCurrentMediaType(DWORD dwStreamIndex, DWORD *pdwReserved, IMFMediaType *pMediaType)
    [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved, IMFMediaType pMediaType);
    [PreserveSig] int Unused5_SetCurrentPosition();
    // 6: ReadSample(DWORD dwStreamIndex, DWORD dwControlFlags, DWORD *pdwActualStreamIndex,
    //               DWORD *pdwStreamFlags, LONGLONG *pllTimestamp, IMFSample **ppSample)
    [PreserveSig] int ReadSample(
        int streamIndex,
        uint controlFlags,
        out uint _actualStreamIndex,
        out uint flags,
        out long timestamp,
        out IntPtr sample);
    [PreserveSig] int Unused7_Flush();
    [PreserveSig] int Unused8_GetServiceForStream();
    [PreserveSig] int Unused9_GetPresentationAttribute();
}

// =========================================================================
// IMFSinkWriter
// We use: AddStream (0), SetInputMediaType (1), BeginWriting (2),
//          WriteSample (3), Finalize_ (8).
// =========================================================================
[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    // 0: AddStream(IMFMediaType *pTargetMediaType, DWORD *pdwStreamIndex)
    [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
    // 1: SetInputMediaType(DWORD dwStreamIndex, IMFMediaType *pInputMediaType, IMFAttributes *pEncodingParameters)
    [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IntPtr pEncodingParameters);
    // 2: BeginWriting()
    [PreserveSig] int BeginWriting();
    // 3: WriteSample(DWORD dwStreamIndex, IMFSample *pSample)
    [PreserveSig] int WriteSample(uint dwStreamIndex, IntPtr pSample);
    [PreserveSig] int Unused4_SendStreamTick();
    [PreserveSig] int Unused5_PlaceMarker();
    [PreserveSig] int Unused6_NotifyEndOfSegment();
    [PreserveSig] int Unused7_Flush();
    // 8: Finalize()  (named Finalize_ to avoid C# Object.Finalize collision)
    [PreserveSig] int Finalize_();
    [PreserveSig] int Unused9_GetServiceForStream();
    [PreserveSig] int Unused10_GetStatistics();
}

// =========================================================================
// IMFCollection — only GetElementCount (0) and GetElement (1) are used.
// =========================================================================
[ComImport, Guid("5BC8A76B-869A-46A3-9B03-FA218A66AEBE"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFCollection
{
    [PreserveSig] int GetElementCount(out uint pcElements);
    [PreserveSig] int GetElement(uint dwElementIndex, [MarshalAs(UnmanagedType.IUnknown)] out object ppUnkElement);
    [PreserveSig] int Unused2_AddElement();
    [PreserveSig] int Unused3_RemoveElement();
    [PreserveSig] int Unused4_InsertElementAt();
    [PreserveSig] int Unused5_RemoveAllElements();
}

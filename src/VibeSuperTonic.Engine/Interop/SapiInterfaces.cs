using System.Runtime.InteropServices;

namespace VibeSuperTonic.Engine.Interop;

[ComImport]
[Guid("5B559F40-E952-11D2-BB91-00C04F8EE6C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISpObjectWithToken
{
    [PreserveSig]
    int SetObjectToken([MarshalAs(UnmanagedType.IUnknown)] object pToken);

    [PreserveSig]
    int GetObjectToken([MarshalAs(UnmanagedType.IUnknown)] out object ppToken);
}

[ComImport]
[Guid("9880499B-CCE9-11D2-B503-00C04F797396")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISpTTSEngineSite
{
    [PreserveSig]
    int AddEvents([In] IntPtr pEventArray, uint ulCount);

    [PreserveSig]
    int GetEventInterest(out ulong pullEventInterest);

    [PreserveSig]
    uint GetActions();

    [PreserveSig]
    int Write([In] IntPtr pBuff, uint cb, out uint pcbWritten);

    [PreserveSig]
    int GetRate(out int pRateAdjust);

    [PreserveSig]
    int GetVolume(out ushort pusVolume);

    [PreserveSig]
    int GetSkipInfo(out uint peType, out int plNumItems);

    [PreserveSig]
    int CompleteSkip(int ulNumSkipped);
}

[ComImport]
[Guid("A74D7C8E-4CC5-4F2F-A6EB-804DEE18500E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISpTTSEngine
{
    [PreserveSig]
    int Speak(
        uint dwSpeakFlags,
        [In] ref Guid rguidFormatId,
        [In] IntPtr pWaveFormatEx,
        [In] IntPtr pTextFragList,
        [In, MarshalAs(UnmanagedType.IUnknown)] object pOutputSite);

    [PreserveSig]
    int GetOutputFormat(
        [In] IntPtr pTargetFmtId,
        [In] IntPtr pTargetWaveFormatEx,
        out Guid pDesiredFormatId,
        out IntPtr ppCoMemDesiredWaveFormatEx);
}

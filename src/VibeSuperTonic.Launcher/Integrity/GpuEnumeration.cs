using System.Runtime.InteropServices;

namespace VibeSuperTonic.Launcher.Integrity;

internal sealed record GpuAdapter(int Index, string Description, long DedicatedVideoMemoryBytes, bool IsSoftware)
{
    public string Display => IsSoftware
        ? $"{Index}: {Description} (software)"
        : DedicatedVideoMemoryBytes > 0
            ? $"{Index}: {Description} ({Math.Round(DedicatedVideoMemoryBytes / (1024.0 * 1024 * 1024), 1)} GB VRAM)"
            : $"{Index}: {Description}";
}

/// <summary>
/// Enumerates D3D12-capable adapters in the same order DirectML uses, so the
/// index returned here is exactly what to pass as <see cref="EngineSettings.DirectMLDeviceId"/>.
/// <para>
/// CRITICAL: DirectML's <c>device_id</c> indexes via
/// <c>IDXGIFactory6::EnumAdapterByGpuPreference(HIGH_PERFORMANCE)</c>, NOT the
/// physical adapter order from <c>IDXGIFactory1::EnumAdapters1</c>. On a typical
/// iGPU+dGPU laptop those orderings differ — using <c>EnumAdapters1</c> here
/// would mean the user picks "NVIDIA" in our dropdown but DirectML routes to
/// the Intel iGPU. We use the IDXGIFactory6 path when available (Windows 10
/// 1803+) and fall back to EnumAdapters1 only if the OS doesn't support it.
/// </para>
/// </summary>
internal static class GpuEnumeration
{
    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var result = new List<GpuAdapter>();
        IntPtr factory1 = IntPtr.Zero;
        IntPtr factory6 = IntPtr.Zero;
        try
        {
            var iidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int hr = CreateDXGIFactory1(ref iidFactory1, out factory1);
            if (hr < 0 || factory1 == IntPtr.Zero) return result;

            // Try IDXGIFactory6 (Windows 10 1803+). Falls back to IDXGIFactory1
            // ordering if QI fails on older OSes.
            var iidFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
            int qi = Marshal.QueryInterface(factory1, iidFactory6, out factory6);
            bool useGpuPreference = qi >= 0 && factory6 != IntPtr.Zero;

            var iidAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");

            unsafe
            {
                for (uint i = 0; ; i++)
                {
                    IntPtr adapter = IntPtr.Zero;
                    int hr2;
                    if (useGpuPreference)
                    {
                        void** vtbl = *(void***)factory6;
                        // IDXGIFactory6::EnumAdapterByGpuPreference is vtable slot 29
                        // (3 IUnknown + 4 IDXGIObject + 5 IDXGIFactory + 2 Factory1
                        //  + 11 Factory2 + 1 Factory3 + 2 Factory4 + 1 Factory5 = slot 29)
                        var enumByPref = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, ref Guid, out IntPtr, int>)vtbl[29];
                        // 2 = DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE — the ONNX Runtime
                        // DirectML provider uses this exact value, so our indices match.
                        hr2 = enumByPref(factory6, i, 2u, ref iidAdapter1, out adapter);
                    }
                    else
                    {
                        void** vtbl = *(void***)factory1;
                        // IDXGIFactory1::EnumAdapters1 is vtable slot 12
                        var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)vtbl[12];
                        hr2 = enumAdapters1(factory1, i, out adapter);
                    }
                    if (hr2 < 0 || adapter == IntPtr.Zero) break;

                    try
                    {
                        void** aVtbl = *(void***)adapter;
                        // IDXGIAdapter1::GetDesc1 is vtable slot 10
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, out DXGI_ADAPTER_DESC1, int>)aVtbl[10];
                        if (getDesc1(adapter, out var desc) >= 0)
                        {
                            string description = new string(desc.Description).TrimEnd('\0');
                            bool isSoftware = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                            result.Add(new GpuAdapter(
                                Index: (int)i,
                                Description: description,
                                DedicatedVideoMemoryBytes: (long)desc.DedicatedVideoMemory,
                                IsSoftware: isSoftware));
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
        }
        catch { /* DXGI not available, no DX support, etc. — return empty list */ }
        finally
        {
            if (factory6 != IntPtr.Zero) Marshal.Release(factory6);
            if (factory1 != IntPtr.Zero) Marshal.Release(factory1);
        }
        return result;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }
}

using System.Runtime.InteropServices;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// Where the machine's power is coming from, asked of Windows each time a
/// session is built.
///
/// <para>One call per decision, no cache, no subscription and no thread: a
/// session is built once per host process, so there is nothing to poll, and
/// nothing to fail to unsubscribe from inside a SAPI host we do not own. The
/// consequence is that a laptop unplugged mid-session keeps the provider it
/// started with until the next session build; that is stated in the setting's
/// tooltip rather than hidden.</para>
///
/// <para><b>Any failure is <see cref="PowerStates.Unknown"/></b>, which the
/// decision treats as "do not change anything". The call can throw in a sandbox
/// that denies the API, and the engine must still speak.</para>
/// </summary>
internal static class PowerSource
{
    public static string Read()
    {
        try
        {
            return GetSystemPowerStatus(out var status)
                ? PowerStates.FromAcLineStatus(status.ACLineStatus)
                : PowerStates.Unknown;
        }
        catch { return PowerStates.Unknown; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

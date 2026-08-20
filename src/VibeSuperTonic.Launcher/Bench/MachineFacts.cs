using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Launcher.Bench;

/// <summary>
/// What this machine is, for stamping onto a benchmark profile and for deciding
/// whether a stored one still describes it.
///
/// <para>The Windows counterpart of the Linux daemon's <c>MachineFacts</c>. Same
/// record, same meaning, different sources: registry and Win32 where Linux reads
/// <c>/proc</c> and <c>/sys</c>. The record itself and the staleness rules live in
/// Core, which is the whole point — a profile written by one platform and read by
/// the other, out of the same portable folder, has to agree about what it says.</para>
///
/// <para><b>Nothing here throws.</b> A profile whose CPU name is "unknown" is
/// still a usable profile; a Control Panel that will not open because a registry
/// key had an unexpected shape is not. Every reader below returns a stable
/// fallback instead.</para>
/// </summary>
internal static class MachineFacts
{
    /// <summary>
    /// Everything a <see cref="BenchmarkProfile"/> records about its machine.
    /// </summary>
    /// <param name="idleCpuPercent">
    /// From <see cref="IdleCpuPercentAsync"/> when a sweep is about to run, and 0
    /// when this is only being assembled to test a stored profile for staleness —
    /// the field is provenance, never a comparison key.
    /// </param>
    public static BenchmarkMachine Current(
        string modelsRoot, int totalStep, string voice, string language, double idleCpuPercent = 0) =>
        new(
            MachineId: MachineId(),
            Cpu: CpuName(),
            LogicalProcessors: Environment.ProcessorCount,
            ModelSet: ModelSet.Fingerprint(modelsRoot),
            TotalStep: totalStep,
            Voice: voice,
            Language: language,
            PowerState: PowerState(),
            IdleCpuPercent: idleCpuPercent);

    /// <summary>Where the models live in the portable layout.</summary>
    public static string ModelsRoot => Path.Combine(DataPaths.BaseDir, "models");

    /// <summary>
    /// A stable per-machine identifier that is not <c>MachineGuid</c> itself.
    ///
    /// <para>Hashed for the same reason systemd asks callers to hash
    /// <c>/etc/machine-id</c>: this value ends up in a JSON file inside a folder
    /// designed to be copied onto a USB stick, and it is a machine-unique
    /// identifier that other software also keys on. A keyed hash gives equality,
    /// which is the only property this needs, and gives away nothing.</para>
    ///
    /// <para>The salt is identical to the Linux side's on purpose — the same
    /// physical machine dual-booting would ideally produce the same id, and
    /// although the underlying values differ so it will not, an accidentally
    /// *different* salt would be a silent reason two platforms could never share
    /// a profile even when they should.</para>
    ///
    /// <para>Falls back to the machine name, then to a constant. The constant is
    /// the honest failure: it makes every machine look identical, so a profile
    /// travels when it should not — which is still better than re-benchmarking on
    /// every launch because the identity keeps changing.</para>
    /// </summary>
    public static string MachineId()
    {
        string? raw = ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64,
                          @"SOFTWARE\Microsoft\Cryptography", "MachineGuid")
                      ?? SafeMachineName();

        if (string.IsNullOrWhiteSpace(raw)) return "unknown-machine";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("vibesupertonic-benchmark\n" + raw.Trim()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Processor model name, for a human reading the profile.</summary>
    public static string CpuName() =>
        ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() is { Length: > 0 } name
            ? name
            : "unknown";

    /// <summary>
    /// "ac", "battery" or "unknown".
    ///
    /// <para>One call, per call, with no cache: the value is always current and
    /// there is nothing to subscribe to or fail to unsubscribe from. A desktop
    /// reports <c>ACLineStatus 1</c> and no battery, which is "ac" — the same
    /// answer the Linux side gives a machine with a mains supply that is online.</para>
    /// </summary>
    public static string PowerState()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return "unknown";
            return status.ACLineStatus switch
            {
                0 => "battery",
                1 => "ac",
                _ => "unknown",   // 255 = unknown, which is what a VM usually says
            };
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// How busy the machine is, as a percentage, sampled over
    /// <paramref name="windowMs"/>.
    ///
    /// <para><b>The guard this exists for.</b> A sweep run while a build is going
    /// picks a profile shaped by the build — and then keeps it, with a timestamp,
    /// looking every bit as authoritative as a good one.</para>
    ///
    /// <para>Returns -1 when it cannot tell, which the caller must not treat as
    /// idle: "I could not measure the load" is not "there is no load".</para>
    ///
    /// <para><b>A full second, and the Linux side paid for that number.</b> It
    /// shipped with a 300 ms window that read 15% and then 24% off a desktop
    /// measuring a steady 5–9% over 1 s, and refused two legitimate sweeps before
    /// the first one ran. A guard that fires on an idle desktop gets its override
    /// typed permanently, which is worse than not having one.</para>
    /// </summary>
    public static async Task<double> IdleCpuPercentAsync(int windowMs = 1000, CancellationToken cancellationToken = default)
    {
        var first = ReadSystemTimes();
        if (first is null) return -1;

        await Task.Delay(windowMs, cancellationToken);

        var second = ReadSystemTimes();
        if (second is null) return -1;

        double total = second.Value.Total - first.Value.Total;
        double idle = second.Value.Idle - first.Value.Idle;
        if (total <= 0) return -1;

        return Math.Clamp((total - idle) / total * 100.0, 0, 100);
    }

    /// <summary>
    /// Kernel + user + idle since boot, in 100 ns units.
    ///
    /// <para><c>GetSystemTimes</c>'s kernel figure <em>includes</em> idle — that is
    /// documented and is the trap in this API. Total is therefore kernel + user
    /// (not kernel + user + idle), and busy is total minus idle.</para>
    /// </summary>
    private static (double Total, double Idle)? ReadSystemTimes()
    {
        try
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
            double total = kernel + (double)user;
            return total > 0 ? (total, idle) : null;
        }
        catch { return null; }
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    private static string? SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

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

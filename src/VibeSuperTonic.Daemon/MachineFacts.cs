using System.Security.Cryptography;
using System.Text;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// What this machine is, for stamping onto a benchmark profile and for deciding
/// whether a stored one still describes it.
///
/// <para><b>Why this is in the daemon and not in Core.</b> Every answer here
/// comes from <c>/proc</c>, <c>/sys</c> or <c>/etc</c>, which is Linux path
/// policy — the same rule that keeps <see cref="LinuxDataPaths"/> out of Core
/// (R-12). Core takes the result as data and never learns where it came from,
/// which is also what lets the sweep be tested without a machine.</para>
///
/// <para>Nothing here throws. A profile whose CPU name is "unknown" is still a
/// usable profile; a daemon that failed to start because <c>/proc/cpuinfo</c> had
/// an unexpected shape is not — and R-5 says anything the daemon refuses to start
/// for is a hotkey that silently does nothing.</para>
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
    /// <param name="engine">
    /// Which engine this sweep measured, or would be applied to — "supertonic" or
    /// "piper". Defaults to Supertonic because that is what <c>benchmark</c> still
    /// sweeps; the parameter exists so the staleness check has something to
    /// compare, and so the day a Piper sweep lands the profile already says which
    /// it was.
    /// </param>
    public static BenchmarkMachine Current(
        string modelsRoot, int totalStep, string voice, string language,
        double idleCpuPercent = 0, string engine = "supertonic") =>
        new(
            MachineId: MachineId(),
            Cpu: CpuName(),
            LogicalProcessors: Environment.ProcessorCount,
            ModelSet: ModelSet.Fingerprint(modelsRoot),
            TotalStep: totalStep,
            Voice: voice,
            Language: language,
            PowerState: PowerState(),
            IdleCpuPercent: idleCpuPercent,
            Engine: engine);

    /// <summary>
    /// A stable per-machine identifier that is not <c>/etc/machine-id</c> itself.
    ///
    /// <para>systemd documents that value as confidential and asks callers to
    /// derive an application-specific one rather than expose it — and this one
    /// ends up in a JSON file inside a folder designed to be copied onto a USB
    /// stick. A keyed hash gives equality, which is the only property needed, and
    /// gives away nothing.</para>
    ///
    /// <para>Falls back to the hostname, then to a constant. The constant is the
    /// honest failure: it makes every machine look identical, so a profile
    /// travels when it should not — which is strictly better than a daemon that
    /// re-benchmarks on every start because its identity keeps changing.</para>
    /// </summary>
    public static string MachineId()
    {
        string? raw = ReadFirstLine("/etc/machine-id")
                      ?? ReadFirstLine("/var/lib/dbus/machine-id")
                      ?? SafeHostName();

        if (string.IsNullOrWhiteSpace(raw)) return "unknown-machine";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("vibesupertonic-benchmark\n" + raw.Trim()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Model name from <c>/proc/cpuinfo</c>, for a human reading the profile.</summary>
    public static string CpuName()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase)) continue;
                int colon = line.IndexOf(':');
                if (colon >= 0 && colon + 1 < line.Length) return line[(colon + 1)..].Trim();
            }
        }
        catch { /* an unreadable /proc is not worth a failure */ }

        return "unknown";
    }

    /// <summary>
    /// "ac", "battery" or "unknown".
    ///
    /// <para>One file read, per call, with no cache and no D-Bus: the value is
    /// always current and there is nothing to subscribe to, keep in sync, or fail
    /// to unsubscribe from. Phase 8b's battery rule reads it per utterance on the
    /// same basis.</para>
    /// </summary>
    /// <summary>
    /// "ac", "battery" or "unknown", read fresh every time it is asked.
    ///
    /// <para>Read per decision rather than watched: it is one file read and it is
    /// always current, which is why the battery rule needs no D-Bus dependency,
    /// no polling loop and no daemon of its own.</para>
    ///
    /// <para><c>VST_POWER</c> overrides it, for the same reason
    /// <c>VST_SELECTION</c> overrides the selection source: the behaviour it
    /// gates — the daemon rebuilding its ONNX session on the CPU when the power
    /// lead comes out — is otherwise only testable by walking over and unplugging
    /// a laptop, which is not a test anything can run twice.</para>
    /// </summary>
    public static string PowerState()
    {
        if (Environment.GetEnvironmentVariable("VST_POWER") is { Length: > 0 } forced)
            return forced.Trim().ToLowerInvariant();

        try
        {
            const string root = "/sys/class/power_supply";
            if (!Directory.Exists(root)) return "unknown";

            bool sawMains = false;
            foreach (string supply in Directory.EnumerateDirectories(root))
            {
                // "type" rather than a name pattern: the mains supply is AC on
                // most laptops, ADP1 on some, and "AC*" would miss the second.
                string type = ReadFirstLine(Path.Combine(supply, "type"))?.Trim() ?? "";
                if (!type.Equals("Mains", StringComparison.OrdinalIgnoreCase)) continue;

                sawMains = true;
                if (ReadFirstLine(Path.Combine(supply, "online"))?.Trim() == "1") return "ac";
            }

            // A desktop has no mains supply entry at all; only a machine that has
            // one, and reports it offline, is actually on battery.
            return sawMains ? "battery" : "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// How busy the machine is, as a percentage, sampled over
    /// <paramref name="windowMs"/>.
    ///
    /// <para><b>The guard this exists for.</b> A sweep run while a build is going
    /// picks a profile shaped by the build — and then keeps it, with a timestamp,
    /// looking every bit as authoritative as a good one. This is the same lesson
    /// Phase 4 paid for with Xephyr, where a live desktop turned a real
    /// measurement into a coin toss.</para>
    ///
    /// <para>Returns -1 when it cannot tell, which the caller must not treat as
    /// idle: "I could not measure the load" is not "there is no load".</para>
    ///
    /// <para><b>A full second, and it was 300 ms first.</b> The question being
    /// asked is whether the next forty seconds will be contended, and a third of
    /// a second of <c>/proc/stat</c> does not answer it — measured on the
    /// development desktop, 1 s windows read a steady 5–9% while 300 ms windows
    /// off the same idle machine returned 15% and then 24%, tripping the guard
    /// twice on a browser repaint. A guard that fires on an idle desktop gets
    /// <c>--force</c> typed permanently, which is worse than not having one.</para>
    /// </summary>
    public static async Task<double> IdleCpuPercentAsync(int windowMs = 1000, CancellationToken cancellationToken = default)
    {
        var first = ReadCpuTotals();
        if (first is null) return -1;

        await Task.Delay(windowMs, cancellationToken);

        var second = ReadCpuTotals();
        if (second is null) return -1;

        double total = second.Value.Total - first.Value.Total;
        double idle = second.Value.Idle - first.Value.Idle;
        if (total <= 0) return -1;

        return Math.Clamp((total - idle) / total * 100.0, 0, 100);
    }

    /// <summary>
    /// The aggregate <c>cpu</c> line of <c>/proc/stat</c>: everything, and the
    /// part of it that was doing nothing.
    /// </summary>
    private static (double Total, double Idle)? ReadCpuTotals()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("cpu ", StringComparison.Ordinal)) continue;

                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double total = 0, idle = 0;
                for (int i = 1; i < fields.Length; i++)
                {
                    if (!double.TryParse(fields[i], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out double v))
                        continue;

                    total += v;
                    // Fields 4 and 5 are idle and iowait. A machine blocked on
                    // disk is not a machine competing for the cores this sweep
                    // is trying to measure.
                    if (i is 4 or 5) idle += v;
                }

                return total > 0 ? (total, idle) : null;
            }
        }
        catch { /* fall through */ }

        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch { return null; }
    }

    private static string? SafeHostName()
    {
        try { return Environment.MachineName; }
        catch { return null; }
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Engine.Settings;

/// <summary>
/// The engine's read-only view of <c>data\benchmark.json</c> — what this machine
/// measured about itself, applied when the ONNX session is built.
///
/// <para><b>This code runs inside Balabolka, NVDA, Word and anything else with a
/// SAPI client in it</b>, which sets every constraint here. It is read at most
/// once per session build, not per utterance; it is cached on file mtime like
/// <see cref="EngineSettingsCache"/>, so the steady-state cost is one
/// <c>FileInfo</c> stat; and nothing in it throws, because a damaged cache file
/// must degrade to "no profile" rather than to a reader that cannot speak.</para>
///
/// <para><b>The staleness rules are Core's and are not re-implemented here.</b>
/// A profile measured on another machine, against other weights, or at another
/// <c>TotalStep</c> describes a machine that is not this one, and applying it
/// anyway would be a wrong thread count wearing a timestamp — the shape nobody
/// re-checks. The Control Panel shows the same reasons; both ask
/// <see cref="BenchmarkProfile.StalenessAgainst"/>.</para>
/// </summary>
internal static class BenchmarkProfileCache
{
    private static readonly object _gate = new();
    private static string? _cachedPath;
    private static DateTime _cachedMtimeUtc;
    private static BenchmarkProfile? _cached;

    /// <summary>
    /// The stored profile if it still describes this machine, else null.
    /// </summary>
    /// <param name="baseDir">Install root, for locating the models.</param>
    /// <param name="onnxDir">The ONNX directory about to be loaded.</param>
    public static BenchmarkProfile? Applicable(string baseDir, string onnxDir)
    {
        try
        {
            var profile = Load();
            if (profile is null) return null;

            // TotalStep comes from the same resolved settings the session is being
            // built for, so a user who edits it invalidates the profile without
            // anything about the machine having moved. That purely local case is
            // the one a machine check would never have caught.
            int totalStep;
            try { totalStep = EngineSettingsCache.Resolve().TotalStep; }
            catch { return null; }

            var now = new BenchmarkMachine(
                MachineId: MachineId(),
                Cpu: "",                       // provenance only, never compared
                LogicalProcessors: Environment.ProcessorCount,
                ModelSet: ModelSet.Fingerprint(Path.GetDirectoryName(onnxDir) ?? Path.Combine(baseDir, "models")),
                TotalStep: totalStep,
                Voice: "",                     // provenance only
                Language: "",                  // provenance only
                PowerState: "unknown",
                IdleCpuPercent: 0);

            return profile.StalenessAgainst(now).Count == 0 ? profile : null;
        }
        catch
        {
            // Never let a profile stop a reader from speaking. Falling back to
            // ORT's own pick is the behaviour that shipped for eight releases.
            return null;
        }
    }

    private static BenchmarkProfile? Load()
    {
        string path = DataPaths.BenchmarkFilePath;

        DateTime mtime;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            mtime = info.LastWriteTimeUtc;
        }
        catch { return null; }

        lock (_gate)
        {
            if (_cached is not null && _cachedPath == path && _cachedMtimeUtc == mtime)
                return _cached;

            var loaded = BenchmarkStore.Load(path);
            _cached = loaded;
            _cachedPath = path;
            _cachedMtimeUtc = mtime;
            return loaded;
        }
    }

    /// <summary>
    /// The same keyed hash the Control Panel stamps onto a profile.
    ///
    /// <para>Duplicated from the launcher's <c>MachineFacts</c> rather than shared,
    /// for the reason <see cref="DataPaths"/> is duplicated: the engine is a COM
    /// in-proc DLL inside arbitrary host processes, and pulling the launcher in by
    /// project reference would put a WinForms application into every one of them.
    /// The salt and the hash length are load-bearing — a profile written by the
    /// Control Panel must compare equal here, and a mismatch would present as a
    /// benchmark that silently never applies.</para>
    /// </summary>
    private static string MachineId()
    {
        string? raw = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            raw = key?.GetValue("MachineGuid") as string;
        }
        catch { /* fall through */ }

        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = Environment.MachineName; } catch { raw = null; }
        }
        if (string.IsNullOrWhiteSpace(raw)) return "unknown-machine";

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("vibesupertonic-benchmark\n" + raw.Trim()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

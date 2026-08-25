using System.Runtime.InteropServices;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The optional CUDA provider pack, and the one thing that has to happen before
/// anything else in this process if it is going to work.
///
/// <para><b>What the pack is.</b> <c>libonnxruntime_providers_cuda.so</c> — 330 MB,
/// which is why it does not ship — and the CUDA and cuDNN shared libraries it
/// needs. <c>build/install-gpu.sh</c> puts <b>all of it</b> in
/// <c>runtime/cuda/</c> under the store, and this class is what puts that
/// directory on the loader's path.</para>
///
/// <para><b>The provider used to live beside <c>libonnxruntime.so</c></b>, on the
/// reasoning that ORT builds a path to it from its own location. That is true of
/// the path ORT tries FIRST and not of the only one it tries: when the file is
/// not there it dlopens the bare name, which <c>LD_LIBRARY_PATH</c> answers. The
/// distinction stopped being academic on 2026-08-24, when an AppImage turned the
/// daemon's own directory into a read-only squashfs mount and a perfectly
/// installed 3.1 GB pack reported "no GPU available". One directory works for
/// both, and it is the one this re-exec already exists to make visible.</para>
///
/// <para><b>Why a re-exec rather than pre-loading them.</b>
/// <c>libonnxruntime_providers_cuda.so</c> carries no RPATH — checked — so its
/// seven NEEDED sonames are resolved by the loader against
/// <c>LD_LIBRARY_PATH</c>, <c>ldconfig</c>, and nothing else useful. Loading them
/// first by absolute path DOES satisfy those references, and it was the first
/// design: no environment variable, no re-exec, the daemon just opens what it
/// needs. It also aborted at exit with a corrupted heap in three runs out of
/// three, while the identical directory reached through <c>LD_LIBRARY_PATH</c> ran
/// clean in three out of three — with <c>NativeLibrary.Load</c> and again with
/// <c>dlopen(RTLD_GLOBAL | RTLD_NODELETE)</c>, so it is not about unloading.
/// Something in that stack expects to be brought in by the loader as a
/// dependency, and a daemon that dies when it exits is not a trade worth taking
/// for avoiding one <c>execv</c>.</para>
///
/// <para><b>execv, not a child process.</b> Same pid, same parent, no window in
/// which two daemons exist and race for the socket — and <c>install.sh</c> finds
/// the running daemon through <c>/proc/&lt;pid&gt;/exe</c>, which a re-exec leaves
/// pointing at exactly the same binary.</para>
/// </summary>
internal static class GpuProviderPack
{
    /// <summary>Set on the far side of the re-exec, so it can only happen once.</summary>
    private const string Guard = "VST_GPU_LDPATH";

    /// <summary>Where install-gpu.sh puts the CUDA and cuDNN libraries.</summary>
    public static string DirectoryIn(string baseDir) => Path.Combine(baseDir, "runtime", "cuda");

    /// <summary>
    /// Re-exec this process with the pack on <c>LD_LIBRARY_PATH</c> if there is a
    /// pack, the variable does not already carry it, and we have not done this
    /// once already. Returns only when there is nothing to do — or when the
    /// re-exec failed, in which case the daemon carries on without a GPU and the
    /// reason is in <paramref name="note"/>.
    /// </summary>
    public static void ReexecIfNeeded(string baseDir, Action<string> log)
    {
        string dir = DirectoryIn(baseDir);
        if (!Directory.Exists(dir)) return;

        // Already done, by us or by whoever launched us. Checked as a path
        // segment rather than a substring: "/opt/vst/runtime/cuda-old" contains
        // the one we want as a prefix and is not it.
        string existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
        if (Environment.GetEnvironmentVariable(Guard) == "1"
            || existing.Split(':').Any(p => p.Length > 0 && Path.GetFullPath(p) == Path.GetFullPath(dir)))
        {
            return;
        }

        string value = existing.Length == 0 ? dir : $"{dir}:{existing}";

        try
        {
            if (SetEnv("LD_LIBRARY_PATH", value, overwrite: 1) != 0)
                throw new InvalidOperationException("setenv LD_LIBRARY_PATH failed");
            if (SetEnv(Guard, "1", overwrite: 1) != 0)
                throw new InvalidOperationException($"setenv {Guard} failed");

            string[] argv = Environment.GetCommandLineArgs();
            log($"gpu: provider pack found in {dir} — re-exec with it on the library path");

            // /proc/self/exe rather than argv[0]: it is what this process actually
            // is, regardless of how it was invoked or what PATH lookup found it.
            Exec("/proc/self/exe", [.. argv, null!]);

            // execv only returns on failure.
            log($"gpu: re-exec failed ({Marshal.GetLastPInvokeErrorMessage()}); " +
                "continuing on the CPU. Start the daemon with " +
                $"LD_LIBRARY_PATH={dir} to use the GPU.");
        }
        catch (Exception ex)
        {
            log($"gpu: could not put the provider pack on the library path " +
                $"({ex.GetType().Name}: {ex.Message}); continuing on the CPU.");
        }
    }

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "execv", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Exec(string path, string?[] argv);
}

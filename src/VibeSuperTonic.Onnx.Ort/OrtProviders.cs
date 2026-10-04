using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Onnx.Ort;

/// <summary>
/// The one place that knows how each <see cref="ExecutionProviders"/> name is
/// asked of ONNX Runtime, and how to find out whether it can be.
///
/// <para>Before this existed the CUDA call was written out in three places (the
/// SDK, the Piper session, the startup probe) and a second vendor would have
/// made it six. Each case here is exactly the call it replaced.</para>
///
/// <para><b>Nothing here falls back.</b> A provider that cannot be appended
/// throws, and the caller — which has the log — decides to land on the CPU and
/// says why. A GPU path that fails quietly is the shape of the bug this
/// product has already paid for once.</para>
/// </summary>
public static class OrtProviders
{
    /// <summary>
    /// Ask <paramref name="options"/> to run on <paramref name="provider"/>.
    /// The CPU needs no call (it is ORT's default), so it appends nothing.
    /// </summary>
    public static void Append(SessionOptions options, string provider, int device = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (provider)
        {
            case ExecutionProviders.Cpu:
                return;
            case ExecutionProviders.Cuda:
                options.AppendExecutionProvider_CUDA(device);
                return;
            case ExecutionProviders.OpenVino:
                options.AppendExecutionProvider("OpenVINO", OpenVinoOptions(device));
                return;
            default:
                throw new ArgumentException($"unknown execution provider '{provider}'", nameof(provider));
        }
    }

    /// <summary>
    /// Whether <paramref name="provider"/> can be initialised on this machine —
    /// null when it can, and one sentence on why not otherwise.
    ///
    /// <para>Asked by appending the provider to a throwaway
    /// <c>SessionOptions</c>, never by looking at files or hardware: every failure
    /// this must detect lives inside that call. For OpenVINO that includes
    /// "there is no GPU device" — measured: <c>device_type=GPU</c> on a machine
    /// without one is refused at this point, not at session creation, with
    /// ORT's unhelpful "Failed to load provider OpenVINO"; the remedy is
    /// appended to that. Call it once at startup.</para>
    /// </summary>
    public static string? Probe(string provider)
    {
        if (provider == ExecutionProviders.Cpu) return null;
        try
        {
            using var probe = new SessionOptions();
            Append(probe, provider);
            return null;
        }
        catch (Exception ex)
        {
            // EntryPointNotFoundException when the runtime was built without the
            // provider at all, OnnxRuntimeException when its library or its
            // dependencies cannot be loaded or no device exists. One sentence.
            string reason = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
            if (provider == ExecutionProviders.OpenVino)
            {
                // Two different states read very differently to a person. "Not
                // supported in this build" is the shipped runtime with no pack
                // loaded; "Failed to load provider" is the pack's runtime saying
                // the device (the GPU) is not there.
                reason += reason.Contains("not supported in this build", StringComparison.Ordinal)
                    ? " (the OpenVINO pack is not in use; install-openvino.sh adds it)"
                    : " (OpenVINO found no usable Intel GPU: it needs the distribution's " +
                      "OpenCL / Level Zero compute runtime, e.g. intel-opencl-icd)";
            }
            return reason;
        }
    }

    private static Dictionary<string, string> OpenVinoOptions(int device)
    {
        string deviceType = GpuPacks.OpenVinoDeviceType(
            device, Environment.GetEnvironmentVariable("VST_OPENVINO_DEVICE"), out string? ignored);
        if (ignored is not null) Console.Error.WriteLine($"openvino: {ignored}");
        return new Dictionary<string, string> { ["device_type"] = deviceType };
    }

    private static int _runtimeSet;

    /// <summary>
    /// Make this process load <c>libonnxruntime.so</c> from
    /// <paramref name="packDirectory"/> instead of from beside the daemon. For
    /// the packs whose provider is not built into the library the archive ships
    /// (<see cref="GpuPack.ReplacesRuntime"/>).
    ///
    /// <para>A resolver, not <c>LD_LIBRARY_PATH</c>: .NET looks in the
    /// application directory before it asks the loader, so the shipped library
    /// would always win. Measured with the OpenVINO pack: with the resolver the
    /// provider and the OpenVINO libraries are found beside the library that was
    /// loaded (their RPATH is <c>$ORIGIN</c>), so nothing needs re-exec'ing.</para>
    ///
    /// <para>Must run before the first ONNX Runtime call in the process. If the
    /// pack's library cannot be loaded the resolver declines and .NET proceeds
    /// with the shipped one — the provider probe then fails with a sentence and
    /// the daemon lands on the CPU, which is the safe direction.</para>
    /// </summary>
    /// <returns>Null on success, or why the pack's runtime is not in use.</returns>
    public static string? UseRuntimeFrom(string packDirectory)
    {
        string lib = Path.Combine(packDirectory, "libonnxruntime.so");
        if (!File.Exists(lib)) return $"{lib} does not exist";
        if (Interlocked.Exchange(ref _runtimeSet, 1) == 1) return "an ONNX Runtime resolver is already installed";

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, (name, _, _) =>
                name == "onnxruntime" && NativeLibrary.TryLoad(lib, out IntPtr handle) ? handle : IntPtr.Zero);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}

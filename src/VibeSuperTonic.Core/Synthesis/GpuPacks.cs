using System.Globalization;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// One optional GPU provider pack: which provider it enables, where it lives
/// under the store, and what installs it.
///
/// <para>A pack is never in the archive. Each is fetched on request by a script
/// that ships in the archive, and each lives in its own directory under
/// <c>runtime/</c> so that removing one is <c>rm -rf</c> of one directory.</para>
/// </summary>
/// <param name="Provider">The <see cref="ExecutionProviders"/> name the pack enables.</param>
/// <param name="DirectoryName">Subdirectory of <c>runtime/</c> the installer fills.</param>
/// <param name="Vendor">Whose hardware it is for, as a person would say it.</param>
/// <param name="PciVendorId">The PCI vendor id <c>/sys/class/drm</c> reports for that hardware.</param>
/// <param name="Installer">The script in the archive that fetches it.</param>
/// <param name="ReplacesRuntime">
/// True when the pack carries its own <c>libonnxruntime.so</c> that must be loaded
/// INSTEAD of the one the archive ships. The CUDA provider plugs into the shipped
/// library; the OpenVINO execution provider is not built into it (measured:
/// "OpenVINO execution provider is not supported in this build" with the provider
/// library sitting beside it), so that pack brings a matching runtime.
/// </param>
public sealed record GpuPack(
    string Provider,
    string DirectoryName,
    string Vendor,
    int PciVendorId,
    string Installer,
    bool ReplacesRuntime);

/// <summary>
/// The known packs, which one is installed, and — separately, and only ever as
/// wording — what graphics hardware the machine appears to have.
///
/// <para><b>Hardware detection is a hint and never a gate.</b> What decides
/// whether a GPU is used is whether the provider initialises (the daemon's
/// startup probe) and whether the benchmark found it faster. A PCI vendor id
/// says neither: a hybrid laptop reports two vendors, a VM reports none that
/// matter, and an NVIDIA card with no driver reports NVIDIA. So this feeds the
/// sentence that tells a person which installer to run, and nothing else; the
/// CPU path never depends on it.</para>
/// </summary>
public static class GpuPacks
{
    public const int PciNvidia = 0x10de;
    public const int PciAmd = 0x1002;
    public const int PciIntel = 0x8086;

    /// <summary>
    /// In precedence order: when more than one pack directory exists the first
    /// wins, because a process loads one <c>libonnxruntime.so</c> and the CUDA
    /// pack is the one that plugs into the library that ships.
    /// </summary>
    public static IReadOnlyList<GpuPack> All { get; } =
    [
        new(ExecutionProviders.Cuda, "cuda", "NVIDIA", PciNvidia, "install-gpu.sh", ReplacesRuntime: false),
        new(ExecutionProviders.OpenVino, "openvino", "Intel", PciIntel, "install-openvino.sh", ReplacesRuntime: true),
    ];

    /// <summary>The pack for a provider name, or null for the CPU and for anything unknown.</summary>
    public static GpuPack? For(string provider) =>
        All.FirstOrDefault(p => p.Provider == provider);

    /// <summary>
    /// The installed pack: the first in <see cref="All"/> whose directory exists.
    /// <paramref name="directoryExists"/> is given the <c>runtime/</c>-relative
    /// directory name, so the rule is testable without a filesystem.
    /// </summary>
    public static GpuPack? Select(Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        return All.FirstOrDefault(p => directoryExists(p.DirectoryName));
    }

    // ------------------------------------------------------------ hardware hint

    private static readonly Regex CardName = new(@"^card\d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a <c>/sys/class/drm/cardN/device/vendor</c> file ("0x8086\n").
    /// Null for anything that is not a hex id, rather than 0, so a garbled file
    /// cannot be mistaken for a vendor.
    /// </summary>
    public static int? ParsePciVendorId(string? text)
    {
        if (text is null) return null;
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return t.Length is >= 1 and <= 4
               && int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int id)
            ? id
            : null;
    }

    /// <summary>
    /// PCI vendor ids of the display adapters under <paramref name="drmRoot"/>
    /// (<c>/sys/class/drm</c>). Never throws: a container with no sysfs, a
    /// permission error or a vanished device is "no hint", which is the correct
    /// outcome for something that is only ever a hint.
    /// </summary>
    public static IReadOnlyList<int> DetectVendorIds(string drmRoot = "/sys/class/drm")
    {
        var ids = new List<int>();
        try
        {
            if (!Directory.Exists(drmRoot)) return ids;
            foreach (string dir in Directory.EnumerateDirectories(drmRoot).Order(StringComparer.Ordinal))
            {
                if (!CardName.IsMatch(Path.GetFileName(dir))) continue;
                string file = Path.Combine(dir, "device", "vendor");
                if (!File.Exists(file)) continue;
                if (ParsePciVendorId(File.ReadAllText(file)) is { } id && !ids.Contains(id)) ids.Add(id);
            }
        }
        catch (Exception)
        {
            // A hint is not worth a stack trace.
        }

        return ids;
    }

    /// <summary>
    /// The one sentence that tells a person with no pack installed what, if
    /// anything, to install for the graphics hardware that is present. Null when
    /// a pack is already installed (the probe's own message says what is wrong
    /// then) or when no recognised vendor is present.
    ///
    /// <para>AMD is named and answered honestly: there is no AMD pack, and
    /// saying "run the installer" for a pack that does not exist would be worse
    /// than saying nothing.</para>
    /// </summary>
    public static string? InstallHint(IReadOnlyCollection<int> vendorIds, GpuPack? installed)
    {
        ArgumentNullException.ThrowIfNull(vendorIds);
        if (installed is not null) return null;

        var parts = new List<string>();
        foreach (var pack in All)
        {
            if (vendorIds.Contains(pack.PciVendorId))
                parts.Add($"{pack.Vendor} graphics detected — {pack.Installer} adds GPU support");
        }

        if (vendorIds.Contains(PciAmd))
            parts.Add("AMD graphics detected — there is no AMD GPU pack yet, so this runs on the CPU");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // ----------------------------------------------------------- OpenVINO device

    private static readonly Regex OpenVinoDevice = new(
        @"^(CPU|GPU|NPU|GPU\.\d+|(AUTO|HETERO|MULTI):(CPU|GPU|NPU|GPU\.\d+)(,(CPU|GPU|NPU|GPU\.\d+))*)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The OpenVINO <c>device_type</c> to ask for. Default is the GPU (device 0
    /// is plain <c>GPU</c>, the rest <c>GPU.n</c>), because the point of the
    /// pack is the GPU; <paramref name="overrideValue"/> (the
    /// <c>VST_OPENVINO_DEVICE</c> environment variable) can select an NPU or a
    /// specific adapter. An override that is not a device string OpenVINO would
    /// accept is ignored in favour of the default and reported through
    /// <paramref name="ignored"/>, rather than passed to the provider to fail
    /// with a message about a configuration key.
    /// </summary>
    public static string OpenVinoDeviceType(int ordinal, string? overrideValue, out string? ignored)
    {
        ignored = null;
        string fallback = ordinal <= 0 ? "GPU" : $"GPU.{ordinal}";
        if (string.IsNullOrWhiteSpace(overrideValue)) return fallback;

        string v = overrideValue.Trim();
        if (OpenVinoDevice.IsMatch(v)) return v;

        ignored = $"VST_OPENVINO_DEVICE='{v}' is not a device OpenVINO accepts (GPU, GPU.1, NPU, AUTO:GPU,CPU ...); using {fallback}";
        return fallback;
    }
}

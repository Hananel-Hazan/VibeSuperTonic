using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// "gpu" is whichever vendor's pack is installed, not a synonym for CUDA. These
/// pin the pure rules behind that: which pack wins, what a provider name means
/// to the decision, and how a hardware hint is worded — a hint that must never
/// become a gate.
/// </summary>
public class GpuPackTests
{
    private static BenchmarkMachine Machine() =>
        new("machine-a", "Test CPU", 20, "models-a", 8, "M1", "en", "ac", 2.0);

    private static BenchmarkProfile Profile(string gpuProvider) =>
        new(4, gpuProvider, "2026-10-03T10:00:00.0000000Z", Machine(),
            [
                new BenchmarkRow("2", 2, "cpu", 5600, 0.197, 2.1, 11),
                new BenchmarkRow("4", 4, "cpu", 5080, 0.179, 4.3, 22),
                new BenchmarkRow($"{gpuProvider} (4)", 4, gpuProvider, 900, 0.03, 1.2, 6),
            ],
            BenchmarkSweep.NotVaried, SampleSeconds: 5.0);

    // ------------------------------------------------------------ provider names

    [Fact]
    public void OpenVino_is_a_known_GPU_provider_and_the_CPU_is_not_a_GPU()
    {
        Assert.True(ExecutionProviders.IsKnown(ExecutionProviders.OpenVino));
        Assert.True(ExecutionProviders.IsGpu(ExecutionProviders.OpenVino));
        Assert.True(ExecutionProviders.IsGpu(ExecutionProviders.Cuda));
        Assert.False(ExecutionProviders.IsGpu(ExecutionProviders.Cpu));
        Assert.False(ExecutionProviders.IsGpu("directml"));
        Assert.Equal("OpenVINO", ExecutionProviders.Display(ExecutionProviders.OpenVino));
    }

    // -------------------------------------------------------------- the decision

    [Fact]
    public void The_gpu_preference_means_the_installed_vendors_provider()
    {
        var onOpenVino = ExecutionDecision.Decide(
            null, Machine(), 20, 20, ProviderPreference.Gpu,
            gpuProvider: ExecutionProviders.OpenVino);
        var onDefault = ExecutionDecision.Decide(null, Machine(), 20, 20, ProviderPreference.Gpu);

        Assert.Equal(ExecutionProviders.OpenVino, onOpenVino.Provider);
        Assert.Equal(ExecutionProviders.Cuda, onDefault.Provider);
    }

    [Fact]
    public void A_benchmark_won_by_OpenVino_applies_when_OpenVino_is_the_installed_pack()
    {
        var d = ExecutionDecision.Decide(
            Profile(ExecutionProviders.OpenVino), Machine(), 20, 20,
            gpuProvider: ExecutionProviders.OpenVino);

        Assert.Equal(ExecutionProviders.OpenVino, d.Provider);
        Assert.Contains("OpenVINO", d.Describe());
    }

    [Fact]
    public void A_benchmark_from_the_other_vendors_pack_does_not_put_the_GPU_back()
    {
        // CUDA removed and OpenVINO installed (or a disk moved between machines):
        // the stored winner is a provider this daemon cannot run. It must land on
        // the CPU and say why, not try CUDA and fail on every press.
        var d = ExecutionDecision.Decide(
            Profile(ExecutionProviders.Cuda), Machine(), 20, 20,
            gpuProvider: ExecutionProviders.OpenVino);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.Contains("benchmark again", d.Reason);
        Assert.Contains("CUDA", d.Reason);
        Assert.Contains("OpenVINO", d.Reason);
        Assert.True(d.FromProfile);          // the CPU rows were measured; use them
    }

    [Fact]
    public void An_unavailable_OpenVino_falls_to_the_CPU_exactly_as_CUDA_does()
    {
        var d = ExecutionDecision.Decide(
            Profile(ExecutionProviders.OpenVino), Machine(), 20, 20,
            gpuUnavailable: "no Intel GPU", gpuProvider: ExecutionProviders.OpenVino);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.Contains("no Intel GPU", d.Reason);
    }

    [Fact]
    public void The_battery_rule_applies_to_every_GPU_vendor()
    {
        var d = ExecutionDecision.Decide(
            Profile(ExecutionProviders.OpenVino), Machine(), 20, 20,
            powerState: PowerStates.Battery, gpuProvider: ExecutionProviders.OpenVino);

        Assert.Equal(ExecutionProviders.Cpu, d.Provider);
        Assert.Contains("on battery", d.Reason);

        var kept = ExecutionDecision.Decide(
            Profile(ExecutionProviders.OpenVino), Machine(), 20, 20,
            powerState: PowerStates.Battery, gpuOnBattery: true,
            gpuProvider: ExecutionProviders.OpenVino);
        Assert.Equal(ExecutionProviders.OpenVino, kept.Provider);
    }

    [Fact]
    public void A_non_GPU_name_for_the_pack_is_treated_as_the_original_meaning_of_gpu()
    {
        var d = ExecutionDecision.Decide(null, Machine(), 20, 20, ProviderPreference.Gpu, gpuProvider: "cpu");
        Assert.Equal(ExecutionProviders.Cuda, d.Provider);
    }

    // ------------------------------------------------------------- pack selection

    [Fact]
    public void No_pack_directory_means_no_pack()
    {
        Assert.Null(GpuPacks.Select(_ => false));
    }

    [Theory]
    [InlineData("cuda", ExecutionProviders.Cuda)]
    [InlineData("openvino", ExecutionProviders.OpenVino)]
    public void A_pack_is_found_by_its_directory(string dir, string provider)
    {
        Assert.Equal(provider, GpuPacks.Select(d => d == dir)!.Provider);
    }

    [Fact]
    public void CUDA_wins_when_both_packs_are_present()
    {
        // One libonnxruntime.so per process, and CUDA is the pack that plugs into
        // the library that ships; the other would have to replace it.
        Assert.Equal(ExecutionProviders.Cuda, GpuPacks.Select(_ => true)!.Provider);
    }

    [Fact]
    public void Only_the_OpenVino_pack_replaces_the_runtime()
    {
        Assert.False(GpuPacks.For(ExecutionProviders.Cuda)!.ReplacesRuntime);
        Assert.True(GpuPacks.For(ExecutionProviders.OpenVino)!.ReplacesRuntime);
        Assert.Null(GpuPacks.For(ExecutionProviders.Cpu));
    }

    [Fact]
    public void Every_pack_has_its_own_directory_and_installer()
    {
        Assert.Equal(GpuPacks.All.Count, GpuPacks.All.Select(p => p.DirectoryName).Distinct().Count());
        Assert.Equal(GpuPacks.All.Count, GpuPacks.All.Select(p => p.Provider).Distinct().Count());
        Assert.All(GpuPacks.All, p => Assert.EndsWith(".sh", p.Installer));
    }

    // ----------------------------------------------------------- hardware hint

    [Theory]
    [InlineData("0x8086\n", 0x8086)]
    [InlineData("0x10DE", 0x10de)]
    [InlineData("1002", 0x1002)]
    [InlineData("  0x1002  \n", 0x1002)]
    public void A_vendor_file_is_parsed(string text, int expected)
    {
        Assert.Equal(expected, GpuPacks.ParsePciVendorId(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("nvidia")]
    [InlineData("0x123456789")]
    public void Garbage_is_no_vendor_rather_than_vendor_zero(string? text)
    {
        Assert.Null(GpuPacks.ParsePciVendorId(text));
    }

    [Fact]
    public void Intel_hardware_with_no_pack_points_at_the_OpenVino_installer()
    {
        string? hint = GpuPacks.InstallHint([GpuPacks.PciIntel], installed: null);
        Assert.NotNull(hint);
        Assert.Contains("install-openvino.sh", hint);
        Assert.DoesNotContain("install-gpu.sh", hint);
    }

    [Fact]
    public void NVIDIA_hardware_with_no_pack_points_at_the_CUDA_installer()
    {
        Assert.Contains("install-gpu.sh", GpuPacks.InstallHint([GpuPacks.PciNvidia], null));
    }

    [Fact]
    public void A_hybrid_laptop_hears_about_both()
    {
        string hint = GpuPacks.InstallHint([GpuPacks.PciIntel, GpuPacks.PciNvidia], null)!;
        Assert.Contains("install-openvino.sh", hint);
        Assert.Contains("install-gpu.sh", hint);
    }

    [Fact]
    public void AMD_hardware_is_told_the_truth_not_an_installer_that_does_not_exist()
    {
        string hint = GpuPacks.InstallHint([GpuPacks.PciAmd], null)!;
        Assert.Contains("AMD", hint);
        Assert.Contains("no AMD GPU pack", hint);
        Assert.DoesNotContain(".sh", hint);
    }

    [Fact]
    public void An_installed_pack_or_unknown_hardware_gets_no_hint()
    {
        Assert.Null(GpuPacks.InstallHint([GpuPacks.PciIntel], GpuPacks.For(ExecutionProviders.OpenVino)));
        Assert.Null(GpuPacks.InstallHint([0x1af4], null));      // virtio, a VM
        Assert.Null(GpuPacks.InstallHint([], null));
    }

    [Fact]
    public void Detection_reads_card_directories_only_and_survives_a_missing_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "vst-drm-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Empty(GpuPacks.DetectVendorIds(root));         // no sysfs at all

            Write(root, "card0", "0x8086\n");
            Write(root, "card1", "0x10de\n");
            Write(root, "card2", "0x8086\n");                      // same vendor twice
            Write(root, "card0-HDMI-A-1", "0x1002\n");             // a connector, not an adapter
            Write(root, "renderD128", "0x1002\n");                 // a render node, not a card
            Directory.CreateDirectory(Path.Combine(root, "card3")); // no device/vendor file

            Assert.Equal([0x8086, 0x10de], GpuPacks.DetectVendorIds(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string card, string vendor)
    {
        string dir = Path.Combine(root, card, "device");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "vendor"), vendor);
    }

    // ----------------------------------------------------------- OpenVINO device

    [Theory]
    [InlineData(0, null, "GPU")]
    [InlineData(1, null, "GPU.1")]
    [InlineData(0, "", "GPU")]
    [InlineData(0, "NPU", "NPU")]
    [InlineData(0, "GPU.1", "GPU.1")]
    [InlineData(0, "AUTO:GPU,CPU", "AUTO:GPU,CPU")]
    [InlineData(0, " HETERO:GPU,CPU ", "HETERO:GPU,CPU")]
    public void The_OpenVino_device_defaults_to_the_GPU_and_accepts_real_device_strings(
        int ordinal, string? over, string expected)
    {
        Assert.Equal(expected, GpuPacks.OpenVinoDeviceType(ordinal, over, out string? ignored));
        Assert.Null(ignored);
    }

    [Theory]
    [InlineData("gpu; rm -rf /")]
    [InlineData("TPU")]
    [InlineData("AUTO:")]
    [InlineData("GPU.")]
    public void A_bad_device_override_is_ignored_and_reported_not_passed_on(string over)
    {
        Assert.Equal("GPU", GpuPacks.OpenVinoDeviceType(0, over, out string? ignored));
        Assert.NotNull(ignored);
        Assert.Contains("VST_OPENVINO_DEVICE", ignored);
    }
}

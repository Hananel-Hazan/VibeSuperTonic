using System.Text.Json;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// <c>benchmark.json</c> — the file two platforms write and three processes read.
///
/// <para><b>Why a file format gets tests.</b> The Linux daemon writes this, the
/// Windows Control Panel writes this, the Windows engine reads it inside every
/// SAPI host, and the same portable folder can be carried between them on a USB
/// stick. Nothing else in the product has that many readers, and every failure
/// mode here is silent: a field that stops round-tripping does not throw, it
/// arrives as a default — which for a thread count means "auto", which is the
/// configuration this whole feature exists to stop choosing by accident.</para>
/// </summary>
public class BenchmarkStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "vst-store-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, BenchmarkStore.FileName);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static BenchmarkProfile Profile() => new(
        Threads: 2,
        Provider: "cpu",
        MeasuredUtc: "2026-08-19T10:00:00.0000000Z",
        Machine: new BenchmarkMachine("machine-a", "Test CPU", 20, "models-a", 8, "M1", "en", "ac", 4.5),
        Table:
        [
            new BenchmarkRow("2", 2, "cpu", 1194, 0.206, 2.2, 2.6, Spread: 0.08),
            new BenchmarkRow("8", 8, "cpu", 3060, 0.529, 8.4, 25.8, Spread: 0.11),
            new BenchmarkRow("DirectML", 0, "directml", 0, 0, 0, 0, Spread: 0, Failed: true, Error: "no DX12 device"),
        ],
        NotVaried: BenchmarkSweep.NotVaried,
        SampleSeconds: 5.8,
        TieBand: 0.15);

    [Fact]
    public void A_profile_survives_the_round_trip_field_for_field()
    {
        Assert.True(BenchmarkStore.TrySave(Path_, Profile(), out string? error));
        Assert.Null(error);

        var read = BenchmarkStore.Load(Path_);

        Assert.NotNull(read);
        Assert.Equal(2, read!.Threads);
        Assert.Equal("cpu", read.Provider);
        Assert.Equal(0.15, read.TieBand);
        Assert.Equal(5.8, read.SampleSeconds);
        Assert.Equal(3, read.Table.Count);
        Assert.Equal(20, read.Machine.LogicalProcessors);
        Assert.Equal(8, read.Machine.TotalStep);
    }

    [Fact]
    public void The_spread_survives_because_the_number_it_guards_depends_on_it()
    {
        BenchmarkStore.TrySave(Path_, Profile(), out _);
        var read = BenchmarkStore.Load(Path_)!;

        Assert.Equal(0.08, read.Table[0].Spread, 4);
        Assert.Equal(0.11, read.MaxSpread, 4);
        Assert.True(read.BandClearsNoise);
    }

    [Fact]
    public void A_failed_row_survives_with_its_reason()
    {
        // A gap in the table reads as "not tried". The reason a GPU row failed is
        // the most interesting line in the file on a machine without a GPU.
        BenchmarkStore.TrySave(Path_, Profile(), out _);
        var read = BenchmarkStore.Load(Path_)!;

        var failed = Assert.Single(read.Table, r => r.Failed);
        Assert.Equal("directml", failed.Provider);
        Assert.Contains("DX12", failed.Error);
    }

    [Fact]
    public void A_profile_written_before_the_spread_existed_still_loads()
    {
        // The cross-version guard, and the one that matters most: a user upgrades,
        // their existing profile is missing two fields, and the alternative to
        // defaulting them is a thread count that silently reverts to auto.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            {
              "Threads": 4,
              "Provider": "cpu",
              "MeasuredUtc": "2026-08-16T09:00:00.0000000Z",
              "Machine": {
                "MachineId": "machine-a", "Cpu": "Old CPU", "LogicalProcessors": 20,
                "ModelSet": "models-a", "TotalStep": 8, "Voice": "M1", "Language": "en",
                "PowerState": "ac", "IdleCpuPercent": 3
              },
              "Table": [
                { "Label": "4", "Threads": 4, "Provider": "cpu", "MedianWallMs": 1204,
                  "Rtf": 0.208, "AvgCores": 4.4, "CoreSeconds": 5.2 }
              ],
              "NotVaried": [ "inter-op threads (held at 1)" ],
              "SampleSeconds": 5.8
            }
            """);

        var read = BenchmarkStore.Load(Path_);

        Assert.NotNull(read);
        Assert.Equal(4, read!.Threads);
        Assert.Equal(0, read.TieBand);
        Assert.Equal(0, read.Table[0].Spread);
        // Nothing to contradict, so it does not accuse itself of measuring noise.
        Assert.True(read.BandClearsNoise);
    }

    [Fact]
    public void A_corrupt_profile_reads_as_no_profile_rather_than_throwing()
    {
        // This is loaded inside arbitrary SAPI hosts. A damaged cache file must
        // cost the user their measured thread count, never their ability to speak.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        Assert.Null(BenchmarkStore.Load(Path_));
    }

    [Fact]
    public void A_missing_profile_reads_as_no_profile()
    {
        Assert.Null(BenchmarkStore.Load(Path_));
    }

    [Fact]
    public void Saving_creates_the_directory_it_was_pointed_at()
    {
        // A fresh portable install has no data folder until something writes one,
        // and the sweep can legitimately be the first thing that does.
        Assert.False(Directory.Exists(_dir));
        Assert.True(BenchmarkStore.TrySave(Path_, Profile(), out _));
        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void A_failed_save_reports_why_and_does_not_throw()
    {
        // A read-only install still gets a correct answer out of a sweep; it just
        // cannot keep it. The caller says so rather than the sweep dying at the end
        // of a run the user waited minutes for.
        string path = Path.Combine(_dir, "not-a-directory", "nested", BenchmarkStore.FileName);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "not-a-directory"), "I am a file");

        Assert.False(BenchmarkStore.TrySave(path, Profile(), out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void No_temp_file_is_left_behind_by_a_failed_save()
    {
        string path = Path.Combine(_dir, "not-a-directory", BenchmarkStore.FileName);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "not-a-directory"), "I am a file");

        BenchmarkStore.TrySave(path, Profile(), out _);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void The_file_is_written_indented_because_it_is_meant_to_be_argued_with()
    {
        // The whole reason the losing rows are kept is so the winning number can be
        // checked rather than trusted, and a single-line JSON blob is not something
        // anyone checks.
        BenchmarkStore.TrySave(Path_, Profile(), out _);

        Assert.Contains("\n", File.ReadAllText(Path_));
    }

    [Fact]
    public void An_overwrite_replaces_the_previous_profile_rather_than_appending_to_it()
    {
        BenchmarkStore.TrySave(Path_, Profile(), out _);
        BenchmarkStore.TrySave(Path_, Profile() with { Threads = 6 }, out _);

        Assert.Equal(6, BenchmarkStore.Load(Path_)!.Threads);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}

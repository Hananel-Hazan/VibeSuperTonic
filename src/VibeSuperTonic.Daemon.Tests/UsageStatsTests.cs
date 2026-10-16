using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The measurements <c>vst-autotune.sh</c> routes on. Worth pinning because the
/// script cannot tell a wrong number from a right one — it will faithfully route
/// to whatever this file says is faster.
/// </summary>
public class UsageStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"vst-usage-{Guid.NewGuid():N}");

    public UsageStatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_ => Path.Combine(_dir, "usage-stats.json");

    [Fact]
    public void TheMedianIsTheMiddleSampleNotTheMean()
    {
        var p = new ProviderUsage { Provider = "cpu" };
        // A mean would be dragged to 2.06 by the outlier; a median should not
        // care. This is the whole reason the median is what gets written: one
        // render that collided with a compile must not decide the routing.
        foreach (double rtf in new[] { 0.20, 0.21, 0.22, 0.23, 10.0 })
            p.Add(rtf, threads: 2, nowUtc: "now");

        Assert.Equal(0.22, p.MedianRtf, precision: 6);
        Assert.Equal(5, p.Count);
    }

    [Fact]
    public void AnEvenNumberOfSamplesAveragesTheTwoInTheMiddle()
    {
        var p = new ProviderUsage { Provider = "cpu" };
        foreach (double rtf in new[] { 0.10, 0.20, 0.30, 0.40 })
            p.Add(rtf, threads: 2, nowUtc: "now");

        Assert.Equal(0.25, p.MedianRtf, precision: 6);
    }

    [Fact]
    public void OnlyTheRecentWindowIsKeptButTheLifetimeCountIsNot()
    {
        var p = new ProviderUsage { Provider = "cuda" };
        for (int i = 0; i < UsageStats.Window + 40; i++)
            p.Add(0.02, threads: 2, nowUtc: "now");

        // The window is what a fixed GPU is judged on; the count is what tells a
        // reader that "12 samples" is 12 out of thousands.
        Assert.Equal(UsageStats.Window, p.Recent.Count);
        Assert.Equal(UsageStats.Window + 40, p.Count);
    }

    [Fact]
    public void ARecoveredProviderOutvotesItsOwnHistoryOnceTheWindowTurnsOver()
    {
        var p = new ProviderUsage { Provider = "cuda" };
        for (int i = 0; i < UsageStats.Window; i++) p.Add(5.0, 2, "then");   // broken and slow
        for (int i = 0; i < UsageStats.Window; i++) p.Add(0.02, 2, "now");   // fixed

        // Without the window this median would still be 2.51 and the GPU would
        // stay switched off for as long as the file survived.
        Assert.Equal(0.02, p.MedianRtf, precision: 6);
    }

    [Fact]
    public void ARenderThatProducedNoAudioIsNotAMeasurement()
    {
        var recorder = new UsageRecorder(Path_, _ => { });
        recorder.Add("cpu", 2, 0);
        recorder.Add("cpu", 2, double.NaN);
        recorder.Add("cpu", 2, double.PositiveInfinity);
        recorder.Add("cpu", 2, -1);
        recorder.Flush();

        // A zero would read as "infinitely fast" and win every comparison
        // forever, so these are dropped rather than clamped.
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void SamplesSurviveARestart()
    {
        var first = new UsageRecorder(Path_, _ => { });
        for (int i = 0; i < 6; i++) first.Add("cpu", 2, 0.20);
        first.Flush();

        var second = new UsageRecorder(Path_, _ => { });
        second.Add("cuda", 2, 0.02);
        second.Flush();

        string json = File.ReadAllText(Path_);
        Assert.Contains("\"cpu\"", json);
        Assert.Contains("\"cuda\"", json);
    }

    [Fact]
    public void AnUnwritableDestinationIsReportedOnceAndNeverAgain()
    {
        var complaints = new List<string>();
        // A directory where the file should be: every write fails, permanently.
        string blocked = Path.Combine(_dir, "blocked.json");
        Directory.CreateDirectory(blocked);

        var recorder = new UsageRecorder(blocked, complaints.Add);
        for (int i = 0; i < 40; i++) recorder.Add("cpu", 2, 0.2);

        // Once. A read-only install must not narrate this on every render for
        // the life of the process.
        Assert.Single(complaints);
        Assert.Contains("provider timings will not be kept", complaints[0]);
    }
}

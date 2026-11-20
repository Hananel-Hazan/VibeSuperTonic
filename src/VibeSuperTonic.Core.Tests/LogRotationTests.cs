using Xunit;
using VibeSuperTonic.Core.Diagnostics;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// LogRotation keeps exactly one rolled generation (log + log.1) and must never throw.
/// </summary>
public sealed class LogRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vst-logrot-" + Guid.NewGuid().ToString("N"));

    public LogRotationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Log => Path.Combine(_dir, "engine.log");

    [Fact]
    public void Below_the_limit_nothing_moves()
    {
        File.WriteAllText(Log, new string('a', 99));
        LogRotation.RollIfNeeded(Log, 100);
        Assert.True(File.Exists(Log));
        Assert.False(File.Exists(Log + ".1"));
    }

    [Fact]
    public void At_the_limit_the_log_is_rolled_aside()
    {
        File.WriteAllText(Log, new string('a', 100));
        LogRotation.RollIfNeeded(Log, 100);
        Assert.False(File.Exists(Log));
        Assert.Equal(100, new FileInfo(Log + ".1").Length);
    }

    [Fact]
    public void Only_one_rolled_generation_is_kept()
    {
        File.WriteAllText(Log, new string('a', 100));
        LogRotation.RollIfNeeded(Log, 100);
        File.WriteAllText(Log, new string('b', 100));
        LogRotation.RollIfNeeded(Log, 100);
        File.WriteAllText(Log, new string('c', 100));
        LogRotation.RollIfNeeded(Log, 100);

        Assert.Equal(new[] { "engine.log.1" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
        Assert.Equal(new string('c', 100), File.ReadAllText(Log + ".1"));
    }

    [Fact]
    public void A_missing_log_directory_does_not_throw_or_create_anything()
    {
        string missing = Path.Combine(_dir, "nope", "engine.log");
        LogRotation.RollIfNeeded(missing, 1);
        Assert.False(Directory.Exists(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void A_missing_log_file_does_not_throw()
    {
        LogRotation.RollIfNeeded(Log, 1);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}

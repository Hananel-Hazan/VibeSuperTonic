using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// When a snap's daemon steps aside for a revision snapd made current while it
/// ran. Written 2026-09-26 with <c>refresh-mode: ignore-running</c>, which
/// stops the daemon holding back every refresh and so makes a stale daemon
/// possible at all. See <see cref="SnapRefreshWatch"/>.
/// </summary>
public sealed class SnapRefreshWatchTests
{
    private static readonly TimeSpan Long = SnapRefreshWatch.Grace + TimeSpan.FromSeconds(1);

    [Fact]
    public void A_daemon_that_is_current_keeps_running()
    {
        Assert.Null(SnapRefreshWatch.StepAside("6", "6", openConnections: 0, speaking: false, Long));
    }

    /// <summary>The case the whole type exists for.</summary>
    [Fact]
    public void An_idle_daemon_of_an_old_revision_steps_aside()
    {
        string? why = SnapRefreshWatch.StepAside("5", "6", openConnections: 0, speaking: false, Long);
        Assert.NotNull(why);
        Assert.Contains("revision 6", why);
        Assert.Contains("revision 5", why);
    }

    /// <summary>snap revert makes an older revision current; follow it too.</summary>
    [Fact]
    public void A_reverted_snap_counts_as_a_different_revision()
    {
        Assert.NotNull(SnapRefreshWatch.StepAside("6", "5", openConnections: 0, speaking: false, Long));
    }

    [Fact]
    public void It_never_interrupts_speech()
    {
        Assert.Null(SnapRefreshWatch.StepAside("5", "6", openConnections: 0, speaking: true, Long));
    }

    /// <summary>An open window, a subscribed tray client, a render in flight.</summary>
    [Fact]
    public void It_never_leaves_a_connected_client()
    {
        Assert.Null(SnapRefreshWatch.StepAside("5", "6", openConnections: 1, speaking: false, Long));
    }

    [Fact]
    public void It_waits_out_the_grace_after_the_last_use()
    {
        Assert.Null(SnapRefreshWatch.StepAside("5", "6", openConnections: 0, speaking: false,
            SnapRefreshWatch.Grace - TimeSpan.FromSeconds(1)));
    }

    /// <summary>Not knowing is never a reason to stop speaking.</summary>
    [Theory]
    [InlineData(null, "6")]
    [InlineData("5", null)]
    [InlineData("", "6")]
    [InlineData("5", "")]
    public void An_unknown_revision_keeps_it_running(string? own, string? current)
    {
        Assert.Null(SnapRefreshWatch.StepAside(own, current, openConnections: 0, speaking: false, Long));
    }

    [Theory]
    [InlineData("/snap/vibesupertonic/5", "5")]
    [InlineData("/snap/vibesupertonic/x7/", "x7")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("relative/5", null)]
    public void Its_own_revision_is_the_last_element_of_SNAP(string? snap, string? expected)
    {
        Assert.Equal(expected, SnapRefreshWatch.OwnRevision(snap));
    }

    /// <summary>The link snapd moves on every refresh, read the way the daemon reads it.</summary>
    [Fact]
    public void The_current_revision_is_read_from_the_link_beside_SNAP()
    {
        string root = Directory.CreateTempSubdirectory("vst-snaprefresh-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "5"));
            Directory.CreateDirectory(Path.Combine(root, "6"));
            string current = Path.Combine(root, "current");
            File.CreateSymbolicLink(current, "5");
            string snap = Path.Combine(root, "5");

            Assert.Equal("5", SnapRefreshWatch.CurrentRevision(snap));

            // What snapd does on refresh: the link moves, the old directory stays.
            File.Delete(current);
            File.CreateSymbolicLink(current, "6");

            Assert.Equal("6", SnapRefreshWatch.CurrentRevision(snap));
            Assert.NotNull(SnapRefreshWatch.StepAside(SnapRefreshWatch.OwnRevision(snap),
                SnapRefreshWatch.CurrentRevision(snap), 0, false, Long));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_current_link_reads_as_unknown()
    {
        string root = Directory.CreateTempSubdirectory("vst-snaprefresh-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "5"));
            Assert.Null(SnapRefreshWatch.CurrentRevision(Path.Combine(root, "5")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

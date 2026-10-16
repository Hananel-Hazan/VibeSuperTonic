using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Noticing an update snapd is holding back while we run. Written 2026-09-27,
/// replacing an automatic step-aside the Store refused and the user did not
/// want. See <see cref="SnapUpdateWatch"/>.
/// </summary>
public sealed class SnapUpdateWatchTests
{
    private static Func<string, bool> On(params int[] revisions) =>
        path => revisions.Any(r => path == $"/var/lib/snapd/snaps/vibesupertonic_{r}.snap");

    [Fact]
    public void Nothing_downloaded_means_nothing_pending()
    {
        Assert.Null(SnapUpdateWatch.Pending("vibesupertonic", "7", "7", On(5, 7)));
    }

    /// <summary>The case the type exists for: snapd pre-downloaded and is waiting.</summary>
    [Fact]
    public void A_newer_downloaded_revision_is_pending()
    {
        Assert.Equal(9, SnapUpdateWatch.Pending("vibesupertonic", "7", "7", On(5, 7, 9)));
    }

    [Fact]
    public void The_newest_of_several_wins()
    {
        Assert.Equal(12, SnapUpdateWatch.Pending("vibesupertonic", "7", "7", On(7, 8, 12)));
    }

    /// <summary>snapd keeps old revisions for revert. They are not updates.</summary>
    [Fact]
    public void An_older_revision_on_disk_is_not_an_update()
    {
        Assert.Null(SnapUpdateWatch.Pending("vibesupertonic", "7", "7", On(3, 5, 7)));
    }

    /// <summary>After snapd's 14 days, or a revert: this daemon is the stale one.</summary>
    [Fact]
    public void A_different_current_revision_is_pending_even_if_older()
    {
        Assert.Equal(9, SnapUpdateWatch.Pending("vibesupertonic", "7", "9", On(7, 9)));
        Assert.Equal(5, SnapUpdateWatch.Pending("vibesupertonic", "7", "5", On(5, 7)));
    }

    /// <summary>A parallel install is <c>name_key</c>, and its files carry that name.</summary>
    [Fact]
    public void The_instance_name_is_what_is_probed()
    {
        Func<string, bool> other = p => p == "/var/lib/snapd/snaps/vibesupertonic_test_9.snap";
        Assert.Equal(9, SnapUpdateWatch.Pending("vibesupertonic_test", "7", "7", other));
        Assert.Null(SnapUpdateWatch.Pending("vibesupertonic", "7", "7", other));
    }

    [Theory]
    [InlineData(null, "7")]
    [InlineData("vibesupertonic", null)]
    [InlineData("vibesupertonic", "x1")]      // a locally installed, unasserted snap
    public void Outside_a_store_snap_nothing_is_pending(string? instance, string? own)
    {
        Assert.Null(SnapUpdateWatch.Pending(instance, own, "9", _ => true));
    }

    [Fact]
    public void Unreadable_current_falls_back_to_the_files()
    {
        Assert.Equal(8, SnapUpdateWatch.Pending("vibesupertonic", "7", null, On(8)));
        Assert.Null(SnapUpdateWatch.Pending("vibesupertonic", "7", null, On(7)));
    }

    [Theory]
    [InlineData("/snap/vibesupertonic/7", "7")]
    [InlineData("/snap/vibesupertonic/7/", "7")]
    [InlineData("", null)]
    [InlineData("relative/7", null)]
    public void Own_revision_is_the_last_element_of_SNAP(string snap, string? expected)
    {
        Assert.Equal(expected, SnapUpdateWatch.OwnRevision(snap));
    }
}

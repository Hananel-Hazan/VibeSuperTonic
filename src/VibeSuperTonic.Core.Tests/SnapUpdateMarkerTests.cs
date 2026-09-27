using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// When the Speech Dispatcher module steps aside for a snap update the user
/// chose. Written 2026-09-27. See <see cref="SnapUpdateMarker"/>.
/// </summary>
public sealed class SnapUpdateMarkerTests
{
    private static readonly DateTime Started = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_marker_for_this_revision_written_after_start_means_exit()
    {
        Assert.True(SnapUpdateMarker.ShouldExit("7", "7\n", Started.AddMinutes(5), Started));
    }

    /// <summary>The new revision's module must ignore the marker the old daemon left.</summary>
    [Fact]
    public void A_marker_for_another_revision_is_ignored()
    {
        Assert.False(SnapUpdateMarker.ShouldExit("9", "7", Started.AddMinutes(5), Started));
    }

    /// <summary>
    /// The update did not happen yet and speech-dispatcher restarted the old
    /// module: it must not exit on sight, leaving the screen reader on its
    /// fallback with nobody having asked again.
    /// </summary>
    [Fact]
    public void A_marker_older_than_the_module_is_ignored()
    {
        Assert.False(SnapUpdateMarker.ShouldExit("7", "7", Started.AddMinutes(-1), Started));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Outside_a_snap_nothing_exits(string? own)
    {
        Assert.False(SnapUpdateMarker.ShouldExit(own, own, Started.AddMinutes(5), Started));
    }

    [Fact]
    public void The_marker_lives_in_the_shared_common_directory()
    {
        Assert.Equal("/home/u/snap/x/common/update-now", SnapUpdateMarker.PathIn("/home/u/snap/x/common"));
        Assert.Null(SnapUpdateMarker.PathIn(null));
    }
}

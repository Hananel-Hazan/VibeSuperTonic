using VibeSuperTonic.Core.Selection;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The policy behind the 2026-08-16 finding: an application that renders
/// selectable text but never claims PRIMARY leaves the previous owner's text in
/// place, and the daemon reads it as if it were current.
/// </summary>
public class SelectionFreshnessTests
{
    private static SelectionStamp Stamp(ulong owner, uint time) => new(owner, time);

    [Fact]
    public void The_first_capture_of_a_session_is_always_fresh()
    {
        // Nothing to compare against yet. Treating an unknown history as stale
        // would put a notice on the first press of every session.
        var use = SelectionFreshness.Decide(Stamp(1, 100), null, null, clipboardFallback: true);

        Assert.Equal(SelectionUse.Primary, use);
    }

    [Fact]
    public void A_new_selection_in_the_same_window_is_fresh()
    {
        // Re-selecting inside one application re-acquires PRIMARY, so the owner
        // is unchanged and the timestamp is not. This is the common case and it
        // must not be mistaken for staleness.
        var use = SelectionFreshness.Decide(
            Stamp(owner: 1, time: 200), Stamp(owner: 1, time: 100), null, clipboardFallback: true);

        Assert.Equal(SelectionUse.Primary, use);
    }

    [Fact]
    public void A_selection_in_a_different_window_is_fresh()
    {
        var use = SelectionFreshness.Decide(
            Stamp(owner: 2, time: 100), Stamp(owner: 1, time: 100), null, clipboardFallback: true);

        Assert.Equal(SelectionUse.Primary, use);
    }

    [Fact]
    public void An_identical_claim_is_reported_as_unchanged()
    {
        // The Gmail attachment viewer case: the user selected text, the viewer
        // never took ownership, and PRIMARY still holds what it held before.
        var use = SelectionFreshness.Decide(
            Stamp(1, 100), Stamp(1, 100), null, clipboardFallback: false);

        Assert.Equal(SelectionUse.PrimaryUnchanged, use);
    }

    [Fact]
    public void A_newer_clipboard_rescues_an_unchanged_selection_when_opted_in()
    {
        var use = SelectionFreshness.Decide(
            primary: Stamp(1, 100),
            lastRead: Stamp(1, 100),
            clipboard: Stamp(9, 150),
            clipboardFallback: true);

        Assert.Equal(SelectionUse.Clipboard, use);
    }

    [Fact]
    public void An_older_clipboard_is_left_alone()
    {
        // Whatever the user copied before making this selection is not what they
        // just asked to hear.
        var use = SelectionFreshness.Decide(
            primary: Stamp(1, 100),
            lastRead: Stamp(1, 100),
            clipboard: Stamp(9, 50),
            clipboardFallback: true);

        Assert.Equal(SelectionUse.PrimaryUnchanged, use);
    }

    [Fact]
    public void The_clipboard_is_never_read_unless_opted_in()
    {
        var use = SelectionFreshness.Decide(
            primary: Stamp(1, 100),
            lastRead: Stamp(1, 100),
            clipboard: Stamp(9, 150),
            clipboardFallback: false);

        Assert.Equal(SelectionUse.PrimaryUnchanged, use);
    }

    [Fact]
    public void A_fresh_selection_is_never_overridden_by_the_clipboard()
    {
        // Ordering matters: freshness is decided first. A user who copies and
        // then selects something else means the selection.
        var use = SelectionFreshness.Decide(
            primary: Stamp(1, 200),
            lastRead: Stamp(1, 100),
            clipboard: Stamp(9, 250),
            clipboardFallback: true);

        Assert.Equal(SelectionUse.Primary, use);
    }

    // ------------------------------------------------------------ server time

    [Fact]
    public void Server_time_comparison_survives_the_32_bit_wrap()
    {
        // X server time wraps every ~49.7 days. Just after the wrap, a plain
        // greater-than says the new timestamp is older than the old one, which
        // would make a fresh clipboard look stale on any machine left running
        // that long -- a bug that cannot be reproduced on a box rebooted weekly.
        uint beforeWrap = uint.MaxValue - 1000;
        uint afterWrap = 1000;

        Assert.True(SelectionFreshness.IsAfter(afterWrap, beforeWrap));
        Assert.False(SelectionFreshness.IsAfter(beforeWrap, afterWrap));
        Assert.False(beforeWrap < afterWrap);   // pins WHY: the naive test disagrees
    }

    [Fact]
    public void A_timestamp_is_not_after_itself()
    {
        Assert.False(SelectionFreshness.IsAfter(500, 500));
    }

    [Fact]
    public void Ordinary_ordering_still_works()
    {
        Assert.True(SelectionFreshness.IsAfter(501, 500));
        Assert.False(SelectionFreshness.IsAfter(499, 500));
    }

    [Fact]
    public void A_clipboard_claimed_in_the_same_millisecond_does_not_win()
    {
        // Equal timestamps mean the copy did not happen after the selection, so
        // there is no evidence the user meant the clipboard.
        var use = SelectionFreshness.Decide(
            primary: Stamp(1, 100),
            lastRead: Stamp(1, 100),
            clipboard: Stamp(9, 100),
            clipboardFallback: true);

        Assert.Equal(SelectionUse.PrimaryUnchanged, use);
    }
}

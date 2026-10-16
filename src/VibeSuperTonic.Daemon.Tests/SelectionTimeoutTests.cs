using VibeSuperTonic.Daemon;
using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What the daemon says when the application holding the selection is slow.
///
/// <para><b>Reported 2026-09-06.</b> "I hit the hotkey on text in Firefox or
/// VS Code and it is not read. I hit stop and then read and it is still not
/// reading, more than ten seconds. Then I copy the text to Kate, select all, hit
/// the hotkey, and it reads it."</para>
///
/// <para>Handing over a selection is a round trip through the OWNING
/// application's event loop. Kate is a light native app and answers in about
/// 15 ms every time; a browser or an Electron app stalls its loop for hundreds
/// of milliseconds under load. The capture allowed 300 ms for the whole
/// transfer, so a busy owner lost the press — measured by freezing an owner's
/// event loop, where a 0.5 s stall was already enough.</para>
///
/// <para>Every sentence below is otherwise reachable only with a compositor, a
/// seat and an application holding a selection, which is why the source takes a
/// capture seam. That absence is how the timeout message came to blame the owner
/// for CLOSING when it was merely busy.</para>
/// </summary>
public sealed class SelectionTimeoutTests
{
    private static WaylandSelectionSource Source(
        WaylandNative.WaylandCapture primary,
        WaylandNative.WaylandCapture? clipboard = null,
        bool clipboardFallback = false) =>
        new(clipboardFallback, isPrimary => isPrimary
            ? primary
            : clipboard ?? new WaylandNative.WaylandCapture(true, true, false, null, null));

    private static WaylandNative.WaylandCapture Got(
        string? text, bool timedOut = false, bool hadOffer = true) =>
        new(Connected: true, ProtocolPresent: true, HadOffer: hadOffer,
            Text: text, Error: null, TimedOut: timedOut);

    /// <summary>The ordinary case, unchanged: a responsive owner.</summary>
    [Fact]
    public void A_responsive_owner_is_read_with_nothing_to_report()
    {
        var result = Source(Got("Hello there.")).Capture();

        Assert.True(result.Ok);
        Assert.Equal("Hello there.", result.Text);
        Assert.Null(result.Notice);
    }

    /// <summary>
    /// THE REPORTED FAILURE. Nothing arrived and the transfer was abandoned, so
    /// the answer must name the actual cause — a busy application — and must not
    /// claim it closed.
    /// </summary>
    [Fact]
    public void A_busy_owner_is_reported_as_busy_and_not_as_gone()
    {
        var result = Source(Got("", timedOut: true)).Capture();

        Assert.False(result.Ok);
        Assert.Contains("did not hand it over", result.Reason);
        Assert.Contains("busy", result.Reason);

        // The old sentence, which described a different failure entirely and sent
        // the reporting user looking in the wrong place for two weeks.
        Assert.DoesNotContain("closed", result.Reason);
    }

    /// <summary>It says what to do, because the thing to do actually works.</summary>
    [Fact]
    public void The_message_tells_the_user_what_gets_them_their_text()
    {
        string reason = Source(Got(null, timedOut: true)).Capture().Reason!;

        Assert.Contains("Press again", reason);
        Assert.Contains("copy the text", reason);
    }

    /// <summary>
    /// A TIMEOUT PART WAY THROUGH IS NOT A SUCCESS. The loop this replaces could
    /// not tell a timeout from an end of file, so a partial transfer came back as
    /// a complete one and the daemon read half a passage and stopped, with
    /// nothing anywhere reporting a fault.
    ///
    /// <para>The text is still spoken — refusing would throw away words the user
    /// can hear — but it is spoken WITH the notice, which reaches the event
    /// stream and now the log.</para>
    /// </summary>
    [Fact]
    public void A_transfer_cut_short_is_spoken_but_says_it_was_cut_short()
    {
        var result = Source(Got("The first half of the passa", timedOut: true)).Capture();

        Assert.True(result.Ok);
        Assert.Equal("The first half of the passa", result.Text);
        Assert.Contains("stopped sending part way through", result.Notice);
        Assert.Contains("27", result.Notice);
    }

    /// <summary>
    /// An owner that offered text, sent none, and did NOT time out really has
    /// gone away. That sentence was right for this case all along and stays.
    /// </summary>
    [Fact]
    public void An_owner_that_finished_without_sending_anything_may_indeed_have_closed()
    {
        var result = Source(Got("", timedOut: false)).Capture();

        Assert.False(result.Ok);
        Assert.Contains("closed", result.Reason);
    }

    /// <summary>
    /// Nothing owns the selection at all — a real state on Wayland, and NOT the
    /// same as a slow owner. It must keep its own answer, or "nothing is
    /// selected" would start blaming applications that are not involved.
    /// </summary>
    [Fact]
    public void Nothing_selected_is_still_its_own_answer()
    {
        var result = Source(Got(null, hadOffer: false)).Capture();

        Assert.False(result.Ok);
        Assert.Equal("nothing is selected.", result.Reason);
    }

    /// <summary>
    /// AND THE CLIPBOARD FALLBACK STILL DOES NOT COVER THIS. It fires when
    /// nothing owns the selection; a busy owner HAS the selection, it is just not
    /// answering. Worth pinning: it is the first thing anyone would reach for on
    /// reading the report, and it would not have helped.
    /// </summary>
    [Fact]
    public void The_clipboard_fallback_does_not_rescue_a_busy_owner()
    {
        var result = Source(
            Got("", timedOut: true),
            clipboard: Got("something in the clipboard"),
            clipboardFallback: true).Capture();

        Assert.False(result.Ok);
        Assert.Contains("busy", result.Reason);
        Assert.DoesNotContain("clipboard", result.Reason);
    }

    /// <summary>But it does still rescue an empty selection, which is its job.</summary>
    [Fact]
    public void The_clipboard_fallback_still_covers_an_empty_selection()
    {
        var result = Source(
            Got(null, hadOffer: false),
            clipboard: Got("something in the clipboard"),
            clipboardFallback: true).Capture();

        Assert.True(result.Ok);
        Assert.Equal("something in the clipboard", result.Text);
    }

    /// <summary>
    /// The budget has to be big enough to cover the stalls that were losing
    /// presses. 300 ms was the old whole-transfer figure and a 0.5 s stall beat
    /// it; anything at or below that is the bug back again.
    /// </summary>
    [Fact]
    public void The_first_byte_wait_outlasts_the_stall_that_was_losing_presses()
    {
        var budget = WaylandNative.ReceiveBudget.Default;

        Assert.True(budget.FirstByteMs >= 1000,
            $"a {budget.FirstByteMs} ms first-byte wait does not survive a busy browser");
        Assert.True(budget.IdleMs > 0);

        // And it is still bounded, or a wedged owner holds the press open forever.
        Assert.True(budget.TotalMs >= budget.FirstByteMs);
        Assert.True(budget.TotalMs <= 10000);
    }
}

using VibeSuperTonic.Daemon;
using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// An application that never publishes what is highlighted in it, on Wayland.
///
/// <para><b>Reported 2026-10-23 from PdfCraft</b> (an egui application):
/// select text, press the hotkey, and an old passage is read. Measured by
/// polling both selections while the user highlighted in PdfCraft: PRIMARY
/// never changed, and Ctrl+C changed CLIPBOARD. The daemon log showed four
/// presses in a row each capturing the 586 characters already playing.</para>
/// </summary>
public sealed class WaylandCopiedSinceTests
{
    private sealed class Desktop
    {
        public string? Primary;
        public string? Clipboard;
        public bool ClipboardTimesOut;

        public WaylandSelectionSource Source(bool clipboardFallback = true) =>
            new(clipboardFallback, isPrimary => isPrimary
                ? Got(Primary)
                : ClipboardTimesOut
                    ? new WaylandNative.WaylandCapture(true, true, true, "", null, TimedOut: true)
                    : Got(Clipboard));

        private static WaylandNative.WaylandCapture Got(string? text) =>
            new(Connected: true, ProtocolPresent: true, HadOffer: text is not null,
                Text: text, Error: null);
    }

    /// <summary>THE REPORTED SEQUENCE, with the fallback on.</summary>
    [Fact]
    public void A_copy_after_the_last_read_is_read_instead_of_the_stale_selection()
    {
        var d = new Desktop { Primary = "Old passage from the browser.", Clipboard = "Something older." };
        var source = d.Source();

        Assert.Equal("Old passage from the browser.", source.Capture().Text);

        // Highlight in PdfCraft changes nothing; Ctrl+C changes the clipboard.
        d.Clipboard = "The sentence highlighted in PdfCraft.";
        var result = source.Capture();

        Assert.True(result.Ok);
        Assert.Equal("The sentence highlighted in PdfCraft.", result.Text);
        Assert.Contains("clipboard", result.Notice);
    }

    /// <summary>
    /// A second press in the same window reads the copy again. Seen failing
    /// with the rule changed to compare only against the previous press, which
    /// hands this press back to the stale selection.
    /// </summary>
    [Fact]
    public void The_copy_stays_newer_until_something_is_selected()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "Older copy." };
        var source = d.Source();
        source.Capture();

        d.Clipboard = "Fresh copy.";
        Assert.Equal("Fresh copy.", source.Capture().Text);
        Assert.Equal("Fresh copy.", source.Capture().Text);

        d.Primary = "A new selection in Kate.";
        Assert.Equal("A new selection in Kate.", source.Capture().Text);
    }

    /// <summary>Pressing twice on the same selection, copying nothing, repeats it.</summary>
    [Fact]
    public void An_unchanged_clipboard_leaves_the_selection_in_charge()
    {
        var d = new Desktop { Primary = "Same selection.", Clipboard = "Unrelated copy." };
        var source = d.Source();

        Assert.Equal("Same selection.", source.Capture().Text);
        Assert.Equal("Same selection.", source.Capture().Text);
    }

    /// <summary>Both changed: there is no order to recover, so the selection wins.</summary>
    [Fact]
    public void When_both_changed_the_selection_is_read()
    {
        var d = new Desktop { Primary = "First.", Clipboard = "First copy." };
        var source = d.Source();
        source.Capture();

        d.Primary = "Second.";
        d.Clipboard = "Second copy.";
        Assert.Equal("Second.", source.Capture().Text);
    }

    /// <summary>The setting stays opt-in, as on X11.</summary>
    [Fact]
    public void Without_the_fallback_the_selection_is_always_read()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "Older copy." };
        var source = d.Source(clipboardFallback: false);
        source.Capture();

        d.Clipboard = "Fresh copy.";
        Assert.Equal("Stale.", source.Capture().Text);
    }

    /// <summary>A clipboard owner that did not answer is not evidence of a copy.</summary>
    [Fact]
    public void A_clipboard_that_timed_out_neither_wins_nor_resets_the_comparison()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "Older copy." };
        var source = d.Source();
        source.Capture();

        d.ClipboardTimesOut = true;
        Assert.Equal("Stale.", source.Capture().Text);

        // Back, and unchanged since the last good read: still not a new copy.
        d.ClipboardTimesOut = false;
        Assert.Equal("Stale.", source.Capture().Text);
    }
}

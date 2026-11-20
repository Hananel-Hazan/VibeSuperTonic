using VibeSuperTonic.Daemon;
using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// AutoCopy: the daemon presses Ctrl+C when the selection has not changed since
/// the last press (PdfCraft and the other applications that never publish a
/// highlight, reported 2026-10-23, measured 2026-10-30).
/// </summary>
public sealed class WaylandAutoCopyTests
{
    private sealed class Desktop : ICopyKey
    {
        public string? Primary;
        public string? Clipboard;
        /// <summary>What the focused application copies when Ctrl+C arrives; null copies nothing.</summary>
        public string? Highlighted;
        public int KeysSent;
        public CopyKeyResult Result = new(true);

        public CopyKeyResult Send()
        {
            if (Result.Sent) { KeysSent++; if (Highlighted is not null) Clipboard = Highlighted; }
            return Result;
        }

        public WaylandSelectionSource Source(bool clipboardFallback = false) =>
            new(clipboardFallback,
                isPrimary => Got(isPrimary ? Primary : Clipboard),
                this, copyWaitMs: 60);

        private static WaylandNative.WaylandCapture Got(string? text) =>
            new(Connected: true, ProtocolPresent: true, HadOffer: text is not null,
                Text: text, Error: null);
    }

    /// <summary>
    /// Item 8 under overlapping presses (batch 2 review). Press 2 waits for the
    /// Wayland connection while press 1 is still in its AutoCopy poll; by the time
    /// press 2 reads PRIMARY, press 1 has started reading that same text. What is
    /// playing must be asked for then, not when press 2 arrived, or the stop
    /// sends Ctrl+C into the terminal.
    /// </summary>
    [Fact]
    public void What_is_playing_is_asked_for_after_the_connection_is_held()
    {
        var d = new Desktop { Primary = "tail -f /var/log/syslog", Clipboard = "x", Highlighted = "copy" };
        d.Source().Capture();                               // press 1's own read
        string? playing = null;                             // press 2 arrives: idle
        var source = new WaylandSelectionSource(false,
            isPrimary =>
            {
                playing = "tail -f /var/log/syslog";        // press 1 started while press 2 waited
                return new WaylandNative.WaylandCapture(Connected: true, ProtocolPresent: true,
                    HadOffer: true, Text: isPrimary ? d.Primary : d.Clipboard, Error: null);
            },
            d, copyWaitMs: 60);
        int before = d.KeysSent;

        var result = source.Capture(null, () => playing);

        Assert.True(result.Ok);
        Assert.Equal("tail -f /var/log/syslog", result.Text);
        Assert.Equal(before, d.KeysSent);
    }

    /// <summary>THE REPORTED CASE: highlight in PdfCraft, press, hear the highlight.</summary>
    [Fact]
    public void An_unchanged_selection_presses_Ctrl_C_and_reads_the_copy()
    {
        var d = new Desktop { Primary = "Old passage from the browser.", Clipboard = "Unrelated.", Highlighted = "Highlighted in PdfCraft." };
        var source = d.Source();

        var result = source.Capture();

        Assert.True(result.Ok);
        Assert.Equal("Highlighted in PdfCraft.", result.Text);
        Assert.Equal(1, d.KeysSent);
        Assert.Contains("Ctrl+C", result.Notice);
    }

    /// <summary>An application that publishes its highlight is never sent a key.</summary>
    [Fact]
    public void A_new_selection_is_read_without_any_key_press()
    {
        var d = new Desktop { Primary = "First.", Clipboard = "x", Highlighted = "copy" };
        var source = d.Source();
        source.Capture();                                   // first press: counts as unchanged
        int afterFirst = d.KeysSent;

        d.Primary = "Newly highlighted in Kate.";
        var result = source.Capture();

        Assert.Equal("Newly highlighted in Kate.", result.Text);
        Assert.Equal(afterFirst, d.KeysSent);
    }

    /// <summary>
    /// The second press on the same highlight copies the same text again. The
    /// clipboard does not change, and the press must still read that text so the
    /// daemon's stop rule (same text as playing) can fire.
    /// </summary>
    [Fact]
    public void A_second_press_on_the_same_highlight_returns_the_same_copy()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "x", Highlighted = "The sentence." };
        var source = d.Source();

        Assert.Equal("The sentence.", source.Capture().Text);
        var again = source.Capture();

        Assert.Equal("The sentence.", again.Text);
        Assert.Equal(2, d.KeysSent);
    }

    /// <summary>Ctrl+C with nothing highlighted must not turn a working press into a failure.</summary>
    [Fact]
    public void When_nothing_was_copied_the_ordinary_selection_is_read()
    {
        var d = new Desktop { Primary = "Selected in Kate.", Clipboard = "x", Highlighted = null };
        var result = d.Source().Capture();

        Assert.True(result.Ok);
        Assert.Equal("Selected in Kate.", result.Text);
    }

    /// <summary>
    /// While the desktop's permission dialog is open, reading the stale selection
    /// would be the very bug this feature exists to remove.
    /// </summary>
    [Fact]
    public void Waiting_for_permission_says_so_and_reads_nothing()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "x", Result = new(false, "approve the dialog", Pending: true) };
        var result = d.Source().Capture();

        Assert.False(result.Ok);
        Assert.Contains("approve the dialog", result.Reason);
    }

    /// <summary>A refused or failed key falls back to the old behaviour rather than silence.</summary>
    [Fact]
    public void A_failed_key_press_falls_back_to_the_ordinary_read()
    {
        var d = new Desktop { Primary = "Selected.", Clipboard = "x", Result = new(false, "refused") };
        var result = d.Source().Capture();

        Assert.True(result.Ok);
        Assert.Equal("Selected.", result.Text);
    }

    /// <summary>After a different selection, an earlier copy is not served back.</summary>
    [Fact]
    public void A_remembered_copy_is_forgotten_when_the_selection_changes()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "x", Highlighted = "The sentence." };
        var source = d.Source();
        source.Capture();                                   // copies "The sentence."

        d.Primary = "Fresh from Kate.";
        Assert.Equal("Fresh from Kate.", source.Capture().Text);

        // Back in PdfCraft with nothing newly copied: the clipboard still holds the
        // old copy, which is not a reason to read it.
        d.Highlighted = null;
        Assert.Equal("Fresh from Kate.", source.Capture().Text);
    }

    [Fact]
    public void Without_a_key_sender_nothing_is_pressed()
    {
        var d = new Desktop { Primary = "Selected.", Clipboard = "x", Highlighted = "copy" };
        var source = new WaylandSelectionSource(false,
            isPrimary => new(true, true, true, isPrimary ? d.Primary : d.Clipboard, null));

        Assert.Equal("Selected.", source.Capture().Text);
        Assert.Equal(0, d.KeysSent);
    }

    /// <summary>
    /// Review item 8. The press that stops a reading captures the text playing,
    /// and PRIMARY unchanged is exactly when AutoCopy fires: a terminal got Ctrl+C
    /// before the daemon decided the press meant stop.
    /// </summary>
    [Fact]
    public void A_press_on_the_text_being_read_sends_no_key()
    {
        var d = new Desktop { Primary = "ls -R /", Clipboard = "x", Highlighted = "ls -R /" };
        var source = d.Source();
        Assert.Equal("ls -R /", source.Capture(null, (string?)null).Text);
        int afterFirst = d.KeysSent;

        var result = source.Capture(null, reading: "ls -R /");

        Assert.Equal("ls -R /", result.Text);
        Assert.Equal(afterFirst, d.KeysSent);
    }

    /// <summary>The check is against the text playing, not a switch: other text still copies.</summary>
    [Fact]
    public void A_press_while_something_else_is_read_still_copies()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "x", Highlighted = "In PdfCraft." };
        var source = d.Source();
        source.Capture();
        int afterFirst = d.KeysSent;

        d.Highlighted = "A new highlight in PdfCraft.";
        var result = source.Capture(null, reading: "In PdfCraft.");

        Assert.Equal("A new highlight in PdfCraft.", result.Text);
        Assert.Equal(afterFirst + 1, d.KeysSent);
    }

    /// <summary>
    /// Review item 86. A press that copies reads PRIMARY, the clipboard before the
    /// key and the clipboard until it changes: all through one connection.
    /// </summary>
    [Fact]
    public void A_press_that_copies_opens_one_connection_for_all_its_reads()
    {
        var d = new Desktop { Primary = "Stale.", Clipboard = "x", Highlighted = null };
        var opened = new List<CountingReader>();
        var source = new WaylandSelectionSource(false,
            () => { var r = new CountingReader(d); opened.Add(r); return r; },
            d, copyWaitMs: 200);

        source.Capture();                        // nothing copies: polls the whole wait

        Assert.Equal(1, d.KeysSent);
        Assert.Single(opened);
        Assert.True(opened[0].Reads >= 3, $"only {opened[0].Reads} reads; the poll did not run");
        Assert.True(opened[0].Disposed);
    }

    private sealed class CountingReader(Desktop d) : WaylandSelectionSource.ISelectionReader
    {
        public int Reads;
        public bool Disposed;

        public WaylandNative.WaylandCapture Read(bool primary)
        {
            Assert.False(Disposed, "read after dispose");
            Reads++;
            string? text = primary ? d.Primary : d.Clipboard;
            return new(true, true, text is not null, text, null);
        }

        public void Dispose() => Disposed = true;
    }
}

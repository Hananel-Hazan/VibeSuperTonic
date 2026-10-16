using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The selection transfer loop, with the compositor taken out of it.
///
/// <para>These exist because the first round of tests for this bug did NOT catch
/// it: they drove the source through its capture seam, so reverting the loop's
/// timeout handling to the single <c>break</c> that caused the fault passed all
/// nine of them. A check that has never been observed failing is not evidence,
/// so the loop is now a function and the clock is a parameter.</para>
/// </summary>
public sealed class SelectionPumpTests
{
    private sealed class Owner
    {
        private readonly Queue<(int TakesMs, WaylandNative.ChunkOutcome Outcome, string Text)> _script = new();
        public long Now;
        private string _text = "";

        public Owner Chunk(int takesMs, string text)
        { _script.Enqueue((takesMs, WaylandNative.ChunkOutcome.Data, text)); return this; }

        public Owner Finishes(int takesMs)
        { _script.Enqueue((takesMs, WaylandNative.ChunkOutcome.Eof, "")); return this; }

        /// <summary>The event loop stalls and never answers.</summary>
        public Owner Stalls() => this;

        public WaylandNative.ChunkResult Read(int allowedMs)
        {
            if (_script.Count == 0)
            {
                // Nothing more is coming: the owner is busy. Burn the whole
                // window the caller was willing to wait, exactly as poll does.
                Now += allowedMs;
                return new WaylandNative.ChunkResult(WaylandNative.ChunkOutcome.Timeout, 0);
            }

            var step = _script.Peek();
            if (step.TakesMs > allowedMs)
            {
                Now += allowedMs;
                return new WaylandNative.ChunkResult(WaylandNative.ChunkOutcome.Timeout, 0);
            }

            _script.Dequeue();
            Now += step.TakesMs;
            _text += step.Text;
            return new WaylandNative.ChunkResult(step.Outcome, step.Text.Length);
        }

        public string Text => _text;
    }

    private static WaylandNative.ReceiveResult Run(
        Owner owner, WaylandNative.ReceiveBudget? budget = null) =>
        WaylandNative.Pump(
            budget ?? WaylandNative.ReceiveBudget.Default,
            owner.Read, () => owner.Text, () => owner.Now);

    /// <summary>A responsive owner: one chunk, then end of file.</summary>
    [Fact]
    public void A_prompt_owner_completes_and_is_not_a_timeout()
    {
        var result = Run(new Owner().Chunk(15, "Hello there.").Finishes(1));

        Assert.Equal("Hello there.", result.Text);
        Assert.False(result.TimedOut);
        Assert.True(result.SawEof);
    }

    /// <summary>
    /// THE REPORTED FAILURE, as the loop sees it. The owner's event loop is
    /// stalled and nothing ever arrives.
    /// </summary>
    [Fact]
    public void A_stalled_owner_times_out_and_says_so()
    {
        var result = Run(new Owner().Stalls());

        Assert.Equal("", result.Text);
        Assert.True(result.TimedOut);
        Assert.False(result.SawEof);
    }

    /// <summary>
    /// A busy-but-working owner. It takes 900 ms to get started — well past the
    /// 300 ms whole-transfer budget that was losing presses — and then finishes.
    /// This is the case the fix exists for.
    /// </summary>
    [Fact]
    public void A_slow_but_working_owner_is_waited_out()
    {
        var result = Run(new Owner().Chunk(900, "The passage that Firefox was too busy to send.").Finishes(5));

        Assert.Equal("The passage that Firefox was too busy to send.", result.Text);
        Assert.False(result.TimedOut);
    }

    /// <summary>
    /// The old budget could not have served that owner. Pinning it makes the
    /// regression concrete rather than a number in a comment.
    /// </summary>
    [Fact]
    public void The_budget_this_replaced_would_still_have_lost_it()
    {
        var old = new WaylandNative.ReceiveBudget(FirstByteMs: 300, IdleMs: 300, TotalMs: 300);
        var result = Run(new Owner().Chunk(900, "The same passage.").Finishes(5), old);

        Assert.True(result.TimedOut);
        Assert.Equal("", result.Text);
    }

    /// <summary>
    /// A TIMEOUT PART WAY THROUGH IS NOT AN END OF FILE. This is the one the
    /// seam-level tests could not see: the loop must return what arrived AND the
    /// fact that it gave up, or the caller reads half a passage and calls it a
    /// success.
    /// </summary>
    [Fact]
    public void A_transfer_abandoned_part_way_reports_both_the_text_and_the_timeout()
    {
        var result = Run(new Owner().Chunk(20, "The first half").Stalls());

        Assert.Equal("The first half", result.Text);
        Assert.True(result.TimedOut);
        Assert.False(result.SawEof);
    }

    /// <summary>
    /// Each chunk earns a fresh idle window, so an owner that trickles steadily
    /// is not cut off merely for taking a while in total.
    /// </summary>
    [Fact]
    public void A_trickling_owner_keeps_earning_time_while_it_is_talking()
    {
        var owner = new Owner();
        for (int i = 0; i < 8; i++) owner.Chunk(400, $"chunk{i} ");
        owner.Finishes(5);

        var result = Run(owner, new WaylandNative.ReceiveBudget(FirstByteMs: 600, IdleMs: 600, TotalMs: 60000));

        Assert.False(result.TimedOut);
        Assert.Contains("chunk7", result.Text);
    }

    /// <summary>
    /// But the ceiling is real: an owner that trickles forever must not hold the
    /// press open, however politely it keeps sending.
    /// </summary>
    [Fact]
    public void The_overall_ceiling_stops_an_owner_that_never_finishes()
    {
        var owner = new Owner();
        for (int i = 0; i < 1000; i++) owner.Chunk(100, "on and on ");

        var result = Run(owner, new WaylandNative.ReceiveBudget(FirstByteMs: 500, IdleMs: 500, TotalMs: 2000));

        Assert.True(result.TimedOut);
        Assert.True(owner.Now <= 2600, $"ran to {owner.Now} ms, past the ceiling it was given");
    }

    /// <summary>A read error is a failure, not a timeout — it must not be reported as one.</summary>
    [Fact]
    public void A_read_failure_is_not_a_timeout()
    {
        var result = WaylandNative.Pump(
            WaylandNative.ReceiveBudget.Default,
            _ => new WaylandNative.ChunkResult(WaylandNative.ChunkOutcome.Error, 0),
            () => "", () => 0);

        Assert.False(result.TimedOut);
        Assert.False(result.SawEof);
    }
}

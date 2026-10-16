namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// What counts as an underrun on this platform.
///
/// <para>The Windows engine counts SAPI write stalls. Linux has no such signal —
/// PulseAudio's own underflow counter is not surfaced by the sink — so the
/// session infers it from the one place it can be seen: <see cref="Session.SpeechSession"/>
/// goes back to the render queue for the next chunk, and if the renderer has not
/// produced it by the time the device has played everything already written, the
/// listener hears a gap that is not the configured inter-chunk silence. That is an
/// underrun by any definition worth reporting.</para>
///
/// <para><b>Approximate on purpose.</b> "Buffered" is the sink's reported latency,
/// which a sink that cannot read it reports as 0 — so a device that hides its
/// latency reads every wait longer than the tolerance as a starvation. The
/// tolerance is there for the other direction: a few milliseconds of scheduling
/// jitter on a queue that was a moment from being full is not a gap.</para>
/// </summary>
public static class UnderrunPolicy
{
    /// <summary>Scheduling jitter that is not an audible gap.</summary>
    public const double ToleranceMs = 20;

    /// <summary>
    /// Whether waiting <paramref name="waitedMs"/> for the next chunk, with
    /// <paramref name="bufferedMs"/> of audio still queued on the device when the
    /// wait began, let the device run dry.
    /// </summary>
    public static bool IsUnderrun(double waitedMs, double bufferedMs)
    {
        if (double.IsNaN(waitedMs) || double.IsNaN(bufferedMs)) return false;
        return waitedMs - Math.Max(0, bufferedMs) > ToleranceMs;
    }
}

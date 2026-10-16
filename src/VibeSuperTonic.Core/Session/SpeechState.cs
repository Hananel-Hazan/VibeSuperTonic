namespace VibeSuperTonic.Core.Session;

/// <summary>
/// What the speech pipeline is doing, and therefore what the next key press
/// means. One key does everything — press to speak, press again to stop — so
/// this enum <em>is</em> the product's interaction model, not an implementation
/// detail.
///
/// <list type="table">
/// <item><term>Idle</term><description>press captures the selection and starts speaking</description></item>
/// <item><term>Preparing</term><description>press cancels</description></item>
/// <item><term>Speaking</term><description>press stops</description></item>
/// <item><term>Stopping</term><description>press is ignored</description></item>
/// </list>
///
/// <para><b><see cref="Preparing"/> counts as active</b>, which is the one
/// non-obvious entry. A cold model load is 2–5 seconds, and that is precisely
/// the window in which a user who has heard nothing yet presses the key again.
/// If the second press only worked once audio had started, the key would feel
/// dead exactly when it is most likely to be pressed.</para>
/// </summary>
public enum SpeechState
{
    Idle,

    /// <summary>Model loading, or the first chunk still rendering. No audio yet.</summary>
    Preparing,

    Speaking,

    /// <summary>
    /// Stop asked for, not yet complete. Transient — a state rather than a flag
    /// because stop is three operations across two threads and presses arriving
    /// mid-teardown must be dropped, not queued.
    /// </summary>
    Stopping,
}

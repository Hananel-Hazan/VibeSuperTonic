namespace VibeSuperTonic.Daemon.Tray;

/// <summary>
/// Whether a tray activation should launch a window, given that the last one
/// may still be starting.
///
/// <para><b>The attached check alone is not a guard.</b> "Is a window open" is
/// answered by whether a UI has subscribed to the daemon, and a UI takes a
/// moment to start and connect. Two activations arriving inside that moment
/// both see "no window open" and both launch one — which is what a user gets
/// for a double click, and what this machine produced from a single one:
/// two processes 100 ms apart, each opening its own window, with the daemon
/// then logging "a window is already open" for every activation afterwards
/// because by THEN the check worked.</para>
///
/// <para>So the gate is the pair: a window that is attached, or a launch that
/// is still in flight. The grace period is deliberately generous — a cold UI
/// start on a loaded machine is seconds, and the cost of being wrong in this
/// direction is one ignored click, against a second unwanted window in the
/// other.</para>
///
/// <para>Separated from <see cref="TrayIcon"/> because it is the only part of
/// that class with a decision in it, and a rule about timing that nothing can
/// observe failing is a rule that comes back.</para>
/// </summary>
internal sealed class WindowLaunchGate(TimeSpan? grace = null)
{
    private readonly TimeSpan _grace = grace ?? TimeSpan.FromSeconds(12);
    private readonly object _lock = new();
    private DateTime _launchedUtc = DateTime.MinValue;

    /// <summary>Why a launch was refused, or null when it may go ahead.</summary>
    internal string? Refuse(bool uiAttached, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (uiAttached)
            {
                // A launch that has arrived cancels the in-flight window: the
                // next activation after the user closes it must open one again.
                _launchedUtc = DateTime.MinValue;
                return "a window is already open";
            }

            if (nowUtc - _launchedUtc < _grace) return "a window is already opening";

            _launchedUtc = nowUtc;
            return null;
        }
    }

    /// <summary>
    /// Forget an in-flight launch, for the case where starting the process
    /// failed: without this, a UI that could not start makes the tray icon do
    /// nothing at all for the length of the grace period, which reads as the
    /// icon being dead.
    /// </summary>
    internal void Failed()
    {
        lock (_lock) _launchedUtc = DateTime.MinValue;
    }
}

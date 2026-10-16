using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Core.Export;

/// <summary>
/// Export while the daemon is reading aloud is refused, not queued: a render
/// would wait behind the reading, and the user asked for a file, not for
/// silence until a paragraph ends. Decided 2026-10-16.
///
/// <para>The refusal message stays on screen <b>until the reading ends</b>, and
/// then says so. Without that, "busy" reads as a fault and the user has no way
/// to tell when trying again would work. The gate holds that one bit of state
/// so the window only has to forward state changes.</para>
/// </summary>
public sealed class ExportBusyGate
{
    public const string BusyMessage =
        "Cannot export while VibeSuperTonic is reading aloud. "
        + "Stop the reading, or wait: this message changes when it finishes.";

    public const string ReadyMessage = "The reading finished. You can export now.";

    /// <summary>True between a refusal and the next Idle.</summary>
    public bool Waiting { get; private set; }

    /// <summary>
    /// Asked before the save dialog opens. Null means go ahead; otherwise the
    /// message to show, and the gate waits for Idle.
    /// </summary>
    public string? Check(SpeechState state)
    {
        if (state == SpeechState.Idle) return null;
        Waiting = true;
        return BusyMessage;
    }

    /// <summary>
    /// The daemon refused the render itself: a reading started between the
    /// check and the request. Its refusal starts with "busy (", which is the
    /// only one this gate takes over; every other error is shown as it is.
    /// </summary>
    public string? FromRefusal(string? error)
    {
        if (error is null || !error.StartsWith("busy (", StringComparison.Ordinal)) return null;
        Waiting = true;
        return BusyMessage;
    }

    /// <summary>
    /// Every state change the window sees. Returns the message that replaces
    /// the refusal when the reading ends, or null when there is nothing to say.
    /// </summary>
    public string? OnState(SpeechState state)
    {
        if (!Waiting || state != SpeechState.Idle) return null;
        Waiting = false;
        return ReadyMessage;
    }
}

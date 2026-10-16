namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// The audio device went away underneath us — the server restarted, the session
/// ended, the sink was removed.
///
/// <para><b>Why this is its own type.</b> The recovery differs from every other
/// write failure, and only this one is safe to retry. A device that is gone
/// should be dropped and reopened; a device that rejected a write for some other
/// reason — a bad format, a buffer the caller sized wrongly — will reject the
/// next one identically, and reconnecting in a loop would turn one error into an
/// unbounded one. Catching a bare <see cref="InvalidOperationException"/> cannot
/// tell those apart.</para>
///
/// <para><b>The failure this exists for.</b> A daemon that holds the login
/// session's audio stream will outlive at least one restart of the audio server;
/// on a PipeWire desktop that is a routine event, not an exotic one. Before this
/// existed, the stream died and nothing noticed: <c>status</c> answered,
/// <c>config</c> answered, <c>speak</c> was accepted and acknowledged, and then
/// the write failed on a worker thread where the only report was an
/// <c>Error</c> event on a stream nothing subscribes to yet. Found in the field
/// after roughly five hours of a hotkey that did nothing and explained
/// nothing.</para>
/// </summary>
public sealed class AudioDeviceLostException : Exception
{
    public AudioDeviceLostException(string message) : base(message) { }

    public AudioDeviceLostException(string message, Exception inner) : base(message, inner) { }
}

using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// Whether a <see cref="RequestVerb.Render"/> may proceed — the server side of
/// the contract <see cref="RenderWav"/> reads on the client side.
///
/// <para><b>This is a decided policy, and it had to be one.</b> Trap 12 of
/// docs/SPEECHD-PLAN.md: <c>render</c> and the hotkey share a single
/// synthesizer, and <c>EspeakPhonemizer</c> serialises every call on an instance
/// lock because espeak-ng keeps its voice and clause cursor in process-global
/// state. So a render arriving while the daemon is speaking does not race — it
/// <em>queues</em>, between the chunks of the passage the user is listening to.
/// Speech Dispatcher calls this dozens of times a minute, so left alone the
/// emergent behaviour is a hotkey press landing behind a queue of key
/// echoes.</para>
///
/// <para><b>The press wins.</b> A render while the session is anything but idle
/// is refused at once, with a reason. That is trap 12's cheapest option, and it
/// follows <c>benchmark</c> and <c>voice install</c>, which refuse concurrent
/// runs on the same reasoning. It is affordable only because of trap 16 — the
/// module falls back to our own bundled espeak rather than to silence — so the
/// user hears the sentence in a flat voice instead of hearing the passage they
/// are listening to stutter. <b>If that fallback is ever removed this policy
/// must change with it</b>, because refusing into silence is the one outcome
/// this feature must never produce.</para>
///
/// <para><b>Renders do not refuse each other.</b> They already serialise in the
/// synthesizer, and Orca's commonest sequence is a stop followed immediately by
/// the next utterance — refusing the second would put an audible voice change on
/// every keystroke, which is worse than a few milliseconds of queuing.</para>
///
/// <para>A pure function, in Core, because it is the whole policy and it is
/// otherwise reachable only through a <c>DaemonServer</c> with an engine, a
/// session and a sink — which is to say, not reachable from a test at all.</para>
/// </summary>
public static class RenderAdmission
{
    /// <summary>
    /// The refusal to send back, or null when the render may proceed.
    /// </summary>
    /// <param name="state">What the speech pipeline is doing right now.</param>
    /// <param name="engineNeedsIdle">
    /// True when serving this request would mean swapping the engine out from
    /// under whatever is playing. Narrower than <paramref name="state"/> and kept
    /// separate from it: the engine cannot change under a live utterance however
    /// the busy policy above is next revised.
    /// </param>
    /// <param name="routingError">
    /// Why the voice could not be selected at all — no models downloaded being
    /// the one a first-time installer meets. <b>Checked first</b>: "there is no
    /// voice" is a truer answer than "try again later", and telling someone with
    /// no models installed that the daemon is busy would send them looking in the
    /// wrong place.
    /// </param>
    public static string? Refuse(SpeechState state, bool engineNeedsIdle, string? routingError)
    {
        if (!string.IsNullOrWhiteSpace(routingError)) return routingError;

        if (state != SpeechState.Idle)
            return $"busy ({state}) — this daemon is speaking, and a render would queue behind it";

        if (engineNeedsIdle)
            return $"busy ({state}) — the engine cannot change while something is playing";

        return null;
    }
}

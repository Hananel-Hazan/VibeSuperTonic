namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// Turns text into PCM. The one seam between Core and ONNX Runtime.
///
/// Core cannot reference ORT at all — the DirectML and CPU builds ship the same
/// managed assembly name with different managed API surfaces, so an assembly
/// shared by both hosts can reference neither (R-13). Everything that touches
/// ORT therefore lives behind this interface, in a per-platform backend:
/// DirectML plus device-loss recovery on Windows, plain CPU on Linux.
///
/// The cancellation token is not decoration. Stopping speech has to cancel the
/// inference that is already running, not merely stop consuming its output — the
/// pipeline renders ahead, so at any moment there is typically a Run in flight
/// that can take seconds, and letting it finish leaves the next utterance queued
/// behind a dead one (R-8). The vendored SDK already threads a token down to
/// _Infer, so the plumbing exists; this makes it part of the contract instead of
/// an implementation detail a backend might forget.
/// </summary>
public interface ISynthesizer : IDisposable
{
    /// <summary>Output sample rate in Hz. Loading the model may be required to answer.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Render one chunk to 16-bit mono PCM at <see cref="SampleRate"/>.
    /// Callers chunk first via <c>SentenceChunker</c>; this renders what it is given.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> fires, including mid-inference.
    /// </exception>
    short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the model set ahead of first use. Idempotent and safe to call
    /// concurrently. Cold load is seconds; a host that skips this pays it on the
    /// first thing the user asks to hear.
    /// </summary>
    Task PreloadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-utterance knobs. Deliberately not the whole settings object: a backend has
/// no business reading user configuration, and passing only what synthesis
/// consumes keeps the settings-storage seam (registry vs XDG) on the host side.
/// </summary>
/// <param name="VoiceId">Voice style to load, e.g. "M1".</param>
/// <param name="Language">Supertonic language code.</param>
/// <param name="TotalStep">Diffusion steps. 8 is the default on both platforms; higher is slower and cleaner.</param>
/// <param name="Speed">Model-side speed factor, applied before any DSP time-stretch.</param>
/// <param name="SilenceSeconds">Silence appended between internally-split segments.</param>
public sealed record SynthesisOptions(
    string VoiceId,
    string Language,
    int TotalStep = 8,
    float Speed = 1.05f,
    float SilenceSeconds = 0.3f);

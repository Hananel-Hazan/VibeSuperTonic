using System.Diagnostics;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Telemetry;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Times every render for the <c>diagnostics</c> verb and otherwise gets out of
/// the way.
///
/// <para><b>Why a wrapper at the top.</b> The only other place that times a render
/// is <see cref="ProviderSwitchingSynthesizer"/>, which sits below the engine
/// router and therefore never sees a Piper voice — a diagnostics panel built on it
/// would go blank the moment somebody chose one. This decorator wraps what the
/// <see cref="Core.Session.SpeechSession"/> holds, so every engine passes through
/// it, and it adds one stopwatch read either side of a call that takes hundreds of
/// milliseconds.</para>
///
/// <para><b>It records successes only.</b> A render that threw — a cancellation
/// from a stop, a GPU fault the switch is about to recover from — measured
/// nothing about throughput. Failures reach the tracker through the session's
/// <see cref="Core.Session.SessionEventKind.Error"/> event, which is the one place
/// a failed utterance is reported exactly once.</para>
/// </summary>
internal sealed class DiagnosticSynthesizer : ISynthesizer
{
    private readonly ISynthesizer _inner;
    private readonly DiagnosticsTracker _tracker;

    public DiagnosticSynthesizer(ISynthesizer inner, DiagnosticsTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    public int SampleRate => _inner.SampleRate;

    public short[] Synthesize(string text, SynthesisOptions options, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        short[] pcm = _inner.Synthesize(text, options, cancellationToken);

        try
        {
            int rate = _inner.SampleRate;
            if (rate > 0 && pcm.Length > 0)
                _tracker.RecordRender(
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    pcm.Length / (double)rate);
        }
        catch
        {
            // Bookkeeping must never fail an utterance that already has its audio.
        }

        return pcm;
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default) =>
        _inner.PreloadAsync(cancellationToken);

    /// <summary>
    /// Not the inner synthesizer's to dispose through here: <c>Program</c> owns
    /// <c>engines</c> with its own <c>using</c>, and disposing it twice is the
    /// shape of bug a shutdown path should not have.
    /// </summary>
    public void Dispose() { }
}

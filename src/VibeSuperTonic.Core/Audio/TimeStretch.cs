using System;

namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// Pitch-preserving time-stretch — single entry point used by the SAPI engine
/// after synthesis to apply the DSP rate.
///
/// Implementation history:
///   1. WSOLA — dropped trailing syllables at chunk boundaries, abandoned.
///   2. Phase vocoder (Laroche-Dolson + transient handling + low-mag bypass) —
///      iterated five rounds of perceptual A/B and converged on a configuration
///      that still produced audible formant smearing on M voices. The phase
///      vocoder's bin-quantized spectral representation appears to be the
///      ceiling for this material.
///   3. <see cref="Sonic"/> — current. Pitch-synchronous overlap-add. Each
///      output pitch period is bit-perfect from the input (minus the crossfade
///      regions), so the spectral envelope is preserved sample-for-sample.
/// </summary>
public static class TimeStretch
{
    /// <summary>
    /// Supertonic's output rate, read from <c>tts.json</c> at 44100 for
    /// supertonic-3. Kept as a named constant for the callers that render at it
    /// — it is no longer this class's assumption. See <see cref="Stretch"/>.
    /// </summary>
    public const int SupertonicSampleRate = 44100;

    /// <summary>
    /// Stretch <paramref name="input"/> by <paramref name="factor"/> in place
    /// of time-domain duration — factor &gt; 1 plays faster (shorter output),
    /// factor &lt; 1 plays slower (longer output). Pitch is preserved.
    /// </summary>
    /// <param name="sampleRate">
    /// The rate <paramref name="input"/> was rendered at. <b>Passed, never
    /// assumed</b>, and the parameter has no default on purpose.
    ///
    /// <para>This was a constant 44100 until 2026-08-25, correctly and with a
    /// comment saying why — it matches Supertonic, and a mismatch plays at the
    /// wrong speed AND pitch. Then a second engine arrived: a Piper voice
    /// renders at 22050 or 16000, so the moment anything on that path reached
    /// this shared stage, Sonic would be told the wrong rate. It is
    /// <see cref="Sonic"/>'s pitch search that breaks — its period band is
    /// 65–400 Hz expressed in SAMPLES, so at double the true rate the floor
    /// (110 samples) sits above the real period of any voice above ~200 Hz and
    /// the algorithm locks onto something that is not the pitch.</para>
    ///
    /// <para>The reason it was worth fixing before the caller existed:
    /// <see href="../../../docs/PIPER-PLAN.md#speed-verdict">P2's listening
    /// test</see> established that nobody hears this stage being wrong —
    /// 35,156 of 35,675 samples different between two renders and the user
    /// could not tell them apart. A defect no ear will catch has to be caught
    /// by the signature.</para>
    /// </param>
    public static short[] Stretch(short[] input, double factor, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (Math.Abs(factor - 1.0) < 0.001) return input;

        // Sonic needs ~2× the max pitch period to grip — 1350 samples at
        // 44.1 kHz, and proportionally fewer at a lower rate, which is why this
        // floor scales rather than staying the 4096 it was when there was only
        // one rate in the product.
        if (input.Length < 4096L * sampleRate / SupertonicSampleRate) return input;

        var sonic = new Sonic(sampleRate, (float)factor);
        sonic.WriteSamples(input, 0, input.Length);
        sonic.Flush();

        int expected = (int)(input.Length / factor);
        var output = new short[expected + 4096];
        int written = 0;
        int read;
        while ((read = sonic.ReadSamples(output, written, output.Length - written)) > 0)
        {
            written += read;
            if (output.Length - written < 1024)
                Array.Resize(ref output, output.Length * 2);
        }
        // Sonic's flush pads with silence, so the tail can run slightly past
        // the expected duration. Truncate to the predicted length to avoid
        // emitting trailing zeros that would extend perceived playback time.
        if (written > expected) written = expected;
        Array.Resize(ref output, written);
        return output;
    }
}

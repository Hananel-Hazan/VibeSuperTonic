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
    // Must match the Supertonic model's output rate (read from tts.json at
    // 44100 for supertonic-3). Mismatches make playback play at the wrong
    // speed AND pitch, hiding any DSP-quality assessment.
    private const int SampleRate = 44100;

    /// <summary>
    /// Stretch <paramref name="input"/> by <paramref name="factor"/> in place
    /// of time-domain duration — factor &gt; 1 plays faster (shorter output),
    /// factor &lt; 1 plays slower (longer output). Pitch is preserved.
    /// </summary>
    public static short[] Stretch(short[] input, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.001) return input;
        // Sonic needs ~2× the max pitch period (≈1350 samples at 44.1 kHz) of
        // headroom to grip; below that we'd just be running silence through it.
        if (input.Length < 4096) return input;

        var sonic = new Sonic(SampleRate, (float)factor);
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

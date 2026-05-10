using VibeSuperTonic.Engine.Settings;

namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// Pitch-preserving time-stretch via phase-locked STFT phase vocoder.
///
/// Replaces the older WSOLA implementation, whose waveform-similarity search
/// dropped trailing syllables at boundaries and produced robotic artifacts above
/// ~1.5x stretch. The phase vocoder operates entirely in the frequency domain
/// (STFT → modify magnitudes/phases → ISTFT → overlap-add), which makes
/// boundary handling trivial — every output frame is self-contained.
///
/// Phase locking (Laroche-Dolson 1999) is the key for clean speech. Without it,
/// adjacent bins track phase independently and harmonics that should move
/// together drift apart, producing the classic "phasy"/reverby distortion of
/// the basic phase vocoder. With it, we identify spectral peaks (typically the
/// fundamental + its harmonics in voiced speech) and lock surrounding bins'
/// phases to the peak's phase plus the original phase offset from the input —
/// the harmonic structure stays rigid through the stretch.
///
/// Algorithm overview per output frame:
///   1. Window the input frame at <c>srcPos</c> with Hann; FFT.
///   2. For each bin, compute magnitude and unwrapped phase.
///   3. Find peak bins (local magnitude maxima within a ±2 neighborhood).
///   4. For peak bins: classic phase-vocoder phase advance using the bin's
///      true instantaneous frequency × the synthesis-hop ratio.
///   5. For non-peak bins: lock to the nearest peak's synth phase plus
///      <c>(input_phase[k] - input_phase[nearest_peak])</c>.
///   6. IFFT, window again on synthesis side, overlap-add into output.
///
/// Frame size 2048 (~46 ms at 44.1 kHz) is a good speech sweet spot; synthesis
/// hop 512 gives 75% overlap which satisfies COLA for the squared-Hann
/// reconstruction below.
/// </summary>
internal static class TimeStretch
{
    private const float Tau = (float)(2.0 * Math.PI);

    /// <summary>
    /// Maps the experimental <see cref="EngineSettings.VocoderMode"/> knob to
    /// (FrameSize, SynthesisHop, PeakRadius, UseLocking). Exposed in the Tune
    /// tab so the user can A/B which combination sounds best on their voice.
    /// Mode 1 is the default.
    /// </summary>
    private static (int frameSize, int synthesisHop, int peakRadius, bool useLocking) ResolveMode(int mode) => mode switch
    {
        // Original A/B set
        0 => (2048,  512, 2, false), // Basic phase vocoder (no locking)
        1 => (2048,  512, 2, true),  // Locked, radius 2
        2 => (2048,  512, 1, true),  // Locked, tight peaks (radius 1)
        3 => (2048,  512, 3, true),  // Locked, wide peaks (radius 3) — default after A/B
        4 => (4096, 1024, 2, true),  // Locked, large FFT, radius 2
        5 => (4096, 1024, 1, true),  // Locked, large FFT, tight peaks
        6 => (8192, 2048, 2, true),  // Locked, very large FFT
        7 => (1024,  256, 2, true),  // Locked, small FFT (better transients)
        // Wider-radius exploration (radius 3 was best of the original set —
        // see if going even wider helps further).
        8 => (2048,  512, 4, true),  // radius 4
        9 => (2048,  512, 5, true),  // radius 5
        10 => (2048, 512, 6, true),  // radius 6
        11 => (4096, 1024, 3, true), // radius 3 + large FFT
        12 => (4096, 1024, 4, true), // radius 4 + large FFT
        _ => (2048,  512, 3, true),  // fallback = current default
    };

    public static short[] Stretch(short[] input, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.001) return input;

        int mode = 1;
        try { mode = EngineSettingsCache.Resolve().VocoderMode; } catch { /* default */ }
        var (frameSize, synthesisHop, peakRadius, useLocking) = ResolveMode(mode);

        int bins = frameSize / 2 + 1;
        if (input.Length < frameSize * 2) return input;

        int analysisHop = Math.Max(1, (int)Math.Round(synthesisHop * factor));
        int outLen = (int)(input.Length / factor);
        if (outLen < frameSize) return Array.Empty<short>();

        var window = new float[frameSize];
        for (int i = 0; i < frameSize; i++)
            window[i] = 0.5f - 0.5f * (float)Math.Cos(Tau * i / (frameSize - 1));

        int outAlloc = outLen + frameSize;
        var output = new float[outAlloc];
        var weights = new float[outAlloc];

        var lastInputPhase = new float[bins];
        var synthPhase = new float[bins];
        var mag = new float[bins];
        var ph = new float[bins];
        var isPeak = new bool[bins];

        var re = new float[frameSize];
        var im = new float[frameSize];

        var omegaA = new float[bins];
        for (int k = 0; k < bins; k++)
            omegaA[k] = (float)(Tau * k * analysisHop / frameSize);
        float hopRatio = (float)synthesisHop / analysisHop;

        int srcPos = 0;
        int outPos = 0;
        bool firstFrame = true;

        while (outPos < outLen && srcPos + frameSize <= input.Length)
        {
            // Window + FFT
            for (int i = 0; i < frameSize; i++)
            {
                re[i] = (input[srcPos + i] / 32768f) * window[i];
                im[i] = 0f;
            }
            Fft.Forward(re, im);

            for (int k = 0; k < bins; k++)
            {
                float r = re[k], iv = im[k];
                mag[k] = (float)Math.Sqrt(r * r + iv * iv);
                ph[k] = (float)Math.Atan2(iv, r);
            }

            if (useLocking)
            {
                // Identify spectral peaks: local maxima vs ±peakRadius neighbors.
                for (int k = 0; k < bins; k++) isPeak[k] = false;
                for (int k = peakRadius; k < bins - peakRadius; k++)
                {
                    float m = mag[k];
                    bool peak = true;
                    for (int d = 1; d <= peakRadius && peak; d++)
                    {
                        if (mag[k - d] >= m || mag[k + d] >= m) peak = false;
                    }
                    if (peak) isPeak[k] = true;
                }
            }

            if (firstFrame)
            {
                for (int k = 0; k < bins; k++) synthPhase[k] = ph[k];
            }
            else if (!useLocking)
            {
                // Basic phase vocoder: per-bin independent advance.
                for (int k = 0; k < bins; k++)
                {
                    float dev = ph[k] - lastInputPhase[k] - omegaA[k];
                    dev -= Tau * (float)Math.Round(dev / Tau);
                    synthPhase[k] += (omegaA[k] + dev) * hopRatio;
                    synthPhase[k] -= Tau * (float)Math.Round(synthPhase[k] / Tau);
                }
            }
            else
            {
                // Phase-locked: advance peak bins by true freq, lock non-peaks to nearest peak.
                int firstPeak = -1;
                for (int k = 0; k < bins; k++)
                {
                    if (!isPeak[k]) continue;
                    if (firstPeak < 0) firstPeak = k;
                    float dev = ph[k] - lastInputPhase[k] - omegaA[k];
                    dev -= Tau * (float)Math.Round(dev / Tau);
                    synthPhase[k] += (omegaA[k] + dev) * hopRatio;
                    synthPhase[k] -= Tau * (float)Math.Round(synthPhase[k] / Tau);
                }

                if (firstPeak < 0)
                {
                    // No structure; classic per-bin advance.
                    for (int k = 0; k < bins; k++)
                    {
                        float dev = ph[k] - lastInputPhase[k] - omegaA[k];
                        dev -= Tau * (float)Math.Round(dev / Tau);
                        synthPhase[k] += (omegaA[k] + dev) * hopRatio;
                    }
                }
                else
                {
                    int curPeak = firstPeak;
                    int nextPeakK = NextPeak(isPeak, curPeak);
                    for (int k = 0; k < bins; k++)
                    {
                        while (nextPeakK < bins && (k - curPeak) > (nextPeakK - k))
                        {
                            curPeak = nextPeakK;
                            nextPeakK = NextPeak(isPeak, curPeak);
                        }
                        if (k != curPeak)
                            synthPhase[k] = synthPhase[curPeak] + (ph[k] - ph[curPeak]);
                    }
                }
            }

            for (int k = 0; k < bins; k++) lastInputPhase[k] = ph[k];

            // Reconstruct spectrum with new phases
            for (int k = 0; k < bins; k++)
            {
                re[k] = mag[k] * (float)Math.Cos(synthPhase[k]);
                im[k] = mag[k] * (float)Math.Sin(synthPhase[k]);
            }
            // Mirror for real-input IFFT
            for (int k = bins; k < frameSize; k++)
            {
                re[k] = re[frameSize - k];
                im[k] = -im[frameSize - k];
            }

            Fft.Inverse(re, im);

            // Synthesis-window + OLA
            for (int i = 0; i < frameSize; i++)
            {
                int idx = outPos + i;
                if (idx >= outAlloc) break;
                float w = window[i];
                output[idx] += re[i] * w;
                weights[idx] += w * w;
            }

            outPos += synthesisHop;
            srcPos += analysisHop;
            firstFrame = false;
        }

        // Normalize and convert to int16
        var result = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            float v = weights[i] > 1e-6f ? output[i] / weights[i] : 0f;
            if (v > 1f) v = 1f;
            else if (v < -1f) v = -1f;
            result[i] = (short)(v * 32767f);
        }
        return result;
    }

    private static int NextPeak(bool[] isPeak, int after)
    {
        for (int k = after + 1; k < isPeak.Length; k++)
            if (isPeak[k]) return k;
        return isPeak.Length; // sentinel
    }
}

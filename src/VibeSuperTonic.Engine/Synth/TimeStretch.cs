namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// WSOLA (Waveform Similarity Overlap-Add) time-stretching for int16 mono PCM.
/// Changes playback duration without altering pitch. Each frame's source position
/// is found via normalized cross-correlation with the natural continuation of the
/// previous frame, so adjacent frames align in phase and the output is free of the
/// "robotic" interference patterns that plain OLA produces at non-unity factors.
///
/// Tuning notes:
///   FrameSize=4096 (≈93ms at 44.1kHz) gives ~9 cycles of a 100Hz male fundamental
///     for stable phase reference (was 2048 ≈ 46ms — too short for low pitches).
///   SynthesisHop=1024 = 75% overlap with FrameSize. Hann window at 75% overlap
///     satisfies COLA so output amplitude is constant.
///   SearchRadius=441 (≈10ms = one full cycle of 100Hz) so the similarity search
///     can find correct phase alignment for any voice fundamental we'll see.
///   CorrelationLen=512 (≈11.6ms) covers about one full cycle of low pitches.
/// </summary>
internal static class TimeStretch
{
    private const int FrameSize = 4096;
    private const int SynthesisHop = 1024;
    private const int SearchRadius = 441;
    private const int CorrelationLen = 512;

    public static short[] Stretch(short[] input, double factor)
    {
        if (Math.Abs(factor - 1.0) < 0.001) return input;
        if (input.Length < FrameSize * 2) return input;

        int analysisHop = Math.Max(1, (int)Math.Round(SynthesisHop * factor));
        int outLen = (int)(input.Length / factor);
        if (outLen < FrameSize) return Array.Empty<short>();

        var output = new float[outLen];
        var weights = new float[outLen];

        var window = new float[FrameSize];
        for (int i = 0; i < FrameSize; i++)
            window[i] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * i / (FrameSize - 1)));

        // Place first frame: input[0..FrameSize] → output[0..FrameSize]
        for (int i = 0; i < FrameSize; i++)
        {
            output[i] += (input[i] / 32768f) * window[i];
            weights[i] += window[i];
        }
        int srcPos = 0;
        int outPos = SynthesisHop;

        while (outPos + FrameSize <= outLen)
        {
            int refStart = srcPos + SynthesisHop;
            if (refStart + CorrelationLen >= input.Length) break;

            int targetSrc = srcPos + analysisHop;
            int bestSrc = targetSrc;
            double bestScore = double.NegativeInfinity;

            // Pre-compute reference norm once per frame (it doesn't depend on candidate)
            double normRef = 0;
            for (int k = 0; k < CorrelationLen; k++)
            {
                double r = input[refStart + k] / 32768.0;
                normRef += r * r;
            }

            for (int delta = -SearchRadius; delta <= SearchRadius; delta++)
            {
                int candidate = targetSrc + delta;
                if (candidate < 0) continue;
                if (candidate + CorrelationLen >= input.Length) continue;

                double dot = 0, normCand = 0;
                for (int k = 0; k < CorrelationLen; k++)
                {
                    double r = input[refStart + k] / 32768.0;
                    double c = input[candidate + k] / 32768.0;
                    dot += r * c;
                    normCand += c * c;
                }
                double norm = Math.Sqrt(normRef * normCand);
                double score = norm > 1e-9 ? dot / norm : 0;

                if (score > bestScore) { bestScore = score; bestSrc = candidate; }
            }

            if (bestSrc + FrameSize > input.Length) break;

            for (int i = 0; i < FrameSize; i++)
            {
                if (outPos + i >= outLen) break;
                float w = window[i];
                output[outPos + i] += (input[bestSrc + i] / 32768f) * w;
                weights[outPos + i] += w;
            }

            srcPos = bestSrc;
            outPos += SynthesisHop;
        }

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
}

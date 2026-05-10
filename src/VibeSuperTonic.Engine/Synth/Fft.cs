namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// In-place radix-2 Cooley-Tukey FFT for power-of-two sizes. Operates on
/// separate real/imaginary float arrays — avoids a Complex struct dependency
/// and keeps cache locality tight for the phase-vocoder hot path.
/// </summary>
internal static class Fft
{
    /// <summary>
    /// Forward FFT (time → frequency). Length must be a power of two.
    /// Result is in-place: real[k] + i·imag[k] for bin k = 0..N-1.
    /// </summary>
    public static void Forward(float[] real, float[] imag)
    {
        int n = real.Length;

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Butterflies
        for (int size = 2; size <= n; size *= 2)
        {
            int halfSize = size / 2;
            double phaseStep = -2.0 * Math.PI / size;
            for (int i = 0; i < n; i += size)
            {
                for (int j = 0; j < halfSize; j++)
                {
                    double phase = phaseStep * j;
                    float wr = (float)Math.Cos(phase);
                    float wi = (float)Math.Sin(phase);
                    int idx1 = i + j;
                    int idx2 = i + j + halfSize;
                    float tr = wr * real[idx2] - wi * imag[idx2];
                    float ti = wr * imag[idx2] + wi * real[idx2];
                    real[idx2] = real[idx1] - tr;
                    imag[idx2] = imag[idx1] - ti;
                    real[idx1] += tr;
                    imag[idx1] += ti;
                }
            }
        }
    }

    /// <summary>
    /// Inverse FFT (frequency → time). Trick: IFFT(x) == conj(FFT(conj(x))) / N.
    /// </summary>
    public static void Inverse(float[] real, float[] imag)
    {
        int n = real.Length;
        for (int i = 0; i < n; i++) imag[i] = -imag[i];
        Forward(real, imag);
        float invN = 1f / n;
        for (int i = 0; i < n; i++)
        {
            real[i] *= invN;
            imag[i] = -imag[i] * invN;
        }
    }
}

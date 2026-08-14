using System;
using System.Numerics;

namespace VibeSuperTonic.Engine.Synth;

/// <summary>
/// C# port of Bill Cook's Sonic library (https://github.com/waywardgeek/sonic),
/// stripped to a single-stream mono int16 time-stretch. BSD license.
///
/// Sonic is pitch-synchronous overlap-add (PSOLA-style), designed specifically
/// for speech speedup — used in Android TTS, audiobook players, screen readers.
/// Replaces the phase-vocoder family that plateaued at audible artifacts after
/// five rounds of perceptual A/B (formant smearing + residual inter-bin
/// phasing — the standard phase-vocoder failure modes for voice).
///
/// Algorithm summary for speedup factor s &gt; 1:
///   • Detect the local pitch period P by AMDF over the 65–400 Hz range.
///   • If s ≥ 2: emit one crossfaded block of length <c>P/(s−1)</c>,
///                consume <c>P + P/(s−1)</c> input samples → ratio 1/s.
///   • If 1 &lt; s &lt; 2: emit one crossfaded block of length <c>P</c>,
///                consume <c>2P</c>, then copy <c>P·(2−s)/(s−1)</c> input
///                samples verbatim — interleaving compression with passthrough
///                to land on ratio 1/s overall.
///
/// For slowdown s &lt; 1 the algorithm inserts extra crossfaded periods. Our
/// engine clamps DSP rate to [0.5, 2.0] but both branches are implemented for
/// completeness and so changing the clamp doesn't silently fall through.
///
/// Why this beats the phase vocoder for speech:
///   • Each output pitch period is bit-perfect from the input (minus the
///     crossfade region). Formants don't shift — the spectral envelope is
///     literally preserved sample-for-sample.
///   • No bin-quantized frequency representation, so no inter-harmonic phasing
///     and no per-bin phase tracking that can drift.
///   • Unvoiced regions still produce a "best-fit" period; crossfading
///     averages out the noise content, which is acoustically benign for /s/,
///     /f/, breath, etc.
/// </summary>
internal sealed class Sonic
{
    // Speech pitch range. 65 Hz covers low male F0 with margin; 400 Hz covers
    // high female / child. Outside this band the algorithm still works but
    // period detection wanders into harmonic multiples.
    private const int MinPitchHz = 65;
    private const int MaxPitchHz = 400;

    private readonly int _minPeriod;
    private readonly int _maxPeriod;
    private readonly int _maxRequired;
    private readonly float _speed;

    private short[] _inputBuffer;
    private short[] _outputBuffer;
    private int _numInputSamples;
    private int _numOutputSamples;
    private int _remainingInputToCopy;

    public Sonic(int sampleRate, float speed)
    {
        _maxPeriod = sampleRate / MinPitchHz;
        _minPeriod = sampleRate / MaxPitchHz;
        _maxRequired = _maxPeriod * 2;
        _speed = speed;
        _inputBuffer  = new short[_maxRequired * 2];
        _outputBuffer = new short[_maxRequired * 2];
    }

    public int NumOutputSamples => _numOutputSamples;

    public void WriteSamples(short[] samples, int offset, int count)
    {
        EnsureCapacity(ref _inputBuffer, _numInputSamples + count);
        Array.Copy(samples, offset, _inputBuffer, _numInputSamples, count);
        _numInputSamples += count;
        ProcessStreamInput();
    }

    /// <summary>
    /// Drain everything that can still be processed by padding with silence.
    /// Output beyond the algorithm's natural tail is silent and the wrapper
    /// truncates to the expected length.
    /// </summary>
    public void Flush()
    {
        if (_numInputSamples == 0) return;
        int pad = _maxRequired;
        EnsureCapacity(ref _inputBuffer, _numInputSamples + pad);
        Array.Clear(_inputBuffer, _numInputSamples, pad);
        _numInputSamples += pad;
        ProcessStreamInput();
    }

    public int ReadSamples(short[] dest, int destOffset, int max)
    {
        int n = Math.Min(_numOutputSamples, max);
        if (n <= 0) return 0;
        Array.Copy(_outputBuffer, 0, dest, destOffset, n);
        _numOutputSamples -= n;
        if (_numOutputSamples > 0)
            Array.Copy(_outputBuffer, n, _outputBuffer, 0, _numOutputSamples);
        return n;
    }

    private static void EnsureCapacity(ref short[] buf, int needed)
    {
        if (needed <= buf.Length) return;
        int newSize = Math.Max(buf.Length * 2, needed);
        Array.Resize(ref buf, newSize);
    }

    private void ShiftInputLeft(int count)
    {
        if (count >= _numInputSamples) { _numInputSamples = 0; return; }
        Array.Copy(_inputBuffer, count, _inputBuffer, 0, _numInputSamples - count);
        _numInputSamples -= count;
    }

    private void ProcessStreamInput()
    {
        // Speed of 1.0 (within tolerance) is a passthrough; the wrapper
        // short-circuits earlier but defend in depth.
        if (Math.Abs(_speed - 1.0f) < 0.0001f)
        {
            EnsureCapacity(ref _outputBuffer, _numOutputSamples + _numInputSamples);
            Array.Copy(_inputBuffer, 0, _outputBuffer, _numOutputSamples, _numInputSamples);
            _numOutputSamples += _numInputSamples;
            _numInputSamples = 0;
            return;
        }

        while (_numInputSamples >= _maxRequired)
        {
            if (_remainingInputToCopy > 0)
            {
                int copy = Math.Min(_remainingInputToCopy, _numInputSamples);
                EnsureCapacity(ref _outputBuffer, _numOutputSamples + copy);
                Array.Copy(_inputBuffer, 0, _outputBuffer, _numOutputSamples, copy);
                _numOutputSamples += copy;
                _remainingInputToCopy -= copy;
                ShiftInputLeft(copy);
            }
            else
            {
                int period = FindPitchPeriod(_inputBuffer, 0);
                if (_speed > 1.0f) SkipPitchPeriod(period);
                else InsertPitchPeriod(period);
            }
        }
    }

    /// <summary>
    /// AMDF (Average Magnitude Difference Function) over the candidate period
    /// range. Pick the period that minimizes diff/period (cross-multiplied to
    /// stay in integer math). Faster than autocorrelation and just as accurate
    /// for the period-spotting use case.
    /// </summary>
    private int FindPitchPeriod(short[] samples, int offset)
    {
        int bestPeriod = 0;
        long bestDiff = 0;
        for (int period = _minPeriod; period <= _maxPeriod; period++)
        {
            long diff = AmdfDiff(samples, offset, period);
            // bestPeriod == 0 → first candidate; otherwise compare diff/period
            // to bestDiff/bestPeriod by cross-multiplication.
            if (bestPeriod == 0 || diff * bestPeriod < bestDiff * period)
            {
                bestDiff = diff;
                bestPeriod = period;
            }
        }
        return bestPeriod;
    }

    /// <summary>
    /// Sum |samples[i] − samples[i+period]| over i ∈ [0, period). SIMD-vectorized
    /// inner loop — the hot path of pitch detection (~225K ops per
    /// <see cref="FindPitchPeriod"/> call, ~500 calls per 5-second chunk).
    ///
    /// Widens int16 to int32 before subtracting (Vector&lt;short&gt; arithmetic
    /// wraps modulo 65536, which would corrupt diffs above ±32767). Per-lane
    /// int32 accumulation tops out around 11M per lane on a 678-sample period,
    /// well under int32 range. The tail loop handles the remainder.
    /// </summary>
    private static long AmdfDiff(short[] samples, int offset, int period)
    {
        int simdLen = Vector<short>.Count;
        Vector<int> accumLo = Vector<int>.Zero;
        Vector<int> accumHi = Vector<int>.Zero;
        int i = 0;
        int vEnd = period - simdLen;
        for (; i <= vEnd; i += simdLen)
        {
            var sV = new Vector<short>(samples, offset + i);
            var pV = new Vector<short>(samples, offset + period + i);
            Vector.Widen(sV, out Vector<int> sLo, out Vector<int> sHi);
            Vector.Widen(pV, out Vector<int> pLo, out Vector<int> pHi);
            accumLo += Vector.Abs(sLo - pLo);
            accumHi += Vector.Abs(sHi - pHi);
        }
        long diff = 0;
        for (int k = 0; k < Vector<int>.Count; k++) diff += accumLo[k] + accumHi[k];
        for (; i < period; i++)
        {
            int s = samples[offset + i];
            int p = samples[offset + period + i];
            diff += s >= p ? s - p : p - s;
        }
        return diff;
    }

    private void SkipPitchPeriod(int period)
    {
        int newSamples;
        if (_speed >= 2.0f)
        {
            newSamples = (int)(period / (_speed - 1.0f));
        }
        else
        {
            // 1 < speed < 2: do one full-period crossfade, then queue up
            // remainingInputToCopy samples to pass through verbatim before
            // the next crossfade. Sum interleaves to ratio 1/speed.
            newSamples = period;
            _remainingInputToCopy = (int)(period * (2.0f - _speed) / (_speed - 1.0f));
        }
        EnsureCapacity(ref _outputBuffer, _numOutputSamples + newSamples);
        OverlapAdd(newSamples,
                   _outputBuffer, _numOutputSamples,
                   _inputBuffer, 0,        // ramp-down: first period of input
                   _inputBuffer, period);  // ramp-up: second period of input
        _numOutputSamples += newSamples;
        ShiftInputLeft(period + newSamples);
    }

    private void InsertPitchPeriod(int period)
    {
        int newSamples;
        if (_speed < 0.5f)
        {
            newSamples = (int)(period * _speed / (1.0f - _speed));
        }
        else
        {
            newSamples = period;
            _remainingInputToCopy = (int)(period * (2.0f * _speed - 1.0f) / (1.0f - _speed));
        }
        EnsureCapacity(ref _outputBuffer, _numOutputSamples + period + newSamples);
        // First emit one full period verbatim
        Array.Copy(_inputBuffer, 0, _outputBuffer, _numOutputSamples, period);
        _numOutputSamples += period;
        // Then a crossfaded "extra" segment that wraps back to the start
        OverlapAdd(newSamples,
                   _outputBuffer, _numOutputSamples,
                   _inputBuffer, period,  // ramp-down: tail of period 2
                   _inputBuffer, 0);      // ramp-up: head of period 1 (reused)
        _numOutputSamples += newSamples;
        ShiftInputLeft(newSamples);
    }

    private static void OverlapAdd(int n,
                                   short[] dst, int dstOff,
                                   short[] rampDown, int downOff,
                                   short[] rampUp, int upOff)
    {
        // Linear crossfade. Sonic's choice (rather than equal-power) — the two
        // sources are correlated (adjacent pitch periods of the same voice),
        // so linear sums to roughly constant energy without the equal-power
        // dip that you'd want for uncorrelated material.
        for (int i = 0; i < n; i++)
        {
            int d = rampDown[downOff + i];
            int u = rampUp[upOff + i];
            int v = (d * (n - i) + u * i) / n;
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            dst[dstOff + i] = (short)v;
        }
    }
}

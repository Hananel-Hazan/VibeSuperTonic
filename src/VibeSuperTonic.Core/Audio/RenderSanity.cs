namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// One frame of the amplitude envelope, plus the geometry needed to read it.
/// </summary>
/// <param name="Frames">RMS per frame, normalised so the loudest frame is 1.0.</param>
/// <param name="HopMs">Milliseconds per frame.</param>
public sealed record SpeechEnvelope(float[] Frames, int HopMs)
{
    public double DurationSeconds => Frames.Length * HopMs / 1000.0;

    /// <summary>Envelope sampling rate in Hz — what modulation analysis works against.</summary>
    public double FrameRateHz => 1000.0 / HopMs;
}

/// <summary>A run of near-silence: a pause between phrases.</summary>
public readonly record struct PauseSpan(double StartSeconds, double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

/// <summary>What the checks concluded, and why.</summary>
public sealed record SanityReport(
    bool IsSpeechLike,
    double DurationSeconds,
    double PeakDbfs,
    int ClippedSamples,
    double ModulationRateHz,
    /// <summary>Fraction of envelope variance at <see cref="ModulationRateHz"/>. Rate without this is not a test.</summary>
    double ModulationStrength,
    /// <summary>Fraction of frames near silence — speech pauses, continuous signals do not.</summary>
    double SilentFrameFraction,
    IReadOnlyList<PauseSpan> Pauses,
    IReadOnlyList<string> Failures);

/// <summary>
/// Decides whether a render is <em>speech</em>, without ears and without a
/// reference recording.
///
/// Phase 0 of the Linux port needed this and had to do it by hand. Its own
/// objection was the sharp one: <em>a fast render of garbage would still pass
/// every number we were measuring.</em> RTF, peak amplitude, RMS and duration
/// are all satisfied perfectly by a buffer of noise, a stuck tone, or the same
/// 200 ms of audio repeated — and by a vocoder that has silently regressed into
/// producing any of those. What distinguishes speech is structure in time:
///
/// <list type="bullet">
/// <item><b>Pauses.</b> Speech is punctuated by gaps at phrase and sentence
/// boundaries. Noise and sustained tones have none; a chunker that dropped its
/// boundaries has the wrong ones.</item>
/// <item><b>Syllable-rate modulation.</b> The amplitude envelope of speech
/// oscillates at roughly 2–8 Hz in every language studied, because that is how
/// fast humans move their jaw. Phase 0 measured 3.59 Hz on Linux against
/// 3.67 Hz on Windows. Noise has no dominant rate; a tone has none; audio
/// played at the wrong speed has the wrong one, which is exactly the failure a
/// DSP-rate bug produces.</item>
/// </list>
///
/// Both survive a different noise realisation from the stochastic sampler, which
/// is why Phase 0 could compare a Linux render against a Windows one at all:
/// envelope correlation <em>rose</em> with window width (0.32 at 20 ms → 0.93 at
/// 500 ms), the signature of the same utterance rendered twice rather than of two
/// different utterances.
///
/// Deliberately dependency-free and fast — a few hundred frames and a seven-bin
/// DFT — so it can run in a unit test, in a packaging check, or on a daemon's
/// first render after a model update.
/// </summary>
public static class RenderSanity
{
    /// <summary>
    /// 20 ms per frame: short enough to resolve a 120 ms pause, and it puts the
    /// envelope's own sample rate at 50 Hz, comfortably above twice the 8 Hz top
    /// of the syllable band.
    /// </summary>
    public const int DefaultHopMs = 20;

    /// <summary>Fraction of peak below which a frame counts as silent.</summary>
    public const double SilenceThreshold = 0.03;

    /// <summary>Shortest run of silence that counts as a pause rather than a stop consonant.</summary>
    public const int MinPauseMs = 120;

    /// <summary>Syllable rate band. Outside this, whatever it is, it is not speech.</summary>
    public const double MinModulationHz = 2.0;
    public const double MaxModulationHz = 8.0;

    /// <summary>
    /// Minimum share of envelope variance the dominant syllable-band component
    /// must carry, for the measured rate to mean anything at all.
    ///
    /// Set low on purpose. Measured: real speech 0.034, white noise 0.015, a
    /// sustained tone 0.000. A threshold between noise and speech would have only
    /// ~1.7x margin on each side and is calibrated against a single render — the
    /// kind of number that false-fails a legitimate voice a year from now. This
    /// one only has to separate "has rhythm" from "has none"; rejecting noise is
    /// <see cref="MinSilentFraction"/>'s job, and it has 10x margin for it.
    ///
    /// Each criterion testing one thing with room to spare beats several
    /// criteria all balanced on the same knife edge.
    /// </summary>
    public const double MinModulationStrength = 0.005;

    /// <summary>
    /// Minimum share of frames near silence. Speech breathes; a continuous signal
    /// does not. Measured: real speech 0.298, white noise and tones 0.000.
    /// </summary>
    public const double MinSilentFraction = 0.03;

    /// <summary>
    /// RMS amplitude per frame, normalised to the loudest frame. Normalising
    /// makes every downstream measure independent of the volume trim, so a
    /// quieter render is not a different render.
    /// </summary>
    public static SpeechEnvelope ComputeEnvelope(ReadOnlySpan<short> pcm, int sampleRate, int hopMs = DefaultHopMs)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (hopMs <= 0) throw new ArgumentOutOfRangeException(nameof(hopMs));

        int frameLen = Math.Max(1, sampleRate * hopMs / 1000);
        int frameCount = pcm.Length / frameLen;
        var frames = new float[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            double sum = 0;
            int start = f * frameLen;
            for (int i = 0; i < frameLen; i++)
            {
                double v = pcm[start + i];
                sum += v * v;
            }
            frames[f] = (float)Math.Sqrt(sum / frameLen);
        }

        float peak = 0;
        foreach (float v in frames) if (v > peak) peak = v;
        if (peak > 0)
            for (int i = 0; i < frames.Length; i++) frames[i] /= peak;

        return new SpeechEnvelope(frames, hopMs);
    }

    /// <summary>
    /// Runs of at least <see cref="MinPauseMs"/> below
    /// <see cref="SilenceThreshold"/> of peak.
    ///
    /// The minimum length is what separates a phrase boundary from the closure of
    /// a stop consonant — the /t/ in "not at all" is a real silence a few tens of
    /// milliseconds long, and counting it would make every utterance look like it
    /// had dozens of pauses.
    /// </summary>
    public static IReadOnlyList<PauseSpan> FindPauses(
        SpeechEnvelope envelope, double threshold = SilenceThreshold, int minMs = MinPauseMs)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var pauses = new List<PauseSpan>();
        int minFrames = Math.Max(1, minMs / envelope.HopMs);

        int run = 0;
        for (int i = 0; i <= envelope.Frames.Length; i++)
        {
            bool silent = i < envelope.Frames.Length && envelope.Frames[i] < threshold;
            if (silent) { run++; continue; }
            if (run >= minFrames)
            {
                int startFrame = i - run;
                pauses.Add(new PauseSpan(
                    startFrame * envelope.HopMs / 1000.0,
                    run * envelope.HopMs / 1000.0));
            }
            run = 0;
        }
        return pauses;
    }

    /// <summary>
    /// Dominant amplitude-modulation frequency within the syllable band.
    ///
    /// A direct DFT over the band rather than an FFT: the envelope is a few
    /// hundred frames and the band is about seven bins wide at any useful
    /// resolution, so the transform costs less than allocating for one.
    /// Returns 0 when the envelope carries no modulation at all — silence, or a
    /// perfectly sustained tone.
    /// </summary>
    public static double ModulationRateHz(SpeechEnvelope envelope) => Modulation(envelope).RateHz;

    /// <summary>
    /// Dominant syllable-band frequency <em>and how much of the envelope actually
    /// sits there</em>.
    ///
    /// The rate alone is not a test, which the first version of this class
    /// learned the hard way: taking the argmax over the band always returns
    /// something inside the band, so white noise scored 2.25 Hz and a 220 Hz sine
    /// scored 7.95 Hz and both were declared speech. Every signal has a loudest
    /// bin; only speech has a loud one.
    ///
    /// <c>Strength</c> is the fraction of the envelope's variance explained by
    /// that single component, so it is near 1 for a cleanly modulated signal and
    /// near <c>1/bins</c> for something with no rhythm at all.
    /// </summary>
    public static (double RateHz, double Strength) Modulation(SpeechEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        int n = envelope.Frames.Length;
        if (n < 8) return (0, 0);

        // Remove the mean: the envelope is strictly positive, and its DC term is
        // larger than everything else combined. Left in, every signal looks like
        // it is modulated at 0 Hz.
        double mean = 0;
        foreach (float v in envelope.Frames) mean += v;
        mean /= n;

        var centred = new double[n];
        double energy = 0;
        for (int i = 0; i < n; i++)
        {
            centred[i] = envelope.Frames[i] - mean;
            energy += centred[i] * centred[i];
        }
        if (energy < 1e-9) return (0, 0);   // flat: nothing is modulating

        double frameRate = envelope.FrameRateHz;
        double best = 0, bestMag = 0;

        // Step finer than the bin spacing an FFT of this length would give, since
        // we are only evaluating a handful of frequencies anyway.
        for (double hz = MinModulationHz; hz <= MaxModulationHz; hz += 0.05)
        {
            double re = 0, im = 0;
            double w = 2 * Math.PI * hz / frameRate;
            for (int i = 0; i < n; i++)
            {
                re += centred[i] * Math.Cos(w * i);
                im -= centred[i] * Math.Sin(w * i);
            }
            double mag = re * re + im * im;
            if (mag > bestMag) { bestMag = mag; best = hz; }
        }

        // Parseval: for a real signal the variance carried by one frequency is
        // 2|X(f)|^2 / N. Dividing by the total gives the fraction explained.
        double strength = 2 * bestMag / (n * energy);
        return (best, Math.Min(1.0, strength));
    }

    /// <summary>
    /// Fraction of frames quieter than <see cref="SilenceThreshold"/>.
    ///
    /// The cheapest thing that separates speech from a continuous signal. Speech
    /// spends real time near silence — closures, phrase boundaries, the gap
    /// before a sentence — and typically lands around 15-30%. Noise and sustained
    /// tones sit at essentially zero, because their short-term RMS barely moves.
    /// </summary>
    public static double SilentFrameFraction(SpeechEnvelope envelope, double threshold = SilenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Frames.Length == 0) return 0;
        int quiet = 0;
        foreach (float v in envelope.Frames) if (v < threshold) quiet++;
        return quiet / (double)envelope.Frames.Length;
    }

    /// <summary>Loudest sample, in dBFS. Returns <see cref="double.NegativeInfinity"/> for pure silence.</summary>
    public static double PeakDbfs(ReadOnlySpan<short> pcm)
    {
        int peak = 0;
        foreach (short s in pcm)
        {
            int a = s == short.MinValue ? short.MaxValue : Math.Abs((int)s);
            if (a > peak) peak = a;
        }
        return peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(peak / (double)short.MaxValue);
    }

    /// <summary>
    /// Samples sitting at full scale. A handful is normal; thousands mean the
    /// signal was clipped, which is audible as distortion and is what a broken
    /// volume trim or a missing clamp produces.
    /// </summary>
    public static int ClippedSamples(ReadOnlySpan<short> pcm)
    {
        int n = 0;
        foreach (short s in pcm)
            if (s >= short.MaxValue || s <= short.MinValue + 1) n++;
        return n;
    }

    /// <summary>
    /// Pearson correlation of two envelopes, after averaging both into
    /// <paramref name="windowMs"/> buckets.
    ///
    /// The window width is the whole trick. Two renders of the same text from a
    /// stochastic sampler disagree sample-by-sample and agree on where the words
    /// are, so correlation climbs with the window — Phase 0 measured 0.32 at
    /// 20 ms and 0.93 at 500 ms on renders it had confirmed by ear were the same
    /// utterance. Comparing at a narrow window and concluding "different" is the
    /// mistake this parameter exists to prevent.
    ///
    /// <b>Returns 0 when either bucketed series is flat</b>, because Pearson
    /// correlation is undefined without variance — and 0 here means "cannot
    /// tell", not "unrelated". Two <em>identical</em> inputs will report 0 if the
    /// window is wide enough to average their structure away, which is the
    /// opposite of what the number looks like it is saying. Keep
    /// <paramref name="windowMs"/> well below the timescale of the structure
    /// being compared; 500 ms against speech is fine because phrases are seconds
    /// long, and it is not fine against anything slower.
    /// </summary>
    public static double Correlate(SpeechEnvelope a, SpeechEnvelope b, int windowMs = 500)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        double[] x = Bucket(a, windowMs), y = Bucket(b, windowMs);
        int n = Math.Min(x.Length, y.Length);
        if (n < 2) return 0;

        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;

        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < n; i++)
        {
            double a1 = x[i] - mx, b1 = y[i] - my;
            num += a1 * b1; dx += a1 * a1; dy += b1 * b1;
        }
        double den = Math.Sqrt(dx * dy);
        return den < 1e-12 ? 0 : num / den;
    }

    private static double[] Bucket(SpeechEnvelope env, int windowMs)
    {
        int per = Math.Max(1, windowMs / env.HopMs);
        int count = env.Frames.Length / per;
        var outp = new double[count];
        for (int i = 0; i < count; i++)
        {
            double sum = 0;
            for (int j = 0; j < per; j++) sum += env.Frames[i * per + j];
            outp[i] = sum / per;
        }
        return outp;
    }

    /// <summary>
    /// The whole check: is this buffer plausibly speech?
    ///
    /// Reports every failed criterion rather than the first, because "not speech"
    /// is not actionable and "no pauses, modulation 0.0 Hz, peak −0.0 dBFS with
    /// 48,000 clipped samples" says which end of the pipeline to look at.
    /// </summary>
    public static SanityReport Check(ReadOnlySpan<short> pcm, int sampleRate, double minSeconds = 0.5)
    {
        var envelope = ComputeEnvelope(pcm, sampleRate);
        var pauses = FindPauses(envelope);
        var (modulation, strength) = Modulation(envelope);
        double silentFraction = SilentFrameFraction(envelope);
        double peak = PeakDbfs(pcm);
        int clipped = ClippedSamples(pcm);
        double seconds = pcm.Length / (double)sampleRate;

        var failures = new List<string>();
        if (seconds < minSeconds)
            failures.Add($"too short: {seconds:F2} s < {minSeconds:F2} s");
        if (double.IsNegativeInfinity(peak))
            failures.Add("silent: every sample is zero");
        else if (peak < -40)
            failures.Add($"far too quiet: peak {peak:F1} dBFS");
        if (clipped > pcm.Length / 100)
            failures.Add($"clipped: {clipped} samples at full scale");
        if (modulation < MinModulationHz || modulation > MaxModulationHz)
            failures.Add($"no syllable-rate modulation: {modulation:F2} Hz outside {MinModulationHz}–{MaxModulationHz} Hz");
        else if (strength < MinModulationStrength)
            failures.Add($"modulation at {modulation:F2} Hz is not prominent: {strength:P1} of envelope variance " +
                         $"< {MinModulationStrength:P0} — continuous signal rather than speech");
        if (silentFraction < MinSilentFraction)
            failures.Add($"no phrase boundaries: {silentFraction:P1} of frames near silence < {MinSilentFraction:P0} " +
                         "— speech pauses to breathe, a continuous signal does not");
        if (seconds >= 2.0 && pauses.Count == 0)
            failures.Add("no pause longer than " + MinPauseMs + " ms in " + $"{seconds:F1} s of audio");

        return new SanityReport(
            failures.Count == 0, seconds, peak, clipped, modulation, strength,
            silentFraction, pauses, failures);
    }
}

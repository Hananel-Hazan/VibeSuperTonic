using System.Diagnostics;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Onnx.Ort;
using VibeSuperTonic.Piper;

namespace VibeSuperTonic.Spike.PiperRender;

/// <summary>
/// P3's calibration run — measures a voice's <c>length_scale</c> curve through
/// the classes that ship, and answers the question P2 left open: is the
/// calibration per tier, or per voice?
///
/// <para><b>Through PiperSynthesizer and EspeakPhonemizer deliberately.</b> The
/// rest of this spike calls ONNX Runtime directly, because P0 and P1 were
/// research and had to depend on nothing. This entry point is the opposite: the
/// numbers it produces become a constant in the product, so they have to come
/// out of the product's own path or they are numbers about something else.</para>
/// </summary>
public static class Calibrate
{
    /// <param name="repeats">
    /// Renders averaged per rung, with the voice's own noise settings — the
    /// product's default is <see cref="PiperCalibrator.Repeats"/>. Pass 0 for the
    /// deterministic curve P2 measured, which is worth having for comparison and
    /// is <b>not</b> what should be stored: noise_w is noise on the duration
    /// predictor, so a curve measured without it describes a render the product
    /// never performs. Measured: 6.4% worst delivered-rate error that way, 2.4%
    /// with the noise on.
    /// </param>
    public static int Run(string[] voicePaths, string? outDir, int repeats = PiperCalibrator.Repeats)
    {
        var resolution = EspeakLibrary.Probe();
        Console.WriteLine(resolution.Describe());
        if (resolution.Path is null)
            Console.Error.WriteLine(
                "  WARNING: binding whatever the loader finds. Ubuntu ships 1.52.0, which has no\n" +
                "  espeak_TextToPhonemesWithTerminator — see EspeakLibrary. Set VST_ESPEAK_LIB.");

        using var phonemizer = new EspeakPhonemizer(resolution);
        var curves = new List<(string Voice, string? Tier, PiperRateCalibration Curve)>();

        foreach (var path in voicePaths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            using var synth = new PiperSynthesizer(path, phonemizer);

            var sw = Stopwatch.StartNew();
            var curve = PiperCalibrator.Measure(
                scale => repeats > 0
                    ? Average(synth, name, scale, synth.Voice, repeats)
                    : Seconds(synth, name, scale),
                DateTimeOffset.UtcNow);
            sw.Stop();

            curves.Add((name, synth.Voice.Quality, curve));

            Console.WriteLine();
            Console.WriteLine($"{name}  ({synth.Voice.Quality ?? "?"}, {synth.SampleRate} Hz, " +
                              $"measured in {sw.Elapsed.TotalSeconds:F1} s)");
            Console.WriteLine("  length_scale   delivered");
            foreach (var p in curve.Points)
                Console.WriteLine($"  {p.LengthScale,10:F2}   {p.DeliveredRate,7:F3}x");
            Console.WriteLine($"  wall at {curve.MaxRate:F3}x");

            if (outDir is not null)
            {
                string file = Path.Combine(outDir, name + ".calibration.json");
                if (curve.TrySave(file, out var error)) Console.WriteLine($"  wrote {file}");
                else Console.Error.WriteLine($"  could not write {file}: {error}");
            }
        }

        // THE QUESTION P2 HANDED FORWARD. If two voices of the same tier answer
        // the same and two tiers of the same voice do not, the calibration is per
        // tier and a shipped table would do. Anything else and it is per voice.
        Console.WriteLine();
        Console.WriteLine("what each voice needs for a requested rate, and what it delivers:");
        Console.WriteLine($"  {"voice",-26} {"tier",-7} " +
                          string.Join(" ", Requests.Select(r => $"{r,8:F2}x")));
        foreach (var (name, tier, curve) in curves)
        {
            var cells = Requests.Select(r =>
            {
                var plan = curve.Plan(r);
                return $"{plan.LengthScale,8:F3}";
            });
            Console.WriteLine($"  {name,-26} {tier ?? "?",-7} {string.Join(" ", cells)}");
        }

        Console.WriteLine();
        Console.WriteLine("cross-applied: each voice planned with ANOTHER's curve, delivered rate error");
        foreach (var (name, _, curve) in curves)
        {
            foreach (var (otherName, _, other) in curves)
            {
                if (ReferenceEquals(curve, other)) continue;
                var worst = Requests.Max(r =>
                {
                    // What this voice would actually deliver if it were driven by
                    // the other voice's calibration.
                    float scale = other.Plan(r).LengthScale;
                    double delivered = Delivered(curve, scale);
                    return Math.Abs(delivered - Math.Min(r, curve.MaxRate)) / r;
                });
                Console.WriteLine($"  {name,-26} driven by {otherName,-26} worst {worst * 100,5:F1}%");
            }
        }

        return 0;
    }

    private static readonly double[] Requests = [0.9, 1.0, 1.05, 1.2, 1.35, 1.6, 1.8];

    /// <summary>
    /// THE EXIT CRITERION, measured rather than argued: plan each rate from the
    /// voice's own curve, render it with the voice's REAL noise settings, and
    /// report what came out.
    ///
    /// <para>Real noise matters and is not a detail. <c>noise_w</c> is noise on
    /// the DURATION predictor — the calibration is measured with it off so the
    /// curve is the model rather than variance, and a shipped render has it at
    /// 0.8. If that biased the result, the calibration would be measuring
    /// something the product never does. Repeats are averaged for the same
    /// reason.</para>
    /// </summary>
    public static int Verify(string[] voicePaths, int repeats)
    {
        var resolution = EspeakLibrary.Probe();
        Console.WriteLine(resolution.Describe());
        using var phonemizer = new EspeakPhonemizer(resolution);

        double worst = 0;
        string worstWhere = "";

        foreach (var path in voicePaths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            using var synth = new PiperSynthesizer(path, phonemizer);

            var curve = PiperRateCalibration.Load(
                Path.Combine(Path.GetDirectoryName(path)!, name + ".calibration.json"));
            if (curve is null)
            {
                Console.Error.WriteLine($"{name}: no calibration.json — run --calibrate --out-dir first");
                continue;
            }

            var voice = synth.Voice;
            double baseline = Average(synth, name, voice.LengthScale, voice, repeats);

            Console.WriteLine();
            Console.WriteLine($"{name} ({voice.Quality ?? "?"}), {repeats} renders per rate, " +
                              $"noise {voice.NoiseScale}/{voice.NoiseW}");
            Console.WriteLine("   requested  length_scale   delivered   error");

            foreach (double requested in Verified)
            {
                var plan = curve.Plan(requested);
                double seconds = Average(synth, name, plan.LengthScale, voice, repeats);
                // The stretch is part of the plan: past the wall it is the only
                // thing serving the rate, so ignoring it would report a failure
                // the product does not have.
                double delivered = baseline / seconds * plan.StretchFactor;
                double error = (delivered - requested) / requested;

                if (Math.Abs(error) > worst)
                {
                    worst = Math.Abs(error);
                    worstWhere = $"{name} at {requested:F2}x";
                }

                Console.WriteLine($"   {requested,9:F2}x  {plan.LengthScale,12:F3}   " +
                                  $"{delivered,8:F3}x   {error * 100,5:F1}%" +
                                  (plan.StretchFactor > 1.001 ? $"  (stretch {plan.StretchFactor:F2})" : ""));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"worst error {worst * 100:F1}%, at {worstWhere}");
        return 0;
    }

    /// <summary>The three the plan's exit criterion names, plus the two edges.</summary>
    private static readonly double[] Verified = [1.0, 1.35, 1.6, 0.9, 1.9];

    private static double Average(
        PiperSynthesizer synth, string voiceId, float lengthScale, PiperVoiceConfig voice, int repeats)
    {
        double total = 0;
        for (int i = 0; i < repeats; i++)
        {
            var options = new PiperOptions(voiceId,
                LengthScale: lengthScale,
                NoiseScale: voice.NoiseScale,
                NoiseW: voice.NoiseW,
                SilenceSeconds: 0f);
            total += synth.Synthesize(PiperCalibrator.ProbeText, options).Length / (double)synth.SampleRate;
        }
        return total / repeats;
    }

    /// <summary>
    /// The curve read forwards: what this voice delivers for a length_scale,
    /// interpolated the same way <see cref="PiperRateCalibration.Plan"/> reads it
    /// backwards. Only the cross-application table needs it.
    /// </summary>
    private static double Delivered(PiperRateCalibration curve, float scale)
    {
        var points = curve.Points.OrderByDescending(p => p.LengthScale).ToArray();
        if (scale >= points[0].LengthScale) return points[0].DeliveredRate;
        if (scale <= points[^1].LengthScale) return points[^1].DeliveredRate;

        for (int i = 1; i < points.Length; i++)
        {
            // Descending by length_scale, so keep going while the bracket is
            // still above the value being looked up.
            if (scale < points[i].LengthScale) continue;
            var lo = points[i - 1];
            var hi = points[i];
            double t = (lo.LengthScale - scale) / (double)(lo.LengthScale - hi.LengthScale);
            return lo.DeliveredRate + t * (hi.DeliveredRate - lo.DeliveredRate);
        }
        return points[^1].DeliveredRate;
    }

    private static double Seconds(PiperSynthesizer synth, string voiceId, float lengthScale)
    {
        // Noise off, so the number is the model rather than variance — the same
        // condition P2 measured the original table under.
        var options = new PiperOptions(voiceId,
            LengthScale: lengthScale,
            NoiseScale: 0f,
            NoiseW: 0f,
            SilenceSeconds: 0f);

        var pcm = synth.Synthesize(PiperCalibrator.ProbeText, options);
        return pcm.Length / (double)synth.SampleRate;
    }
}

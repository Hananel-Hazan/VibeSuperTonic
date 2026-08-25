using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Core.Synthesis.Piper;

/// <summary>
/// One measured point on a voice's <c>length_scale</c> curve.
/// </summary>
/// <param name="LengthScale">What was asked of the model.</param>
/// <param name="DeliveredRate">
/// What came out, as a multiple of the same voice's duration at
/// <c>length_scale</c> 1.0 — measured on rendered audio, never asked of the
/// model.
/// </param>
public readonly record struct PiperRatePoint(float LengthScale, double DeliveredRate);

/// <summary>What to ask of the model, and what the DSP stage must then do.</summary>
/// <param name="LengthScale">For <see cref="PiperOptions.LengthScale"/>.</param>
/// <param name="StretchFactor">
/// For <c>TimeStretch.Stretch</c>, at the voice's own sample rate. 1.0 means the
/// stage is off, which is the ordinary case — it is only non-1.0 past the
/// model's saturation, where nothing else can serve the rate.
/// </param>
public readonly record struct PiperRatePlan(float LengthScale, double StretchFactor);

/// <summary>
/// A voice's measured <c>length_scale</c> curve, and the inversion of it.
///
/// <para><b>Why this exists.</b> <c>length_scale</c> is a duration multiplier,
/// so the obvious rate control is its reciprocal — and the obvious thing is
/// wrong. Measured in
/// <see href="../../../../docs/PIPER-PLAN.md#length-scale-nonlinear">P2</see>:
/// ask for 1.6x by passing 0.625 and the model delivers <b>1.39x</b>. It is not
/// fixed leading and trailing silence — that was measured too, and the speech
/// itself scales by the same 1.39x. The duration predictor simply does not
/// respond linearly to the scale it is given.</para>
///
/// <para>A UI that says 1.6x and delivers 1.39x is the silent divergence this
/// project keeps writing traps about, so the curve is measured per voice and
/// inverted here.</para>
///
/// <para><b>And it saturates.</b> Below roughly <c>length_scale</c> 0.15 the
/// output stops changing — 0.15, 0.05 and 0.01 produce byte-for-byte the same
/// duration. The wall was ~1.97x for lessac <c>high</c> and ~1.88x for
/// <c>medium</c>. Past it the model physically will not go faster, so the rate
/// can only be served by the time-stretch: that is what
/// <see cref="PiperRatePlan.StretchFactor"/> carries, and it is a capability
/// limit rather than a quality one.</para>
/// </summary>
public sealed class PiperRateCalibration
{
    /// <summary>Ascending by <see cref="PiperRatePoint.DeliveredRate"/>.</summary>
    public IReadOnlyList<PiperRatePoint> Points { get; }

    /// <summary>
    /// The text the curve was measured with, and when. Kept because the curve is
    /// mildly text-dependent — the duration predictor rounds each phoneme up to a
    /// whole frame, so a phoneme mix with more short segments loses more to
    /// rounding — and because a calibration whose provenance is unknown is one
    /// nobody can decide to distrust.
    /// </summary>
    public string ProbeText { get; }
    public DateTimeOffset MeasuredUtc { get; }

    /// <summary>The fastest the model will go, whatever it is asked for.</summary>
    public double MaxRate => Points.Count > 0 ? Points[^1].DeliveredRate : 1.0;

    /// <summary>The slowest measured point. Below it the stretch takes over too.</summary>
    public double MinRate => Points.Count > 0 ? Points[0].DeliveredRate : 1.0;

    public PiperRateCalibration(
        IReadOnlyList<PiperRatePoint> points, string probeText, DateTimeOffset measuredUtc)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2)
            throw new ArgumentException("a curve needs at least two points", nameof(points));

        var sorted = points.OrderBy(p => p.DeliveredRate).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i].DeliveredRate <= sorted[i - 1].DeliveredRate)
                throw new ArgumentException(
                    $"the curve is not strictly increasing: {sorted[i - 1].DeliveredRate:F3} then " +
                    $"{sorted[i].DeliveredRate:F3}. A flat pair is the saturation point and belongs " +
                    "to the measurement, not to the table", nameof(points));

        Points = sorted;
        ProbeText = probeText;
        MeasuredUtc = measuredUtc;
    }

    /// <summary>
    /// What upstream does: scale the voice's own <c>length_scale</c> by the
    /// reciprocal of the rate, and hope. The fallback for a voice that has not
    /// been measured yet, and it is <b>wrong by up to 20%</b> — it exists so that
    /// an unmeasured voice speaks rather than refuses, and the daemon says which
    /// it used.
    /// </summary>
    /// <param name="defaultLengthScale">
    /// The voice's own <c>inference.length_scale</c>. <b>Not always 1.0</b>:
    /// <c>en_GB-vctk-medium</c> ships 1.4, and treating that voice's natural
    /// speed as 1.0 makes "no speed change" 16% faster than the voice was meant
    /// to sound.
    /// </param>
    public static PiperRatePlan Reciprocal(double requestedRate, float defaultLengthScale = 1.0f) =>
        new((float)(defaultLengthScale / Math.Clamp(requestedRate, 0.1, 10.0)), 1.0);

    /// <summary>
    /// The <c>length_scale</c> that delivers <paramref name="requestedRate"/>,
    /// and whatever the model cannot deliver as a stretch factor.
    ///
    /// <para>Linear interpolation between measured points. The curve is smooth
    /// and convex, so with a ladder of ten points the interpolation error is far
    /// under the tolerance the plan sets; the alternative — fitting a parametric
    /// curve — would put a model of the model between the measurement and the
    /// answer for no accuracy anyone can hear.</para>
    /// </summary>
    public PiperRatePlan Plan(double requestedRate)
    {
        if (double.IsNaN(requestedRate) || requestedRate <= 0) requestedRate = 1.0;

        // Past the wall: the model gives what it can and the stretch does the
        // rest. This is the one case the "speed comes from Piper" decision
        // cannot cover, and the reason the shared stretch had to learn about
        // sample rates before this phase could use it.
        if (requestedRate > MaxRate)
            return new PiperRatePlan(Points[^1].LengthScale, requestedRate / MaxRate);

        // Slower than anything measured. Symmetric, and reached only by a
        // setting well below 0.5x.
        if (requestedRate < MinRate)
            return new PiperRatePlan(Points[0].LengthScale, requestedRate / MinRate);

        for (int i = 1; i < Points.Count; i++)
        {
            var lo = Points[i - 1];
            var hi = Points[i];
            if (requestedRate > hi.DeliveredRate) continue;

            double span = hi.DeliveredRate - lo.DeliveredRate;
            double t = span <= 0 ? 0 : (requestedRate - lo.DeliveredRate) / span;
            return new PiperRatePlan(
                (float)(lo.LengthScale + t * (hi.LengthScale - lo.LengthScale)), 1.0);
        }

        return new PiperRatePlan(Points[^1].LengthScale, 1.0);
    }

    // ------------------------------------------------------------------ storage

    private sealed record Dto(
        [property: JsonPropertyName("probe_text")] string ProbeText,
        [property: JsonPropertyName("measured_utc")] DateTimeOffset MeasuredUtc,
        [property: JsonPropertyName("points")] IReadOnlyList<PointDto> Points);

    private sealed record PointDto(
        [property: JsonPropertyName("length_scale")] float LengthScale,
        [property: JsonPropertyName("delivered_rate")] double DeliveredRate);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Read a stored curve, or null if there is none or it cannot be read.
    ///
    /// <para>Null rather than an exception for the same reason
    /// <c>BenchmarkStore.Load</c> does it: a measurement that will not parse is a
    /// reason to measure again, never a reason to refuse to speak.</para>
    /// </summary>
    public static PiperRateCalibration? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            if (dto is null || dto.Points.Count < 2) return null;

            return new PiperRateCalibration(
                dto.Points.Select(p => new PiperRatePoint(p.LengthScale, p.DeliveredRate)).ToArray(),
                dto.ProbeText, dto.MeasuredUtc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write it, reporting failure rather than throwing.</summary>
    public bool TrySave(string path, out string? error)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var dto = new Dto(ProbeText, MeasuredUtc,
                Points.Select(p => new PointDto(p.LengthScale, p.DeliveredRate)).ToArray());
            File.WriteAllText(path, JsonSerializer.Serialize(dto, Json));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

/// <summary>
/// Measures a voice's curve, once, by rendering a probe at a ladder of
/// <c>length_scale</c> values and timing what comes out.
///
/// <para>The renderer is a delegate so this is testable without ONNX Runtime,
/// and because the thing being measured is "what this machine's session actually
/// produces" rather than anything the model reports about itself.</para>
/// </summary>
public static class PiperCalibrator
{
    /// <summary>
    /// The probe. Long enough that the fixed leading and trailing silence
    /// (~0.06 s and ~0.09 s, measured in P2) is a small share of the duration,
    /// and phonetically broad enough that the phoneme mix is not unusual.
    /// </summary>
    public const string ProbeText =
        "The quick brown fox jumps over the lazy dog. " +
        "Pack my box with five dozen liquor jugs.";

    /// <summary>
    /// Rungs <b>relative to the voice's own <c>length_scale</c></b>, descending,
    /// going well past the wall on purpose: the saturation point is found rather
    /// than assumed, because it differs per voice — 1.55x for
    /// <c>en_GB-vctk-medium</c> against 2.23x for <c>en_US-lessac-high</c> — and
    /// a hardcoded one would be wrong for the first voice nobody measured.
    ///
    /// <para><b>Relative, and that was a defect.</b> These were absolute
    /// <c>length_scale</c> values, which is the same thing for every voice whose
    /// <c>inference.length_scale</c> is 1.0 — and the five voices measured while
    /// this was written all were. <c>en_GB-vctk-medium</c> ships <b>1.4</b>, and
    /// against an absolute ladder it measured a uniform 18.7% error at every
    /// rate INCLUDING 1.0x, which cannot be a curve problem: the voice's natural
    /// speed is its own default, and anchoring 1.0x anywhere else makes "no speed
    /// change" a speed change.</para>
    /// </summary>
    public static readonly IReadOnlyList<float> Ladder =
    [
        1.5f, 1.3f, 1.15f, 1.0f, 0.9f, 0.8f, 0.7f, 0.6f,
        0.5f, 0.4f, 0.3f, 0.2f, 0.1f,
    ];

    /// <summary>
    /// Two rungs whose durations differ by less than this produced the same
    /// render, so the second one has nothing to add to the table.
    ///
    /// <para><b>A flat pair is skipped, not treated as the end of the curve.</b>
    /// That distinction cost a measurement run: with a coarse ladder a flat pair
    /// only happens at the wall, so "stop here" looked right — and with a dense
    /// one, 1.05 and 1.00 differ by less than half a percent on some voices, so
    /// the first German voice measured reported its wall at 1.000x and would have
    /// been driven by a two-point table. Saturation is now simply where the
    /// ladder runs out of rungs that change anything.</para>
    /// </summary>
    private const double SameRenderWithin = 0.005;

    /// <summary>
    /// Renders averaged per rung, and <b>with the voice's own noise settings</b>.
    ///
    /// <para>Both halves of that were measured, on five voices, and both matter
    /// more than they look. <c>noise_w</c> is noise on the DURATION predictor, so
    /// a curve measured with the noise off describes a render the product never
    /// performs: calibrating that way left a worst-case delivered-rate error of
    /// <b>6.4%</b>, and calibrating through the voice's real settings brought the
    /// same five voices to <b>2.1%</b>. The averaging is what makes a stochastic
    /// predictor's output a measurement rather than a sample — one render per
    /// rung is a coin toss written into a file the product then trusts.</para>
    ///
    /// <para>Four is where the cost stops buying accuracy: 17 rungs at 4 renders
    /// is about 5 seconds for a <c>medium</c> voice and 25 for a <c>high</c> one,
    /// once per voice, and the verified error stops improving below it.</para>
    /// </summary>
    public const int Repeats = 4;

    /// <param name="secondsAt">
    /// Renders the probe at an absolute <c>length_scale</c> and returns the
    /// audio's duration in seconds. Called once per ladder rung, plus nothing
    /// else.
    /// </param>
    /// <param name="now">Injected so a stored curve's timestamp is testable.</param>
    /// <param name="defaultLengthScale">
    /// The voice's own <c>inference.length_scale</c>, which is what <b>1.0x
    /// means</b> for it — the ladder is scaled by this and the baseline is
    /// measured at it. See <see cref="Ladder"/> for the voice that proved this
    /// is not always 1.0.
    /// </param>
    public static PiperRateCalibration Measure(
        Func<float, double> secondsAt,
        DateTimeOffset now,
        float defaultLengthScale = 1.0f,
        IReadOnlyList<float>? ladder = null,
        string probeText = ProbeText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secondsAt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(defaultLengthScale);
        ladder ??= Ladder;

        double baseline = secondsAt(defaultLengthScale);
        if (baseline <= 0)
            throw new InvalidOperationException(
                $"the probe rendered no audio at the voice's own length_scale " +
                $"({defaultLengthScale}), so there is nothing to measure against");

        var points = new List<PiperRatePoint>();
        double previousSeconds = double.NaN;

        foreach (float relative in ladder.OrderByDescending(s => s))
        {
            cancellationToken.ThrowIfCancellationRequested();

            float scale = relative * defaultLengthScale;
            double seconds = relative == 1.0f ? baseline : secondsAt(scale);
            if (seconds <= 0) continue;

            // Nothing changed at this rung. Recording it would put a duplicate
            // rate in a table that has to be strictly increasing to be
            // invertible; skipping it leaves the last rung that DID change as the
            // wall, which is what the wall is.
            if (!double.IsNaN(previousSeconds)
                && Math.Abs(seconds - previousSeconds) / previousSeconds < SameRenderWithin)
                continue;

            previousSeconds = seconds;
            points.Add(new PiperRatePoint(scale, baseline / seconds));
        }

        if (points.Count < 2)
            throw new InvalidOperationException(
                $"the probe produced {points.Count} usable point(s); a curve needs at least two");

        return new PiperRateCalibration(points, probeText, now);
    }
}

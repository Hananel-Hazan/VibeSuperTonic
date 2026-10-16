using System.Globalization;

namespace VibeSuperTonic.Core.Settings;

/// <summary>
/// The ranges the Tune tab accepts for the three knobs it shares with the Windows
/// Advanced tab, which clamps them with numeric up-downs.
///
/// <para>A text box cannot clamp, and the daemon would take a typo without
/// complaint — a <c>RateClampCeiling</c> of 13 instead of 1.3 is a model asked to
/// speak thirteen times faster than it can, and the symptom is garbled audio from
/// a setting that "saved fine". So the tab refuses out-of-range input with a
/// sentence instead. The numbers are Windows' own (<c>AdvancedTab</c>), so one
/// settings.json is valid on both.</para>
/// </summary>
public static class KnobRanges
{
    public const double RateClampMin = 1.10, RateClampMax = 1.50;
    public const double SilenceMin = 0.0, SilenceMax = 1.0;

    /// <summary>Why <paramref name="value"/> is not acceptable for <paramref name="key"/>, or null when it is (or the key has no range).</summary>
    public static string? Check(string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "that is not a number";

        switch (key)
        {
            case "RateClampCeiling":
                return value is >= RateClampMin and <= RateClampMax
                    ? null
                    : $"must be between {Fmt(RateClampMin)} and {Fmt(RateClampMax)}";

            case "SynthesisSilenceSec":
                return value is >= SilenceMin and <= SilenceMax
                    ? null
                    : $"must be between {Fmt(SilenceMin)} and {Fmt(SilenceMax)} seconds";

            case "OnnxInterOpThreads":
                if (value != Math.Floor(value)) return "must be a whole number";
                return value is >= 1 and <= 16 ? null : "must be between 1 and 16";

            default:
                return null;
        }
    }

    private static string Fmt(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);
}

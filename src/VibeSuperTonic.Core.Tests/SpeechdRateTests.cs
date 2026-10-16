using VibeSuperTonic.Core.Audio;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// A screen reader's rate, and the one map both voices read it through.
///
/// <para><b>Reported 2026-09-06 as part of "piper does not obey the speed
/// change."</b> Through Orca the rate slider did nothing at all: the module
/// forwarded <c>SET RATE</c> only to the espeak fallback, and the neural path —
/// <c>vst-ctl render</c> — had no way to carry it. So the setting a screen
/// reader user changes most often reached the one voice they were not
/// listening to.</para>
///
/// <para><b>Why one map and not two.</b> Trap 16 makes the espeak fallback
/// answer whenever the neural voice cannot — no daemon, a model still loading, a
/// hotkey already speaking — and that happens mid-session, between one utterance
/// and the next. Two rate curves would mean the fallback also changed speed, so
/// a user would hear the voice AND the pace change and have no way to tell which
/// failure they were listening to. Both voices read the same wpm map, so a
/// fallback changes timbre and nothing else.</para>
/// </summary>
public sealed class SpeechdRateTests
{
    /// <summary>
    /// espeak-ng's own module uses 175 wpm as its default and 80..450 as its
    /// range. Those are not ours to pick: a user who has spent a year at rate 40
    /// in Orca has calibrated it against every other module on their machine.
    /// </summary>
    [Fact]
    public void The_map_is_espeaks_own()
    {
        Assert.Equal(175, SpeechRate.SpeechdWordsPerMinute(0));
        Assert.Equal(80, SpeechRate.SpeechdWordsPerMinute(-100));
        Assert.Equal(450, SpeechRate.SpeechdWordsPerMinute(100));
    }

    /// <summary>Piecewise linear, and continuous at the default.</summary>
    [Fact]
    public void It_is_linear_either_side_of_the_default()
    {
        Assert.Equal(175 - 95 / 2, SpeechRate.SpeechdWordsPerMinute(-50));
        Assert.Equal(175 + 275 / 2, SpeechRate.SpeechdWordsPerMinute(50));
    }

    /// <summary>
    /// The neural voice has no words per minute — it has a rate multiplier — so
    /// the same map is divided by its own default. Rate 0 must be exactly 1.0:
    /// a screen reader that has never touched the slider must not change how the
    /// voice sounds compared to the hotkey.
    /// </summary>
    [Fact]
    public void The_default_rate_changes_nothing()
    {
        Assert.Equal(1.0, SpeechRate.SpeechdRateScale(0));
    }

    [Fact]
    public void The_scale_is_the_same_curve_the_fallback_speaks_at()
    {
        foreach (int rate in new[] { -100, -50, -10, 0, 10, 50, 100 })
        {
            Assert.Equal(
                SpeechRate.SpeechdWordsPerMinute(rate) / 175.0,
                SpeechRate.SpeechdRateScale(rate),
                precision: 10);
        }
    }

    /// <summary>
    /// speech-dispatcher documents -100..100 and a client is free to be wrong.
    /// Clamped rather than refused: a module that errors on a parameter is a
    /// module the server drops, and a dropped module is a screen reader with no
    /// voice.
    /// </summary>
    [Fact]
    public void A_rate_outside_the_range_is_clamped_not_refused()
    {
        Assert.Equal(SpeechRate.SpeechdRateScale(100), SpeechRate.SpeechdRateScale(9999));
        Assert.Equal(SpeechRate.SpeechdRateScale(-100), SpeechRate.SpeechdRateScale(-9999));
    }

    /// <summary>
    /// The scale multiplies the configured rate rather than replacing it. A
    /// person who set 1.2x in the Tune tab and never moved Orca's slider hears
    /// 1.2x; moving the slider is an adjustment ON that, not a different answer
    /// to the same question.
    /// </summary>
    [Fact]
    public void It_adjusts_the_configured_rate_rather_than_replacing_it()
    {
        Assert.Equal(1.2, 1.2 * SpeechRate.SpeechdRateScale(0), precision: 10);
        Assert.True(1.2 * SpeechRate.SpeechdRateScale(50) > 1.2);
        Assert.True(1.2 * SpeechRate.SpeechdRateScale(-50) < 1.2);
    }
}

using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.SpeechD;
using Xunit;

namespace VibeSuperTonic.SpeechD.Tests;

/// <summary>
/// The screen reader's rate reaching the neural voice.
///
/// <para><b>Reported 2026-09-06.</b> <c>StartNeural</c> took no rate at all, so
/// <c>SET RATE</c> reached only the espeak fallback — the voice a user is not
/// listening to — and Orca's speed slider did nothing to the one they were.</para>
/// </summary>
public sealed class NeuralRateTests
{
    private static IReadOnlyList<string> Args(int rate) =>
        Voices.NeuralArguments("hello", "en_US-lessac-medium", null, rate);

    [Fact]
    public void The_rate_is_on_the_command_line()
    {
        var args = Args(35);
        int at = args.ToList().IndexOf("--rate");

        Assert.True(at >= 0, "--rate was not passed to vst-ctl");
        Assert.Equal("35", args[at + 1]);
    }

    /// <summary>
    /// A negative rate is a value, not a flag. `--rate -50` has to survive both
    /// this list and vst-ctl's own option parsing, where an argument beginning
    /// with a dash is the trap `--voice` already documents.
    /// </summary>
    [Fact]
    public void A_negative_rate_survives_as_a_value()
    {
        var args = Args(-50);
        Assert.Equal("-50", args[args.ToList().IndexOf("--rate") + 1]);
    }

    /// <summary>
    /// THE DEFAULT SENDS NOTHING, and that is a compatibility decision rather
    /// than tidiness: a daemon older than this flag refuses an unknown option, so
    /// every utterance would fall through to espeak. A client that never set a
    /// rate keeps the command line it has always had.
    /// </summary>
    [Fact]
    public void Rate_zero_adds_no_flag_at_all()
    {
        Assert.DoesNotContain("--rate", Args(0));
        Assert.Equal(["render", "--out", "-", "--voice", "en_US-lessac-medium", "hello"], Args(0));
    }

    /// <summary>
    /// The text stays last and is never mistaken for an option, whatever else is
    /// on the line. It comes from whatever window had focus.
    /// </summary>
    [Fact]
    public void The_text_is_last_however_many_options_precede_it()
    {
        Assert.Equal("--dangerous", Voices.NeuralArguments("--dangerous", "v", "de", -20)[^1]);
    }

    /// <summary>
    /// Both voices read one map, so a [trap 16] fallback mid-session changes the
    /// timbre and not the pace. Two copies of these numbers would let that rot
    /// the first time one was edited.
    /// </summary>
    [Fact]
    public void The_fallback_speaks_at_the_same_pace_the_neural_voice_was_asked_for()
    {
        foreach (int rate in new[] { -100, -40, 0, 40, 100 })
        {
            Assert.Equal(
                SpeechRate.SpeechdWordsPerMinute(rate),
                Voices.EspeakWordsPerMinute(rate));

            Assert.Equal(
                SpeechRate.SpeechdWordsPerMinute(rate) / 175.0,
                SpeechRate.SpeechdRateScale(rate),
                precision: 10);
        }
    }
}

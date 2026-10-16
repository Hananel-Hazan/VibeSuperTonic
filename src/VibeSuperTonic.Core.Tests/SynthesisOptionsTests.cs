using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The shape P3 settled on, held against the thing it was chosen to prevent:
/// one engine's per-utterance knobs reaching the other's implementation.
/// </summary>
public class SynthesisOptionsTests
{
    [Fact]
    public void A_backend_handed_the_other_engines_options_says_which_is_which()
    {
        var piper = new PiperOptions("en_US-lessac-medium", LengthScale: 0.5f);

        var ex = Assert.Throws<ArgumentException>(
            () => piper.Require<SupertonicOptions>("Supertonic"));

        // The message has to name both ends. A mis-route is a wiring bug in the
        // host, and "invalid cast" in a daemon log is a morning of bisecting.
        Assert.Contains("Supertonic", ex.Message);
        Assert.Contains("PiperOptions", ex.Message);
        Assert.Contains("en_US-lessac-medium", ex.Message);
    }

    [Fact]
    public void Require_returns_the_same_instance_when_it_matches()
    {
        var supertonic = new SupertonicOptions("M1", "en");
        Assert.Same(supertonic, supertonic.Require<SupertonicOptions>("Supertonic"));
    }

    [Fact]
    public void Both_engines_carry_the_voice_and_the_inter_segment_silence()
    {
        // The two fields that are genuinely shared — and the test exists so that
        // a later "this is only used by one of them" refactor has to argue with
        // something. VoiceId is what selects the engine on Linux at all.
        SynthesisOptions supertonic = new SupertonicOptions("M1", "en", SilenceSeconds: 0.25f);
        SynthesisOptions piper = new PiperOptions("en_US-lessac-medium", SilenceSeconds: 0.25f);

        Assert.Equal("M1", supertonic.VoiceId);
        Assert.Equal("en_US-lessac-medium", piper.VoiceId);
        Assert.Equal(0.25f, supertonic.SilenceSeconds);
        Assert.Equal(0.25f, piper.SilenceSeconds);
    }

    [Fact]
    public void A_voice_id_is_required_of_both()
    {
        Assert.Throws<ArgumentException>(() => new SupertonicOptions("", "en"));
        Assert.Throws<ArgumentException>(() => new PiperOptions("   "));
    }

    [Fact]
    public void Piper_defaults_are_upstreams_and_are_not_meant_to_ship()
    {
        // Recorded rather than asserted for their own sake: every voice carries
        // its own inference block and the host is expected to pass those. If
        // these ever become what a render actually uses, the voice config was
        // not read — which is a bug that sounds like a slightly different voice.
        var piper = new PiperOptions("en_US-lessac-medium");
        Assert.Equal(0.667f, piper.NoiseScale);
        Assert.Equal(0.8f, piper.NoiseW);
        Assert.Equal(1.0f, piper.LengthScale);
        Assert.Equal(0, piper.SpeakerId);
    }
}

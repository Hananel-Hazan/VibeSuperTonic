using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The voice file, and the id assembly it feeds. Both are places where being
/// wrong produces audio that is wrong rather than a render that fails, which is
/// the failure class P1 spent a day proving this project had avoided.
/// </summary>
public class PiperVoiceConfigTests
{
    /// <summary>A real voice's file, trimmed to the keys that are read.</summary>
    private const string Lessac = """
        {
          "audio": { "sample_rate": 22050, "quality": "medium" },
          "espeak": { "voice": "en-us" },
          "inference": { "noise_scale": 0.667, "length_scale": 1, "noise_w": 0.8 },
          "phoneme_type": "espeak",
          "phoneme_id_map": { "_": [0], "^": [1], "$": [2], " ": [3], "a": [10], "ɚ": [20, 21] },
          "num_symbols": 256,
          "num_speakers": 1,
          "language": { "code": "en_US" },
          "dataset": "lessac"
        }
        """;

    [Fact]
    public void The_keys_a_render_needs_are_read()
    {
        var config = PiperVoiceConfig.Parse(Lessac);

        Assert.Equal(22050, config.SampleRate);
        Assert.Equal("en-us", config.EspeakVoice);
        Assert.Equal(1, config.NumSpeakers);
        Assert.Equal(0.667f, config.NoiseScale);
        Assert.Equal(1.0f, config.LengthScale);
        Assert.Equal(0.8f, config.NoiseW);
        Assert.Equal("medium", config.Quality);
        Assert.Equal([20, 21], config.PhonemeIdMap["ɚ"]);
    }

    [Fact]
    public void A_voice_supplies_its_own_defaults_as_options()
    {
        var options = PiperVoiceConfig.Parse(Lessac).DefaultOptions("en_US-lessac-medium");

        Assert.Equal("en_US-lessac-medium", options.VoiceId);
        Assert.Equal(0.667f, options.NoiseScale);
        Assert.Equal(0.8f, options.NoiseW);
    }

    [Theory]
    // Each of these reaches something that divides by it, renders in the wrong
    // language, or indexes a key that is not there. All three are worth a
    // sentence at load rather than a surprise at the first press.
    [InlineData("\"sample_rate\": 22050", "\"sample_rate\": 0", "sample_rate")]
    [InlineData("\"espeak\": { \"voice\": \"en-us\" },", "", "espeak.voice")]
    [InlineData("\"^\": [1], ", "", "'^'")]
    public void What_a_render_cannot_do_without_is_refused_at_load(
        string find, string replace, string mentions)
    {
        var broken = Lessac.Replace(find, replace);
        var ex = Assert.Throws<InvalidDataException>(() => PiperVoiceConfig.Parse(broken, "voice.onnx.json"));
        Assert.Contains(mentions, ex.Message);
        Assert.Contains("voice.onnx.json", ex.Message);
    }

    [Theory]
    // uk_UA's ukrainian_tts is a real voice with this key, and it shipped in the
    // 0.2.10 catalog. Its id map is Cyrillic LETTERS: it carries an espeak.voice
    // like everything else, its map is well formed, and it holds all three
    // required symbols — so nothing but this key separates it from a voice that
    // works. Fed IPA it looks up phonemes that are almost all absent and renders
    // noise, which is why it is refused rather than rendered.
    [InlineData("\"text\"")]
    [InlineData("\"PhonemeType.TEXT\"")]
    public void A_voice_whose_symbols_are_not_phonemes_is_refused(string phonemeType)
    {
        var graphemes = Lessac.Replace("\"phoneme_type\": \"espeak\"", $"\"phoneme_type\": {phonemeType}");
        var ex = Assert.Throws<InvalidDataException>(
            () => PiperVoiceConfig.Parse(graphemes, "voice.onnx.json"));
        Assert.Contains("phoneme_type", ex.Message);
        Assert.Contains("voice.onnx.json", ex.Message);
    }

    [Theory]
    // es_MX-ald-medium says "PhonemeType.ESPEAK" — a Python enum repr that leaked
    // into upstream's config. It is espeak, and rejecting it over its spelling
    // would cost a working voice for a formatting accident. Absent means espeak,
    // which is upstream's own default in config.py.
    [InlineData("\"phoneme_type\": \"espeak\",")]
    [InlineData("\"phoneme_type\": \"PhonemeType.ESPEAK\",")]
    [InlineData("\"phoneme_type\": \"ESPEAK\",")]
    [InlineData("")]
    public void The_espeak_phoneme_type_is_accepted_however_it_is_spelled(string line)
    {
        var config = Lessac.Replace("\"phoneme_type\": \"espeak\",", line);
        Assert.Equal("en-us", PiperVoiceConfig.Parse(config).EspeakVoice);
    }

    [Fact]
    public void Keys_upstream_may_add_are_ignored_rather_than_refused()
    {
        // This is the file `alignments` would arrive in, and a voice that
        // refuses to load because upstream added a key is a voice the user
        // cannot use for a reason that does not affect rendering.
        var extended = Lessac.Replace("\"dataset\": \"lessac\"",
            "\"dataset\": \"lessac\", \"alignments\": { \"whatever\": true }");
        Assert.Equal(22050, PiperVoiceConfig.Parse(extended).SampleRate);
    }

    [Fact]
    public void Ids_are_bos_pad_then_every_phoneme_followed_by_pad_then_eos()
    {
        var map = PiperVoiceConfig.Parse(Lessac).PhonemeIdMap;

        // ^ _ a _ ɚ(two ids) _ $ — the interleave is not optional: a model fed a
        // bare id sequence renders audio, just wrong audio.
        Assert.Equal([1, 0, 10, 0, 20, 21, 0, 2], PiperPhonemes.ToIds(["a", "ɚ"], map));
    }

    [Fact]
    public void A_phoneme_the_map_does_not_know_is_skipped_not_substituted()
    {
        // Upstream logs a warning and carries on. Matching that is the
        // difference between agreeing with piper and being more correct than it,
        // and P1's zero divergences are a statement about agreeing.
        var map = PiperVoiceConfig.Parse(Lessac).PhonemeIdMap;
        Assert.Equal([1, 0, 10, 0, 2], PiperPhonemes.ToIds(["a", "ǀ"], map));
    }
}

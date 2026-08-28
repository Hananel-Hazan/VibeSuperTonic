using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Settings;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Settings scoped to all voices, to one engine, or to one voice.
///
/// <para>Asked for on 2026-08-28: "make the setting to be selected to all, all
/// in the packet (all supertonic, all in piper, etc), or specific on specific
/// voice only". The resolution is root, then this engine, then this voice, and
/// the thing that makes it safe is that an override says only what it says —
/// merged as RAW JSON, so a key that is absent is absent rather than zero.</para>
/// </summary>
public sealed class ScopedSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-scope-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");
    private string Models => Path.Combine(_root, "models");

    public ScopedSettingsTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Models);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Settings(string json) =>
        File.WriteAllText(Path.Combine(Data, "settings.json"), json);

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Models);
        config.Reload(force: true);
        return config;
    }

    /// <summary>Every install that has never used the selector.</summary>
    [Fact]
    public void With_no_overrides_every_voice_gets_the_same_settings()
    {
        Settings("""{ "TotalStep": 6, "EngineSpeed": 1.2 }""");
        var config = Loaded();

        Assert.Equal(6, config.SettingsFor("supertonic:M4").TotalStep);
        Assert.Equal(6, config.SettingsFor("piper:en_US-ljspeech-high").TotalStep);

        // The SAME object, not an equal one: the common path must not rebuild
        // settings per utterance.
        Assert.Same(config.Settings, config.SettingsFor("supertonic:M4"));
    }

    [Fact]
    public void An_engine_override_applies_to_every_voice_of_that_engine()
    {
        Settings("""
            {
              "EngineSpeed": 1.0,
              "PerEngine": { "piper": { "EngineSpeed": 1.4 } }
            }
            """);
        var config = Loaded();

        Assert.Equal(1.4f, config.SettingsFor("piper:en_US-ljspeech-high").EngineSpeed);
        Assert.Equal(1.4f, config.SettingsFor("piper:en_GB-cori-high").EngineSpeed);
        Assert.Equal(1.0f, config.SettingsFor("supertonic:M4").EngineSpeed);
    }

    [Fact]
    public void A_voice_override_beats_its_engine_which_beats_the_file()
    {
        Settings("""
            {
              "EngineSpeed": 1.0,
              "TotalStep": 8,
              "PerEngine": { "piper": { "EngineSpeed": 1.4, "VolumeTrimDb": -3 } },
              "PerVoice":  { "piper:en_US-ljspeech-high": { "EngineSpeed": 0.9 } }
            }
            """);
        var config = Loaded();
        var ljspeech = config.SettingsFor("piper:en_US-ljspeech-high");

        Assert.Equal(0.9f, ljspeech.EngineSpeed);      // the voice wins
        Assert.Equal(-3f, ljspeech.VolumeTrimDb);      // the engine still applies
        Assert.Equal(8, ljspeech.TotalStep);           // and the file underneath
    }

    /// <summary>
    /// The reason overrides are merged as raw JSON. A typed override has to say
    /// "unset" with a sentinel, and 0 dB is a real volume trim — the value a
    /// user picks when they want no trim at all, which a sentinel scheme reads
    /// as "inherit" and silently ignores.
    /// </summary>
    [Fact]
    public void A_zero_in_an_override_is_a_value_not_an_absence()
    {
        Settings("""
            {
              "VolumeTrimDb": -6,
              "PerVoice": { "M4": { "VolumeTrimDb": 0 } }
            }
            """);
        Assert.Equal(0f, Loaded().SettingsFor("supertonic:M4").VolumeTrimDb);
    }

    /// <summary>
    /// Supertonic is keyed by the bare style, which is what the Windows engine's
    /// own PerVoice uses — one settings.json, both platforms.
    /// </summary>
    [Fact]
    public void Supertonic_is_keyed_by_its_style_and_piper_by_its_qualified_id()
    {
        Assert.Equal("M4", SettingsScope.KeyFor(VoiceId.Parse("supertonic:M4")));
        Assert.Equal("M4", SettingsScope.KeyFor(VoiceId.Parse("M4")));
        Assert.Equal("piper:en_US-libritts-high",
            SettingsScope.KeyFor(VoiceId.Parse("piper:en_US-libritts-high")));
    }

    /// <summary>
    /// The speaker is not part of the key: it selects WHO speaks, not how, and a
    /// settings file with 904 sections for one voice is not one anybody can read.
    /// </summary>
    [Fact]
    public void The_speaker_does_not_split_a_voices_settings()
    {
        Settings("""
            {
              "PerVoice": { "piper:en_US-libritts-high": { "EngineSpeed": 1.3 } }
            }
            """);
        var config = Loaded();

        Assert.Equal(1.3f, config.SettingsFor("piper:en_US-libritts-high").EngineSpeed);
        Assert.Equal(1.3f, config.SettingsFor("piper:en_US-libritts-high#57").EngineSpeed);
    }

    /// <summary>
    /// An override section may only carry the keys that describe how a voice
    /// sounds. Otherwise "all Piper voices" could change WHICH voice speaks, or
    /// what the daemon does to the machine.
    /// </summary>
    [Fact]
    public void An_override_cannot_smuggle_in_an_unscoped_key()
    {
        Settings("""
            {
              "VoiceId": "supertonic:M4",
              "Provider": "cpu",
              "PerEngine": { "supertonic": { "VoiceId": "piper:sneaky", "Provider": "gpu", "TotalStep": 4 } }
            }
            """);
        var config = Loaded();
        var scoped = config.SettingsFor("supertonic:M4");

        Assert.Equal(4, scoped.TotalStep);                 // allowed
        Assert.Equal("supertonic:M4", scoped.VoiceId);     // refused
        Assert.Equal("cpu", scoped.Provider);              // refused
    }

    /// <summary>A hand-edited override that does not parse must not silence the voice.</summary>
    [Fact]
    public void A_broken_override_falls_back_to_the_global_settings()
    {
        Settings("""
            {
              "TotalStep": 7,
              "PerVoice": { "M4": { "TotalStep": "not a number" } }
            }
            """);
        Assert.Equal(7, Loaded().SettingsFor("supertonic:M4").TotalStep);
    }

    [Fact]
    public void Session_options_follow_the_same_scope()
    {
        Settings("""
            {
              "MaxChunkChars": 200,
              "PerEngine": { "piper": { "MaxChunkChars": 90, "InterChunkSilenceMs": 0 } }
            }
            """);
        var config = Loaded();

        Assert.Equal(200, config.SessionOptionsFor("supertonic:M4").MaxChunkChars);
        Assert.Equal(90, config.SessionOptionsFor("piper:en_GB-cori-high").MaxChunkChars);
        Assert.Equal(0, config.SessionOptionsFor("piper:en_GB-cori-high").InterChunkSilenceMs);
    }
}

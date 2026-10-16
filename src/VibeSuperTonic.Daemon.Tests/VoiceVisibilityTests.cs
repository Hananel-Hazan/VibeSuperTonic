using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// A voice installed while the daemon is running has to be speakable.
///
/// <para><b>Observed 2026-09-01 in a real log.</b>
/// <c>en_US-hfc_female-medium</c> finished installing at 17:24:26 and fifteen
/// seconds later the router sent it to SUPERTONIC — <c>Voice style not found for
/// 'en_US-hfc_female-medium'</c>, five presses in a row, until the user gave up
/// and chose something else. It worked after the next daemon restart.</para>
///
/// <para>The store cached its answer against the timestamp of <c>piper/</c>. An
/// install creates <c>piper/&lt;id&gt;/</c>, which moves that timestamp, and then
/// downloads two files INTO the new directory over several seconds, which does
/// not. So a scan during the download saw an incomplete directory, cached the
/// set without it, and had no reason to look again.</para>
/// </summary>
public sealed class VoiceVisibilityTests : IDisposable
{
    private readonly string _models = Path.Combine(Path.GetTempPath(), $"vst-voices-{Guid.NewGuid():N}");

    private string PiperRoot => Path.Combine(_models, "piper");

    public VoiceVisibilityTests() => Directory.CreateDirectory(PiperRoot);

    public void Dispose()
    {
        try { Directory.Delete(_models, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The directory appears first; the weights land in it afterwards.</summary>
    private string BeginInstall(string id)
    {
        string dir = Path.Combine(PiperRoot, id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void FinishInstall(string id)
    {
        string dir = Path.Combine(PiperRoot, id);
        File.WriteAllText(Path.Combine(dir, id + ".onnx"), "weights");
        File.WriteAllText(Path.Combine(dir, id + ".onnx.json"), """
            { "audio": { "sample_rate": 22050 },
              "inference": { "length_scale": 1.0, "noise_scale": 0.667, "noise_w": 0.8 },
              "espeak": { "voice": "en-us" },
              "num_speakers": 1,
              "phoneme_id_map": { "^": [1], "$": [2], "_": [0], "a": [3] } }
            """);
    }

    /// <summary>
    /// THE REPORTED FAILURE. Something asks the store mid-download — a press, a
    /// voice list, the calibration of the voice installed just before — and the
    /// answer must not outlive the download that was in progress when it was
    /// given.
    /// </summary>
    [Fact]
    public void A_voice_asked_about_mid_download_is_visible_once_it_lands()
    {
        var store = new PiperVoiceStore(_models);

        BeginInstall("en_US-hfc_female-medium");

        // The scan that poisoned the cache: the directory exists, the files do not.
        Assert.Null(store.ModelPath("en_US-hfc_female-medium"));

        FinishInstall("en_US-hfc_female-medium");

        Assert.NotNull(store.ModelPath("en_US-hfc_female-medium"));
        Assert.Contains("en_US-hfc_female-medium", store.Voices);
    }

    /// <summary>
    /// And its config, which is what the router actually asks for. A null here is
    /// what routed a Piper voice to Supertonic.
    /// </summary>
    [Fact]
    public void Its_config_is_readable_too_so_the_router_sends_it_to_piper()
    {
        var store = new PiperVoiceStore(_models);
        BeginInstall("en_US-hfc_female-medium");
        Assert.Null(store.Config("en_US-hfc_female-medium"));

        FinishInstall("en_US-hfc_female-medium");

        Assert.NotNull(store.Config("en_US-hfc_female-medium"));
    }

    /// <summary>
    /// The explicit hook the install path uses, for the case a filesystem's
    /// timestamp resolution is too coarse to have moved at all.
    /// </summary>
    [Fact]
    public void Invalidate_makes_the_next_question_look_again()
    {
        var store = new PiperVoiceStore(_models);
        BeginInstall("en_GB-alba-medium");
        Assert.Empty(store.Voices);

        FinishInstall("en_GB-alba-medium");
        store.Invalidate();

        Assert.Contains("en_GB-alba-medium", store.Voices);
    }

    /// <summary>
    /// A voice that goes away stops being answered with — the case the old
    /// timestamp DID cover, and which must keep working.
    /// </summary>
    [Fact]
    public void A_removed_voice_stops_being_offered()
    {
        var store = new PiperVoiceStore(_models);
        BeginInstall("en_US-ryan-high");
        FinishInstall("en_US-ryan-high");
        Assert.Contains("en_US-ryan-high", store.Voices);

        Directory.Delete(Path.Combine(PiperRoot, "en_US-ryan-high"), recursive: true);

        Assert.Empty(store.Voices);
        Assert.Null(store.Config("en_US-ryan-high"));
    }

    /// <summary>
    /// A voice dropped in by hand, which INSTALL.txt invites and no explicit
    /// invalidation can know about. This is why the stamp folds in the
    /// subdirectories rather than relying on the install path alone.
    /// </summary>
    [Fact]
    public void A_voice_copied_in_by_hand_is_found_without_being_announced()
    {
        var store = new PiperVoiceStore(_models);
        Assert.Empty(store.Voices);

        BeginInstall("de_DE-thorsten-high");
        FinishInstall("de_DE-thorsten-high");

        Assert.Contains("de_DE-thorsten-high", store.Voices);
    }
}

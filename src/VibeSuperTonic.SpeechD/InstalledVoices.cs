using VibeSuperTonic.Core.Models;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.SpeechD;

/// <summary>
/// What is installed, read from the models directory — the filesystem half of
/// S3, kept apart from <see cref="VoiceList"/> so the mapping it feeds can be
/// tested without one.
///
/// <para><b>The disk, not the daemon.</b> <c>vst-ctl voices</c> would answer this
/// question more completely, and asking it would be wrong: speech-dispatcher
/// starts its modules when the session starts and clients ask for the voice list
/// immediately, so routing that through <c>vst-ctl</c> would boot an 830 MB
/// daemon on every login of every user who has this module installed — including
/// the ones who never speak a word through it. <c>--no-start</c> is not the way
/// out either: it would answer "no voices" in exactly the situation that is
/// normal at login, which is trap 14's silent-install failure with a voice list
/// attached.</para>
///
/// <para>The enumeration itself is Core's, and deliberately: the same
/// <see cref="PiperVoiceInstaller"/> the Voices tab and <c>vst-ctl</c> read
/// through, whose remarks already promise it answers "on a machine where nothing
/// is running". That is what makes this list and the tab's one surface rather
/// than two.</para>
/// </summary>
internal sealed class InstalledVoices
{
    private readonly string _modelsRoot;

    internal InstalledVoices(string modelsRoot) => _modelsRoot = modelsRoot;

    /// <summary>
    /// Read the store and build the rows.
    ///
    /// <para><b>Never throws.</b> This is called from inside a protocol reply, and
    /// an exception here would desynchronise the stream — which is the failure
    /// that makes speech-dispatcher refuse to start at all, taking the user's
    /// working espeak-ng with it. An unreadable store is an empty list.</para>
    /// </summary>
    internal IReadOnlyList<SpeechdVoice> Read()
    {
        try
        {
            return VoiceList.Build(
                SupertonicStyles(),
                new PiperVoiceInstaller(_modelsRoot).Installed(),
                SupertonicLanguages.All.Select(l => l.Code).ToArray());
        }
        catch
        {
            return Array.Empty<SpeechdVoice>();
        }
    }

    /// <summary>
    /// Supertonic's styles, from the filenames in <c>voice_styles/</c> — the same
    /// rule the daemon's own voice verb uses, because there is no manifest of them
    /// and never has been: the engine resolves <c>&lt;style&gt;.json</c> by name,
    /// so the directory is the list.
    /// </summary>
    private IReadOnlyList<string> SupertonicStyles()
    {
        try
        {
            string dir = Path.Combine(_modelsRoot, "voice_styles");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}

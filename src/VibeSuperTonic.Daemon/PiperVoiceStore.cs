using VibeSuperTonic.Core.Models;
using VibeSuperTonic.Core.Synthesis.Piper;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// The Piper voices installed on this machine, and the fact that decides which
/// engine speaks.
///
/// <para><b>The voice selects the engine.</b> There is no <c>Engine</c> setting
/// and there deliberately is not one: a voice belongs to exactly one engine, so
/// a setting that could disagree with the voice is a setting that will. So
/// <c>DefaultVoice</c>, <c>vst-ctl speak --voice</c> and the UI's voice list all
/// route by the same rule — if the id names a directory under
/// <c>models/piper/</c>, it is a Piper voice; otherwise it is a Supertonic
/// style.</para>
///
/// <para><b>The layout</b>, which P4 will fill from the catalog and P3 fills by
/// hand:</para>
/// <code>
///     models/piper/en_US-lessac-medium/en_US-lessac-medium.onnx
///                                     /en_US-lessac-medium.onnx.json
///                                     /calibration.json      &lt;- written by us
/// </code>
///
/// <para>A directory per voice rather than a flat folder, because the
/// calibration is ours and has to live somewhere the voice's own files do not
/// collide with it — and because a voice is then deleted by deleting one
/// directory.</para>
///
/// <para><b>Re-read on demand, cached by directory mtime.</b> Same bargain
/// <see cref="HostConfig"/> makes for settings.json: one <c>stat</c> per
/// utterance, and a voice installed while the daemon runs is usable on the next
/// press rather than the next start.</para>
/// </summary>
public sealed class PiperVoiceStore
{
    // Defined by Core, not here. PiperVoiceInstaller writes this layout and this
    // class reads it; two spellings of "piper" that agree by inspection is how a
    // downloaded voice ends up somewhere the daemon never looks, with both sides
    // reporting success.
    public const string FolderName = PiperVoiceInstaller.StoreFolderName;
    public const string CalibrationFileName = PiperVoiceInstaller.CalibrationFileName;

    private readonly string _root;
    private readonly object _gate = new();
    private Dictionary<string, string> _voices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PiperVoiceConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Mtime, PiperRateCalibration? Curve)> _calibrations =
        new(StringComparer.OrdinalIgnoreCase);
    private long _mtime = long.MinValue;
    private bool _everScanned;

    /// <param name="modelsRoot">The folder holding <c>onnx/</c>, <c>voice_styles/</c> and <c>piper/</c>.</param>
    public PiperVoiceStore(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _root = Path.Combine(modelsRoot, FolderName);
    }

    /// <summary>Where voices are installed, whether or not it exists yet.</summary>
    public string Root => _root;

    /// <summary>Installed voice ids, ordered. Empty on an install with none.</summary>
    public IReadOnlyList<string> Voices
    {
        get { Rescan(); lock (_gate) return _voices.Keys.OrderBy(v => v, StringComparer.Ordinal).ToArray(); }
    }

    /// <summary>
    /// The <c>.onnx</c> for <paramref name="voiceId"/>, or null when this is not
    /// a Piper voice — which is the same question as "should Supertonic speak
    /// this", and is why it is one lookup rather than two.
    /// </summary>
    public string? ModelPath(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return null;
        Rescan();
        lock (_gate) return _voices.GetValueOrDefault(voiceId);
    }

    /// <summary>Where this voice's measured curve is kept.</summary>
    public string CalibrationPath(string voiceId) =>
        Path.Combine(_root, voiceId, CalibrationFileName);

    /// <summary>
    /// The voice's own config — its rate, its espeak voice, its inference
    /// defaults. Cached: it is read on the press path to build the per-utterance
    /// options, and parsing a 5 KB JSON with a 154-entry map per press is a cost
    /// with no reason.
    /// </summary>
    public PiperVoiceConfig? Config(string voiceId)
    {
        if (ModelPath(voiceId) is not { } model) return null;

        lock (_gate)
        {
            if (_configs.TryGetValue(voiceId, out var cached)) return cached;
        }

        PiperVoiceConfig? parsed;
        try { parsed = PiperVoiceConfig.Load(model + ".json"); }
        catch { return null; }      // the caller refuses the utterance and says why

        lock (_gate)
        {
            _configs[voiceId] = parsed;
            return parsed;
        }
    }

    /// <summary>
    /// The stored curve for a voice, or null when it has not been measured yet —
    /// in which case the caller falls back to
    /// <see cref="PiperRateCalibration.Reciprocal"/> and says so.
    ///
    /// <para>Re-read only when the file's mtime moves, which is what lets a
    /// background calibration take effect on the next press: the measurement
    /// finishes, writes, and the utterance after it is planned from the curve
    /// with nothing to invalidate by hand.</para>
    /// </summary>
    public PiperRateCalibration? Calibration(string voiceId)
    {
        string path = CalibrationPath(voiceId);
        long mtime;
        try { mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : long.MinValue; }
        catch { mtime = long.MinValue; }

        lock (_gate)
        {
            if (_calibrations.TryGetValue(voiceId, out var cached) && cached.Mtime == mtime)
                return cached.Curve;
        }

        var curve = PiperRateCalibration.Load(path);
        lock (_gate) _calibrations[voiceId] = (mtime, curve);
        return curve;
    }

    private void Rescan()
    {
        long mtime;
        try
        {
            mtime = Directory.Exists(_root) ? Directory.GetLastWriteTimeUtc(_root).Ticks : long.MinValue;
        }
        catch
        {
            return;   // keep what we have; a store that went away is not a reason to forget it
        }

        lock (_gate)
        {
            if (_everScanned && mtime == _mtime) return;
            _mtime = mtime;
            _everScanned = true;

            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (string dir in Directory.GetDirectories(_root))
                    {
                        string id = Path.GetFileName(dir);
                        string model = Path.Combine(dir, id + ".onnx");

                        // Both files or neither. A directory holding weights
                        // without a config is a half-finished download, and it
                        // must not shadow the Supertonic voice of the same name
                        // or claim to be speakable.
                        if (File.Exists(model) && File.Exists(model + ".json"))
                            found[id] = model;
                    }
                }
            }
            catch
            {
                // A store being replaced under a running daemon. Whatever was
                // enumerated before this point is still true.
            }

            // A voice that went away must not leave its config behind: the
            // directory can be replaced under a running daemon, and a stale
            // sample rate would re-tune the sink to the wrong number.
            foreach (string gone in _configs.Keys.Where(k => !found.ContainsKey(k)).ToArray())
                _configs.Remove(gone);

            _voices = found;
        }
    }
}

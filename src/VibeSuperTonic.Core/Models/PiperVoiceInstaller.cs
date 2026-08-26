namespace VibeSuperTonic.Core.Models;

/// <summary>What became of an install or a remove, in the shape a client reports.</summary>
/// <param name="Ok">Whether the store now holds what was asked for.</param>
/// <param name="VoiceId">The voice acted on.</param>
/// <param name="Message">One sentence, for a user. Present on success and failure alike.</param>
/// <param name="Path">The voice's directory, when there is one.</param>
public sealed record VoiceInstallResult(bool Ok, string VoiceId, string Message, string? Path = null);

/// <summary>
/// Installing and removing Piper voices under a models root — the filesystem
/// half of P4, and pointedly not a downloader.
///
/// <para><b>Pure filesystem work over a models root</b>, which is why it is in
/// Core: the layout <c>models/piper/&lt;id&gt;/</c> is a contract the Voices tab,
/// <c>vst-ctl</c>, the daemon's store and both packers all depend on, and it is
/// the thing that keeps the packers' "no models in the archive" assertion true by
/// construction — a voice can only ever land under a directory the archive does
/// not contain.</para>
///
/// <para><b>The bytes are <see cref="ModelDownloader"/>'s job.</b> One caller
/// more, not a second implementation: mirrors, size and SHA-256 checking, and the
/// re-verify-after-each-source rule all come along, and a voice that fails its
/// hash leaves nothing behind that the store would mistake for a voice.</para>
///
/// <para><b>Removal is the part with a trap in it.</b> A voice directory holds
/// two files that came from upstream and one — <c>calibration.json</c> — that we
/// measured on this machine and that costs 47 seconds to produce for a
/// <c>high</c> tier. Deleting the directory removes all three, which is right for
/// a remove and wrong for a reinstall, so those are two different operations
/// here rather than one with a flag.</para>
/// </summary>
public sealed class PiperVoiceInstaller
{
    /// <summary>
    /// The store's folder name under the models root, and the single definition
    /// of it: the daemon's <c>PiperVoiceStore.FolderName</c> is this constant
    /// rather than a second spelling of "piper". Two spellings that agree by
    /// inspection is how a downloaded voice ends up somewhere the daemon never
    /// looks, with the installer and the store both reporting success.
    /// </summary>
    public const string StoreFolderName = "piper";

    /// <summary>Written by the daemon after measuring, never downloaded.</summary>
    public const string CalibrationFileName = "calibration.json";

    private readonly string _modelsRoot;
    private readonly Func<Manifest, ModelDownloader> _downloaders;

    /// <param name="modelsRoot">The directory holding <c>onnx/</c>, <c>voice_styles/</c> and <c>piper/</c>.</param>
    /// <param name="downloaders">
    /// How to build a downloader for a manifest. Injected so a test can install a
    /// voice without a network, and so the host supplies its own lock describer
    /// on the platform that needs one.
    /// </param>
    public PiperVoiceInstaller(string modelsRoot, Func<Manifest, ModelDownloader>? downloaders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _modelsRoot = modelsRoot;
        _downloaders = downloaders ?? (m => new ModelDownloader(modelsRoot, m));
    }

    /// <summary>Where voices live, whether or not the directory exists.</summary>
    public string StoreRoot => Path.Combine(_modelsRoot, StoreFolderName);

    /// <summary>This voice's directory, whether or not it exists.</summary>
    public string DirectoryFor(string voiceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        return Path.Combine(StoreRoot, voiceId.Trim());
    }

    /// <summary>
    /// Whether the store holds this voice — by the store's own rule, <b>both
    /// files or neither</b>. A directory with weights and no config is a
    /// half-finished download, and answering "yes" for it is how an interrupted
    /// fetch comes to shadow a Supertonic voice of the same name.
    /// </summary>
    public bool IsInstalled(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return false;
        string dir = DirectoryFor(voiceId);
        string model = Path.Combine(dir, voiceId.Trim() + ".onnx");
        return File.Exists(model) && File.Exists(model + ".json");
    }

    /// <summary>
    /// Installed voice ids, ordered. The same enumeration the daemon's store
    /// performs, available to a client that has no daemon — <c>vst-ctl voices</c>
    /// on a machine where nothing is running should still be able to answer.
    /// </summary>
    public IReadOnlyList<string> Installed()
    {
        try
        {
            if (!Directory.Exists(StoreRoot)) return Array.Empty<string>();
            return Directory.GetDirectories(StoreRoot)
                .Select(Path.GetFileName)
                .Where(id => !string.IsNullOrEmpty(id) && IsInstalled(id!))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// How many bytes this voice occupies, or 0 when it is not installed.
    /// Includes the calibration, because the Voices tab's Remove button should
    /// report what actually goes away.
    /// </summary>
    public long BytesOnDisk(string voiceId)
    {
        try
        {
            var dir = new DirectoryInfo(DirectoryFor(voiceId));
            return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Download and verify a voice. Idempotent: a voice already present with the
    /// right hashes is confirmed rather than refetched, which is what makes this
    /// safe to use as a repair.
    /// </summary>
    /// <param name="voice">The catalog entry — the only source of a URL and a hash.</param>
    /// <param name="log">Per-file lines, as the first-run flow produces.</param>
    /// <param name="bytes">Byte-level progress for the file in flight.</param>
    public async Task<VoiceInstallResult> InstallAsync(
        PiperCatalogVoice voice,
        IProgress<string>? log,
        IProgress<DownloadProgress>? bytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voice);
        string dir = DirectoryFor(voice.Id);

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new VoiceInstallResult(false, voice.Id,
                $"cannot create {dir}: {ex.Message}");
        }

        var manifest = PiperCatalog.ManifestFor(voice, StoreFolderName);
        bool ok;
        try
        {
            ok = await _downloaders(manifest).EnsureAllAsync(log, bytes, ct);
        }
        catch (OperationCanceledException)
        {
            // A cancel is not a failure to report as one, but it does leave a
            // directory that may hold half a voice.
            await PruneAsync(voice, manifest, CancellationToken.None);
            throw;
        }

        if (!ok)
        {
            await PruneAsync(voice, manifest, ct);
            return new VoiceInstallResult(false, voice.Id,
                $"{voice.Id} did not install — no source produced a verified file", dir);
        }

        if (!IsInstalled(voice.Id))
        {
            // Belt and braces: the downloader said yes and the store's own rule
            // says no. That can only mean the manifest and the store disagree
            // about where a file lands, which is a wiring bug worth naming
            // rather than a download worth retrying.
            return new VoiceInstallResult(false, voice.Id,
                $"{voice.Id} downloaded but is not where the store looks ({dir})", dir);
        }

        return new VoiceInstallResult(true, voice.Id,
            $"{voice.Id} installed — {voice.SizeSummary()}, {voice.Licence.Name}", dir);
    }

    /// <summary>
    /// Remove a voice and everything in its directory, calibration included.
    ///
    /// <para>The calibration goes deliberately. It describes the relationship
    /// between a requested rate and this voice's <c>length_scale</c> on this
    /// machine; keeping it for a voice that is gone means a reinstall silently
    /// inherits a curve measured against bytes nobody can prove are the same
    /// ones.</para>
    /// </summary>
    public VoiceInstallResult Remove(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return new VoiceInstallResult(false, voiceId ?? "", "no voice named");

        string id = voiceId.Trim();
        string dir = DirectoryFor(id);

        if (!Directory.Exists(dir))
            return new VoiceInstallResult(false, id, $"{id} is not installed");

        long freed = BytesOnDisk(id);
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            return new VoiceInstallResult(false, id, $"could not remove {id}: {ex.Message}", dir);
        }

        return new VoiceInstallResult(true, id,
            $"{id} removed — {freed / (1024 * 1024)} MB freed", dir);
    }

    /// <summary>
    /// After a failed or cancelled install: delete every file that is not the one
    /// the catalog pinned, then delete the directory if what remains is not a
    /// voice.
    ///
    /// <para><b>The rule this enforces is that an install leaves a verified voice
    /// or leaves no voice</b>, and it exists because the obvious version of this
    /// method was wrong in a way a test caught. It asked <see cref="IsInstalled"/>
    /// and returned early when the answer was yes — but "installed" only means
    /// both files are <em>present</em>. A truncated <c>.onnx</c> that fails its
    /// hash is still moved into place by the downloader, and its <c>.onnx.json</c>
    /// downloads perfectly well afterwards, so a voice with corrupt weights
    /// answered yes to that question and was left in the store. It would then
    /// load, or fail inside ORT with something about a protobuf, and the one
    /// thing it would never do is report that its bytes were wrong.</para>
    ///
    /// <para><b>A failed upgrade therefore removes the old voice</b>, and that is
    /// the intended reading rather than an accepted cost: the id's pinned hash
    /// changed, so whatever is on disk is by definition no longer what the
    /// catalog describes. The remedy is the same click again. The calibration
    /// goes with it, for the reason <see cref="Remove"/> gives.</para>
    /// </summary>
    private async Task PruneAsync(PiperCatalogVoice voice, Manifest manifest, CancellationToken ct)
    {
        try
        {
            foreach (var entry in manifest.Files)
            {
                string full = Path.Combine(_modelsRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                if (await ModelDownloader.VerifyAsync(full, entry, ct)) continue;
                try { File.Delete(full); } catch { }
            }

            if (IsInstalled(voice.Id)) return;

            string dir = DirectoryFor(voice.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* a store being written by something else; the store's rule still holds */ }
    }
}

using System.Security.Cryptography;

namespace VibeSuperTonic.Core.Models;

/// <summary>
/// Describes which processes hold <paramref name="path"/> open, or returns null
/// when nothing does.
///
/// Exists so <see cref="ModelDownloader"/> can refuse to overwrite a model a
/// running SAPI host has mapped, without Core knowing how that is discovered.
/// On Windows the launcher supplies a Restart Manager probe; on Linux nothing
/// supplies one, because replacing an open file is legal there — the running
/// process keeps the old inode and the new one lands cleanly.
///
/// This is the whole reason the downloader could not move into Core before: it
/// called LockProbe directly, which is Restart Manager and therefore Windows
/// only. The Phase 0 spike reimplemented manifest parsing and downloading from
/// scratch rather than drag that across — 90 lines of duplicate logic whose
/// existence is recorded in its own comment.
/// </summary>
public delegate string? FileLockDescriber(string path);

public sealed class ModelDownloader
{
    private readonly string _baseDir;
    private readonly Manifest _manifest;
    private readonly HttpClient _http;
    private readonly FileLockDescriber? _lockDescriber;

    /// <param name="lockDescriber">
    /// Optional. When supplied, a locked target is reported and skipped instead
    /// of being overwritten. Omit on platforms where replacing an open file is
    /// safe.
    /// </param>
    public ModelDownloader(string baseDir, Manifest manifest, FileLockDescriber? lockDescriber = null)
    {
        _baseDir = baseDir;
        _manifest = manifest;
        _lockDescriber = lockDescriber;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeSuperTonic/1.0");
    }

    public async Task<bool> EnsureAllAsync(IProgress<string>? log, CancellationToken ct)
    {
        bool allOk = true;
        bool anyEmptyUrl = false;
        foreach (var f in _manifest.Files)
        {
            string fullPath = Path.Combine(_baseDir, f.Path);
            if (await VerifyAsync(fullPath, f, ct))
            {
                log?.Report($"OK   {f.Path}");
                continue;
            }

            // Skip entries with no usable source: the manifest is a template
            // until release time. Tell the user what's missing and how to get it
            // instead of throwing.
            var sources = f.AllSources().ToList();
            if (sources.Count == 0)
            {
                log?.Report($"SKIP {f.Path} — no download URL in manifest. " +
                            "Either copy this file in manually, or fill in a URL in models-manifest.json.");
                anyEmptyUrl = true;
                allOk = false;
                continue;
            }

            // Block if a process holds the existing (stale) file. No describer
            // means the platform does not need the check.
            if (File.Exists(fullPath) && _lockDescriber is not null)
            {
                string? who = _lockDescriber(fullPath);
                if (!string.IsNullOrEmpty(who))
                {
                    log?.Report($"LOCKED {f.Path} — held by: {who}. Close them and retry.");
                    allOk = false;
                    continue;
                }
            }

            log?.Report($"DL   {f.Path}  ({f.Bytes / (1024 * 1024)} MB)…");

            // Try every source in turn, and re-verify after EACH one. A mirror
            // that serves a truncated or stale file must fall through to the
            // next source rather than leaving a corrupt model on disk — which
            // is also why the hash check lives inside the loop instead of after
            // it. A source that downloads fine but hashes wrong is a bad source.
            bool got = false;
            for (int i = 0; i < sources.Count && !got; i++)
            {
                string src = sources[i];
                if (i > 0) log?.Report($"     primary failed — trying mirror {i}: {Shorten(src)}");
                if (!await DownloadAsync(fullPath, src, f, log, ct)) continue;
                if (await VerifyAsync(fullPath, f, ct)) { got = true; break; }
                log?.Report($"FAIL hash mismatch on {f.Path} from {Shorten(src)}");
            }

            if (got) log?.Report($"OK   {f.Path}");
            else
            {
                log?.Report($"FAIL {f.Path} — no source produced a verified file " +
                            $"({sources.Count} tried)");
                allOk = false;
            }
        }
        if (anyEmptyUrl)
        {
            log?.Report("");
            log?.Report("Note: this build's manifest has empty URLs (template). For a self-fixing");
            log?.Report("install, edit models-manifest.json with real Hugging Face download URLs and");
            log?.Report("SHA-256 hashes, then click Repair again.");
        }
        return allOk;
    }

    /// <summary>Host + last path segment — enough to tell sources apart in the log.</summary>
    private static string Shorten(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}/…/{u.Segments[^1]}" : url;

    private async Task<bool> DownloadAsync(string fullPath, string url, ManifestEntry f, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string tmp = fullPath + ".part";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                await src.CopyToAsync(dst, 81920, ct);
            File.Move(tmp, fullPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"FAIL {f.Path}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    private static async Task<bool> VerifyAsync(string fullPath, ManifestEntry f, CancellationToken ct)
    {
        if (!File.Exists(fullPath)) return false;
        if (f.Bytes > 0)
        {
            var info = new FileInfo(fullPath);
            if (info.Length != f.Bytes) return false;
        }
        if (string.IsNullOrEmpty(f.Sha256)) return true;
        try
        {
            using var sha = SHA256.Create();
            await using var fs = File.OpenRead(fullPath);
            var hash = await sha.ComputeHashAsync(fs, ct);
            var hex = Convert.ToHexString(hash);
            return string.Equals(hex, f.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

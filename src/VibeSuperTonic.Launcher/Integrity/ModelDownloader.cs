using System.Security.Cryptography;

namespace VibeSuperTonic.Launcher.Integrity;

internal sealed class ModelDownloader
{
    private readonly string _baseDir;
    private readonly Manifest _manifest;
    private readonly HttpClient _http;

    public ModelDownloader(string baseDir, Manifest manifest)
    {
        _baseDir = baseDir;
        _manifest = manifest;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VibeSuperTonic-Control-Panel/1.0");
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

            // Skip entries with no URL: the manifest is a template until release time.
            // Tell the user what's missing and how to get it instead of throwing.
            if (string.IsNullOrWhiteSpace(f.Url) || !Uri.TryCreate(f.Url, UriKind.Absolute, out _))
            {
                log?.Report($"SKIP {f.Path} — no download URL in manifest. " +
                            "Either copy this file in manually, or fill in a URL in models-manifest.json.");
                anyEmptyUrl = true;
                allOk = false;
                continue;
            }

            // Block if a process holds the existing (stale) file.
            if (File.Exists(fullPath) && !LockProbe.IsWritable(fullPath))
            {
                var holders = LockProbe.GetHolders(fullPath);
                var who = holders.Count > 0
                    ? string.Join(", ", holders.Select(h => $"{h.FriendlyName} (PID {h.Pid})"))
                    : "(unknown processes)";
                log?.Report($"LOCKED {f.Path} — held by: {who}. Close them and retry.");
                allOk = false;
                continue;
            }

            log?.Report($"DL   {f.Path}  ({f.Bytes / (1024 * 1024)} MB)…");
            if (!await DownloadAsync(fullPath, f, log, ct))
            {
                allOk = false;
                continue;
            }
            if (!await VerifyAsync(fullPath, f, ct))
            {
                log?.Report($"FAIL hash mismatch on {f.Path}");
                allOk = false;
            }
            else log?.Report($"OK   {f.Path}");
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

    private async Task<bool> DownloadAsync(string fullPath, ManifestEntry f, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string tmp = fullPath + ".part";
        try
        {
            using var resp = await _http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
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

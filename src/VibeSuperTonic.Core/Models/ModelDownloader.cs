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

/// <summary>
/// How far one file has got. Reported alongside the log lines rather than
/// instead of them.
///
/// <para><b>Added in P4, and the reason is a bar rather than a scroll.</b> The
/// first-run flow downloads 383 MB of Supertonic weights as a fixed set with a
/// line per file, and a log was the right shape for it. A voice install is one
/// file the user chose, of 20 to 137 MB, started from a row they clicked — and a
/// UI that can say only "DL en_US-ljspeech-high.onnx (114 MB)…" and then nothing
/// for two minutes is indistinguishable from one that has hung.</para>
///
/// <para><paramref name="BytesTotal"/> is the manifest's figure, not the
/// server's <c>Content-Length</c>, so it is known before the first byte and
/// cannot be moved by a mirror serving something else — the hash check is what
/// catches that.</para>
/// </summary>
/// <param name="Path">The manifest-relative path being fetched.</param>
/// <param name="BytesReceived">Written so far, this attempt.</param>
/// <param name="BytesTotal">Expected total, or 0 when the manifest does not say.</param>
public readonly record struct DownloadProgress(string Path, long BytesReceived, long BytesTotal)
{
    /// <summary>0..1, or null when the total is unknown.</summary>
    public double? Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesReceived / BytesTotal, 0, 1) : null;
}

public sealed class ModelDownloader
{
    private readonly string _baseDir;
    private readonly Manifest _manifest;
    /// <summary>
    /// One client for the process (review item 64). A client per downloader meant
    /// one per voice install, each holding its own connection pool and never
    /// disposed. Static is the documented lifetime for <see cref="HttpClient"/>.
    /// </summary>
    private static readonly HttpClient _http = CreateClient();

    /// <summary>How many files may be in flight at once (review item 89).</summary>
    internal const int MaxParallelFiles = 2;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VibeSuperTonic/1.0");
        return c;
    }
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
    }

    /// <summary>
    /// A manifest path resolved under <see cref="_baseDir"/>, or null when it
    /// escapes — in which case nothing is written and the caller reports it.
    /// See <see cref="StorePath"/> for why a manifest path needs checking at all.
    /// </summary>
    internal string? ResolveInsideBase(string relativePath) =>
        StorePath.Under(_baseDir, relativePath);

    /// <inheritdoc cref="EnsureAllAsync(IProgress{string}?, IProgress{DownloadProgress}?, CancellationToken)"/>
    public Task<bool> EnsureAllAsync(IProgress<string>? log, CancellationToken ct) =>
        EnsureAllAsync(log, null, ct);

    /// <param name="log">Line per file, as the first-run flow has always had.</param>
    /// <param name="bytes">
    /// Optional byte-level progress for the file in flight. Null costs nothing —
    /// the copy loop is only entered when someone is listening.
    /// </param>
    /// <param name="ct">
    /// Cancellation. <b>The <c>.part</c> file is kept</b>, so the next run resumes
    /// it with a <c>Range</c> request; it is removed only when it can never be
    /// completed (wrong length, a server that refuses the range, a failed hash).
    /// </param>
    public async Task<bool> EnsureAllAsync(
        IProgress<string>? log, IProgress<DownloadProgress>? bytes, CancellationToken ct)
    {
        bool allOk = true;
        bool anyEmptyUrl = false;
        using var gate = new SemaphoreSlim(MaxParallelFiles);
        var tasks = _manifest.Files.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (ok, empty) = await EnsureOneAsync(f, log, bytes, ct);
                if (!ok) Volatile.Write(ref allOk, false);
                if (empty) Volatile.Write(ref anyEmptyUrl, true);
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        if (anyEmptyUrl)
        {
            log?.Report("");
            log?.Report("Note: this build's manifest has empty URLs (template). For a self-fixing");
            log?.Report("install, edit models-manifest.json with real Hugging Face download URLs and");
            log?.Report("SHA-256 hashes, then click Repair again.");
        }
        return allOk;
    }

    private async Task<(bool Ok, bool EmptyUrl)> EnsureOneAsync(
        ManifestEntry f, IProgress<string>? log, IProgress<DownloadProgress>? bytes, CancellationToken ct)
    {
        {
            if (ResolveInsideBase(f.Path) is not { } fullPath)
            {
                log?.Report($"SKIP {f.Path} — escapes the models directory");
                return (false, false);
            }

            if (await VerifyAsync(fullPath, f, ct))
            {
                log?.Report($"OK   {f.Path}");
                return (true, false);
            }

            var sources = f.AllSources().ToList();
            if (sources.Count == 0)
            {
                log?.Report($"SKIP {f.Path} — no download URL in manifest. " +
                            "Either copy this file in manually, or fill in a URL in models-manifest.json.");
                return (false, true);
            }

            if (File.Exists(fullPath) && _lockDescriber is not null)
            {
                string? who = _lockDescriber(fullPath);
                if (!string.IsNullOrEmpty(who))
                {
                    log?.Report($"LOCKED {f.Path} — held by: {who}. Close them and retry.");
                    return (false, false);
                }
            }

            log?.Report($"DL   {f.Path}  ({f.Bytes / (1024 * 1024)} MB)…");

            // Try every source in turn, and re-verify after EACH one. A mirror
            // that serves a truncated or stale file must fall through to the
            // next source rather than leaving a corrupt model on disk. A source
            // that downloads fine but hashes wrong is a bad source.
            bool got = false;
            for (int i = 0; i < sources.Count && !got; i++)
            {
                string src = sources[i];
                if (i > 0)
                {
                    log?.Report($"     primary failed — trying mirror {i}: {Shorten(src)}");
                    // A partial from another source must not be resumed by this one.
                    try { File.Delete(fullPath + ".part"); } catch { }
                }
                if (!await DownloadAsync(fullPath, src, f, log, bytes, ct)) continue;
                if (await VerifyAsync(fullPath, f, ct)) { got = true; break; }
                log?.Report($"FAIL hash mismatch on {f.Path} from {Shorten(src)}");
            }

            if (got) log?.Report($"OK   {f.Path}");
            else
            {
                log?.Report($"FAIL {f.Path} — no source produced a verified file " +
                            $"({sources.Count} tried)");
            }
            return (got, false);
        }
    }

    /// <summary>Host + last path segment — enough to tell sources apart in the log.</summary>
    private static string Shorten(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}/…/{u.Segments[^1]}" : url;

    private async Task<bool> DownloadAsync(string fullPath, string url, ManifestEntry f,
        IProgress<string>? log, IProgress<DownloadProgress>? bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string tmp = fullPath + ".part";
        try
        {
            // A .part at least as long as the manifest says can never be completed
            // by appending; start over.
            long have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            if (have > 0 && f.Bytes > 0 && have >= f.Bytes) { File.Delete(tmp); have = 0; }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(tmp);
                log?.Report($"FAIL {f.Path}: server refused the resume range; partial discarded, retry will start over");
                return false;
            }
            resp.EnsureSuccessStatusCode();

            bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && have > 0;
            if (!resumed) have = 0;   // a 200 sends the whole body

            // Refuse a wrong length before reading a byte (review item 47).
            long? declared = resp.Content.Headers.ContentLength;
            long expected = f.Bytes > 0 ? f.Bytes - have : -1;
            if (declared is long d && expected >= 0 && d != expected)
            {
                log?.Report($"FAIL {f.Path}: server declares {d} bytes, expected {expected}; refused before download");
                if (!resumed) { try { File.Delete(tmp); } catch { } }
                return false;
            }

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(tmp, resumed ? FileMode.Append : FileMode.Create,
                                                  FileAccess.Write, FileShare.None))
            {
                if (bytes is null)
                {
                    await src.CopyToAsync(dst, 81920, ct);
                }
                else
                {
                    bytes.Report(new DownloadProgress(f.Path, have, f.Bytes));
                    var buffer = new byte[81920];
                    long done = have;
                    long lastReported = have;
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;

                        // Roughly every 512 KB rather than every 80 KB chunk; the
                        // receiver is a UI thread marshal on the far side of a socket.
                        if (done - lastReported < 512 * 1024 && done != f.Bytes) continue;
                        lastReported = done;
                        bytes.Report(new DownloadProgress(f.Path, done, f.Bytes));
                    }
                }
            }
            File.Move(tmp, fullPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Keep the .part: the next run resumes it.
            throw;
        }
        catch (Exception ex)
        {
            // Network failure mid-body: the .part is a valid prefix, keep it for Range resume.
            log?.Report($"FAIL {f.Path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Does the file on disk match what the manifest pinned — exact size, then
    /// SHA-256 when one is recorded?
    ///
    /// <para>Public because <see cref="PiperVoiceInstaller"/> has to ask the same
    /// question after a failed install, to decide what to delete. A second
    /// implementation of "is this file the one we meant" is precisely the kind of
    /// duplicate that drifts, and it would drift on the side that decides whether
    /// corrupt weights stay in the store.</para>
    ///
    /// <para><b>An empty <see cref="ManifestEntry.Sha256"/> passes.</b> That is
    /// deliberate for the Supertonic manifest's small JSON blobs, which have no
    /// LFS hash to pin, and it is why the Piper catalog hashes both of its files
    /// at generation time instead of relying on this.</para>
    /// </summary>
    public static async Task<bool> VerifyAsync(string fullPath, ManifestEntry f, CancellationToken ct)
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

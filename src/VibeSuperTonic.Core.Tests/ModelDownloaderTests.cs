using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using VibeSuperTonic.Core.Models;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// <see cref="ModelDownloader"/> against a loopback <see cref="HttpListener"/>
/// (127.0.0.1 only, free port). Covers review items 47, 64, 79 (downloader part)
/// and 89: a wrong <c>Content-Length</c> is refused before the body is read, a
/// <c>.part</c> resumes with <c>Range</c>, one shared client, a hash mismatch on
/// the first source falls through to the next, and cancel keeps the <c>.part</c>
/// (the contract is "kept for resume", not "removed").
/// </summary>
public class ModelDownloaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vst-dl-" + Guid.NewGuid().ToString("N"));

    public ModelDownloaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Prefix { get; }
        /// <summary>path to handler(request, response).</summary>
        public Dictionary<string, Func<HttpListenerRequest, HttpListenerResponse, Task>> Routes { get; } = new();
        public List<(string Path, string? Range)> Requests { get; } = new();

        public Server()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Prefix = $"http://127.0.0.1:{port}/";
            _l.Prefixes.Add(Prefix);
            _l.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    string name = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                    lock (Requests) Requests.Add((name, ctx.Request.Headers["Range"]));
                    try
                    {
                        if (Routes.TryGetValue(name, out var h)) await h(ctx.Request, ctx.Response);
                        else ctx.Response.StatusCode = 404;
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        }

        /// <summary>Serves a body honouring a single open-ended Range.</summary>
        public void AddRanged(string name, byte[] body) => Routes[name] = async (req, res) =>
        {
            int start = 0;
            if (req.Headers["Range"] is { } r && r.StartsWith("bytes=") && r.EndsWith("-"))
                start = int.Parse(r[6..^1]);
            if (start > 0) res.StatusCode = 206;
            res.ContentLength64 = body.Length - start;
            await res.OutputStream.WriteAsync(body.AsMemory(start));
        };

        public void Dispose() { try { _l.Stop(); } catch { } }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(7).NextBytes(b);
        return b;
    }

    private ModelDownloader Make(ManifestEntry e) =>
        new(_root, new Manifest { Files = { e } });

    private string Target => Path.Combine(_root, "f.bin");

    [Fact]
    public async Task A_wrong_Content_Length_is_refused_before_the_body_is_read()
    {
        using var server = new Server();
        var good = Bytes(1000);
        // Declares 5000, sends 10 bytes, then stalls: a client that starts copying waits here.
        server.Routes["f.bin"] = async (req, res) =>
        {
            res.ContentLength64 = 5000;
            await res.OutputStream.WriteAsync(good.AsMemory(0, 10));
            await res.OutputStream.FlushAsync();
            await Task.Delay(6000);
        };
        var e = new ManifestEntry { Path = "f.bin", Url = server.Prefix + "f.bin", Bytes = 1000, Sha256 = Sha(good) };

        var seen = new List<DownloadProgress>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = await Make(e).EnsureAllAsync(null, new Progress<DownloadProgress>(p => { lock (seen) seen.Add(p); }),
            CancellationToken.None);
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}: the body was read");
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".part"));
        await Task.Delay(100);
        lock (seen) Assert.DoesNotContain(seen, p => p.BytesReceived > 0);
    }

    [Fact]
    public async Task A_short_part_file_resumes_with_a_Range_header()
    {
        using var server = new Server();
        var good = Bytes(200_000);
        server.AddRanged("f.bin", good);
        File.WriteAllBytes(Target + ".part", good[..80_000]);
        var e = new ManifestEntry { Path = "f.bin", Url = server.Prefix + "f.bin", Bytes = good.Length, Sha256 = Sha(good) };

        Assert.True(await Make(e).EnsureAllAsync(null, CancellationToken.None));

        Assert.Equal(good, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".part"));
        lock (server.Requests) Assert.Equal("bytes=80000-", Assert.Single(server.Requests).Range);
    }

    [Fact]
    public void One_HttpClient_is_shared_by_every_downloader()
    {
        var f = typeof(ModelDownloader).GetField("_http", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(f);
        Assert.True(f!.IsInitOnly);
        Assert.NotNull(f.GetValue(null));
    }

    [Fact]
    public async Task A_hash_mismatch_on_the_first_mirror_falls_through_to_the_next()
    {
        using var server = new Server();
        var good = Bytes(5000);
        var bad = Bytes(5000); bad[0] ^= 0xFF;   // right length, wrong content
        server.AddRanged("a.bin", bad);
        server.AddRanged("b.bin", good);
        var e = new ManifestEntry
        {
            Path = "f.bin", Url = server.Prefix + "a.bin", Bytes = good.Length, Sha256 = Sha(good),
            Mirrors = { server.Prefix + "b.bin" },
        };
        var lines = new List<string>();

        Assert.True(await Make(e).EnsureAllAsync(new Progress<string>(l => { lock (lines) lines.Add(l); }),
            CancellationToken.None));

        Assert.Equal(good, File.ReadAllBytes(Target));
        lock (server.Requests)
            Assert.Equal(new[] { "a.bin", "b.bin" }, server.Requests.Select(r => r.Path).ToArray());
        await Task.Delay(100);
        lock (lines) Assert.Contains(lines, l => l.Contains("hash mismatch"));
    }

    [Fact]
    public async Task Cancelling_keeps_the_part_file_for_resume_and_leaves_no_final_file()
    {
        using var server = new Server();
        var good = Bytes(400_000);
        var half = new TaskCompletionSource();
        // First request: send half, then hang until the client goes away.
        server.Routes["f.bin"] = async (req, res) =>
        {
            if (req.Headers["Range"] is null)
            {
                res.ContentLength64 = good.Length;
                await res.OutputStream.WriteAsync(good.AsMemory(0, 200_000));
                await res.OutputStream.FlushAsync();
                half.TrySetResult();
                await Task.Delay(10_000);
            }
            else
            {
                int start = int.Parse(req.Headers["Range"]!.Substring(6).TrimEnd('-'));
                res.StatusCode = 206;
                res.ContentLength64 = good.Length - start;
                await res.OutputStream.WriteAsync(good.AsMemory(start));
            }
        };
        var e = new ManifestEntry { Path = "f.bin", Url = server.Prefix + "f.bin", Bytes = good.Length, Sha256 = Sha(good) };

        using var cts = new CancellationTokenSource();
        var run = Make(e).EnsureAllAsync(null, cts.Token);
        await half.Task;
        await Task.Delay(300);   // let the client drain the 200 KB into the .part
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(File.Exists(Target));
        Assert.True(File.Exists(Target + ".part"));
        long kept = new FileInfo(Target + ".part").Length;
        Assert.InRange(kept, 1, good.Length - 1);

        // The kept part is what the next run resumes.
        Assert.True(await Make(e).EnsureAllAsync(null, CancellationToken.None));
        Assert.Equal(good, File.ReadAllBytes(Target));
        lock (server.Requests) Assert.Equal($"bytes={kept}-", server.Requests.Last().Range);
    }
}

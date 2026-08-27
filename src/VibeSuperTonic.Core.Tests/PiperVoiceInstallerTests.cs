using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using VibeSuperTonic.Core.Models;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Installing and removing a Piper voice, against a real
/// <see cref="ModelDownloader"/> over a loopback HTTP server.
///
/// <para><b>Why a server rather than a stub.</b> The whole value of routing voice
/// installs through Core's existing downloader is that they inherit its
/// integrity rules — mirrors tried in order, size and SHA-256 checked, and
/// re-verified after <em>each</em> source so a mirror serving a truncated file
/// falls through instead of leaving a corrupt model on disk. A stubbed downloader
/// tests the wiring and none of that. <see cref="HttpListener"/> is framework-only,
/// binds to 127.0.0.1, and keeps this project's promise that it needs no native
/// libraries and no model files.</para>
///
/// <para><b>What is actually at stake.</b> A half-installed voice is not a
/// cosmetic problem: the store's rule is "both files or neither" precisely
/// because a directory holding weights with no config would otherwise shadow a
/// Supertonic voice of the same name and claim to be speakable. Every failure
/// path below is checked for what it leaves behind, not just for what it
/// returns.</para>
/// </summary>
public class PiperVoiceInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vst-installer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ the fixture

    /// <summary>A one-request-per-path HTTP server that serves exactly what it is told to.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files = new();
        private readonly HashSet<string> _truncate = new();
        public string Prefix { get; }

        public Server()
        {
            // Port 0 through a throwaway TcpListener: HttpListener will not
            // allocate one itself, and a fixed port makes a test that fails when
            // it is run twice at once.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public string Add(string name, byte[] content, bool truncate = false)
        {
            _files[name] = content;
            if (truncate) _truncate.Add(name);
            return Prefix + name;
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                string name = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                try
                {
                    if (!_files.TryGetValue(name, out var body))
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    else
                    {
                        // A truncated response is the mirror failure the
                        // downloader's re-verify rule exists for: the transfer
                        // succeeds and the bytes are wrong.
                        var send = _truncate.Contains(name) ? body[..(body.Length / 2)] : body;
                        ctx.Response.ContentLength64 = send.Length;
                        await ctx.Response.OutputStream.WriteAsync(send);
                    }
                }
                catch { /* client went away */ }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static PiperCatalogVoice Voice(string id, string onnxUrl, byte[] onnx, string cfgUrl, byte[] cfg) =>
        new()
        {
            Id = id,
            Name = id.Split('-')[1],
            Quality = id.Split('-')[^1],
            SampleRate = 22050,
            Speakers = 1,
            Language = new PiperCatalogLanguage("en_US", "English", "English", "United States"),
            Licence = new PiperLicence("CC0", "public", "https://example/terms"),
            Files =
            {
                new PiperCatalogFile { Name = id + ".onnx",      Url = onnxUrl, Sha256 = Sha(onnx), Bytes = onnx.Length },
                new PiperCatalogFile { Name = id + ".onnx.json", Url = cfgUrl,  Sha256 = Sha(cfg),  Bytes = cfg.Length },
            },
        };

    private PiperVoiceInstaller NewInstaller() => new(_root);

    // --------------------------------------------------- what a catalog may do
    //
    // A catalog is a file beside the binaries that anything can edit, and these
    // are the two places where a string in it becomes a path on disk. Neither is
    // covered by the hash pin: a hash says the bytes are the ones the manifest
    // named, not that the manifest named a sane place to put them.

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../../../etc")]
    [InlineData("piper/nested")]
    [InlineData("/tmp")]
    public void An_id_that_is_a_path_removes_nothing(string hostile)
    {
        // The path that ends in Directory.Delete(recursive: true), reached from
        // `vst-ctl voice remove` with whatever was typed. The assertion is not
        // that it returns false — it is that the directory it would have deleted
        // is still there afterwards.
        string sentinel = Path.Combine(_root, "piper", "en_US-ljspeech-high");
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "en_US-ljspeech-high.onnx"), "weights");

        var result = NewInstaller().Remove(hostile);

        Assert.False(result.Ok);
        Assert.Contains("not a voice id", result.Message);
        Assert.True(Directory.Exists(sentinel), "the store was deleted by an id that is a path");
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task A_catalog_entry_whose_id_is_a_path_downloads_nothing()
    {
        // Shaped like a real id on purpose. A catalog somebody would actually
        // paste in does not announce itself with a bare "../.." — it looks like
        // every other row and carries the traversal in front of a plausible name.
        var voice = Voice("../../../en_US-escaped-high",
                          "https://example/a.onnx", new byte[] { 1 },
                          "https://example/a.onnx.json", new byte[] { 2 });

        var result = await NewInstaller().InstallAsync(voice, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not a voice id", result.Message);
        // Nothing created anywhere — not the escaped path, and not a stray root.
        Assert.False(Directory.Exists(
            Path.GetFullPath(Path.Combine(_root, "..", "..", "..", "en_US-escaped-high"))));
    }

    [Fact]
    public void A_voice_id_that_is_a_path_is_never_reported_as_installed()
    {
        // IsInstalled is asked while enumerating, so it answers rather than
        // throwing — and it must answer "no" rather than probing outside.
        Assert.False(NewInstaller().IsInstalled("../.."));
        Assert.False(NewInstaller().IsInstalled("/etc"));
    }


    // ------------------------------------------------------------- happy path

    [Fact]
    public async Task A_voice_installs_where_the_store_looks_for_it()
    {
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes(new string('w', 4096));
        var cfg = Encoding.UTF8.GetBytes("""{"audio":{"sample_rate":22050}}""");
        var voice = Voice("en_US-x-high",
            server.Add("x.onnx", onnx), onnx,
            server.Add("x.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        var result = await installer.InstallAsync(voice, null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.True(installer.IsInstalled("en_US-x-high"));

        // The exact layout the daemon's store and both packers depend on.
        string dir = Path.Combine(_root, "piper", "en_US-x-high");
        Assert.True(File.Exists(Path.Combine(dir, "en_US-x-high.onnx")));
        Assert.True(File.Exists(Path.Combine(dir, "en_US-x-high.onnx.json")));
        Assert.Equal(new[] { "en_US-x-high" }, installer.Installed());
    }

    [Fact]
    public async Task Byte_progress_is_reported_and_ends_at_the_full_size()
    {
        using var server = new Server();
        var onnx = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(onnx);
        var cfg = Encoding.UTF8.GetBytes("{}");
        var voice = Voice("en_US-x-medium",
            server.Add("m.onnx", onnx), onnx,
            server.Add("m.onnx.json", cfg), cfg);

        var seen = new List<DownloadProgress>();
        var result = await NewInstaller().InstallAsync(
            voice, null, new Progress<DownloadProgress>(p => { lock (seen) seen.Add(p); }),
            CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        await Task.Delay(150);                 // Progress<T> marshals; let the posts land

        List<DownloadProgress> snapshot;
        lock (seen) snapshot = seen.ToList();

        Assert.NotEmpty(snapshot);
        var weights = snapshot.Where(p => p.Path.EndsWith(".onnx")).ToList();
        Assert.Contains(weights, p => p.BytesReceived == onnx.Length);
        Assert.All(weights, p => Assert.Equal(onnx.Length, p.BytesTotal));
        Assert.Equal(1.0, weights.Last().Fraction);
    }

    [Fact]
    public async Task Installing_a_voice_that_is_already_present_verifies_rather_than_refetches()
    {
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes("weights");
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-x-low",
            server.Add("l.onnx", onnx), onnx,
            server.Add("l.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        Assert.True((await installer.InstallAsync(voice, null, null, CancellationToken.None)).Ok);

        // The server answers each path once per request; a second install that
        // re-fetched would still succeed, so the observable difference is that
        // no bytes were reported.
        var seen = new List<DownloadProgress>();
        var again = await installer.InstallAsync(
            voice, null, new Progress<DownloadProgress>(p => { lock (seen) seen.Add(p); }),
            CancellationToken.None);

        Assert.True(again.Ok);
        await Task.Delay(150);
        lock (seen) Assert.Empty(seen);
    }

    // ---------------------------------------------------------- failure paths

    [Fact]
    public async Task A_voice_whose_weights_do_not_hash_leaves_nothing_behind()
    {
        // The failure that matters. Half a voice in the store is worse than no
        // voice: the directory exists, `voices` could list it, and it would
        // shadow a Supertonic style of the same name.
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes(new string('w', 8192));
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-bad-high",
            server.Add("bad.onnx", onnx, truncate: true), onnx,
            server.Add("bad.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        var result = await installer.InstallAsync(voice, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(installer.IsInstalled("en_US-bad-high"));
        Assert.False(Directory.Exists(Path.Combine(_root, "piper", "en_US-bad-high")));
        Assert.Empty(installer.Installed());
    }

    [Fact]
    public async Task Corrupt_weights_are_deleted_even_though_both_files_are_present()
    {
        // The defect this whole prune step exists for, stated on its own because
        // the assertion above passes for the wrong reason if it regresses.
        //
        // A truncated .onnx still gets moved into place by the downloader — the
        // transfer succeeded, only the hash did not — and its .onnx.json then
        // downloads perfectly. So the directory holds BOTH files, "is it
        // installed" answers yes, and the store cannot tell that the weights are
        // half a file. What catches it is asking the pinned hash, not the
        // filesystem.
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes(new string('w', 8192));
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-corrupt-high",
            server.Add("c.onnx", onnx, truncate: true), onnx,
            server.Add("c.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        string dir = installer.DirectoryFor("en_US-corrupt-high");

        Assert.False((await installer.InstallAsync(voice, null, null, CancellationToken.None)).Ok);

        Assert.False(File.Exists(Path.Combine(dir, "en_US-corrupt-high.onnx")));
        Assert.False(installer.IsInstalled("en_US-corrupt-high"));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task A_failed_upgrade_does_not_leave_bytes_the_catalog_no_longer_describes()
    {
        // The deliberate consequence of the rule, pinned so it is a decision
        // rather than a surprise. The voice is installed; the catalog's pinned
        // hash then changes (upstream moved) and the new fetch fails. What is on
        // disk is by definition no longer what the catalog describes, so it goes.
        using var server = new Server();
        var v1 = Encoding.UTF8.GetBytes("weights-one");
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-upgrade-high",
            server.Add("u1.onnx", v1), v1,
            server.Add("u.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        Assert.True((await installer.InstallAsync(voice, null, null, CancellationToken.None)).Ok);
        Assert.True(installer.IsInstalled("en_US-upgrade-high"));

        var v2 = Encoding.UTF8.GetBytes("weights-two-which-never-arrive");
        voice.Files[0].Sha256 = Sha(v2);
        voice.Files[0].Bytes = v2.Length;
        voice.Files[0].Url = server.Prefix + "missing-upgrade.onnx";

        Assert.False((await installer.InstallAsync(voice, null, null, CancellationToken.None)).Ok);
        Assert.False(installer.IsInstalled("en_US-upgrade-high"));
        Assert.Empty(installer.Installed());
    }

    [Fact]
    public async Task A_voice_whose_config_never_arrives_is_not_a_voice()
    {
        // Weights land, config 404s. The store's "both files or neither" rule is
        // what makes this safe even before the cleanup runs.
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes("weights");
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-half-high",
            server.Add("h.onnx", onnx), onnx,
            server.Prefix + "missing.onnx.json", cfg);

        var installer = NewInstaller();
        var result = await installer.InstallAsync(voice, null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(installer.IsInstalled("en_US-half-high"));
        Assert.Empty(installer.Installed());
    }

    [Fact]
    public async Task A_mirror_rescues_a_primary_that_serves_the_wrong_bytes()
    {
        // Inherited from ModelDownloader rather than reimplemented, and this is
        // what proves the inheritance is real.
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes(new string('w', 4096));
        var cfg = Encoding.UTF8.GetBytes("config");

        var voice = Voice("en_US-mirror-high",
            server.Add("primary.onnx", onnx, truncate: true), onnx,
            server.Add("mir.onnx.json", cfg), cfg);
        voice.Files[0].Mirrors.Add(server.Add("mirror.onnx", onnx));

        var installer = NewInstaller();
        var result = await installer.InstallAsync(voice, null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.True(installer.IsInstalled("en_US-mirror-high"));
    }

    // ---------------------------------------------------------------- removal

    [Fact]
    public async Task Removing_a_voice_takes_its_calibration_with_it()
    {
        // Deliberate. The curve describes this voice's length_scale on this
        // machine; keeping it for a voice that is gone means a reinstall
        // silently inherits a measurement against bytes nobody can prove are the
        // same ones.
        using var server = new Server();
        var onnx = Encoding.UTF8.GetBytes("weights");
        var cfg = Encoding.UTF8.GetBytes("config");
        var voice = Voice("en_US-gone-high",
            server.Add("g.onnx", onnx), onnx,
            server.Add("g.onnx.json", cfg), cfg);

        var installer = NewInstaller();
        Assert.True((await installer.InstallAsync(voice, null, null, CancellationToken.None)).Ok);

        string dir = installer.DirectoryFor("en_US-gone-high");
        File.WriteAllText(Path.Combine(dir, PiperVoiceInstaller.CalibrationFileName), "{}");
        Assert.True(installer.BytesOnDisk("en_US-gone-high") > 0);

        var removed = installer.Remove("en_US-gone-high");

        Assert.True(removed.Ok, removed.Message);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(installer.Installed());
        Assert.Equal(0, installer.BytesOnDisk("en_US-gone-high"));
    }

    [Fact]
    public void Removing_a_voice_that_is_not_installed_says_so_rather_than_throwing()
    {
        var r = NewInstaller().Remove("en_US-never-high");
        Assert.False(r.Ok);
        Assert.Contains("not installed", r.Message);
    }

    // ----------------------------------------------------------- enumeration

    [Fact]
    public void A_directory_with_weights_and_no_config_is_not_reported_as_installed()
    {
        // The half-finished download, arrived at by hand rather than by an
        // interrupted fetch. Same rule, same answer.
        string dir = Path.Combine(_root, "piper", "en_US-partial-high");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "en_US-partial-high.onnx"), "weights");

        var installer = NewInstaller();
        Assert.False(installer.IsInstalled("en_US-partial-high"));
        Assert.Empty(installer.Installed());
    }

    [Fact]
    public void An_empty_or_absent_store_enumerates_to_nothing()
    {
        var installer = NewInstaller();
        Assert.Empty(installer.Installed());
        Assert.False(installer.IsInstalled("anything"));
        Assert.False(installer.IsInstalled(""));
    }

    [Fact]
    public void The_store_folder_the_installer_writes_is_the_one_the_daemon_reads()
    {
        // These two constants live in two assemblies because Core may not depend
        // on the daemon. This is the seam that keeps them equal; the daemon side
        // asserts the same thing from its end.
        Assert.Equal("piper", PiperVoiceInstaller.StoreFolderName);
        Assert.Equal("calibration.json", PiperVoiceInstaller.CalibrationFileName);
        Assert.Equal(Path.Combine(_root, "piper"), NewInstaller().StoreRoot);
    }
}

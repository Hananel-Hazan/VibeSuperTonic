using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Where an AppImage keeps 383 MB of models, 200 KB of settings and the
/// benchmark profile — which is the one question Phase 9 could get wrong in a
/// way a user would report as data loss rather than as a bug.
///
/// <para>Driven through <see cref="LinuxDataPaths.ResolveStore"/>'s injected
/// probes rather than against a real filesystem: the interesting cases are a
/// read-only directory, a store that exists in one of two places, and a file
/// that has been moved since it last ran, and building those for real would
/// make the test slower than the thing it tests and dependent on the machine
/// running it.</para>
/// </summary>
public sealed class AppImageStoreTests
{
    private const string AppImage = "/home/me/Apps/VibeSuperTonic-0.2.9-x86_64.AppImage";
    private const string Beside = "/home/me/Apps/VibeSuperTonic";
    private const string Xdg = "/home/me/.local/share/vibesupertonic";

    private static LinuxDataPaths.StorePick Resolve(
        string? appImage = AppImage,
        string baseDir = "/tmp/.mount_abc123",
        IEnumerable<string>? existing = null,
        bool writable = true)
    {
        var dirs = new HashSet<string>(existing ?? []);
        return LinuxDataPaths.ResolveStore(
            appImage, baseDir,
            xdgDataHome: "/home/me/.local/share",
            home: "/home/me",
            dirExists: dirs.Contains,
            canCreateIn: _ => writable);
    }

    [Fact]
    public void An_ordinary_install_keeps_everything_beside_the_executable()
    {
        // The portability story, unchanged. Every install that is not an
        // AppImage must take this branch and never look at $HOME — a portable
        // folder on a stick that reads settings from the home directory is the
        // exact defect Phase 4b's own documentation records fixing.
        var pick = Resolve(appImage: null, baseDir: "/media/stick/VibeSuperTonic");

        Assert.Equal("/media/stick/VibeSuperTonic", pick.Root);
    }

    [Fact]
    public void A_fresh_appimage_creates_its_store_beside_the_file()
    {
        var pick = Resolve();

        Assert.Equal(Beside, pick.Root);
        Assert.Contains("beside", pick.Reason);
    }

    [Fact]
    public void An_appimage_in_an_unwritable_directory_falls_back_to_xdg()
    {
        // /opt, a read-only medium, someone else's home. The product must start
        // rather than refuse: a daemon that will not start is a hotkey that
        // silently does nothing (R-5).
        var pick = Resolve(writable: false);

        Assert.Equal(Xdg, pick.Root);
    }

    [Fact]
    public void An_existing_store_beside_the_file_wins()
    {
        var pick = Resolve(existing: [Beside + "/models"]);

        Assert.Equal(Beside, pick.Root);
    }

    [Fact]
    public void An_existing_xdg_store_wins_when_there_is_none_beside_the_file()
    {
        // The upgrade path for anyone whose first run landed in XDG because the
        // directory was not writable then. Preferring "beside" here would strand
        // the store they already have and re-download 383 MB.
        var pick = Resolve(existing: [Xdg + "/data"]);

        Assert.Equal(Xdg, pick.Root);
    }

    [Fact]
    public void Data_alone_counts_as_a_store_and_so_does_models_alone()
    {
        // A first run interrupted after the settings were written and before the
        // download finished leaves data/ with no models/; changing a setting
        // before downloading leaves the same. Requiring both halves would
        // discard a store in the two states a person is most likely to be in.
        Assert.Equal(Beside, Resolve(existing: [Beside + "/data"]).Root);
        Assert.Equal(Beside, Resolve(existing: [Beside + "/models"]).Root);
    }

    [Fact]
    public void A_moved_appimage_finds_the_store_that_moved_with_it()
    {
        // The failure this whole rule exists for, in its second form: the file
        // and its store are moved together — which is what "portable" means —
        // and the mount path is different on every start anyway, so nothing may
        // be remembered from last time.
        var moved = Resolve(
            appImage: "/media/stick/VibeSuperTonic-0.2.9-x86_64.AppImage",
            baseDir: "/tmp/.mount_zzz999",
            existing: ["/media/stick/VibeSuperTonic/models"]);

        Assert.Equal("/media/stick/VibeSuperTonic", moved.Root);
    }

    [Fact]
    public void The_store_never_lands_inside_the_read_only_mount()
    {
        // The mount is gone at the next start and read-only in the meantime.
        // Asserted for every branch rather than for the interesting one, because
        // this is the invariant, not a case.
        foreach (var pick in new[]
        {
            Resolve(),
            Resolve(writable: false),
            Resolve(existing: [Beside + "/models"]),
            Resolve(existing: [Xdg + "/data"]),
        })
        {
            Assert.DoesNotContain("/.mount_", pick.Root);
        }
    }

    [Fact]
    public void The_reason_is_always_sayable()
    {
        // config reports it and the Status tab prints it. An empty string there
        // is a user asking "where are my models" and getting a blank.
        foreach (var pick in new[] { Resolve(appImage: null), Resolve(), Resolve(writable: false) })
            Assert.False(string.IsNullOrWhiteSpace(pick.Reason));
    }
}

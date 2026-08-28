using VibeSuperTonic.Core.Install;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The startup check's rule: what counts as "this install moved", and what each
/// kind of move invalidates.
///
/// <para><b>The fast path is the feature</b>, so the test that matters most is
/// the boring one — an unchanged install reports nothing at all. The daemon is
/// frequently started by a hotkey press, and a check that found something to say
/// on an ordinary start would put a banner in front of a user who did
/// nothing.</para>
/// </summary>
public class InstallFingerprintTests
{
    private static InstallIdentity Where(
        string baseDir = "/opt/vst", string store = "/opt/vst", string models = "/opt/vst/models",
        string appImage = "", string home = "/home/x", string machine = "m1",
        int cpus = 8, string version = "0.2.13") =>
        new(baseDir, store, models, appImage, home, machine, cpus, version);

    private static InstallFingerprint Recorded(InstallIdentity id) => new(id, "2026-08-28T00:00:00Z");

    [Fact]
    public void An_install_that_has_not_moved_reports_nothing()
    {
        Assert.Empty(Recorded(Where()).ChangesAgainst(Where()));
    }

    /// <summary>
    /// An upgrade replaces the binaries in place. Every path that pointed here
    /// still does, so nothing outside this folder needs rebinding — and a banner
    /// on every release, in front of every user, for an action none of them could
    /// take, is worse than saying nothing.
    /// </summary>
    [Fact]
    public void A_new_version_in_the_same_place_is_not_a_move()
    {
        Assert.Empty(Recorded(Where(version: "0.2.13")).ChangesAgainst(Where(version: "0.3.0")));
    }

    [Fact]
    public void A_moved_folder_invalidates_the_bindings()
    {
        var changes = Recorded(Where(baseDir: "/opt/vst")).ChangesAgainst(Where(baseDir: "/srv/vst"));

        var change = Assert.Single(changes);
        Assert.Contains("/opt/vst", change.What, StringComparison.Ordinal);
        Assert.Equal(InstallImpact.Bindings, change.Impact);
    }

    /// <summary>
    /// A DIFFERENT MACHINE IS REPORTED ALONE. A portable folder carried to
    /// another machine has different paths too, and reporting "the program moved"
    /// beside it would describe a move that never happened — the folder is
    /// exactly where its owner put it.
    /// </summary>
    [Fact]
    public void A_different_machine_is_one_sentence_and_not_four()
    {
        var changes = Recorded(Where(baseDir: "/media/stick/vst", machine: "m1", cpus: 8))
            .ChangesAgainst(Where(baseDir: "/home/y/vst", machine: "m2", cpus: 16));

        var change = Assert.Single(changes);
        Assert.Contains("different machine", change.What, StringComparison.Ordinal);
        Assert.Equal(
            InstallImpact.Bindings | InstallImpact.Benchmark | InstallImpact.Calibration,
            change.Impact);
    }

    /// <summary>
    /// An AppImage's BaseDir is a temporary mount that is SUPPOSED to change on
    /// every run. The file is the durable thing, and renaming it is the move that
    /// breaks a shortcut.
    /// </summary>
    [Fact]
    public void A_renamed_appimage_is_a_move_even_though_the_mount_always_changes()
    {
        var changes = Recorded(Where(baseDir: "/tmp/.mount_aaa", appImage: "/apps/vst.AppImage"))
            .ChangesAgainst(Where(baseDir: "/tmp/.mount_bbb", appImage: "/apps/tts.AppImage"));

        Assert.Contains(changes, c => c.What.Contains("AppImage moved", StringComparison.Ordinal));
        Assert.All(changes, c => Assert.Equal(InstallImpact.Bindings, c.Impact));
    }

    [Fact]
    public void A_moved_store_touches_the_voices_and_not_the_bindings()
    {
        var changes = Recorded(Where(store: "/a", models: "/a/models"))
            .ChangesAgainst(Where(store: "/b", models: "/b/models"));

        var change = Assert.Single(changes);
        Assert.Equal(InstallImpact.Voices, change.Impact);
    }

    /// <summary>
    /// `--models` can move the models without the store moving, and that is still
    /// only a voice-list question.
    /// </summary>
    [Fact]
    public void A_models_override_alone_is_reported()
    {
        var changes = Recorded(Where(models: "/a/models")).ChangesAgainst(Where(models: "/mnt/models"));

        Assert.Equal(InstallImpact.Voices, Assert.Single(changes).Impact);
    }

    /// <summary>
    /// Reached only on the same machine, so this is a CPU that gained or lost
    /// cores — a resized VM — rather than a different box.
    /// </summary>
    [Fact]
    public void A_changed_core_count_invalidates_only_the_benchmark()
    {
        var changes = Recorded(Where(cpus: 8)).ChangesAgainst(Where(cpus: 4));

        var change = Assert.Single(changes);
        Assert.Equal(InstallImpact.Benchmark, change.Impact);
        Assert.Contains("8", change.What, StringComparison.Ordinal);
        Assert.Contains("4", change.What, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_home_invalidates_the_bindings()
    {
        var changes = Recorded(Where(home: "/home/x")).ChangesAgainst(Where(home: "/apps/vst.AppImage.home"));

        Assert.Equal(InstallImpact.Bindings, Assert.Single(changes).Impact);
    }

    // ------------------------------------------------------------------ check

    /// <summary>
    /// A fresh install has nothing to compare against, and a banner on first
    /// launch would be the product's opening statement.
    /// </summary>
    [Fact]
    public void A_first_run_needs_no_attention()
    {
        var check = new InstallCheck(
            Array.Empty<InstallChange>(), Array.Empty<string>(), Array.Empty<string>(), FirstRun: true);

        Assert.False(check.NeedsAttention);
        Assert.Null(check.Headline());
    }

    /// <summary>
    /// The benchmark goes stale for reasons that have nothing to do with moving —
    /// an edited TotalStep, a changed model set — so it must raise the banner on
    /// its own.
    /// </summary>
    [Fact]
    public void A_stale_benchmark_alone_still_needs_attention()
    {
        var check = new InstallCheck(
            Array.Empty<InstallChange>(), new[] { "measured on a different machine" },
            Array.Empty<string>());

        Assert.True(check.NeedsAttention);
        Assert.Equal(InstallImpact.Benchmark, check.Impact);
        Assert.Contains("re-measure", string.Join(" ", check.Advice()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A broken launcher is measured rather than inferred, so it is a finding on
    /// its own even when the fingerprint says nothing moved.
    /// </summary>
    [Fact]
    public void A_broken_launcher_alone_still_needs_attention()
    {
        var check = new InstallCheck(
            Array.Empty<InstallChange>(), Array.Empty<string>(), new[] { "/old/vst-ctl" });

        Assert.True(check.NeedsAttention);
        Assert.Equal(InstallImpact.Bindings, check.Impact);
    }

    /// <summary>
    /// ADVICE ONLY FOR IMPACTS THAT ARE PRESENT. Telling a user to re-bind their
    /// hotkeys because their core count changed would send them to a script that
    /// fixes nothing, and the next real warning would be read as noise.
    /// </summary>
    [Fact]
    public void Advice_covers_what_changed_and_nothing_else()
    {
        var check = new InstallCheck(
            new[] { new InstallChange("cores changed", InstallImpact.Benchmark) },
            Array.Empty<string>(), Array.Empty<string>());

        string advice = string.Join(" ", check.Advice());
        Assert.Contains("Re-measure", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("install.sh", advice, StringComparison.Ordinal);
    }

    /// <summary>
    /// The voice list is read from disk every time it is asked for, so a moved
    /// store needs nothing done to it — and saying nothing at all would leave a
    /// user to assume the worst about their 383 MB of models.
    /// </summary>
    [Fact]
    public void A_moved_store_is_reported_as_needing_no_action()
    {
        var check = new InstallCheck(
            new[] { new InstallChange("the store moved", InstallImpact.Voices) },
            Array.Empty<string>(), Array.Empty<string>());

        Assert.Contains("nothing to do", string.Join(" ", check.Advice()), StringComparison.Ordinal);
    }

    /// <summary>The headline names the cause, which is the thing a user can connect to what they did.</summary>
    [Fact]
    public void The_headline_is_the_first_change()
    {
        var check = new InstallCheck(
            new[]
            {
                new InstallChange("the program moved", InstallImpact.Bindings),
                new InstallChange("the store moved", InstallImpact.Voices),
            },
            Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("the program moved", check.Headline());
    }

    // ------------------------------------------------------------------ store

    [Fact]
    public void A_fingerprint_survives_a_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "install.json");
        var written = new InstallFingerprint(Where(appImage: "/apps/v.AppImage"), "2026-08-28T00:00:00Z");

        Assert.True(InstallStore.Save(path, written));
        Assert.Equal(written, InstallStore.Load(path));
    }

    /// <summary>
    /// Absent, unreadable and malformed all mean "never recorded", because the
    /// alternative is a daemon that will not start over a corrupt note to itself.
    /// </summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    public void A_damaged_file_reads_as_never_recorded(string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "install.json");
        File.WriteAllText(path, content);

        Assert.Null(InstallStore.Load(path));
    }

    [Fact]
    public void A_missing_file_reads_as_never_recorded()
    {
        Assert.Null(InstallStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.json")));
    }

    /// <summary>
    /// A directory is a caller bug, never a missing fingerprint — the trap
    /// BenchmarkStore documents, where the wrong argument reads back forever as
    /// "this has never happened".
    /// </summary>
    [Fact]
    public void Load_refuses_a_directory_rather_than_answering_never_recorded()
    {
        Assert.Throws<ArgumentException>(() => InstallStore.Load(Path.GetTempPath()));
    }
}

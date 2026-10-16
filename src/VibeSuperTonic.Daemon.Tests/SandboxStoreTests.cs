using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Where a snap or a Flatpak keeps its models and settings, and what the install
/// check remembers about it.
///
/// <para>Both sandboxes make the program's directory read-only, so the portable
/// rule cannot apply. The quieter danger is the snap: snapd names each revision's
/// directory and <c>$HOME</c> by revision, so a check that compared either would
/// greet every snap user with "the program moved" after every refresh.</para>
/// </summary>
public sealed class SandboxStoreTests
{
    private static LinuxDataPaths.SandboxPick? Detect(
        Dictionary<string, string> env, string baseDir, bool flatpakInfo = false) =>
        LinuxDataPaths.DetectSandbox(
            name => env.TryGetValue(name, out var v) ? v : null,
            baseDir,
            path => path == "/.flatpak-info" && flatpakInfo);

    private static Dictionary<string, string> SnapEnv(string revision) => new()
    {
        ["SNAP"] = $"/snap/vibesupertonic/{revision}",
        ["SNAP_NAME"] = "vibesupertonic",
        ["SNAP_INSTANCE_NAME"] = "vibesupertonic",
        ["SNAP_USER_COMMON"] = "/home/me/snap/vibesupertonic/common",
        ["SNAP_USER_DATA"] = $"/home/me/snap/vibesupertonic/{revision}",
        ["SNAP_REAL_HOME"] = "/home/me",
        ["HOME"] = $"/home/me/snap/vibesupertonic/{revision}",
    };

    [Fact]
    public void A_tarball_is_not_in_a_sandbox()
    {
        Assert.Null(Detect(new() { ["HOME"] = "/home/me" }, "/opt/VibeSuperTonic/"));
    }

    [Fact]
    public void A_snap_keeps_its_store_in_common_which_refreshes_do_not_copy()
    {
        var pick = Detect(SnapEnv("x12"), "/snap/vibesupertonic/x12/");

        Assert.NotNull(pick);
        Assert.Equal(LinuxDataPaths.SandboxKind.Snap, pick.Kind);
        Assert.Equal("/home/me/snap/vibesupertonic/common", pick.StoreRoot);
    }

    [Fact]
    public void Two_snap_revisions_look_like_the_same_install_to_the_install_check()
    {
        // The bug this exists for: the raw BaseDir and $HOME both change on every
        // refresh, and the install check compares both.
        var before = Detect(SnapEnv("x12"), "/snap/vibesupertonic/x12/")!;
        var after = Detect(SnapEnv("x13"), "/snap/vibesupertonic/x13/")!;

        Assert.Equal(before.StableBaseDir, after.StableBaseDir);
        Assert.Equal(before.RealHome, after.RealHome);
        Assert.Equal(before.StoreRoot, after.StoreRoot);
        Assert.Equal("/snap/vibesupertonic/current", after.StableBaseDir);
        Assert.Equal("/home/me", after.RealHome);
    }

    [Fact]
    public void Snap_variables_inherited_by_a_program_outside_the_snap_are_ignored()
    {
        // A tarball daemon started from a terminal that a snap opened would
        // otherwise write its store into that snap's directory.
        Assert.Null(Detect(SnapEnv("x12"), "/opt/VibeSuperTonic/"));
    }

    [Fact]
    public void A_snap_parallel_install_uses_its_instance_name()
    {
        var env = SnapEnv("x3");
        env["SNAP_INSTANCE_NAME"] = "vibesupertonic_beta";
        env["SNAP"] = "/snap/vibesupertonic_beta/x3";

        var pick = Detect(env, "/snap/vibesupertonic_beta/x3");

        Assert.Equal("/snap/vibesupertonic_beta/current", pick!.StableBaseDir);
    }

    [Fact]
    public void A_flatpak_keeps_its_store_in_its_own_data_directory()
    {
        var env = new Dictionary<string, string>
        {
            ["FLATPAK_ID"] = "io.github.hananel_hazan.VibeSuperTonic",
            ["HOME"] = "/home/me",
            ["XDG_DATA_HOME"] = "/home/me/.var/app/io.github.hananel_hazan.VibeSuperTonic/data",
        };

        var pick = Detect(env, "/app/lib/vibesupertonic/", flatpakInfo: true);

        Assert.NotNull(pick);
        Assert.Equal(LinuxDataPaths.SandboxKind.Flatpak, pick.Kind);
        Assert.Equal("/home/me/.var/app/io.github.hananel_hazan.VibeSuperTonic/data/vibesupertonic",
                     pick.StoreRoot);
        Assert.Equal("/app/lib/vibesupertonic", pick.StableBaseDir);
        Assert.Equal("/home/me", pick.RealHome);
    }

    [Fact]
    public void A_flatpak_without_xdg_data_home_still_finds_its_own_directory()
    {
        var env = new Dictionary<string, string>
        {
            ["FLATPAK_ID"] = "io.github.hananel_hazan.VibeSuperTonic",
            ["HOME"] = "/home/me",
        };

        var pick = Detect(env, "/app/lib/vibesupertonic", flatpakInfo: true);

        Assert.Equal("/home/me/.var/app/io.github.hananel_hazan.VibeSuperTonic/data/vibesupertonic",
                     pick!.StoreRoot);
    }

    [Fact]
    public void A_flatpak_variable_without_the_sandbox_is_ignored()
    {
        var env = new Dictionary<string, string> { ["FLATPAK_ID"] = "io.github.x.Y", ["HOME"] = "/home/me" };

        Assert.Null(Detect(env, "/app/lib/vibesupertonic", flatpakInfo: false));
        Assert.Null(Detect(env, "/opt/VibeSuperTonic", flatpakInfo: true));
    }

    [Fact]
    public void The_setup_command_names_the_path_that_survives_an_update()
    {
        // The window shows this to the user. The snap's revision directory and the
        // Flatpak's commit directory both vanish at the next update.
        var snap = Detect(SnapEnv("x12"), "/snap/vibesupertonic/x12/")!;
        Assert.Equal("bash /snap/vibesupertonic/current/sandbox-setup.sh bind",
                     LinuxDataPaths.SetupCommand(snap, "bind"));

        var flatpak = Detect(new()
        {
            ["FLATPAK_ID"] = "io.github.hananel_hazan.VibeSuperTonic",
            ["HOME"] = "/home/me",
        }, "/app/lib/vibesupertonic", flatpakInfo: true)!;
        Assert.Equal(
            "bash \"$(flatpak info --show-location io.github.hananel_hazan.VibeSuperTonic)" +
            "/files/lib/vibesupertonic/sandbox-setup.sh\" speechd-install",
            LinuxDataPaths.SetupCommand(flatpak, "speechd-install"));
    }
}

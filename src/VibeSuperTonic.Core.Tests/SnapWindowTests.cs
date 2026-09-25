using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// How a snap's daemon opens the window. Revision 3 ran <c>$SNAP/vibesupertonic-ui</c>
/// directly from a daemon the bare hotkey client had started, and every click on
/// the tray icon aborted the window: without the GNOME extension's command chain
/// there is no libfontconfig. These pin reading that chain from the snap's own
/// <c>meta/snap.yaml</c>.
/// </summary>
public sealed class SnapWindowTests
{
    private const string Root = "/snap/vibesupertonic/3";

    // The shape snapd writes: block style, lists at the key's own indentation,
    // the window app wrapped by the extension and the other apps bare.
    private const string Meta = """
        name: vibesupertonic
        version: 0.2.17
        confinement: strict
        apps:
          vibesupertonic:
            command: vibesupertonic-ui
            plugs:
            - desktop
            - network-bind
            command-chain:
            - snap/command-chain/gpu-2404-wrapper
            - snap/command-chain/desktop-launch
            common-id: io.github.hananel_hazan.VibeSuperTonic
          daemon:
            command: vibesupertonicd
            command-chain:
            - snap/command-chain/not-the-window
          ctl:
            command: vst-ctl
        plugs:
          gnome-46-2404:
            interface: content
            target: $SNAP/gnome-platform
        environment:
          LD_LIBRARY_PATH: $SNAP/lib/native
        """;

    private static readonly Func<string, string?> NoEnv = _ => null;

    [Fact]
    public void The_window_is_started_through_its_own_command_chain()
    {
        var window = SnapWindow.Parse(Meta, Root, NoEnv, out var why);

        Assert.Null(why);
        Assert.Equal(new[]
        {
            "/snap/vibesupertonic/3/snap/command-chain/gpu-2404-wrapper",
            "/snap/vibesupertonic/3/snap/command-chain/desktop-launch",
            "/snap/vibesupertonic/3/vibesupertonic-ui",
        }, window!.Argv);
        Assert.Empty(window.Environment);
    }

    [Fact]
    public void Another_apps_chain_and_the_top_level_environment_are_not_the_windows()
    {
        var window = SnapWindow.Parse(Meta, Root, NoEnv, out _);

        Assert.DoesNotContain(window!.Argv, a => a.Contains("not-the-window"));
        Assert.False(window.Environment.ContainsKey("LD_LIBRARY_PATH"));
    }

    [Fact]
    public void The_apps_own_environment_is_expanded_in_order()
    {
        const string meta = """
            apps:
              vibesupertonic:
                command: $SNAP/bin/ui --flag
                environment:
                  RUNTIME: $SNAP/gnome-platform
                  FONTS: "${RUNTIME}/etc/fonts:$HOME/.fonts"
            """;

        var window = SnapWindow.Parse(meta, Root, n => n == "HOME" ? "/home/u" : null, out _);

        Assert.Equal(new[] { "/snap/vibesupertonic/3/bin/ui", "--flag" }, window!.Argv);
        Assert.Equal("/snap/vibesupertonic/3/gnome-platform", window.Environment["RUNTIME"]);
        Assert.Equal("/snap/vibesupertonic/3/gnome-platform/etc/fonts:/home/u/.fonts", window.Environment["FONTS"]);
    }

    [Fact]
    public void A_flow_style_chain_is_read_too()
    {
        const string meta = """
            apps:
              vibesupertonic:
                command-chain: [snap/command-chain/desktop-launch, 'snap/command-chain/x']
                command: vibesupertonic-ui
            """;

        var window = SnapWindow.Parse(meta, Root, NoEnv, out _);

        Assert.Equal(new[]
        {
            "/snap/vibesupertonic/3/snap/command-chain/desktop-launch",
            "/snap/vibesupertonic/3/snap/command-chain/x",
            "/snap/vibesupertonic/3/vibesupertonic-ui",
        }, window!.Argv);
    }

    [Fact]
    public void A_snap_without_the_window_app_says_so()
    {
        const string meta = """
            apps:
              ctl:
                command: vst-ctl
            """;

        Assert.Null(SnapWindow.Parse(meta, Root, NoEnv, out var why));
        Assert.Contains("no 'vibesupertonic' app", why);
    }

    [Fact]
    public void A_window_app_with_no_command_says_so()
    {
        const string meta = """
            apps:
              vibesupertonic:
                command-chain:
                - snap/command-chain/desktop-launch
            """;

        Assert.Null(SnapWindow.Parse(meta, Root, NoEnv, out var why));
        Assert.Contains("no command", why);
    }

    [Fact]
    public void Resolve_reads_meta_snap_yaml_under_the_snap()
    {
        string root = Directory.CreateTempSubdirectory("vst-snapwin-").FullName;
        try
        {
            Assert.Null(SnapWindow.Resolve(root, NoEnv, out var missing));
            Assert.Contains("meta/snap.yaml", missing);

            Directory.CreateDirectory(Path.Combine(root, "meta"));
            File.WriteAllText(Path.Combine(root, "meta", "snap.yaml"), Meta);

            var window = SnapWindow.Resolve(root, NoEnv, out var why);
            Assert.Null(why);
            Assert.Equal(Path.Combine(root, "vibesupertonic-ui"), window!.Argv[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

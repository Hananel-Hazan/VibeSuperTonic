using System.Net.Sockets;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The link a snap's daemon makes to the compositor's socket. Written 2026-09-27,
/// after a reboot on Kubuntu left the hotkey reading nothing in every Wayland
/// application. See <see cref="SnapWaylandLink"/>.
/// </summary>
public sealed class SnapWaylandLinkTests : IDisposable
{
    // Short, under /tmp: a Unix socket path is limited to ~107 bytes.
    private readonly string _root = Path.Combine("/tmp", "vst-wl-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _runtime;
    private readonly List<Socket> _sockets = [];

    public SnapWaylandLinkTests()
    {
        _runtime = Path.Combine(_root, "snap.vibesupertonic");
        Directory.CreateDirectory(_runtime);
    }

    public void Dispose()
    {
        foreach (var s in _sockets) s.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private void Compositor(string name)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(new UnixDomainSocketEndPoint(Path.Combine(_root, name)));
        _sockets.Add(s);
    }

    /// <summary>The case the type exists for: no window has opened since boot.</summary>
    [Fact]
    public void A_missing_link_is_made_to_the_socket_one_level_up()
    {
        Compositor("wayland-0");
        Assert.NotNull(SnapWaylandLink.Ensure(_runtime, "wayland-0"));

        string link = Path.Combine(_runtime, "wayland-0");
        Assert.NotNull(new FileInfo(link).LinkTarget);
        // Through the link, as libwayland would.
        using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _sockets[0].Listen();
        c.Connect(new UnixDomainSocketEndPoint(link));
    }

    [Fact]
    public void An_unset_display_means_wayland_0()
    {
        Compositor("wayland-0");
        Assert.NotNull(SnapWaylandLink.Ensure(_runtime, null));
        Assert.NotNull(new FileInfo(Path.Combine(_runtime, "wayland-0")).LinkTarget);
    }

    /// <summary>The window's desktop-launch got there first. Leave its link alone.</summary>
    [Fact]
    public void An_existing_link_is_left_alone()
    {
        Compositor("wayland-0");
        File.CreateSymbolicLink(Path.Combine(_runtime, "wayland-0"), "/elsewhere/wayland-0");
        Assert.Null(SnapWaylandLink.Ensure(_runtime, "wayland-0"));
        Assert.Equal("/elsewhere/wayland-0", new FileInfo(Path.Combine(_runtime, "wayland-0")).LinkTarget);
    }

    /// <summary>An X11 session: no socket, so no link, and X11 is the right answer.</summary>
    [Fact]
    public void No_compositor_means_no_link()
    {
        Assert.Null(SnapWaylandLink.Ensure(_runtime, "wayland-0"));
        Assert.False(File.Exists(Path.Combine(_runtime, "wayland-0")));
    }

    [Fact]
    public void An_absolute_display_is_not_the_runtime_dirs_business()
    {
        Compositor("wayland-0");
        Assert.Null(SnapWaylandLink.Ensure(_runtime, Path.Combine(_root, "wayland-0")));
        Assert.Empty(Directory.GetFileSystemEntries(_runtime));
    }

    [Fact]
    public void The_display_name_is_honoured()
    {
        Compositor("wayland-1");
        Assert.NotNull(SnapWaylandLink.Ensure(_runtime, "wayland-1"));
        Assert.NotNull(new FileInfo(Path.Combine(_runtime, "wayland-1")).LinkTarget);
    }

    /// <summary>A failure is reported, not thrown: the daemon must still start.</summary>
    [Fact]
    public void A_link_that_cannot_be_made_is_reported_not_thrown()
    {
        Compositor("wayland-0");
        // Something that is neither a socket nor a link is in the way. A read-only
        // directory would say the same, but not to a test running as root.
        Directory.CreateDirectory(Path.Combine(_runtime, "wayland-0"));
        string? why = SnapWaylandLink.Ensure(_runtime, "wayland-0");
        Assert.NotNull(why);
        Assert.Contains("could not link", why);
    }
}

using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The session a window opened from the tray gets. After a logout and login the
/// daemon, started by the Speech Dispatcher module, still held the old session's
/// XAUTHORITY and every tray click aborted the window (revision 4, 2026-09-25).
/// A hotkey press reports the current one; these pin how that is used.
/// </summary>
public sealed class ClientDisplayTests
{
    private const string Current = "/run/user/1000/xauth_jHeGTu";
    private static readonly Func<string, bool> Exists = p => p == Current;

    private static Request Read(string? display, string? xauth) =>
        new() { Verb = RequestVerb.Read, Display = display, XAuthority = xauth };

    [Fact]
    public void A_daemon_no_client_has_reported_to_keeps_its_own_environment()
    {
        Assert.Empty(new ClientDisplay().ForWindow(Exists));
    }

    [Fact]
    public void The_window_gets_the_session_a_hotkey_press_reported()
    {
        var session = new ClientDisplay();
        session.Observe(Read(":0", Current));

        var vars = session.ForWindow(Exists);

        Assert.Equal(":0", vars["DISPLAY"]);
        Assert.Equal(Current, vars["XAUTHORITY"]);
    }

    [Fact]
    public void The_latest_report_wins()
    {
        var session = new ClientDisplay();
        session.Observe(Read(":1", "/run/user/1000/xauth_RxnJOD"));
        session.Observe(Read(":0", Current));

        Assert.Equal(Current, session.ForWindow(Exists)["XAUTHORITY"]);
        Assert.Equal(":0", session.ForWindow(Exists)["DISPLAY"]);
    }

    [Fact]
    public void A_request_that_reports_nothing_does_not_erase_what_was_reported()
    {
        var session = new ClientDisplay();
        session.Observe(Read(":0", Current));
        session.Observe(new Request { Verb = RequestVerb.Status });

        Assert.Equal(Current, session.ForWindow(Exists)["XAUTHORITY"]);
    }

    [Fact]
    public void A_reported_key_file_that_is_gone_is_not_used()
    {
        var session = new ClientDisplay();
        session.Observe(Read(":0", "/run/user/1000/xauth_RxnJOD"));

        var vars = session.ForWindow(Exists);

        Assert.Equal(":0", vars["DISPLAY"]);
        Assert.False(vars.ContainsKey("XAUTHORITY"));
    }

    [Fact]
    public void A_client_with_no_key_file_means_the_default_so_a_stale_one_is_removed()
    {
        var session = new ClientDisplay();
        session.Observe(Read(":0", null));

        var vars = session.ForWindow(Exists);

        Assert.True(vars.ContainsKey("XAUTHORITY"));
        Assert.Null(vars["XAUTHORITY"]);
    }

    [Fact]
    public void XAuthority_travels_on_the_wire()
    {
        var decoded = Protocol.TryDecode<Request>(Protocol.Encode(Read(":0", Current)));

        Assert.Equal(Current, decoded!.XAuthority);
        Assert.Equal(":0", decoded.Display);
    }
}

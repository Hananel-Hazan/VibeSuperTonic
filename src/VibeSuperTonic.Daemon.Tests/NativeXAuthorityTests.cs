using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Which X cookie file the selection capture uses after a re-login. Written
/// 2026-09-27; see <see cref="NativeXAuthority"/>.
/// </summary>
public sealed class NativeXAuthorityTests
{
    private static Dictionary<string, string?> Session(string? xauth) => new() { ["DISPLAY"] = ":0", ["XAUTHORITY"] = xauth };

    /// <summary>The case: a daemon from the last session, a press from this one.</summary>
    [Fact]
    public void A_newer_session_cookie_replaces_the_stale_one()
    {
        Assert.Equal((true, "/run/user/1000/xauth_new"),
            NativeXAuthority.Decide("/run/user/1000/xauth_old", Session("/run/user/1000/xauth_new")));
    }

    [Fact]
    public void The_same_cookie_changes_nothing()
    {
        Assert.Equal((false, null), NativeXAuthority.Decide("/x/a", Session("/x/a")));
    }

    /// <summary>A client with none uses ~/.Xauthority, which a stale value would hide.</summary>
    [Fact]
    public void A_client_with_no_cookie_file_removes_the_stale_one()
    {
        Assert.Equal((true, null), NativeXAuthority.Decide("/run/user/1000/xauth_old", Session(null)));
    }

    /// <summary>ClientDisplay omits the key when nothing was reported or the reported file is gone.</summary>
    [Fact]
    public void No_report_changes_nothing()
    {
        Assert.Equal((false, null), NativeXAuthority.Decide("/x/a", new Dictionary<string, string?>()));
        Assert.Equal((false, null), NativeXAuthority.Decide("/x/a", new Dictionary<string, string?> { ["DISPLAY"] = ":0" }));
    }

    /// <summary>Through libc, where libX11 reads it: .NET's own copy is not enough.</summary>
    [Fact]
    public void Apply_reaches_the_native_environment()
    {
        string path = Path.Combine(Path.GetTempPath(), "vst-xauth-" + Guid.NewGuid().ToString("N"));
        string? before = Environment.GetEnvironmentVariable("XAUTHORITY");
        try
        {
            Assert.NotNull(NativeXAuthority.Apply(Session(path)));
            Assert.Equal(path, Native());
            Assert.Null(NativeXAuthority.Apply(Session(path)));           // already so
            Assert.NotNull(NativeXAuthority.Apply(Session(null)));
            Assert.Null(Native());
        }
        finally
        {
            NativeXAuthority.Apply(Session(before));
        }

        static string? Native() => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(getenv("XAUTHORITY"));
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern IntPtr getenv(string name);
}

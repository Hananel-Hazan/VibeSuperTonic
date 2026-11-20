using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Daemon.Tray;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What a tray click sends, on which thread, and what <c>status</c> says about
/// the icon afterwards (review items 22, 58 and 60, 2026-11-09).
///
/// <para>In the process-environment collection because the first test sets
/// <c>$DISPLAY</c>: without one set, a tray that still copied it into the
/// request would pass.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class TrayPressTests
{
    private static MenuRow ReadRow(TrayIcon tray) => tray.Rows().Single(r => r.Id == 1);

    /// <summary>
    /// Item 22. The daemon's own <c>$DISPLAY</c> is from the session it started in,
    /// and the daemon prefers a request's display over the latest client report.
    /// </summary>
    [Fact]
    public async Task The_read_row_sends_no_display_of_its_own()
    {
        string? saved = Environment.GetEnvironmentVariable("DISPLAY");
        Environment.SetEnvironmentVariable("DISPLAY", ":77");
        try
        {
            var sent = new TaskCompletionSource<Request>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tray = new TrayIcon(r => { sent.TrySetResult(r); return Response.Success(ToggleAction.Speak); },
                                    () => false, _ => { });

            ReadRow(tray).Click();

            var request = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RequestVerb.Toggle, request.Verb);
            Assert.Null(request.Display);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", saved);
        }
    }

    /// <summary>
    /// Item 58. The click runs on the D-Bus receive loop; a capture that waits on a
    /// busy owner must not hold it.
    /// </summary>
    [Fact]
    public async Task A_click_returns_while_the_capture_is_still_waiting()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tray = new TrayIcon(_ =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            return Response.Success(ToggleAction.Speak);
        }, () => false, _ => { });

        try
        {
            // Run the click as the bus would, and give it a second to come back.
            var click = Task.Run(() => ReadRow(tray).Click());
            Assert.True(click.Wait(TimeSpan.FromSeconds(1)),
                "the click held its thread until the capture finished");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// Off the bus, clicks still do not overlap: the second click of a pair made
    /// to cancel a slow capture must see the first one's result, not run beside it.
    /// </summary>
    [Fact]
    public async Task Two_clicks_run_one_after_the_other()
    {
        int running = 0, most = 0;
        var tray = new TrayIcon(_ =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            Thread.Sleep(200);
            Interlocked.Decrement(ref running);
            return Response.Success(ToggleAction.Speak);
        }, () => false, _ => { });

        await Task.WhenAll(tray.Invoke(RequestVerb.Toggle), tray.Invoke(RequestVerb.Toggle))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, most);

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target))
                   && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    /// <summary>Item 60, first half: a watcher that leaves takes the icon with it.</summary>
    [Fact]
    public async Task Status_stops_saying_registered_when_the_watcher_leaves_and_says_it_again_on_return()
    {
        var log = new List<string>();
        var tray = new TrayIcon(_ => Response.Success(ToggleAction.Ignored), () => false, log.Add);
        tray.Registered("org.kde.StatusNotifierWatcher", "org.kde.StatusNotifierItem-1-1", ":1.10");
        Assert.StartsWith("registered with", tray.Status);

        await tray.FollowOnceAsync(() => Task.FromResult<string?>(null),
                                   () => throw new InvalidOperationException("must not register with nobody"));
        Assert.DoesNotContain("registered with", tray.Status);
        Assert.Contains("left the session bus", tray.Status);

        int registrations = 0;
        await tray.FollowOnceAsync(() => Task.FromResult<string?>(":1.42"),
                                   () => { registrations++; return Task.FromResult<string?>("org.kde.StatusNotifierWatcher"); });
        Assert.Equal(1, registrations);
        Assert.StartsWith("registered with", tray.Status);
    }

    /// <summary>A watcher that comes back and refuses is asked again on the next look.</summary>
    [Fact]
    public async Task A_refused_re_registration_is_retried()
    {
        var tray = new TrayIcon(_ => Response.Success(ToggleAction.Ignored), () => false, _ => { });
        tray.Registered("org.kde.StatusNotifierWatcher", "item", ":1.10");

        int registrations = 0;
        Task<string?> Refuse() { registrations++; return Task.FromResult<string?>(null); }
        await tray.FollowOnceAsync(() => Task.FromResult<string?>(":1.42"), Refuse);
        Assert.Contains("refused", tray.Status);
        await tray.FollowOnceAsync(() => Task.FromResult<string?>(":1.42"), Refuse);
        Assert.Equal(2, registrations);
    }

    /// <summary>
    /// Item 60, second half: the disconnect continuation read <c>t.Result</c>,
    /// which throws on a faulted task, so the status was never updated.
    /// </summary>
    [Fact]
    public void A_faulted_disconnect_still_reaches_the_status()
    {
        var faulted = Task.FromException<Exception?>(new IOException("the bus went away"));
        Assert.Equal("tray icon lost the session bus: the bus went away", TrayIcon.LostBusStatus(faulted));

        var ended = Task.FromResult<Exception?>(null);
        Assert.Equal("tray icon lost the session bus: disconnected", TrayIcon.LostBusStatus(ended));
    }
}

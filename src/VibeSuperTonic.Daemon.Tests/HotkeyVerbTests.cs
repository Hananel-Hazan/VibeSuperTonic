using System.Collections.Concurrent;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using VibeSuperTonic.Daemon.Tray;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The keys batch 4B added (review items 92, 93, 95) as the daemon answers them,
/// and the tray notice that used to vanish on the Idle after it (item 42's open
/// end from batch 3).
/// </summary>
public sealed class HotkeyVerbTests : IDisposable
{
    private const string Three = "One is first. Two is second. Three is last.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-keys-{Guid.NewGuid():N}");
    private readonly ClipboardSelection _selection = new();
    private readonly SpeechSession _session;
    private readonly DaemonServer _server;
    private readonly ConcurrentQueue<SessionEvent> _events = new();

    public HotkeyVerbTests()
    {
        string data = Path.Combine(_root, "data");
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(models, "onnx"));

        var config = new HostConfig(data, models);
        config.Reload(force: true);
        var synth = new UntilCancelled();
        _session = new SpeechSession(synth, new Sink(), new SpeechSessionOptions(PrimeMs: 10));
        _session.Emitted += e => _events.Enqueue(e);
        _server = new DaemonServer(new DaemonOptions(Version: "test"), config, synth, _session, _selection);
    }

    public void Dispose()
    {
        _server.Dispose();
        _session.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private class Selection : ISelectionSource
    {
        public string Text { get; set; } = "";
        public SelectionResult Capture(string? display = null) => SelectionResult.Captured(Text);
    }

    private sealed class ClipboardSelection : Selection, IClipboardCapture
    {
        public string Clipboard { get; set; } = "";
        public SelectionResult CaptureClipboard(string? display) => SelectionResult.Captured(Clipboard);
    }

    private sealed class UntilCancelled : ISynthesizer
    {
        public int SampleRate => 24000;
        public short[] Synthesize(string text, SynthesisOptions options, CancellationToken ct = default)
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();
            return [];
        }
        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class Sink : IAudioSink
    {
        public int SampleRate => 24000;
        public long WrittenFrames => 0;
        public long LatencyUsec => 0;
        public void Write(ReadOnlySpan<short> pcm) { }
        public void Flush() { }
        public void RequestFlush() { }
        public void Drain() { }
        public void Dispose() { }
    }

    private Response Send(Request r) => _server.Invoke(r);

    private async Task UntilIdle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_session.State != SpeechState.Idle && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(SpeechState.Idle, _session.State);
    }

    private int LastPreparingOffset() =>
        _events.Where(e => e.Kind == SessionEventKind.StateChanged && e.State == SpeechState.Preparing)
               .Last().SourceOffset ?? -1;

    // ---------------------------------------------------------------- item 93

    [Theory]
    // Reading, at the first sentence: next is the second, previous stays on the first.
    [InlineData(0, false, 1, 14)]
    [InlineData(0, false, -1, 0)]
    [InlineData(0, false, 0, 0)]
    // Heard part way into the second sentence: 0 restarts it, -1 is the first.
    [InlineData(20, false, 0, 14)]
    [InlineData(20, false, -1, 0)]
    [InlineData(20, false, 1, 29)]
    // In the last: nothing after it.
    [InlineData(35, false, 1, null)]
    public void A_sentence_skip_moves_from_the_sentence_being_heard(int position, bool idle, int skip, int? expected)
    {
        Assert.Equal(expected, DaemonServer.SentenceTarget(Three, position, idle, skip));
    }

    [Fact]
    public void After_the_reading_ended_back_one_is_the_last_sentence()
    {
        Assert.Equal(29, DaemonServer.SentenceTarget(Three, null, idle: true, skip: -1));
        Assert.Equal(14, DaemonServer.SentenceTarget(Three, null, idle: true, skip: -2));
        Assert.Null(DaemonServer.SentenceTarget(Three, null, idle: true, skip: 0));
        Assert.Null(DaemonServer.SentenceTarget(Three, null, idle: true, skip: 1));
    }

    /// <summary>
    /// Sentences, not render chunks: the session's chunker merges sentences under
    /// 100 characters, which would make "next" skip all three of these at once.
    /// </summary>
    [Fact]
    public void Short_sentences_are_not_merged_into_one_skip()
    {
        Assert.NotNull(DaemonServer.SentenceTarget(Three, 0, idle: false, skip: 2));
    }

    [Fact]
    public async Task Seek_by_sentence_restarts_the_reading_at_the_next_one()
    {
        Assert.True(Send(new Request { Verb = RequestVerb.Speak, Text = Three }).Ok);

        var r = Send(new Request { Verb = RequestVerb.Seek, Sentences = 1 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(14, LastPreparingOffset());

        var past = Send(new Request { Verb = RequestVerb.Seek, Sentences = 5 });
        Assert.False(past.Ok);
        Assert.Contains("no sentence after", past.Error);

        _session.Stop();
        await UntilIdle();

        var back = Send(new Request { Verb = RequestVerb.Seek, Sentences = -1 });
        Assert.True(back.Ok, back.Error);
        Assert.Equal(29, LastPreparingOffset());
        _session.Stop();
        await UntilIdle();
    }

    // ---------------------------------------------------------------- item 92

    [Fact]
    public async Task Pause_toggle_pauses_then_resumes_and_says_which()
    {
        var idle = Send(new Request { Verb = RequestVerb.PauseToggle });
        Assert.True(idle.Ok);
        Assert.Equal("nothing is being read", idle.Notice);

        Assert.True(Send(new Request { Verb = RequestVerb.Speak, Text = Three }).Ok);

        var paused = Send(new Request { Verb = RequestVerb.PauseToggle });
        Assert.Equal("paused", paused.Notice);
        Assert.True(_session.IsPaused);

        var resumed = Send(new Request { Verb = RequestVerb.PauseToggle });
        Assert.Equal("resumed", resumed.Notice);
        Assert.False(_session.IsPaused);

        _session.Stop();
        await UntilIdle();
    }

    [Fact]
    public async Task A_rate_step_is_held_clamped_reported_and_reaches_the_next_utterance()
    {
        Assert.True(Send(new Request { Verb = RequestVerb.Speak, Text = Three }).Ok);
        double baseline = _session.Options.StretchFactor;
        _session.Stop();
        await UntilIdle();

        var up = Send(new Request { Verb = RequestVerb.Rate, RateSteps = 1 });
        Assert.True(up.Ok, up.Error);
        Assert.Contains("1.10x", up.Notice);
        Assert.Equal(1, Send(new Request { Verb = RequestVerb.Status }).Status!.RateSteps);

        Assert.True(Send(new Request { Verb = RequestVerb.Speak, Text = Three }).Ok);
        Assert.Equal(baseline * 1.1, _session.Options.StretchFactor, 3);
        _session.Stop();
        await UntilIdle();

        Send(new Request { Verb = RequestVerb.Rate, RateSteps = 50 });
        Assert.Equal(Protocol.MaxRateSteps, Send(new Request { Verb = RequestVerb.Status }).Status!.RateSteps);

        Send(new Request { Verb = RequestVerb.Rate, RateSteps = 0, RateAbsolute = true });
        Assert.Null(Send(new Request { Verb = RequestVerb.Status }).Status!.RateSteps);
    }

    // ---------------------------------------------------------------- item 95

    [Fact]
    public async Task Read_clipboard_reads_the_clipboard_not_the_selection()
    {
        _selection.Text = "The selection.";
        _selection.Clipboard = "The clipboard.";

        var r = Send(new Request { Verb = RequestVerb.Read, Clipboard = true });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("The clipboard.", _session.CurrentText);
        _session.Stop();
        await UntilIdle();
    }

    [Fact]
    public void A_source_that_cannot_read_the_clipboard_refuses_rather_than_reading_the_selection()
    {
        string data = Path.Combine(_root, "data2");
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(data);
        var config = new HostConfig(data, models);
        config.Reload(force: true);
        var synth = new UntilCancelled();
        using var session = new SpeechSession(synth, new Sink(), new SpeechSessionOptions(PrimeMs: 10));
        using var server = new DaemonServer(new DaemonOptions(Version: "test"), config, synth, session,
            new Selection { Text = "The selection." });

        var r = server.Invoke(new Request { Verb = RequestVerb.Read, Clipboard = true });
        Assert.False(r.Ok);
        Assert.Contains("cannot read the clipboard", r.Error);
        Assert.Null(session.CurrentText);
    }

    // ------------------------------------------------- item 42, the tray's end

    [Fact]
    public void A_notice_survives_the_idle_that_follows_until_the_next_press()
    {
        var tray = new TrayIcon(_ => Response.Success(), () => false, _ => { });

        tray.OnSessionEvent(SessionEvent.Preparing("   ", 0));
        tray.OnSessionEvent(SessionEvent.NoticeOf("nothing to read: the text has no speakable characters"));
        tray.OnSessionEvent(SessionEvent.Of(SessionEventKind.Finished));
        tray.OnSessionEvent(SessionEvent.Of(SpeechState.Idle));
        Assert.Contains("no speakable characters", tray.ToolTip());

        tray.OnSessionEvent(SessionEvent.Preparing("Hello there.", 0));
        Assert.DoesNotContain("no speakable characters", tray.ToolTip());
    }

    [Fact]
    public void An_error_survives_the_idle_too()
    {
        var tray = new TrayIcon(_ => Response.Success(), () => false, _ => { });
        tray.OnSessionEvent(SessionEvent.Preparing("Hello.", 0));
        tray.OnSessionEvent(SessionEvent.Error("synthesis failed: out of memory"));
        tray.OnSessionEvent(SessionEvent.Of(SpeechState.Idle));
        Assert.Contains("out of memory", tray.ToolTip());
    }

    [Fact]
    public async Task The_pause_row_shows_only_while_reading_and_sends_the_toggle()
    {
        var sent = new TaskCompletionSource<Request>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tray = new TrayIcon(r => { sent.TrySetResult(r); return Response.Success(); }, () => false, _ => { });
        var row = tray.Rows().Single(r => r.Id == 6);

        Assert.False(row.Visible!());
        tray.OnSessionEvent(SessionEvent.Preparing("Hello.", 0));
        Assert.True(row.Visible!());

        row.Click();
        Assert.Equal(RequestVerb.PauseToggle, (await sent.Task.WaitAsync(TimeSpan.FromSeconds(5))).Verb);
    }
}

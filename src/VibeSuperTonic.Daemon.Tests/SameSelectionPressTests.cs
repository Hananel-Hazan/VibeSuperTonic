using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The read key pressed while the selection is still the text being read.
///
/// <para>The desktop holds one selection. Highlighting text in a second window
/// takes it from the first, and the first keeps its highlight on screen without
/// publishing it any more. So select in two windows, read one, press in the
/// other: the capture returns the text already playing. Until 2026-10-18 that
/// press did nothing, and the key looked dead. It now stops the reading, the
/// user's choice; the next press after selecting again reads the new text.</para>
/// </summary>
public sealed class SameSelectionPressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-same-{Guid.NewGuid():N}");
    private readonly Selection _selection = new();
    private readonly SpeechSession _session;
    private readonly DaemonServer _server;

    public SameSelectionPressTests()
    {
        string data = Path.Combine(_root, "data");
        string models = Path.Combine(_root, "models");
        Directory.CreateDirectory(data);
        // NotReadyToSpeak refuses a daemon with no models; the synthesizer is fake.
        Directory.CreateDirectory(Path.Combine(models, "onnx"));

        var config = new HostConfig(data, models);
        config.Reload(force: true);
        var synth = new UntilCancelled();
        _session = new SpeechSession(synth, new Sink(), new SpeechSessionOptions(PrimeMs: 10));
        _server = new DaemonServer(new DaemonOptions(Version: "test"), config, synth, _session, _selection);
    }

    public void Dispose()
    {
        _server.Dispose();
        _session.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Selection : ISelectionSource
    {
        public string Text { get; set; } = "";
        public SelectionResult Capture(string? display = null) => SelectionResult.Captured(Text);
    }

    /// <summary>Renders nothing until the reading is stopped, so it stays active.</summary>
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

    private Response Press() => _server.Invoke(new Request { Verb = RequestVerb.Read });

    /// <summary>Past the gate's debounce, so each press is a real one.</summary>
    private static void Pause() => Thread.Sleep(ToggleGate.DefaultDebounceMs + 50);

    private async Task UntilIdle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_session.State != SpeechState.Idle && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(SpeechState.Idle, _session.State);
    }

    [Fact]
    public async Task A_press_on_the_text_already_being_read_stops_it()
    {
        _selection.Text = "The first window's paragraph.";
        var first = Press();
        Assert.True(first.Ok, first.Error);
        Assert.Equal(ToggleAction.Speak, first.Action);
        Assert.NotEqual(SpeechState.Idle, _session.State);

        Pause();
        var second = Press();
        Assert.True(second.Ok, second.Error);
        Assert.Equal(ToggleAction.Stop, second.Action);
        await UntilIdle();
    }

    [Fact]
    public async Task After_that_stop_the_next_press_reads_again()
    {
        _selection.Text = "The first window's paragraph.";
        Press();
        Pause();
        Press();
        await UntilIdle();

        // The user selects again in the second window.
        _selection.Text = "The second window's paragraph.";
        Pause();
        var third = Press();
        Assert.Equal(ToggleAction.Speak, third.Action);
        Assert.Equal("The second window's paragraph.", _session.CurrentText);
    }

    [Fact]
    public void A_press_on_different_text_interrupts_and_reads_it()
    {
        _selection.Text = "The first window's paragraph.";
        Press();

        _selection.Text = "The second window's paragraph.";
        Pause();
        var second = Press();
        Assert.Equal(ToggleAction.Speak, second.Action);
        Assert.Equal("The second window's paragraph.", _session.CurrentText);
    }
}

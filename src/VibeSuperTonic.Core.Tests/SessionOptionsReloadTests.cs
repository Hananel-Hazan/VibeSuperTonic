using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Phase 4b: configuration reaching the session, and doing so at a moment that
/// cannot corrupt an utterance.
///
/// <para>The defect these exist for is not subtle and had no test that could
/// see it: <see cref="SpeechSession"/> threaded pronunciation rules through
/// <see cref="SynthTextPipeline"/> from Phase 3 onward and <b>nothing ever
/// filled them in</b>, so the Linux daemon applied no rules at all. Every unit
/// test passed, because each component was correct in isolation and the gap was
/// that no caller supplied the input. The first test here is the one that would
/// have failed.</para>
///
/// <para>The rest pin the reload semantics. <see cref="SpeechSession.Options"/>
/// is settable so an edited <c>settings.json</c> reaches a running daemon, and
/// the risk that introduces is a swap landing <i>between</i> the pipeline's
/// rewrite and the chunker's — which would let the offset maps disagree about
/// the same text, i.e. R-2 and R-14 with a new cause. The guarantee is that one
/// utterance sees exactly one generation of config.</para>
/// </summary>
public class SessionOptionsReloadTests
{
    private static readonly SupertonicOptions Voice = new("M1", "en");

    private static PronunciationsConfig Rules(params (string Match, string Replace)[] rules)
    {
        var config = new PronunciationsConfig();
        foreach (var (match, replace) in rules)
            config.Rules.Add(new PronunciationRule { Match = match, Replace = replace });
        return config;
    }

    private static SpeechSessionOptions With(PronunciationsConfig? config, int primeMs = 10)
    {
        Regex?[] compiled = config is null
            ? Array.Empty<Regex?>()
            : config.Rules.Select(PronunciationsConfig.Compile).ToArray();
        return new SpeechSessionOptions(config, config is null ? null : compiled, PrimeMs: primeMs);
    }

    [Fact]
    public async Task Pronunciation_rules_reach_the_model()
    {
        // The Phase 4b bug in one assertion. Before the daemon loaded
        // pronunciations.json, this text arrived at the synthesizer verbatim on
        // Linux and rewritten on Windows — the two platforms speaking the same
        // input differently, which is exactly the drift Core exists to prevent.
        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, new FakeSink(), With(Rules(("kg", "kilograms"))));

        Assert.True(session.Speak("It weighs 5 kg today.", Voice));
        await session.Completion;

        Assert.Contains("kilograms", string.Join(" ", synth.Rendered));
        Assert.DoesNotContain("5 kg ", string.Join(" ", synth.Rendered));
    }

    [Fact]
    public async Task No_rules_means_the_text_is_untouched()
    {
        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, new FakeSink(), With(null));

        Assert.True(session.Speak("It weighs 5 kg today.", Voice));
        await session.Completion;

        Assert.Contains("kg", string.Join(" ", synth.Rendered));
        Assert.DoesNotContain("kilograms", string.Join(" ", synth.Rendered));
    }

    [Fact]
    public async Task Swapping_options_takes_effect_on_the_next_utterance()
    {
        // What `vst-ctl reload` has to deliver, and what the mtime check
        // delivers by itself when the UI saves the file.
        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, new FakeSink(), With(null));

        Assert.True(session.Speak("It weighs 5 kg today.", Voice));
        await session.Completion;
        Assert.DoesNotContain("kilograms", string.Join(" ", synth.Rendered));

        session.Options = With(Rules(("kg", "kilograms")));

        synth.Rendered.Clear();
        Assert.True(session.Speak("It weighs 5 kg today.", Voice));
        await session.Completion;

        Assert.Contains("kilograms", string.Join(" ", synth.Rendered));
    }

    [Fact]
    public async Task An_utterance_in_flight_keeps_the_options_it_started_with()
    {
        // The reason Run snapshots rather than reading the field per use. A
        // reload arriving mid-utterance must not change the rules between one
        // chunk and the next: the pipeline map and every chunk map are computed
        // from one generation of config and are only consistent with each other.
        var synth = new FakeSynthesizer();
        var sink = new FakeSink { WriteDelayMs = 4 };
        using var session = new SpeechSession(synth, sink, With(Rules(("kg", "kilograms"))));

        // Several sentences, so chunks are still being rendered when the swap
        // lands. Each is its own chunk, and each carries the token under test.
        string text = string.Join(" ",
            Enumerable.Range(0, 6).Select(i => $"Sentence {i} weighs 5 kg exactly."));

        Assert.True(session.Speak(text, Voice));

        // Swap as soon as the first chunk has reached the model. Everything
        // rendered after this point is rendered under the new options if the
        // session re-reads them, and under the old ones if it snapshots.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (synth.Rendered.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(1);

        session.Options = With(Rules(("kg", "POUNDS")));

        await session.Completion;

        string rendered = string.Join(" ", synth.Rendered);
        Assert.DoesNotContain("POUNDS", rendered);
        Assert.Contains("kilograms", rendered);

        // And the swap did land — otherwise this proves nothing at all.
        Assert.Contains("POUNDS", session.Options.Pronunciations!.Rules[0].Replace);
    }

    [Fact]
    public void Options_cannot_be_set_to_null()
    {
        using var session = new SpeechSession(new FakeSynthesizer(), new FakeSink());
        Assert.Throws<ArgumentNullException>(() => session.Options = null!);
    }

    [Fact]
    public void Options_defaults_are_not_null_when_none_are_supplied()
    {
        using var session = new SpeechSession(new FakeSynthesizer(), new FakeSink());
        Assert.NotNull(session.Options);
        Assert.Null(session.Options.Pronunciations);
    }

    // ------------------------------------------------------------------- DSP

    [Fact]
    public async Task The_stretch_shortens_what_reaches_the_device()
    {
        // Proves the DSP stage is actually WIRED, not merely present. The Linux
        // daemon read DspRate 1.35 from a real install and did nothing with it,
        // so the platforms spoke the same settings.json at different speeds.
        var synth = new FakeSynthesizer { FramesPerChunk = 44100 };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink,
            new SpeechSessionOptions(PrimeMs: 10, StretchFactor: 1.35));

        Assert.True(session.Speak("One sentence.", Voice));
        await session.Completion;

        // 44100 / 1.35 = 32667, and Sonic is approximate rather than exact.
        Assert.InRange(sink.WrittenFrames, 31000, 34500);
    }

    [Fact]
    public async Task A_unity_stretch_leaves_the_audio_alone()
    {
        var synth = new FakeSynthesizer { FramesPerChunk = 44100 };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));

        Assert.True(session.Speak("One sentence.", Voice));
        await session.Completion;

        Assert.Equal(44100, sink.WrittenFrames);
    }

    [Fact]
    public async Task Boundaries_are_planned_against_the_stretched_audio()
    {
        // The reason the stretch happens in the renderer rather than on the
        // write path: boundaries are planned from pcm.Length, so stretching
        // first means every boundary lands in the audio that will actually be
        // heard. Applying it after planning would desynchronise every highlight
        // by the stretch ratio -- silently, because a wrong offset sounds
        // exactly like a right one [trap 12].
        var events = new List<SessionEvent>();
        var synth = new FakeSynthesizer { FramesPerChunk = 44100 };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink,
            new SpeechSessionOptions(PrimeMs: 10, StretchFactor: 2.0));
        session.Emitted += e => { lock (events) events.Add(e); };

        Assert.True(session.Speak("Alpha bravo charlie delta.", Voice));
        await session.Completion;

        double total = (double)sink.WrittenFrames / sink.SampleRate;
        var boundaries = events.Where(e => e.Kind == SessionEventKind.WordBoundary).ToList();

        Assert.NotEmpty(boundaries);
        // Every boundary must sit inside the audio that was actually written.
        // Planned against the unstretched buffer they would run to ~2x that.
        foreach (var b in boundaries)
            Assert.InRange(b.AudioSeconds ?? 0, 0, total + 0.01);
    }

    // ------------------------------------------------- stop, then speak again

    [Fact]
    public async Task Speaking_works_immediately_after_a_stop_during_preparing()
    {
        // Reported from real use: press the key, press stop, press the key
        // again -- and nothing happens until you press it a couple more times.
        //
        // Stop() sets the sink's flush flag from the stopping thread and only
        // the writer consumes it, inside Write. A stop during Preparing unwinds
        // through the renderer without ever entering Write, so the flag
        // survived into the NEXT utterance, whose first write tripped it and
        // died -- and tripping it cleared it, so the press after that worked.
        var synth = new FakeSynthesizer { StallUntilCancelled = true };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));

        var events = new List<SessionEvent>();
        session.Emitted += e => { lock (events) events.Add(e); };

        // Press: the synthesizer stalls, so this never reaches the write loop.
        Assert.True(session.Speak("Alpha bravo charlie.", Voice));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.State != SpeechState.Preparing && DateTime.UtcNow < deadline)
            await Task.Delay(1);

        // Stop while still preparing — the case that leaked the flag.
        session.Stop();
        await session.Completion;
        Assert.Equal(SpeechState.Idle, session.State);

        // Press again. This must actually speak.
        synth.StallUntilCancelled = false;
        lock (events) events.Clear();

        Assert.True(session.Speak("Delta echo foxtrot.", Voice));
        await session.Completion;

        List<SessionEvent> seen;
        lock (events) seen = events.ToList();

        Assert.Contains(seen, e => e.Kind == SessionEventKind.Started);
        Assert.Contains(seen, e => e.Kind == SessionEventKind.Finished);
        Assert.DoesNotContain(seen, e => e.Kind == SessionEventKind.Stopped);
        Assert.True(sink.WrittenFrames > 0, "the second utterance wrote no audio at all");
    }

    [Fact]
    public async Task A_stop_that_never_reached_the_writer_still_clears_the_sink()
    {
        // The mechanism on its own, so a future change to the teardown that
        // drops the flush is caught here rather than by a person pressing a key.
        var synth = new FakeSynthesizer { StallUntilCancelled = true };
        var sink = new FakeSink();
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));

        Assert.True(session.Speak("Alpha bravo charlie.", Voice));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.State != SpeechState.Preparing && DateTime.UtcNow < deadline)
            await Task.Delay(1);

        session.Stop();
        await session.Completion;

        // Nothing was ever written, so Write cannot have done the clearing.
        Assert.Equal(0, sink.WriteCount);
        Assert.True(sink.FlushCount > 0, "teardown left the sink holding a stop request");
    }

    // --------------------------------------------------- restart (the read key)

    [Fact]
    public async Task Restart_replaces_a_speaking_utterance_with_a_new_one()
    {
        // The primary key's contract: a press always reads what is selected
        // NOW, interrupting whatever is playing. Speak alone cannot do this --
        // it refuses unless the session is Idle.
        var synth = new FakeSynthesizer();
        var sink = new FakeSink { WriteDelayMs = 2 };
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));

        Assert.True(session.Speak("First selection here.", Voice));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (synth.Rendered.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);

        Assert.True(session.Restart("Second selection here.", Voice));
        await session.Completion;

        Assert.Contains("Second selection here.", string.Join(" ", synth.Rendered));
        Assert.Equal(SpeechState.Idle, session.State);
    }

    [Fact]
    public async Task Restart_from_idle_is_just_a_speak()
    {
        var synth = new FakeSynthesizer();
        using var session = new SpeechSession(synth, new FakeSink(), new SpeechSessionOptions(PrimeMs: 10));

        Assert.True(session.Restart("Only selection.", Voice));
        await session.Completion;

        Assert.Contains("Only selection.", string.Join(" ", synth.Rendered));
    }

    [Fact]
    public async Task Restart_survives_being_used_repeatedly()
    {
        // A user holding down the read key on changing selections. Each press
        // must land, and none may leave the session wedged -- which is the
        // failure mode a stop-then-speak pair written by the caller would have.
        var synth = new FakeSynthesizer();
        var sink = new FakeSink { WriteDelayMs = 1 };
        using var session = new SpeechSession(synth, sink, new SpeechSessionOptions(PrimeMs: 10));

        for (int i = 0; i < 8; i++)
            Assert.True(session.Restart($"Selection number {i}.", Voice), $"restart {i} refused");

        await session.Completion;
        Assert.Equal(SpeechState.Idle, session.State);
        Assert.Contains("Selection number 7.", string.Join(" ", synth.Rendered));
    }

    [Fact]
    public void The_read_key_never_means_stop_and_is_still_debounced()
    {
        var gate = new ToggleGate(debounceMs: 150);

        Assert.True(gate.PressToRead(1000));
        Assert.False(gate.PressToRead(1100));      // inside the window
        Assert.True(gate.PressToRead(1200));

        // Even while speaking, the answer is speak — that is the whole point.
        gate.NoteState(SpeechState.Speaking);
        Assert.True(gate.PressToRead(2000));
    }
}

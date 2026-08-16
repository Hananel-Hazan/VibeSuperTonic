using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The two seams Phase 6 consumes, both added as preparation for it rather than
/// during it.
///
/// <para><b>The text on the stream.</b> Every boundary reports an offset, and
/// until now nothing on the stream said what those offsets index into. A
/// subscriber that connected mid-read got the answer in the <c>subscribe</c>
/// snapshot; one that stayed connected across utterances — a window left open,
/// which is the normal case for a Reader tab — got positions in a string it had
/// never been shown.</para>
///
/// <para><b>Seek.</b> Click-a-word-to-jump. The plan said "reuse <c>ApplySkip</c>",
/// which is not available: that method is private to the Windows SAPI engine and
/// walks a COM exec plan. What made this cheap instead was
/// <see cref="BoundaryPlanner.PlanChunk"/> already taking a <c>sourceBase</c>
/// that nothing had ever passed anything but zero to.</para>
/// </summary>
public class SeekAndStreamTextTests
{
    private static readonly SynthesisOptions Voice = new("M1", "en");

    private static SpeechSessionOptions Options() => new(PrimeMs: 10);

    private sealed class Recorder
    {
        private readonly List<SessionEvent> _events = new();
        public IReadOnlyList<SessionEvent> Events { get { lock (_events) return _events.ToList(); } }
        public void Add(SessionEvent e) { lock (_events) _events.Add(e); }
    }

    private static (SpeechSession Session, FakeSynthesizer Synth, Recorder Log) Build()
    {
        var synth = new FakeSynthesizer();
        var session = new SpeechSession(synth, new FakeSink(), Options());
        var log = new Recorder();
        session.Emitted += log.Add;
        return (session, synth, log);
    }

    // ------------------------------------------- the text arrives on the stream

    [Fact]
    public async Task Preparing_carries_the_text_about_to_be_spoken()
    {
        const string Text = "The sea is everything.";
        var (session, _, log) = Build();
        using var _s = session;

        session.Speak(Text, Voice);
        await session.Completion;

        var preparing = Assert.Single(log.Events.Where(e =>
            e.Kind == SessionEventKind.StateChanged && e.State == SpeechState.Preparing));

        Assert.Equal(Text, preparing.Text);
        Assert.Equal(0, preparing.SourceOffset);
    }

    [Fact]
    public async Task No_other_event_repeats_the_text()
    {
        // A selection may be 100 KB (R-9). Repeating it on the Speaking and Idle
        // transitions would triple that on a socket to say nothing new.
        var (session, _, log) = Build();
        using var _s = session;

        session.Speak("One two three.", Voice);
        await session.Completion;

        var carrying = log.Events.Where(e => e.Text is not null).ToList();
        Assert.Single(carrying);
        Assert.Equal(SpeechState.Preparing, carrying[0].State);
    }

    [Fact]
    public async Task A_subscriber_reacting_to_Preparing_sees_the_new_text_in_CurrentText()
    {
        // The ordering fix. CurrentText was assigned AFTER the Preparing event
        // was emitted, so a subscriber whose handler asked the session what it
        // was speaking was told the PREVIOUS utterance -- or null, on the first.
        // The event carrying the text is the real fix; this pins that the
        // snapshot agrees with it, because a client using both must not see two
        // answers for one utterance.
        var (session, _, _) = Build();
        using var _s = session;

        string? observed = null;
        session.Emitted += e =>
        {
            if (e.Kind == SessionEventKind.StateChanged && e.State == SpeechState.Preparing)
                observed ??= session.CurrentText;
        };

        session.Speak("Second utterance.", Voice);
        await session.Completion;

        Assert.Equal("Second utterance.", observed);
    }

    [Fact]
    public async Task LastText_survives_the_utterance_that_CurrentText_does_not()
    {
        // Clicking a word after the reading has finished is the ordinary Reader
        // gesture: the text is still on screen. CurrentText is cleared on the
        // terminal event by design, so seeking needs something that is not.
        var (session, _, _) = Build();
        using var _s = session;

        session.Speak("All done now.", Voice);
        await session.Completion;

        Assert.Null(session.CurrentText);
        Assert.Equal("All done now.", session.LastText);
    }

    // ------------------------------------------------------------------- seek

    [Fact]
    public async Task Seeking_reports_offsets_against_the_whole_text_not_the_fragment()
    {
        // The assertion that matters, and the reason sourceBase is threaded
        // through instead of clients adding the offset back themselves: every
        // reported span must still spell its own word when used to substring the
        // text the CALLER passed in. An offset in fragment coordinates would
        // substring the wrong characters here -- which is R-2 and R-14 exactly,
        // an index from one coordinate space added to an offset from another.
        const string Text = "First one.  Second one.\n\nThird one here.";
        int from = Text.IndexOf("Second", StringComparison.Ordinal);

        var (session, _, log) = Build();
        using var _s = session;

        session.Speak(Text, Voice, startOffset: from);
        await session.Completion;

        var words = log.Events
            .Where(e => e.Kind == SessionEventKind.WordBoundary)
            .Select(e => Text.Substring(e.SourceOffset!.Value, e.SourceLength!.Value))
            .ToList();

        Assert.Equal(new[] { "Second", "one", "Third", "one", "here" }, words);
    }

    [Fact]
    public async Task Seeking_does_not_render_the_text_before_the_offset()
    {
        const string Text = "Skip this part. Speak this part.";
        int from = Text.IndexOf("Speak", StringComparison.Ordinal);

        var (session, synth, _) = Build();
        using var _s = session;

        session.Speak(Text, Voice, startOffset: from);
        await session.Completion;

        Assert.NotEmpty(synth.Rendered);
        Assert.DoesNotContain(synth.Rendered, chunk => chunk.Contains("Skip this"));
    }

    [Fact]
    public async Task Seeking_keeps_the_whole_text_as_CurrentText()
    {
        // The window still shows everything; only playback starts part way in.
        // If this reported the fragment, the highlight would index into one
        // string while the user reads another.
        const string Text = "Alpha beta. Gamma delta.";
        var (session, _, log) = Build();

        string? during = null;
        session.Emitted += e =>
        {
            if (e.Kind == SessionEventKind.StateChanged && e.State == SpeechState.Speaking)
                during ??= session.CurrentText;
        };

        session.Speak(Text, Voice, startOffset: Text.IndexOf("Gamma", StringComparison.Ordinal));
        await session.Completion;
        session.Dispose();

        Assert.Equal(Text, during);
    }

    [Fact]
    public async Task Preparing_reports_where_a_seek_starts()
    {
        const string Text = "Alpha beta. Gamma delta.";
        int from = Text.IndexOf("Gamma", StringComparison.Ordinal);

        var (session, _, log) = Build();
        using var _s = session;

        session.Speak(Text, Voice, startOffset: from);
        await session.Completion;

        var preparing = Assert.Single(log.Events.Where(e =>
            e.Kind == SessionEventKind.StateChanged && e.State == SpeechState.Preparing));

        Assert.Equal(Text, preparing.Text);
        Assert.Equal(from, preparing.SourceOffset);
    }

    [Fact]
    public async Task Seeking_to_zero_is_an_ordinary_read()
    {
        const string Text = "One two three.";
        var (a, _, logA) = Build();
        var (b, _, logB) = Build();
        using var _a = a;
        using var _b = b;

        a.Speak(Text, Voice);
        await a.Completion;
        b.Speak(Text, Voice, startOffset: 0);
        await b.Completion;

        static IEnumerable<(int, int)> Spans(IReadOnlyList<SessionEvent> events) => events
            .Where(e => e.Kind == SessionEventKind.WordBoundary)
            .Select(e => (e.SourceOffset!.Value, e.SourceLength!.Value));

        Assert.Equal(Spans(logA.Events), Spans(logB.Events));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    public void An_offset_outside_the_text_is_rejected(int offset)
    {
        var (session, _, _) = Build();
        using var _s = session;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Speak("Hello world.", Voice, offset));   // 12 characters
    }

    // ------------------------------------------------------- snapping a click

    [Fact]
    public void A_click_inside_a_word_snaps_to_its_first_character()
    {
        const string Text = "Weighs 40 kilograms today.";
        int k = Text.IndexOf("kilograms", StringComparison.Ordinal);

        Assert.Equal(k, BoundaryPlanner.SnapToWordStart(Text, k + 4));
        Assert.Equal(k, BoundaryPlanner.SnapToWordStart(Text, k));
    }

    [Fact]
    public void Snapping_uses_the_highlighters_own_idea_of_a_word()
    {
        // Hyphens and apostrophes are word characters to the boundary planner, so
        // "well-known" is drawn as ONE highlight. A click target that disagreed
        // would start speech at a place the user could not have clicked.
        const string Text = "A well-known problem, isn't it?";
        int w = Text.IndexOf("well-known", StringComparison.Ordinal);
        int i = Text.IndexOf("isn't", StringComparison.Ordinal);

        Assert.Equal(w, BoundaryPlanner.SnapToWordStart(Text, w + 7));   // inside "known"
        Assert.Equal(i, BoundaryPlanner.SnapToWordStart(Text, i + 4));   // after the apostrophe
    }

    [Fact]
    public void A_click_on_a_separator_is_left_where_it_is()
    {
        // Already between words; the renderer starts at the next one. Snapping
        // backwards here would jump to the START of the previous word, which is
        // audibly a word the user did not click.
        const string Text = "Alpha beta gamma.";
        int space = Text.IndexOf(' ');

        Assert.Equal(space, BoundaryPlanner.SnapToWordStart(Text, space));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void Snapping_clamps_rather_than_throwing(int offset, int expected) =>
        Assert.Equal(expected, BoundaryPlanner.SnapToWordStart("Alpha beta.", offset));

    [Fact]
    public void Snapping_past_the_end_clamps_to_the_length() =>
        Assert.Equal(11, BoundaryPlanner.SnapToWordStart("Alpha beta.", 99));
}

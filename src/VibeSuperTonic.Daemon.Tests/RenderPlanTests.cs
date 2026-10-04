using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Daemon;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// What an SSML document turns into on the render path: the steps the loop
/// executes, with the language and pace each text fragment is spoken at.
///
/// <para>This is the Linux half of what <c>SapiEngine.BuildSpeakPlan</c> does with
/// SAPI's fragment list. The loop itself is reachable only through a socket and
/// a model, so the planning is separated out and asserted here — the part that
/// decides <em>what is said, how, and in what order</em> is exactly the part that
/// can be wrong without any error anywhere.</para>
/// </summary>
public class RenderPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vst-renderplan-{Guid.NewGuid():N}");

    private string Data => Path.Combine(_root, "data");

    public RenderPlanTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllText(Path.Combine(Data, "settings.json"),
            """{ "EngineSpeed": 1.0, "DspRate": 1.0, "RateClampCeiling": 1.3, "Language": "en" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private HostConfig Loaded()
    {
        var config = new HostConfig(Data, Path.Combine(_root, "models"));
        config.Reload(force: true);
        return config;
    }

    private static List<DaemonServer.RenderStep> Plan(HostConfig config, string ssml, Request? request = null)
    {
        Assert.True(SsmlDocument.TryParse(ssml, out var fragments), "fixture must be a document");
        return DaemonServer.PlanRender(
            config, fragments, config.SessionOptionsFor(null),
            request ?? new Request { Verb = RequestVerb.Render });
    }

    private static string LanguageOf(DaemonServer.RenderStep step) =>
        Assert.IsType<SupertonicOptions>(step.Plan.Synthesis).Language;

    // ------------------------------------------------------------- language

    [Fact]
    public void A_fragments_language_beats_the_requests_which_beats_the_configured_one()
    {
        var config = Loaded();
        string ssml = "<speak>Hello. <voice xml:lang=\"de-DE\">Hallo.</voice> <voice xml:lang=\"th-TH\">Sawasdee.</voice></speak>";

        // No request language: configured (en), German for the tagged span, and
        // the unsupported Thai tag falls back to configured — not to German.
        var plain = Plan(config, ssml);
        Assert.Equal(["en", "de", "en"], plain.Select(LanguageOf).ToArray());

        // A request language (the speechd SET language) replaces "configured" as
        // the fallback, and still loses to xml:lang.
        var withRequest = Plan(config, ssml, new Request { Verb = RequestVerb.Render, Language = "fr" });
        Assert.Equal(["fr", "de", "fr"], withRequest.Select(LanguageOf).ToArray());
    }

    // ----------------------------------------------------------------- rate

    [Fact]
    public void A_fragments_rate_is_the_engines_factor_on_its_own_text_only()
    {
        var config = Loaded();
        var steps = Plan(config,
            "<speak>normal <prosody rate=\"x-fast\">quick</prosody> <prosody rate=\"x-slow\">slow</prosody></speak>");

        Assert.Equal(["normal", "quick", "slow"], steps.Select(s => s.Text!).ToArray());

        // x-fast is +6 and x-slow -6 on SAPI's scale; the engine applies 1.5^(n/10).
        Assert.Equal(1.0, steps[0].Plan.StretchFactor, 3);
        Assert.Equal(Math.Pow(1.5, 0.6), steps[1].Plan.StretchFactor, 3);
        Assert.Equal(Math.Pow(1.5, -0.6), steps[2].Plan.StretchFactor, 3);
    }

    [Fact]
    public void A_fragments_rate_composes_with_the_screen_readers()
    {
        var config = Loaded();
        var steps = Plan(config,
            "<speak><prosody rate=\"fast\">quick</prosody></speak>",
            new Request { Verb = RequestVerb.Render, Rate = 20 });

        // speechd +20 is 230/175 wpm; the fragment's +3 multiplies it. (A bigger
        // speechd rate would reach the DSP's 2.0 ceiling and prove nothing.)
        double expected = 230.0 / 175.0 * Math.Pow(1.5, 0.3);
        Assert.Equal(expected, steps.Single().Plan.StretchFactor, 3);
    }

    [Fact]
    public void An_extreme_rate_is_clamped_by_the_plans_own_guard_not_passed_through()
    {
        var config = Loaded();
        var steps = Plan(config, "<speak><prosody rate=\"100000%\">fast</prosody></speak>");

        // +10, the end of the scale: 1.5x, however absurd the request was.
        Assert.Equal(1.5, steps.Single().Plan.StretchFactor, 3);
    }

    // ------------------------------------------------- silence and bookmarks

    [Fact]
    public void Silence_and_marks_keep_their_place_between_the_speech()
    {
        var steps = Plan(Loaded(),
            "<speak><mark name=\"a\"/>one<break time=\"750ms\"/><mark name=\"b\"/>two<mark name=\"c\"/></speak>");

        Assert.Equal(
            ["mark:a", "text:one", "silence:750", "mark:b", "text:two", "mark:c"],
            steps.Select(s =>
                s.Mark is { } m ? "mark:" + m
                : s.SilenceMs > 0 ? "silence:" + s.SilenceMs
                : "text:" + s.Text).ToArray());
    }

    [Fact]
    public void A_document_with_nothing_to_say_plans_no_speech()
    {
        var steps = Plan(Loaded(), "<speak><mark name=\"only\"/><break time=\"1s\"/></speak>");

        Assert.DoesNotContain(steps, s => s.Text is not null);
    }

    // ----------------------------------------------------------- the pipeline

    /// <summary>
    /// DON'T FORK THE PIPELINE. A fragment goes through the same
    /// SynthTextPipeline the hotkey and the Windows engine run, so a
    /// pronunciation rule fires on a screen reader's text as it does everywhere
    /// else — and the invisible characters that wedged the synth are stripped.
    /// </summary>
    [Fact]
    public void Each_text_fragment_runs_the_shared_pipeline()
    {
        File.WriteAllText(
            LinuxDataPaths.PronunciationsFile(Data),
            """{ "Enabled": true, "Rules": [ { "Match": "kubectl", "Replace": "cube control", "WholeWord": true } ] }""");

        var steps = Plan(Loaded(), "<speak>run kubectl now<break time=\"1s\"/>he​llo</speak>");

        Assert.Equal(["run cube control now", "hello"], steps.Where(s => s.Text is not null).Select(s => s.Text!).ToArray());
    }

    [Fact]
    public void A_long_fragment_is_chunked_and_every_chunk_keeps_the_fragments_plan()
    {
        var steps = Plan(Loaded(),
            "<speak><prosody rate=\"fast\">" + string.Join(" ", Enumerable.Repeat("This is a fairly ordinary sentence.", 40)) + "</prosody></speak>");

        Assert.True(steps.Count > 1, "forty sentences should not be one chunk");
        Assert.All(steps, s => Assert.Equal(steps[0].Plan.StretchFactor, s.Plan.StretchFactor));
        Assert.DoesNotContain(steps, s => string.IsNullOrWhiteSpace(s.Text));
    }
}

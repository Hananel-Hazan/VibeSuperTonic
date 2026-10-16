using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Trap 12 of docs/SPEECHD-PLAN.md, which asked for a decided policy rather than
/// an emergent one. This is the decision, and these are the sentences a Speech
/// Dispatcher module will actually receive.
///
/// <para>Nothing here needs an engine, because the point of extracting the
/// decision was that with the policy inside <c>RenderAsync</c> it could only be
/// exercised by building a daemon with a model, a session and an audio sink —
/// which is why the S0 prototype shipped with no test of it at all.</para>
/// </summary>
public class RenderAdmissionTests
{
    [Fact]
    public void An_idle_daemon_renders()
    {
        Assert.Null(RenderAdmission.Refuse(SpeechState.Idle, engineNeedsIdle: false, routingError: null));
    }

    /// <summary>
    /// THE PRESS WINS. Speaking is the obvious case; Preparing is the one worth
    /// writing down, because a cold model load is 2–5 seconds during which the
    /// user has heard nothing yet and the session looks idle to anyone reading
    /// for "is audio coming out". SpeechState counts it as active for exactly
    /// this reason and so does this.
    /// </summary>
    [Theory]
    [InlineData(SpeechState.Preparing)]
    [InlineData(SpeechState.Speaking)]
    [InlineData(SpeechState.Stopping)]
    public void A_busy_daemon_refuses_rather_than_queueing_behind_the_hotkey(SpeechState state)
    {
        string? refusal = RenderAdmission.Refuse(state, engineNeedsIdle: false, routingError: null);

        Assert.NotNull(refusal);
        Assert.Contains("busy", refusal);

        // The state is named. speechd logs a module's stderr and this is the one
        // refusal a user will meet in normal use, so "busy" alone would send
        // whoever reads that log looking for a fault that is not there.
        Assert.Contains(state.ToString(), refusal);
    }

    [Fact]
    public void An_engine_that_would_have_to_swap_is_refused_even_when_idle()
    {
        string? refusal = RenderAdmission.Refuse(SpeechState.Idle, engineNeedsIdle: true, routingError: null);

        Assert.NotNull(refusal);
        Assert.Contains("engine cannot change", refusal);
    }

    /// <summary>
    /// TRAP 14, AND THE ORDER IS THE WHOLE POINT. "No voice is installed" is the
    /// state a first-time installer is actually in, and answering it with "the
    /// daemon is busy" sends them to look at speech-dispatcher, at their audio
    /// device, at anything except the first-run screen they have not accepted
    /// yet. The truer answer wins even when both are true.
    /// </summary>
    [Fact]
    public void No_voice_installed_is_reported_ahead_of_being_busy()
    {
        const string why = "no voices are installed — open the app and download one";

        Assert.Equal(why, RenderAdmission.Refuse(SpeechState.Speaking, engineNeedsIdle: true, routingError: why));
        Assert.Equal(why, RenderAdmission.Refuse(SpeechState.Idle, engineNeedsIdle: false, routingError: why));
    }

    /// <summary>
    /// A routing error that is present but blank is not an explanation, and
    /// passing it through would produce an empty line on speechd's stderr — the
    /// silent-failure shape this whole feature is built to avoid.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_routing_error_is_not_treated_as_a_reason(string blank)
    {
        Assert.Null(RenderAdmission.Refuse(SpeechState.Idle, engineNeedsIdle: false, routingError: blank));

        string? busy = RenderAdmission.Refuse(SpeechState.Speaking, engineNeedsIdle: false, routingError: blank);
        Assert.NotNull(busy);
        Assert.Contains("busy", busy);
    }

    /// <summary>
    /// Every refusal reaches a module that must decide whether to fall back to
    /// espeak, and a module cannot act on an empty string. There is no path
    /// through this function that refuses without saying why.
    /// </summary>
    [Theory]
    [InlineData(SpeechState.Idle)]
    [InlineData(SpeechState.Preparing)]
    [InlineData(SpeechState.Speaking)]
    [InlineData(SpeechState.Stopping)]
    public void No_refusal_is_ever_empty(SpeechState state)
    {
        foreach (bool needsIdle in new[] { true, false })
        foreach (string? error in new[] { null, "no engine" })
        {
            string? refusal = RenderAdmission.Refuse(state, needsIdle, error);
            if (refusal is not null) Assert.NotEqual("", refusal.Trim());
        }
    }
}

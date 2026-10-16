using System.Globalization;

namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// The switches a process sets on itself when it is measuring the engine rather
/// than speaking to somebody.
///
/// <para>There are two, and they are deliberately independent — see
/// <see cref="NoProfileVariable"/> for why folding them into one would be a trap
/// set for the preset Benchmark tab.</para>
///
/// <para><b>Why an environment variable keyed to a process id.</b> The engine is
/// a COM in-process server, so a benchmark helper loads it into itself and the
/// signal only has to cross a function call. What it must never do is cross a
/// process boundary: either of these applied to a real SAPI host would change
/// what that host does, silently, in a way its user never asked for. Requiring
/// the value to be the reading process's own id means the only thing that can
/// switch one on is a process naming itself — so setting it globally enables it
/// nowhere, rather than everywhere.</para>
///
/// <para>The same reasoning is already load-bearing elsewhere in the product:
/// the telemetry file and the session-reset sentinel are both pid-keyed for the
/// same reason, that a process should only be able to signal about itself.</para>
/// </summary>
public static class BenchSwitches
{
    /// <summary>
    /// Render at the speed of the hardware instead of the speed of the audio.
    ///
    /// <para><b>What it turns off, and why it has to be both halves.</b> The
    /// engine paces its writes to real time — it hands SAPI at most ~100 ms of
    /// audio ahead of playback — and then waits at end-of-Speak for the device to
    /// drain what is left. Neither is optional for playback: without the pacing,
    /// SAPI's buffer runs deep and clients that close their output the moment
    /// Speak returns lose the trailing words (R-14); without the drain, the
    /// device's own hardware buffer gets cut. Both are pure waste when the output
    /// is a file, and the engine cannot tell a file render from an audio
    /// device.</para>
    ///
    /// <para>Turning off only the pacing buys nothing: the drain is computed as
    /// "audio duration minus how long we have already been writing", so every
    /// second the pacing stops costing is a second the drain starts costing
    /// instead. A change that gated one and not the other would look like it
    /// worked, run in the same six seconds, and produce exactly the numbers it
    /// was meant to fix.</para>
    /// </summary>
    public const string UnpacedVariable = "VIBESUPERTONIC_UNPACED";

    /// <summary>
    /// Build the session from the settings as written, ignoring any stored
    /// benchmark profile.
    ///
    /// <para><b>The collision this exists to break.</b> <c>OnnxThreads = 0</c>
    /// means "auto", and auto means "apply <c>benchmark.json</c> if one fits" —
    /// correct, deliberate, and the only way a measurement ever reaches a SAPI
    /// host. But the thread sweep selects its <c>auto</c> candidate by writing
    /// that same 0, so without this flag the sweep measures the profile the LAST
    /// sweep saved instead of ORT's own pick. Measured 2026-08-20: the
    /// contaminated row read 0.69 cores at RTF 0.25 with "DirectML provider
    /// appended" in the engine log, while the clean row on the same machine
    /// twenty minutes later read 13.2 cores at 0.35.</para>
    ///
    /// <para>That is worse than a wrong row on two counts. It is
    /// <b>self-referential</b> — sweep N's baseline is sweep N-1's output, so the
    /// auto row cannot reproduce across sweeps even on a silent machine. And it
    /// is <b>silent</b>: the thread read-back that catches every other way a
    /// configuration fails to reach ORT exempts <c>auto</c> by design, because
    /// the engine reports the count ORT chose rather than the 0 it was asked
    /// for.</para>
    ///
    /// <para><b>Why this is NOT the same flag as <see cref="UnpacedVariable"/>.</b>
    /// The thread sweep wants both. The preset Benchmark tab wants neither today
    /// and may well want unpaced-without-this tomorrow: that tab answers "what
    /// will my machine actually do for me", and the honest answer to that
    /// question <em>includes</em> the profile the engine will really apply.
    /// Coupling them would silently change what that tab measures the first time
    /// somebody made it faster, which is precisely the class of accident this
    /// whole area keeps producing. One value meaning two things is what caused
    /// the defect above; the fix should not repeat it.</para>
    /// </summary>
    public const string NoProfileVariable = "VIBESUPERTONIC_NO_PROFILE";

    /// <summary>
    /// The value a host must publish to enable one of these for the engine it is
    /// about to load — its own process id. Callers set the variable for the
    /// current process only; see <see cref="IsAuthorised"/> for why the value is
    /// not simply "1".
    /// </summary>
    public static string TokenFor(int processId) =>
        processId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether <paramref name="value"/> authorises the switch it came from in the
    /// process identified by <paramref name="processId"/>.
    ///
    /// <para>Pure and separated from the environment lookup so the guard itself
    /// can be tested. What is worth pinning is not that a matching id passes —
    /// it is that a bare <c>"1"</c>, an empty string and someone else's id all
    /// fail, because those are the values a machine-wide setting would actually
    /// carry, and the cost of getting this wrong is a shipping engine quietly
    /// behaving like a benchmark inside somebody's screen reader.</para>
    /// </summary>
    public static bool IsAuthorised(string? value, int processId)
    {
        // A non-positive id is not a real process. Guarding it keeps the
        // function total rather than letting a caller that passed 0 by accident
        // be authorised by a literal "0" in the environment.
        if (processId <= 0) return false;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // NumberStyles.None on purpose: no sign, no decimal point, no thousands
        // separators, no hex. "1234" is the only shape that means anything here,
        // and a looser parse would let "+1234" or " 1234 " through in one build
        // and not another depending on the current culture.
        return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int claimed)
            && claimed == processId;
    }
}

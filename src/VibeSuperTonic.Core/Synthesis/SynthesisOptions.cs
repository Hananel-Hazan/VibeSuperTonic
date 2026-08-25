namespace VibeSuperTonic.Core.Synthesis;

/// <summary>
/// Per-utterance knobs. Deliberately not the whole settings object: a backend has
/// no business reading user configuration, and passing only what synthesis
/// consumes keeps the settings-storage seam (registry vs XDG) on the host side.
///
/// <para><b>A CLOSED HIERARCHY, decided in P3 on 2026-08-25.</b> This was one
/// record with <c>Language</c>, <c>TotalStep</c> and <c>Speed</c> on it, which is
/// Supertonic's shape and nobody else's — <c>length_scale</c>, <c>noise_scale</c>,
/// <c>noise_w</c> and <c>speaker_id</c> have no Supertonic meaning either. The
/// three shapes considered:</para>
///
/// <list type="bullet">
///   <item>One flat record carrying every field of both engines. Rejected: each
///   backend then has to know which fields are not its own and ignore them, and
///   nothing stops a caller setting <c>TotalStep</c> on a Piper utterance and
///   believing it did something.</item>
///   <item>One record with two nullable payloads. Rejected for a weaker version
///   of the same reason: "both set" and "neither set" are representable, so every
///   backend opens with a null check that can fail at render time.</item>
///   <item><b>This one</b> — a base carrying what is genuinely shared, and one
///   sealed record per engine. An utterance is FOR an engine; that is the fact
///   the type now states. A backend handed the wrong one says so in a sentence
///   instead of rendering something plausible.</item>
/// </list>
///
/// <para><b>What is shared, and why only this.</b> <see cref="VoiceId"/> — every
/// engine has voices, and on Linux it is also what SELECTS the engine, since the
/// daemon routes to Piper exactly when the id names an installed Piper voice.
/// <see cref="SilenceSeconds"/> — both engines split a chunk internally (the
/// Supertonic SDK by length, Piper by sentence, because espeak-ng returns clauses
/// grouped that way) and both have to put something between the pieces.</para>
///
/// <para>Not shared: language. Supertonic takes a language code per utterance;
/// a Piper voice IS a language — its espeak voice is baked into the voice's own
/// config file and asking for a different one is not a thing that can be
/// honoured.</para>
///
/// <para>The hierarchy is closed by the <c>private protected</c> constructor:
/// only records in this assembly can derive, so a backend's type test is
/// exhaustive rather than hopeful.</para>
/// </summary>
public abstract record SynthesisOptions
{
    private protected SynthesisOptions(string voiceId, float silenceSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
        VoiceId = voiceId;
        SilenceSeconds = silenceSeconds;
    }

    /// <summary>
    /// Which voice to render with — a Supertonic style ("M1") or a Piper voice
    /// key ("en_US-lessac-medium"). On Linux this is also what decides which
    /// engine speaks; see <c>PiperVoiceStore</c>.
    /// </summary>
    public string VoiceId { get; init; }

    /// <summary>Silence appended between internally-split segments.</summary>
    public float SilenceSeconds { get; init; }

    /// <summary>
    /// This options object as <typeparamref name="T"/>, or a sentence naming
    /// both types. Every backend's first line, and it exists so that all of them
    /// fail the same way: a mis-routed utterance is a wiring bug in the host, and
    /// the message has to say which engine was asked and which was handed the
    /// work.
    /// </summary>
    public T Require<T>(string engine) where T : SynthesisOptions =>
        this as T ?? throw new ArgumentException(
            $"the {engine} backend was handed {GetType().Name} for voice '{VoiceId}'; " +
            $"it renders {typeof(T).Name} only", nameof(SynthesisOptions));
}

/// <param name="VoiceId">Voice style to load, e.g. "M1".</param>
/// <param name="Language">Supertonic language code.</param>
/// <param name="TotalStep">Diffusion steps. 8 is the default on both platforms; higher is slower and cleaner.</param>
/// <param name="Speed">
/// Model-side speed factor, applied before any DSP time-stretch. The CLAMPED
/// half of the requested rate — see <see cref="Audio.SpeechRate.Compute"/>.
/// </param>
/// <param name="SilenceSeconds">Silence appended between internally-split segments.</param>
public sealed record SupertonicOptions(
    string VoiceId,
    string Language,
    int TotalStep = 8,
    float Speed = 1.05f,
    float SilenceSeconds = 0.3f)
    : SynthesisOptions(VoiceId, SilenceSeconds);

/// <summary>
/// Piper's three inference scales, plus the speaker on a multi-speaker voice.
///
/// <para>The defaults here are neutral placeholders and are <b>not</b> what
/// should reach a render: every voice ships its own <c>inference</c> block in its
/// <c>.onnx.json</c>, and those are the values upstream uses. The host builds
/// this from the voice's config and then overrides <see cref="LengthScale"/> for
/// the requested rate. See <c>PiperRate</c> for why that override is not the
/// reciprocal of the rate.</para>
/// </summary>
/// <param name="VoiceId">Piper voice key, e.g. "en_US-lessac-medium".</param>
/// <param name="LengthScale">
/// Duration multiplier the model applies to every predicted phoneme length.
/// SMALLER IS FASTER, and the relationship is not linear — 0.625 (the reciprocal
/// of 1.6) delivers about 1.39x, and it saturates just under 2x.
/// </param>
/// <param name="NoiseScale">Sampling noise on the latent. Upstream default 0.667.</param>
/// <param name="NoiseW">Sampling noise on the duration predictor. Upstream default 0.8.</param>
/// <param name="SpeakerId">
/// Ignored on a single-speaker voice, where the graph has no <c>sid</c> input at
/// all — sending one is an error rather than a no-op.
/// </param>
/// <param name="SilenceSeconds">Silence between the sentences espeak-ng returns.</param>
public sealed record PiperOptions(
    string VoiceId,
    float LengthScale = 1.0f,
    float NoiseScale = 0.667f,
    float NoiseW = 0.8f,
    int SpeakerId = 0,
    float SilenceSeconds = 0.3f)
    : SynthesisOptions(VoiceId, SilenceSeconds);

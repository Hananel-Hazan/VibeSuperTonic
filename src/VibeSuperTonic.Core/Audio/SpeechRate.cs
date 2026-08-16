namespace VibeSuperTonic.Core.Audio;

/// <summary>
/// How a requested speaking rate is split between the model and the DSP stage,
/// and how a volume trim in dB becomes a linear scale.
///
/// <para><b>This is a port, not an improvement.</b> It is a transcription of
/// <c>SapiEngine.ComputeSpeed</c> and <c>SapiEngine.ApplyVolume</c>, which are
/// what five shipped releases do. A better rule on Linux would make the two
/// platforms speak the same <c>settings.json</c> at different speeds with no way
/// to say which was wrong, and every field report would have to establish which
/// product it came from before it could be read — the identical argument Phase 2
/// settled for boundary placement. <c>SpeechRateTests</c> holds this against a
/// transcription of the engine's version.</para>
///
/// <para>Windows does not call this yet; it still has its own copy. Pointing
/// <c>SapiEngine</c> at it is a behaviour-affecting change to the shipping
/// platform and belongs to the Windows convergence, on its own branch with a
/// TestHarness run either side — not to a Linux phase.</para>
///
/// <para><b>Why the split exists at all.</b> The model is only well-behaved in
/// roughly [0.9, 1.3]; driving it faster degrades quality rather than speeding
/// speech up cleanly. So the model takes what it can and a pitch-preserving
/// time-stretch absorbs whatever the user asked for beyond that, which is how
/// the DSP knob reaches past the model's ceiling at all.</para>
/// </summary>
public static class SpeechRate
{
    /// <summary>Model default when <c>EngineSpeed</c> is unset or nonsensical.</summary>
    public const float DefaultEngineSpeed = 1.05f;

    /// <summary>The model's safe floor, and the default ceiling if none is configured.</summary>
    public const float MinSynthSpeed = 0.9f;
    public const float DefaultCeiling = 1.3f;

    /// <summary>
    /// Extreme stretch sounds bad regardless of how it was asked for, so the DSP
    /// half is bounded independently of the model's clamp.
    /// </summary>
    public const double MinStretch = 0.5;
    public const double MaxStretch = 2.0;

    /// <summary>
    /// Split a requested rate into what the model should render at and what the
    /// time-stretch should then do to it.
    /// </summary>
    /// <param name="engineSpeed">The <c>EngineSpeed</c> setting.</param>
    /// <param name="dspRate">The <c>DspRate</c> setting.</param>
    /// <param name="rateClampCeiling">The <c>RateClampCeiling</c> setting.</param>
    /// <param name="rateAdjust">
    /// SAPI's −10…+10 rate, which has no Linux equivalent and is 0 there. Kept
    /// in the signature so this stays a faithful port that the Windows engine
    /// could adopt unchanged.
    /// </param>
    /// <returns>
    /// <c>SynthSpeed</c> for <c>SynthesisOptions.Speed</c>, and
    /// <c>StretchFactor</c> for <see cref="TimeStretch.Stretch"/> — greater than
    /// 1 plays faster.
    /// </returns>
    public static (float SynthSpeed, double StretchFactor) Compute(
        float engineSpeed, float dspRate, float rateClampCeiling, int rateAdjust = 0)
    {
        int clampedAdjust = Math.Clamp(rateAdjust, -10, 10);
        float baseEngine = engineSpeed > 0 ? engineSpeed : DefaultEngineSpeed;
        float ratePower = (float)Math.Pow(1.5, clampedAdjust / 10.0);
        float ceiling = rateClampCeiling > 1.0f ? rateClampCeiling : DefaultCeiling;

        float dsp = dspRate > 0 ? dspRate : 1.0f;
        float requestedTotal = baseEngine * dsp * ratePower;

        float synthSpeed = Math.Clamp(baseEngine * ratePower, MinSynthSpeed, ceiling);
        double stretchFactor = synthSpeed > 0 ? requestedTotal / synthSpeed : 1.0;
        stretchFactor = Math.Clamp(stretchFactor, MinStretch, MaxStretch);

        return (synthSpeed, stretchFactor);
    }

    /// <summary>
    /// <c>VolumeTrimDb</c> as a linear scale, clamped to the same −12…+6 dB the
    /// engine allows. Positive values can clip, which is why the ceiling is well
    /// under what dB arithmetic would otherwise permit.
    /// </summary>
    public static float VolumeScale(float volumeTrimDb) =>
        (float)Math.Pow(10.0, Math.Clamp(volumeTrimDb, -12f, 6f) / 20.0);

    /// <summary>
    /// Scale PCM in place, saturating rather than wrapping. A wrap turns a loud
    /// passage into noise, which is far worse than the clipping it replaces.
    /// </summary>
    public static void ApplyGain(short[] pcm, float scale)
    {
        if (Math.Abs(scale - 1f) < 0.001f) return;
        for (int i = 0; i < pcm.Length; i++)
        {
            int v = (int)(pcm[i] * scale);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            pcm[i] = (short)v;
        }
    }
}

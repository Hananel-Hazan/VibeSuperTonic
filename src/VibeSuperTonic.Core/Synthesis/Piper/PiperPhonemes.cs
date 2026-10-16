namespace VibeSuperTonic.Core.Synthesis.Piper;

/// <summary>
/// Phonemes to model ids, exactly as upstream assembles them.
///
/// <para>Promoted from
/// <see href="../../../../spike/piper-phonemes">the P1 spike</see>, unchanged.
/// P1 diffed it against <c>python -m piper</c> over 327 sentences in 8 languages
/// and found zero divergences, so this is not a place to improve anything: a
/// phoneme the map does not know is SKIPPED, not substituted and not an error,
/// because that is what upstream does with it.</para>
/// </summary>
public static class PiperPhonemes
{
    /// <summary>Beginning of sentence.</summary>
    public const string Bos = "^";

    /// <summary>End of sentence.</summary>
    public const string Eos = "$";

    /// <summary>The separator interleaved between every phoneme.</summary>
    public const string Pad = "_";

    /// <summary>
    /// What every voice's id map must contain for <see cref="ToIds"/> to work at
    /// all. Checked when a voice config loads.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredSymbols = [Bos, Eos, Pad];

    /// <summary>
    /// BOS, PAD, then every phoneme followed by PAD, then EOS.
    ///
    /// <para>The interleave is not optional. A model fed a bare id sequence still
    /// renders audio, just wrong audio — which is the kind of failure that reads
    /// as "the route does not work".</para>
    /// </summary>
    public static long[] ToIds(IEnumerable<string> phonemes, IReadOnlyDictionary<string, int[]> idMap)
    {
        ArgumentNullException.ThrowIfNull(phonemes);
        ArgumentNullException.ThrowIfNull(idMap);

        var pad = idMap[Pad];
        var ids = new List<long>();
        foreach (var id in idMap[Bos]) ids.Add(id);
        foreach (var id in pad) ids.Add(id);

        foreach (var phoneme in phonemes)
        {
            if (!idMap.TryGetValue(phoneme, out var mapped)) continue;
            foreach (var id in mapped) ids.Add(id);
            foreach (var id in pad) ids.Add(id);
        }

        foreach (var id in idMap[Eos]) ids.Add(id);
        return ids.ToArray();
    }
}

/// <summary>
/// Text to phonemes, grouped by sentence. The one thing on the Piper path that
/// needs a native library, held behind an interface for the same reason
/// <see cref="ISynthesizer"/> exists: Core cannot link espeak-ng, and a fake
/// makes everything above this testable without one.
///
/// <para><b>espeak-ng is GPL-3.0</b>, which is why the whole product is —
/// see the plan. This interface is not an arm's-length seam and was never meant
/// to be one; it is a testing seam.</para>
/// </summary>
public interface IPhonemizer
{
    /// <summary>
    /// Phonemise <paramref name="text"/> in <paramref name="voice"/> (an espeak
    /// voice name, e.g. "en-us"), one list per SENTENCE.
    ///
    /// <para>Sentence grouping is upstream's, not ours: espeak-ng reports a
    /// clause terminator per clause and only some terminators end a sentence.
    /// Each sentence becomes one model render.</para>
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> Phonemize(string voice, string text);
}

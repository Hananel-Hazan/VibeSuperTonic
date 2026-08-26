using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Daemon;

[JsonSourceGenerationOptions(
    // Same bargain as benchmark.json: a file a person is meant to open and argue
    // with, because the point of keeping the samples is that the winning number
    // can be checked rather than trusted.
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UsageStats))]
internal partial class UsageStatsJsonContext : JsonSerializerContext { }

/// <summary>
/// What each execution provider actually costs on THIS machine, measured from
/// the user's own utterances rather than from a sample chosen by the benchmark.
///
/// <para><b>Why this exists beside <c>benchmark.json</c>.</b> The sweep is a
/// controlled measurement of one fixed passage, run once, on request. It answers
/// "which provider is faster on the benchmark text, on an idle machine, right
/// now". That is the right question to ask before there is any other evidence,
/// and it is not the same question as "which provider has been faster for the
/// things this person actually reads" — their text is longer or shorter, their
/// machine is busy with their real work, and the GPU may have degraded or
/// recovered in the weeks since. This file is the second question, and
/// <c>vst-autotune.sh</c> is what acts on it.</para>
///
/// <para><b>Real time per real second of audio.</b> Every sample is one render:
/// wall time divided by the duration of the audio it produced, so a long chunk
/// and a short one are directly comparable and the number means the same thing
/// as the sweep's <c>Rtf</c>. Below 1.0 is faster than real time.</para>
///
/// <para><b>A window, not a total.</b> Only the most recent
/// <see cref="Window"/> samples per provider are kept, so a GPU that has just
/// been fixed — or has just started thermally throttling — is reflected in days
/// rather than being outvoted forever by history. The lifetime count is kept
/// separately, because "12 samples" and "12 samples out of 4000" should not read
/// the same.</para>
/// </summary>
internal sealed class UsageStats
{
    /// <summary>How many recent samples per provider are retained.</summary>
    public const int Window = 64;

    public string UpdatedUtc { get; set; } = "";

    public List<ProviderUsage> Providers { get; set; } = [];

    /// <summary>The record for a provider, created on its first render.</summary>
    public ProviderUsage For(string provider)
    {
        var found = Providers.FirstOrDefault(p =>
            string.Equals(p.Provider, provider, StringComparison.Ordinal));

        if (found is null)
        {
            found = new ProviderUsage { Provider = provider };
            Providers.Add(found);
        }

        return found;
    }
}

/// <summary>One provider's recent behaviour.</summary>
internal sealed class ProviderUsage
{
    public string Provider { get; set; } = "";

    /// <summary>The thread count the most recent sample was taken at.</summary>
    public int Threads { get; set; }

    /// <summary>Renders ever recorded, not just the retained ones.</summary>
    public long Count { get; set; }

    /// <summary>
    /// The median of <see cref="Recent"/>, written out so the file can be read
    /// by anything that does not want to reimplement a median — which is the
    /// whole of <c>vst-autotune.sh</c>.
    /// </summary>
    public double MedianRtf { get; set; }

    /// <summary>
    /// The retained window, oldest first. Kept rather than reduced to the median
    /// because a median alone cannot show that a provider has become erratic,
    /// and because a person checking this file deserves the samples.
    /// </summary>
    public List<double> Recent { get; set; } = [];

    public string LastUtc { get; set; } = "";

    public void Add(double rtf, int threads, string nowUtc)
    {
        Count++;
        Threads = threads;
        LastUtc = nowUtc;

        Recent.Add(rtf);
        if (Recent.Count > UsageStats.Window)
            Recent.RemoveRange(0, Recent.Count - UsageStats.Window);

        var sorted = Recent.Order().ToList();
        MedianRtf = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }
}

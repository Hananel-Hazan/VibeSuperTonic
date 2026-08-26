using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Core.Models;

/// <summary>
/// One downloadable file of a voice. Deliberately the same five fields
/// <see cref="ManifestEntry"/> carries, because it becomes one.
/// </summary>
public sealed class PiperCatalogFile
{
    /// <summary>Filename inside the voice's directory — <c>&lt;id&gt;.onnx</c> or <c>&lt;id&gt;.onnx.json</c>.</summary>
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("url")]     public string Url { get; set; } = "";
    [JsonPropertyName("mirrors")] public List<string> Mirrors { get; set; } = new();
    [JsonPropertyName("sha256")]  public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")]   public long Bytes { get; set; }
}

/// <summary>
/// A voice's licence, as its own <c>MODEL_CARD</c> stated it.
///
/// <para>The second licence axis, and not the engine's (trap 7). This is what the
/// Voices tab shows <b>before</b> any bytes arrive and what the download is gated
/// on — the same principle as the OpenRAIL-M screen, a different licence, and a
/// different one per voice.</para>
/// </summary>
/// <param name="Name">The terms as written — "CC0", "CC BY 4.0", a URL for some.</param>
/// <param name="Class">
/// The normalised family: <c>public</c>, <c>by</c>, <c>apache</c>, <c>by-sa</c>,
/// <c>agpl</c>, <c>nc</c>. What a filter and a warning can be written against;
/// <paramref name="Name"/> is what a person reads.
/// </param>
/// <param name="Url">Where the dataset and its terms live.</param>
public sealed record PiperLicence(
    [property: JsonPropertyName("name")]  string Name,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("url")]   string Url)
{
    /// <summary>
    /// True when the terms restrict the user commercially. The one class that
    /// needs saying out loud at the moment of choosing, because unlike every
    /// other difference here it constrains what the person may do with the audio
    /// rather than what we may do with the model.
    /// </summary>
    [JsonIgnore]
    public bool IsNonCommercial =>
        string.Equals(Class, "nc", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when reuse requires crediting the dataset.</summary>
    [JsonIgnore]
    public bool RequiresAttribution =>
        Class is "by" or "by-sa" or "apache" or "nc";
}

/// <summary>The language a voice speaks. A Piper voice is exactly one.</summary>
public sealed record PiperCatalogLanguage(
    [property: JsonPropertyName("code")]    string Code,
    [property: JsonPropertyName("english")] string English,
    [property: JsonPropertyName("native")]  string Native,
    [property: JsonPropertyName("country")] string Country)
{
    /// <summary>"English (United States)" — what a language filter lists.</summary>
    public string Display() =>
        string.IsNullOrWhiteSpace(Country) ? English : $"{English} ({Country})";
}

/// <summary>One voice at one quality tier — the unit that is installed and removed.</summary>
public sealed class PiperCatalogVoice
{
    [JsonPropertyName("id")]           public string Id { get; set; } = "";

    /// <summary>The voice's own name, shared across its tiers — "lessac", "thorsten".</summary>
    [JsonPropertyName("name")]         public string Name { get; set; } = "";

    [JsonPropertyName("language")]     public PiperCatalogLanguage Language { get; set; } = new("", "", "", "");

    /// <summary><c>high</c>, <c>medium</c>, <c>low</c>, <c>x_low</c>.</summary>
    [JsonPropertyName("quality")]      public string Quality { get; set; } = "";

    /// <summary>
    /// The voice's native rate, 16000 or 22050. A fact worth carrying in the
    /// catalog rather than reading from the config after download, because it is
    /// half of what makes a tier a choice — and because since P3 the sink follows
    /// the voice, so this is the rate the machine will actually play.
    /// </summary>
    [JsonPropertyName("sampleRate")]   public int SampleRate { get; set; }

    [JsonPropertyName("speakers")]     public int Speakers { get; set; } = 1;

    /// <summary>
    /// Speaker names ordered by id, so index N <b>is</b> <c>sid</c> N and a
    /// picker never carries the map around. Empty on a single-speaker voice,
    /// where the graph has no <c>sid</c> input at all.
    /// </summary>
    [JsonPropertyName("speakerNames")] public List<string> SpeakerNames { get; set; } = new();

    [JsonPropertyName("licence")]      public PiperLicence Licence { get; set; } = new("", "", "");

    /// <summary>The <c>.onnx</c> first, then its <c>.onnx.json</c>. The order is load-bearing — see <see cref="Files"/>.</summary>
    [JsonPropertyName("files")]        public List<PiperCatalogFile> Files { get; set; } = new();

    /// <summary>Total download, both files.</summary>
    [JsonIgnore]
    public long Bytes => Files.Sum(f => f.Bytes);

    /// <summary>Tier order for display and for "the highest available".</summary>
    [JsonIgnore]
    public int TierRank => Quality switch
    {
        "high" => 0,
        "medium" => 1,
        "low" => 2,
        "x_low" => 3,
        _ => 4,
    };

    /// <summary>"piper:en_US-ljspeech-high" — the qualified id for settings and the protocol.</summary>
    public VoiceId Qualified() => VoiceId.ForPiper(Id);

    /// <summary>"22 kHz · 114 MB" — the two numbers a tier choice is actually made on.</summary>
    public string SizeSummary() =>
        $"{SampleRate / 1000} kHz · {Bytes / (1024 * 1024)} MB";
}

/// <summary>
/// The curated, hash-pinned catalog of downloadable Piper voices —
/// <c>piper-voices.json</c>, generated by <c>build/gen-piper-catalog.py</c> and
/// shipped in the archive beside <c>models-manifest.json</c>.
///
/// <para><b>Not the live upstream index</b>, decided 2026-08-24. 142 voices is
/// not a dropdown, integrity would depend on an index we do not control, and a
/// curated list is the only version of this that can be reviewed — trap 7
/// requires reading each voice's <c>MODEL_CARD</c>, which is possible for
/// thirty-five and theatre for a thousand. 33 upstream voices state no usable
/// licence and are in no policy.</para>
///
/// <para><b>It ships no bytes and it is not a downloader.</b> Every voice arrives
/// through Core's own <see cref="ModelDownloader"/> — resume-free, mirrored,
/// hash-checked — which gets one caller more rather than a second
/// implementation. This type turns a catalog entry into the
/// <see cref="Manifest"/> that downloader already understands.</para>
/// </summary>
public sealed class PiperCatalog
{
    public const string FileName = "piper-voices.json";

    [JsonPropertyName("version")] public string Version { get; set; } = "1";
    [JsonPropertyName("source")]  public PiperCatalogSource? Source { get; set; }
    [JsonPropertyName("voices")]  public List<PiperCatalogVoice> Voices { get; set; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load the catalog from a directory, or null when it is absent or
    /// unreadable — the same contract <see cref="Manifest.TryLoad"/> has, and for
    /// the same reason: a missing catalog means the Voices tab can list what is
    /// installed and offer nothing new, which is a degraded product rather than a
    /// broken one.
    /// </summary>
    public static PiperCatalog? TryLoad(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        string path = System.IO.Path.Combine(baseDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize<PiperCatalog>(fs, ReadOptions);
            return loaded is null ? null : Sanitised(loaded);
        }
        catch { return null; }
    }

    /// <summary>Parse from a string. Same contract; used by tests and by the daemon's reply path.</summary>
    public static PiperCatalog? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<PiperCatalog>(json, ReadOptions);
            return loaded is null ? null : Sanitised(loaded);
        }
        catch { return null; }
    }

    /// <summary>
    /// Drop entries that cannot be acted on, rather than carrying them to the UI
    /// to fail there. A voice with no id, no files, or a file with no source is
    /// not a thing that can be downloaded, and a hand-edited catalog is the
    /// likely author of all three.
    /// </summary>
    private static PiperCatalog Sanitised(PiperCatalog c)
    {
        c.Voices = c.Voices
            .Where(v => !string.IsNullOrWhiteSpace(v.Id)
                        && v.Files.Count > 0
                        && v.Files.All(f => !string.IsNullOrWhiteSpace(f.Name)
                                            && Uri.TryCreate(f.Url, UriKind.Absolute, out _)))
            .ToList();
        return c;
    }

    /// <summary>The entry for an id, or null. Case-insensitive: ids come from settings files people type into.</summary>
    public PiperCatalogVoice? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Voices.FirstOrDefault(v => string.Equals(v.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every tier of one voice name in one language, best first. What the Voices
    /// tab lists under a single row: the default is the highest tier available,
    /// and the smaller ones stay one click away with their size and rate shown.
    /// </summary>
    public IReadOnlyList<PiperCatalogVoice> Tiers(PiperCatalogVoice voice) =>
        Voices
            .Where(v => v.Name == voice.Name && v.Language.Code == voice.Language.Code)
            .OrderBy(v => v.TierRank)
            .ToArray();

    /// <summary>
    /// The tier a person gets when they do not choose one — the highest
    /// available, per the decision of 2026-08-24. Resolves to <c>medium</c> for
    /// most languages, not because we chose it but because nothing better was
    /// trained.
    /// </summary>
    public PiperCatalogVoice? PreferredTier(string voiceName, string languageCode) =>
        Voices
            .Where(v => v.Name == voiceName && v.Language.Code == languageCode)
            .MinBy(v => v.TierRank);

    /// <summary>Distinct languages, ordered for a filter.</summary>
    public IReadOnlyList<PiperCatalogLanguage> Languages() =>
        Voices
            .GroupBy(v => v.Language.Code)
            .Select(g => g.First().Language)
            .OrderBy(l => l.English, StringComparer.CurrentCulture)
            .ToArray();

    /// <summary>
    /// One voice as a <see cref="Manifest"/> rooted at the models directory —
    /// the whole bridge to <see cref="ModelDownloader"/>, and the reason there is
    /// no second downloader in this codebase.
    ///
    /// <para><b>The <c>.onnx</c> is first and that is not cosmetic.</b> The
    /// store's rule is "both files or neither": a directory holding weights with
    /// no config is a half-finished download and is not a voice, which is what
    /// stops an interrupted fetch from shadowing a Supertonic voice of the same
    /// name. Downloading the weights first means an interruption always leaves
    /// the store in that safe state rather than briefly in the other one.</para>
    /// </summary>
    /// <param name="voice">The entry to install.</param>
    /// <param name="storeFolder">
    /// The models-root-relative folder holding voices — <c>piper</c>. Passed
    /// rather than assumed so Core need not know the store's layout.
    /// </param>
    public static Manifest ManifestFor(PiperCatalogVoice voice, string storeFolder)
    {
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeFolder);

        var files = voice.Files
            .OrderBy(f => f.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(f => new ManifestEntry
            {
                // Forward slashes: Path.Combine on the far side handles both, and
                // a manifest path is a relative locator rather than a native path.
                Path = $"{storeFolder}/{voice.Id}/{f.Name}",
                Url = f.Url,
                Mirrors = new List<string>(f.Mirrors),
                Sha256 = f.Sha256,
                Bytes = f.Bytes,
            })
            .ToList();

        return new Manifest { Version = "1", Files = files };
    }
}

/// <summary>Where the catalog came from, so a stale one is diagnosable.</summary>
public sealed class PiperCatalogSource
{
    [JsonPropertyName("repo")]          public string Repo { get; set; } = "";
    [JsonPropertyName("revision")]      public string Revision { get; set; } = "";
    [JsonPropertyName("generated")]     public string Generated { get; set; } = "";
    [JsonPropertyName("policy")]        public string Policy { get; set; } = "";
    [JsonPropertyName("policyClasses")] public List<string> PolicyClasses { get; set; } = new();
}

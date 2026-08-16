using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Core.Models;

public sealed class ManifestEntry
{
    [JsonPropertyName("path")]    public string Path { get; set; } = "";
    [JsonPropertyName("url")]     public string Url  { get; set; } = "";
    /// <summary>
    /// Fallback download locations, tried in order after <see cref="Url"/>.
    /// Upstream is archived, so the primary host is no longer maintained by
    /// anyone; a mirror is what keeps a fresh install working the day that
    /// repository moves or disappears. Integrity does not depend on trusting
    /// the mirror — every source is checked against the same size and SHA-256.
    /// </summary>
    [JsonPropertyName("mirrors")] public List<string> Mirrors { get; set; } = new();
    [JsonPropertyName("sha256")]  public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")]   public long   Bytes { get; set; }

    /// <summary>Primary first, then mirrors — skipping anything unusable.</summary>
    public IEnumerable<string> AllSources()
    {
        if (IsUsable(Url)) yield return Url;
        foreach (var m in Mirrors)
            if (IsUsable(m)) yield return m;

        static bool IsUsable(string u) =>
            !string.IsNullOrWhiteSpace(u) && Uri.TryCreate(u, UriKind.Absolute, out _);
    }
}

public sealed class Manifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "1";
    [JsonPropertyName("files")]   public List<ManifestEntry> Files { get; set; } = new();

    public static Manifest? TryLoad(string baseDir)
    {
        string path = System.IO.Path.Combine(baseDir, "models-manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<Manifest>(fs, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch { return null; }
    }
}

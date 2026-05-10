using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Launcher.Integrity;

internal sealed class ManifestEntry
{
    [JsonPropertyName("path")]    public string Path { get; set; } = "";
    [JsonPropertyName("url")]     public string Url  { get; set; } = "";
    [JsonPropertyName("sha256")]  public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")]   public long   Bytes { get; set; }
}

internal sealed class Manifest
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

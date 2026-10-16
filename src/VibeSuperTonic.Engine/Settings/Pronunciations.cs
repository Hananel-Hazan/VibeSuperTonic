using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Engine.Settings;

// PronunciationRule and PronunciationsConfig now live in VibeSuperTonic.Core.Text.
// They are pure text logic with no Windows or ONNX dependency, and the launcher
// needs the identical implementation for its Test pane — a rule that behaves one
// way in the preview and another way in the engine is worse than no preview.
// What stays here is the part that genuinely belongs to the host: locating the
// file through DataPaths (registry-backed on Windows) and caching it by mtime.

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PronunciationsConfig))]
internal partial class PronunciationsJsonContext : JsonSerializerContext { }

/// <summary>
/// Mtime-cached reader for <c>pronunciations.json</c>. Mirrors
/// <see cref="EngineSettingsCache"/> — one stat per call, reparse only when the
/// timestamp moves. Compiled regexes are cached alongside the config so each
/// Speak pays the regex setup cost zero times.
/// </summary>
internal static class PronunciationsCache
{
    private static readonly object _gate = new();
    private static long _cachedMtimeTicks = DateTime.MinValue.Ticks;
    private static PronunciationsConfig _cached = new();
    private static Regex?[] _compiled = Array.Empty<Regex?>();
    private static bool _everLoaded;

    public static (PronunciationsConfig config, Regex?[] compiled) Resolve()
    {
        string path = PronunciationsPath;
        long mtimeTicks;
        try { mtimeTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : DateTime.MinValue.Ticks; }
        catch { mtimeTicks = DateTime.MinValue.Ticks; }

        if (Volatile.Read(ref _everLoaded) && Volatile.Read(ref _cachedMtimeTicks) == mtimeTicks)
            return (_cached, _compiled);
        lock (_gate)
        {
            if (_everLoaded && _cachedMtimeTicks == mtimeTicks) return (_cached, _compiled);
            _cached = LoadFromFile(path);
            _compiled = new Regex?[_cached.Rules.Count];
            for (int i = 0; i < _cached.Rules.Count; i++)
                _compiled[i] = PronunciationsConfig.Compile(_cached.Rules[i]);
            _cachedMtimeTicks = mtimeTicks;
            _everLoaded = true;
            return (_cached, _compiled);
        }
    }

    public static string PronunciationsPath =>
        Path.Combine(DataPaths.DataDir, "pronunciations.json");

    private static PronunciationsConfig LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new PronunciationsConfig();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, PronunciationsJsonContext.Default.PronunciationsConfig)
                   ?? new PronunciationsConfig();
        }
        catch { return new PronunciationsConfig(); }
    }
}

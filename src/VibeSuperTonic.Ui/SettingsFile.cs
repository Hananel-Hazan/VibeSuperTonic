using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeSuperTonic.Ui;

/// <summary>
/// The one thing in this window that is not a verb: configuration is written as
/// a file and the daemon re-reads it when the mtime moves. Phase 4b decided
/// there is no <c>config set</c> precisely so that there is exactly one writer,
/// which is the shape the Windows Control Panel and engine already have.
///
/// <para><b>Edited as a JSON tree, not as a typed object.</b> The file crosses
/// platforms — a portable folder on a stick, a dual-boot mount, a synced
/// directory — so a Linux save must not erase <c>UseDirectML</c>,
/// <c>DirectMLDeviceId</c>, <c>OnnxThreads</c>, <c>PerVoice</c>, or anything a
/// later Windows release adds. The daemon's own type carries
/// <c>[JsonExtensionData]</c> for the same reason; a tree goes further, because
/// it also preserves keys that type does not declare and cannot lose a value by
/// failing to model it.</para>
///
/// <para><b>Written atomically.</b> A settings file half-replaced when a laptop
/// suspends is a daemon that will not start; write beside it and rename, which
/// on Linux is atomic within a filesystem.</para>
/// </summary>
internal static class SettingsFile
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The file as a tree, or an empty object when it does not exist yet. Throws
    /// on malformed JSON: overwriting a file we could not parse would discard
    /// settings the user is currently looking for.
    /// </summary>
    public static JsonObject Read(string path)
    {
        if (!File.Exists(path)) return new JsonObject();

        string text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        return JsonNode.Parse(text, documentOptions: Lenient) as JsonObject
               ?? throw new InvalidDataException($"{path} is not a JSON object.");
    }

    public static void Write(string path, JsonObject root)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Same directory, so the rename cannot cross a filesystem boundary and
        // degrade into a copy.
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.new");
        File.WriteAllText(temporary, root.ToJsonString(Pretty) + "\n");
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Set a value, removing the key when <paramref name="value"/> is null so an
    /// emptied field falls back to the daemon's default rather than pinning an
    /// empty string.
    /// </summary>
    public static void Set(this JsonObject root, string key, JsonNode? value)
    {
        if (value is null) root.Remove(key);
        else root[key] = value;
    }

    public static string? String(this JsonObject root, string key) =>
        root[key]?.GetValue<string>();

    public static double? Number(this JsonObject root, string key)
    {
        // A value written by hand may be an integer, a decimal or a string; the
        // point of a settings file people edit is that they do edit it.
        var node = root[key];
        if (node is null) return null;

        try { return node.GetValue<double>(); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return double.TryParse(node.ToString(), out double parsed) ? parsed : null;
        }
    }

    public static bool? Bool(this JsonObject root, string key)
    {
        var node = root[key];
        if (node is null) return null;

        try { return node.GetValue<bool>(); }
        catch (InvalidOperationException) { return null; }
    }
}

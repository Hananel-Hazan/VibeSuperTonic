using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeSuperTonic.Core.Settings;

/// <summary>
/// What a scoped settings editor's boxes should say, and whether anything in
/// them is unsaved.
///
/// <para><b>Why this is a class in Core rather than four fields in the tab.</b>
/// Three bugs in three days had the same shape — the display disagreeing with
/// what was saved — and every one of them was a second place that filled the
/// same boxes. A speaker that snapped back to 0, a rate that reverted on save,
/// and a volume trim that reverted to the file's value while the file held the
/// scope's. The remedy is not a fourth guard: it is that <em>nothing outside
/// this class decides where a value comes from</em>. Callers say what happened
/// — the file was re-read, the scope moved, somebody typed — and read
/// <see cref="Values"/> back.</para>
///
/// <para><b>The touched rule.</b> A refill must never silently discard what
/// somebody typed: the read from disk is asynchronous and makes two round trips
/// before it lands, which is long enough to have typed a rate and pressed Save.
/// So once <see cref="Touched"/> is set, nothing refills until a save or a
/// deliberate discard clears it — and the editor is expected to say on screen
/// which of the two is showing.</para>
/// </summary>
public sealed class ScopedFields
{
    private readonly string[] _keys;
    private readonly Dictionary<string, string> _values = [];

    private JsonObject _file = [];
    private SettingsScopeKind _kind = SettingsScopeKind.All;
    private string _engine = "supertonic";
    private string _voiceKey = "";

    /// <param name="keys">Every key the editor shows, scoped or not.</param>
    public ScopedFields(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        foreach (string key in keys) _values[key] = "";
    }

    /// <summary>Whether anything has been typed since the last load or save.</summary>
    public bool Touched { get; private set; }

    /// <summary>The text each box should show. Unset keys are the empty string.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public string this[string key] => _values.TryGetValue(key, out string? value) ? value : "";

    /// <summary>The settings file has been read. Refills unless something is unsaved.</summary>
    public void Load(JsonObject file)
    {
        _file = file ?? [];
        Refill();
    }

    /// <summary>
    /// The scope selector moved, or the voice under it did. Refills unless
    /// something is unsaved — switching scope SHOULD change these boxes, each
    /// scope having its own values, but not at the cost of an edit in progress.
    /// </summary>
    public void Scoped(SettingsScopeKind kind, string engine, string voiceKey)
    {
        _kind = kind;
        _engine = engine;
        _voiceKey = voiceKey;
        Refill();
    }

    /// <summary>Somebody typed. The value is kept, so a repaint cannot lose it.</summary>
    public void Typed(string key, string? text)
    {
        _values[key] = text ?? "";
        Touched = true;
    }

    /// <summary>A save landed: what is on screen is now what is on disk.</summary>
    public void Committed() => Touched = false;

    /// <summary>Revert — throw the edits away and show the file again.</summary>
    public void Discard()
    {
        Touched = false;
        Refill();
    }

    private void Refill()
    {
        if (Touched) return;

        var source = SettingsScope.Resolve(_file, _kind, _engine, _voiceKey);
        foreach (string key in _keys)
            _values[key] = Text(source[key]);
    }

    /// <summary>
    /// A value as a person would type it. A settings file people edit by hand
    /// holds integers, decimals and quoted numbers for the same key, and all
    /// three have to come back as the same box.
    /// </summary>
    private static string Text(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>() ?? "",
        JsonValueKind.Number => node.GetValue<double>().ToString(CultureInfo.InvariantCulture),
        _ => "",
    };
}

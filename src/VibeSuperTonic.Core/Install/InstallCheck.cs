using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSuperTonic.Core.Install;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstallFingerprint))]
public partial class InstallJsonContext : JsonSerializerContext { }

/// <summary>
/// What the startup check concluded — the thing the GUI puts in its banner.
///
/// <para><b>Empty is the normal answer</b> and costs one file read. Everything
/// below describes the handful of starts in an install's life where something
/// actually moved.</para>
/// </summary>
/// <param name="Changes">What is not what it was. Empty when nothing moved.</param>
/// <param name="BenchmarkStale">
/// Why the stored benchmark no longer describes this machine, from the profile's
/// own check. Separate from <paramref name="Changes"/> because it is true for
/// reasons that have nothing to do with moving — a changed model set, a changed
/// <c>TotalStep</c> — and it was already being computed at startup before any of
/// this existed.
/// </param>
/// <param name="BrokenLaunchers">
/// Desktop-entry commands naming a program that is not there. **The one thing
/// here that is measured rather than inferred**: a fingerprint change says the
/// bindings are *probably* stale, and this says a launcher *is* broken, which is
/// the difference between warning a user and telling them.
/// </param>
/// <param name="FirstRun">
/// No fingerprint had ever been recorded. Not a change and must not be reported
/// as one — a fresh install has nothing to compare against, and a banner saying
/// so on first launch would be the product's opening statement.
/// </param>
public sealed record InstallCheck(
    IReadOnlyList<InstallChange> Changes,
    IReadOnlyList<string> BenchmarkStale,
    IReadOnlyList<string> BrokenLaunchers,
    bool FirstRun = false)
{
    public static InstallCheck Unchanged { get; } =
        new(Array.Empty<InstallChange>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Everything that changed, or-ed together.</summary>
    public InstallImpact Impact =>
        Changes.Aggregate(InstallImpact.None, (all, c) => all | c.Impact)
        | (BenchmarkStale.Count > 0 ? InstallImpact.Benchmark : InstallImpact.None)
        | (BrokenLaunchers.Count > 0 ? InstallImpact.Bindings : InstallImpact.None);

    /// <summary>Is there anything to tell the user about?</summary>
    public bool NeedsAttention =>
        Changes.Count > 0 || BenchmarkStale.Count > 0 || BrokenLaunchers.Count > 0;

    /// <summary>
    /// One line for the top of a banner, or null when there is nothing to say.
    ///
    /// <para><b>It names the cause, not the symptom.</b> "This install moved" is
    /// something a user can connect to what they did five minutes ago; "some
    /// settings may be invalid" is not.</para>
    /// </summary>
    public string? Headline()
    {
        if (!NeedsAttention) return null;

        if (Changes.Count > 0) return Changes[0].What;
        if (BrokenLaunchers.Count > 0) return "a shortcut points at a program that is not there";
        return "the recorded measurements no longer describe this machine";
    }

    /// <summary>
    /// What to do about it, in the order the user should do it — and <b>only for
    /// impacts that are actually present</b>.
    ///
    /// <para><b>Rebinding is the user's, and this is the honest reason why.</b>
    /// The hotkeys live in the desktop's own configuration, and on KDE writing
    /// them is a file edit that a running <c>kglobalaccel</c> does not re-read —
    /// while doing it the other way, over D-Bus, has already crashed a session on
    /// this project. So the product says which script to run and does not run
    /// it.</para>
    /// </summary>
    public IReadOnlyList<string> Advice()
    {
        var advice = new List<string>();
        var impact = Impact;

        if (impact.HasFlag(InstallImpact.Bindings))
        {
            advice.Add(
                "Re-bind the hotkeys and the desktop entry: run install.sh in this folder " +
                "(or appimage-bind.sh for an AppImage). They name the old location, so the " +
                "keys will do nothing until they are rewritten.");
        }

        if (impact.HasFlag(InstallImpact.Benchmark))
            advice.Add("Re-measure this machine, so the thread count and provider fit it again.");

        if (impact.HasFlag(InstallImpact.Calibration))
            advice.Add("Re-calibrate the installed voices, so the speed control lands where it says.");

        // Deliberately last and deliberately reassuring: the voice list is read
        // from disk every time it is asked for, so a moved store needs nothing
        // done to it. Saying nothing here would leave a user to assume the worst.
        if (impact.HasFlag(InstallImpact.Voices))
            advice.Add("The voice list follows the models on its own — nothing to do.");

        return advice;
    }
}

/// <summary>
/// <c>install.json</c> — where this install records where it was, so the next
/// start can tell whether it has been moved.
///
/// <para>Beside <c>benchmark.json</c> and written by the same rule: it is the
/// program's own note to itself rather than anything a user maintains, and
/// failing to write it is not a failure — a read-only install simply checks
/// against nothing and reports nothing, which is what it did before this
/// existed.</para>
/// </summary>
public static class InstallStore
{
    public const string FileName = "install.json";

    /// <summary>The recorded fingerprint, or null when absent, unreadable or malformed.</summary>
    public static InstallFingerprint? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A directory is a caller bug, never a missing fingerprint — the same
        // trap BenchmarkStore.Load documents, where both overloads take a string
        // and the wrong one reads back forever as "never recorded".
        if (Directory.Exists(path))
            throw new ArgumentException(
                $"{path} is a directory. Load takes the path of the file — see InstallStore.FileName.",
                nameof(path));

        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, InstallJsonContext.Default.InstallFingerprint);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Record where we are now. False when it could not be written.</summary>
    public static bool Save(string path, InstallFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fingerprint);

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, fingerprint, InstallJsonContext.Default.InstallFingerprint);
            return true;
        }
        catch (Exception) { return false; }
    }
}

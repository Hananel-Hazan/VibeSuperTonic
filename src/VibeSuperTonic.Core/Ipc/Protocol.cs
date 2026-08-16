using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSuperTonic.Core.Session;

namespace VibeSuperTonic.Core.Ipc;

/// <summary>What a client is asking the daemon to do.</summary>
public enum RequestVerb
{
    /// <summary>
    /// Press. The daemon decides whether that means speak or stop — the client
    /// is stateless and does not know. See <see cref="ToggleGate"/>.
    /// </summary>
    Toggle,

    /// <summary>Speak specific text, regardless of what the hotkey would have captured.</summary>
    Speak,

    /// <summary>
    /// Read the current selection, interrupting whatever is playing. Unlike
    /// <see cref="Toggle"/> this never means stop — a press always starts the
    /// selection that is highlighted now.
    ///
    /// <para>This is the primary key's verb. It exists because the toggle
    /// contract's "selecting new text while speaking does not switch to it" is
    /// only defensible when ONE key does everything; with a separate
    /// unconditional stop bound alongside, a press that means "read this"
    /// unambiguously is simply better, and saves a press on the commonest
    /// action there is.</para>
    ///
    /// <para>The selection is captured <b>before</b> anything is stopped, so a
    /// press with nothing selected leaves the current reading alone instead of
    /// silencing it and then failing.</para>
    /// </summary>
    Read,

    /// <summary>
    /// Start again from a given character offset in the text being read — the
    /// click-a-word-to-jump gesture.
    ///
    /// <para><c>Offset</c> is in the coordinates of the text the daemon reports,
    /// which is the text the user selected, so a client seeks with the same
    /// number a <c>WordBoundary</c> gave it. The daemon snaps to the start of the
    /// word containing it, so a click need not be precise.</para>
    ///
    /// <para>Works after a reading has finished as well as during one: the text
    /// is still on screen and clicking a word in it is the obvious thing to do.
    /// With no <c>Text</c> it seeks within the most recent utterance; supplying
    /// <c>Text</c> makes it a <see cref="Speak"/> that starts part way in.</para>
    /// </summary>
    Seek,

    Stop,
    Pause,
    Resume,
    Status,

    /// <summary>Stream events until the client disconnects. The connection stays open.</summary>
    Subscribe,

    /// <summary>
    /// Re-read <c>settings.json</c> and <c>pronunciations.json</c> from the
    /// portable data directory. Takes effect on the next utterance, never the
    /// one in flight.
    ///
    /// <para>The daemon also reloads by itself when either file's mtime moves,
    /// which is what the Windows engine does and is why an edit in the UI
    /// applies on both platforms with nobody sending anything. This verb exists
    /// on top of that for scripts, and because "did it pick up my change"
    /// deserves an answer that is not "speak something and listen".</para>
    /// </summary>
    Reload,

    /// <summary>
    /// Report the effective configuration and where it was read from.
    ///
    /// <para>Chiefly a portability answer: the product runs from a folder that
    /// can be copied between machines, so "which data directory is this
    /// instance actually using, and is it writable" is the first question when
    /// settings appear not to apply. There is deliberately no <c>config set</c>
    /// — the UI writes the file and the daemon reads it, which is the shape the
    /// Windows Control Panel and engine already have and keeps exactly one
    /// writer.</para>
    /// </summary>
    Config,
}

/// <summary>
/// One line in, from client to daemon.
///
/// <para><c>Text</c> is only meaningful for <see cref="RequestVerb.Speak"/>. For
/// <see cref="RequestVerb.Toggle"/> the daemon captures the selection itself —
/// that is Phase 4's job, and keeping it daemon-side is what lets the hotkey,
/// the tray menu and D-Bus behave identically.</para>
/// </summary>
public sealed record Request
{
    public required RequestVerb Verb { get; init; }
    public string? Text { get; init; }

    /// <summary>Voice style, e.g. "M1". Null takes the daemon's configured default.</summary>
    public string? Voice { get; init; }

    /// <summary>Supertonic language code — "en", not "en-US". Null takes the default.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Where to start, for <see cref="RequestVerb.Seek"/>. A character index into
    /// the text being read, in the same coordinates every reported boundary uses.
    /// Ignored by every other verb.
    /// </summary>
    public int? Offset { get; init; }

    /// <summary>
    /// The client's <c>$DISPLAY</c>, forwarded so the daemon can read the X11
    /// PRIMARY selection from the session the press came from.
    ///
    /// <para>Only meaningful for <see cref="RequestVerb.Toggle"/>, and only from
    /// Phase 4 on. It is here rather than left to the daemon's own environment
    /// because the daemon's environment is whatever it happened to be started
    /// with — a bare shell, systemd --user, or <c>vst-ctl</c>'s autostart (R-5)
    /// — and is then fixed for the life of the process. The client always runs
    /// inside the session; the daemon may not. Sending it costs one short
    /// string on a socket that is already 0600 and owned by the same user.</para>
    /// </summary>
    public string? Display { get; init; }
}

/// <summary>
/// One line back. Every request gets exactly one of these, including
/// <see cref="RequestVerb.Subscribe"/> — which then keeps the connection open
/// and follows with a stream of <see cref="SessionEvent"/> lines.
/// </summary>
public sealed record Response
{
    public required bool Ok { get; init; }

    /// <summary>Why not, when <see cref="Ok"/> is false. Null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>Present on <see cref="RequestVerb.Status"/>.</summary>
    public StatusPayload? Status { get; init; }

    /// <summary>
    /// What a <see cref="RequestVerb.Toggle"/> turned out to mean.
    ///
    /// <para>Needed because a debounced press is a <em>success</em> that does
    /// nothing, and without this it is indistinguishable from one that started
    /// speech. That ambiguity is fine for the finger it was designed for and bad
    /// for everything else: a script cannot tell whether to expect audio, and
    /// anyone diagnosing "the key sometimes does nothing" has no way to
    /// establish whether the press even arrived.</para>
    /// </summary>
    public ToggleAction? Action { get; init; }

    /// <summary>
    /// Something the user should know about a request that nonetheless
    /// succeeded. Null when there is nothing to say.
    ///
    /// <para>Distinct from <see cref="Error"/>, and the distinction is the
    /// point: a selection truncated at 100 KB (R-9) did work, and reporting it
    /// as a failure would be a lie, while reporting nothing would leave the user
    /// wondering why the reading stopped early. Phase 6's tray tooltip is the
    /// intended reader.</para>
    /// </summary>
    public string? Notice { get; init; }

    /// <summary>Present on <see cref="RequestVerb.Config"/>.</summary>
    public ConfigPayload? Config { get; init; }

    public static Response Success() => new() { Ok = true };

    public static Response Success(ToggleAction action) => new() { Ok = true, Action = action };
    public static Response Fail(string error) => new() { Ok = false, Error = error };
}

/// <param name="State">Where the state machine is.</param>
/// <param name="Paused">Only meaningful while speaking.</param>
/// <param name="ModelLoaded">
/// False before first use if the daemon loads lazily. Worth exposing because it
/// is the difference between a 150 ms response and a 2–5 s one, and a user
/// wondering why the first press was slow deserves an answer.
/// </param>
/// <param name="Voice">Configured default voice.</param>
/// <param name="Language">Configured default language.</param>
/// <param name="Version">Daemon version, so a stale client is diagnosable.</param>
/// <param name="Text">
/// The utterance being spoken, or null when idle. Present so a subscriber that
/// arrives mid-read knows what the offsets index into.
/// </param>
/// <param name="SourceOffset">Where in <paramref name="Text"/> the reader is, or null.</param>
/// <param name="SourceLength">How many characters that covers, or null.</param>
public sealed record StatusPayload(
    SpeechState State,
    bool Paused,
    bool ModelLoaded,
    string Voice,
    string Language,
    string Version,
    string? Text = null,
    int? SourceOffset = null,
    int? SourceLength = null);

/// <summary>
/// Where this instance reads its configuration, and what it made of it.
/// </summary>
/// <param name="BaseDir">Directory holding the daemon executable.</param>
/// <param name="DataDir"><c>settings.json</c> and <c>pronunciations.json</c> live here.</param>
/// <param name="ModelsRoot">Directory holding <c>onnx/</c> and <c>voice_styles/</c>.</param>
/// <param name="DataDirWritable">
/// False on a read-only portable medium. Settings still load and still apply;
/// only saving is unavailable, which is worth saying out loud before a user
/// concludes their edits are being ignored.
/// </param>
/// <param name="SettingsFound">False when the file is absent and defaults are in use.</param>
/// <param name="PronunciationsFound">False when the file is absent.</param>
/// <param name="RuleCount">Rules loaded, including disabled ones.</param>
/// <param name="RulesEnabled">The config-level on/off switch.</param>
/// <param name="InterChunkSilenceMs">
/// The gap written between chunks. Reported for the same reason the chunk sizes
/// are: it is a setting whose effect is audible and whose value is not, so
/// "is the daemon actually reading my settings.json" needs an answer that is not
/// "speak something and listen" — which for a timing knob is no answer at all.
/// </param>
/// <param name="Notes">Non-fatal problems — an unreadable file, a rule that did not compile.</param>
public sealed record ConfigPayload(
    string BaseDir,
    string DataDir,
    string ModelsRoot,
    bool DataDirWritable,
    bool SettingsFound,
    bool PronunciationsFound,
    int RuleCount,
    bool RulesEnabled,
    string Voice,
    string Language,
    int TotalStep,
    int MaxChunkChars,
    int MinChunkChars,
    int InterChunkSilenceMs,
    IReadOnlyList<string> Notes);

/// <summary>
/// JSON, one object per line, both directions.
///
/// <para>Chosen over anything framed or binary for one reason: it can be driven
/// and read by hand. <c>vst-ctl</c> is the normal client, but a protocol whose
/// traffic is legible in a terminal is one that can be debugged from a field
/// report, scripted against with <c>jq</c>, and extended without a version
/// negotiation. The cost is a few microseconds per message against a pipeline
/// whose unit of work is a sentence of speech.</para>
///
/// <para>Enums go over the wire as names, not numbers, for the same reason:
/// <c>"state":"Speaking"</c> survives someone reordering the enum, and reads
/// correctly in a log.</para>
/// </summary>
public static class Protocol
{
    /// <summary>
    /// Where the socket lives. <c>$XDG_RUNTIME_DIR</c> is the correct home — it
    /// is per-user, mode 0700, on tmpfs, and cleaned up at logout, so a stale
    /// socket cannot outlive the session that made it.
    /// </summary>
    public const string SocketDirName = "vibesupertonic";
    public const string SocketFileName = "ctl.sock";

    /// <summary>
    /// Kept for callers that want the options directly. The serializer itself
    /// goes through <see cref="ProtocolJson"/> — see <see cref="Encode"/>.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Resolve the control socket path, creating nothing.
    /// </summary>
    /// <remarks>
    /// <c>$XDG_RUNTIME_DIR</c> is unset in some <c>su</c>'d and container
    /// sessions. Falling back to a per-uid directory under <c>/tmp</c> keeps the
    /// daemon usable there; it is second choice because <c>/tmp</c> is shared
    /// and survives logout, so the directory has to be mode 0700 and the socket
    /// 0600 explicitly rather than inheriting them.
    /// </remarks>
    public static string SocketPath()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string root = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, SocketDirName)
            : Path.Combine(Path.GetTempPath(), $"{SocketDirName}-{Environment.UserName}");

        return Path.Combine(root, SocketFileName);
    }

    public static string Encode<T>(T message) =>
        JsonSerializer.Serialize(message, typeof(T), ProtocolJson.Default);

    /// <summary>
    /// Parse one line. Returns null on anything unparseable rather than
    /// throwing: the daemon reads from a socket any local process can connect
    /// to, and a malformed line is a client bug or a port scan, neither of which
    /// should end a speech session.
    /// </summary>
    public static T? TryDecode<T>(string line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize(line, typeof(T), ProtocolJson.Default) as T; }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }   // a shape the context cannot map
    }
}

/// <summary>
/// Source-generated serialization for everything that crosses the socket.
///
/// <para>Not an optimisation. <c>vst-ctl</c> is what a hotkey binding runs, so
/// its process start is charged to every press the user makes, and NativeAOT is
/// the only way to get that from ~100 ms down to single digits. AOT disables
/// reflection-based serialization outright: the reflecting client built and then
/// aborted on its first message with "Reflection-based serialization has been
/// disabled for this application". This is the fix, and it has to live beside
/// the types rather than in the client, because the daemon must serialize
/// exactly the same way.</para>
///
/// <para>Costs Core nothing it is not allowed to spend — <c>System.Text.Json</c>
/// is part of the shared framework on net10.0, so this adds no
/// <c>PackageReference</c>, which Core is forbidden to have.</para>
///
/// <para><b>Add a type here when you add one to the protocol.</b> A type that is
/// missing throws <see cref="NotSupportedException"/> at runtime under AOT while
/// continuing to work perfectly in every JIT build and test — so it fails only
/// on the shipped binary.</para>
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(ConfigPayload))]
[JsonSerializable(typeof(SessionEvent))]
public sealed partial class ProtocolJson : JsonSerializerContext
{
}

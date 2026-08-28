using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSuperTonic.Core.Session;
using VibeSuperTonic.Core.Synthesis;

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

    /// <summary>
    /// Sweep this machine's execution profile and record the answer.
    ///
    /// <para><b>A verb before it is a button.</b> It has to work headless, on a
    /// server, over ssh, and before any window exists — and the Tune tab's
    /// control calls exactly this rather than having a private path of its own.
    /// Phase 8 was originally sequenced after the tab that calls it, which is how
    /// a button wired to nothing gets shipped.</para>
    ///
    /// <para>Unlike every other verb this one answers more than once: a row at a
    /// time as the sweep proceeds, then a final reply carrying the whole profile.
    /// It takes tens of seconds, and a client that printed nothing until the end
    /// would be indistinguishable from one that had hung.</para>
    /// </summary>
    Benchmark,

    /// <summary>
    /// What voices exist — installed on this machine, and offered by the
    /// catalog.
    ///
    /// <para><b>A verb before it is a tab</b>, the same rule
    /// <see cref="Benchmark"/> was landed under. The Voices tab is a client and
    /// may not have a private path to the store, and this has to answer over ssh
    /// on a machine with no window, before any of it exists.</para>
    ///
    /// <para>Answers for <em>both</em> engines, because "which voices do I have"
    /// is not a question a user asks per engine — and because the answer is what
    /// makes the routing rule visible: the list is where you can see that a
    /// Piper id and a Supertonic style are the same kind of thing in the same
    /// setting.</para>
    /// </summary>
    Voices,

    /// <summary>
    /// Download and verify one catalog voice into the store.
    ///
    /// <para>Like <see cref="Benchmark"/> and unlike everything else, this
    /// answers more than once: a progress line as the bytes arrive, then a final
    /// reply. A 137 MB voice over a slow link is minutes, and a client that
    /// printed nothing until the end would be indistinguishable from one that
    /// had hung — which is the same argument the sweep made, for the same
    /// reason.</para>
    ///
    /// <para><b>Gated on the voice's own licence.</b> The catalog carries terms
    /// per voice (trap 7) and the daemon refuses without
    /// <see cref="Request.AcceptLicence"/>, so a client cannot download a
    /// NonCommercial voice for someone who was never shown that it was one. The
    /// refusal names the terms, so a script can be written that accepts them
    /// deliberately.</para>
    /// </summary>
    VoiceInstall,

    /// <summary>
    /// Delete an installed Piper voice and its directory, calibration included.
    ///
    /// <para>Refuses to remove the voice currently configured as the default —
    /// removing the voice the next press will ask for leaves the daemon with a
    /// setting it cannot honour, and the fix belongs in front of the user rather
    /// than in the log.</para>
    /// </summary>
    VoiceRemove,

    /// <summary>
    /// Synthesise text and hand the audio back instead of playing it.
    ///
    /// <para><b>The only verb that returns samples</b>, and it exists because
    /// something else needs to own playback. A Speech Dispatcher module is the
    /// first customer: speechd starts an utterance and must be able to stop it
    /// on the next keystroke, which it can only do to a process it owns. If this
    /// daemon played, <c>spd-say -C</c> would kill a pipe that is not the daemon
    /// and the speech would carry on — see docs/SPEECHD-PLAN.md, trap 3.</para>
    ///
    /// <para><b>Multi-reply, like <see cref="Benchmark"/> and
    /// <see cref="VoiceInstall"/>.</b> The first reply carries
    /// <see cref="Response.Audio"/> with the format and no samples, because the
    /// caller needs the rate to write a WAV header before any audio arrives.
    /// Every reply after it carries one chunk, and the last carries none. The
    /// samples are base64 in JSON: 33% over a unix socket, which is not a number
    /// anyone can measure against a neural render, and it keeps the protocol one
    /// thing rather than two.</para>
    ///
    /// <para><b>It does not touch the session.</b> No sink, no playback clock, no
    /// tray state — a render is not speech and must not interrupt any.</para>
    /// </summary>
    Render,

    /// <summary>
    /// Stop the daemon: stop any speech, close the socket, exit 0.
    ///
    /// <para><b>This is not "quit the application".</b> R-5 brings the daemon
    /// straight back on the next hotkey press, so what this actually does is
    /// <em>stop holding ~830 MB until I need you again</em> — and it should be
    /// labelled as
    /// that wherever a person sees it, never as Quit. The product has no "off":
    /// the hotkey works either way, and the only difference is whether the next
    /// press waits for a model load.</para>
    ///
    /// <para><b>Two customers, and neither is an afterthought.</b> Phase 6's tray
    /// menu needs a fourth item, and a menu item with no verb behind it breaks the
    /// parity rule — the window is a client and may not have a private path to the
    /// daemon. Separately, ORT sizes its thread pool when the session is built, so
    /// a profile written by <see cref="Benchmark"/> cannot reach the session that
    /// measured it: <c>benchmark</c> then <c>shutdown</c> is how a measurement
    /// takes effect.</para>
    ///
    /// <para>The reply is sent before the daemon begins tearing down. A verb whose
    /// success is indistinguishable from a dropped connection is one nobody
    /// trusts.</para>
    /// </summary>
    Shutdown,
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
    /// <summary>
    /// The <see cref="ClientKind"/> the daemon recognises. A constant rather than
    /// a literal in each process, because the string is a contract between two
    /// binaries and a typo would present as a tray that never noticed the window.
    /// </summary>
    public const string ClientKindUi = "ui";

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

    /// <summary>
    /// Who is subscribing, and as what — <c>hello</c>, sent on
    /// <see cref="RequestVerb.Subscribe"/> and ignored by every other verb.
    ///
    /// <para>Exists for exactly one customer, and is deliberately no larger than
    /// that customer needs: the tray icon changes appearance while a window is
    /// attached, and <c>_subscribers</c> used to hold nothing but a writer, so
    /// "is a UI attached" was unanswerable and <c>vst-ctl subscribe | jq</c>
    /// looked identical to the Reader.</para>
    ///
    /// <para>Two fields on the subscribe request rather than a verb of its own:
    /// a subscriber already sends exactly one request and then reads forever, so
    /// a separate greeting would either precede the snapshot — adding a round
    /// trip to the one path Phase 3 made gapless on purpose — or follow it, and
    /// arrive after the icon had already decided. <see cref="ClientKindUi"/> is
    /// the only value the daemon acts on; anything else, including nothing at
    /// all, is an anonymous subscriber and stays that way.</para>
    /// </summary>
    public string? ClientKind { get; init; }

    /// <inheritdoc cref="ClientKind"/>
    public int? ClientPid { get; init; }

    /// <summary>
    /// Proceed despite a guard that would otherwise refuse. Only
    /// <see cref="RequestVerb.Benchmark"/> reads it, where the guard is "this
    /// machine is too busy to measure honestly".
    ///
    /// <para>An override rather than a warning because the refusal is usually
    /// right: a sweep run while a build is going measures the build. But the
    /// person who knows the load is a video call they are about to leave should
    /// not have to kill it to get an answer.</para>
    /// </summary>
    public bool? Force { get; init; }

    /// <summary>
    /// The user has been shown this voice's licence and accepted it. Required by
    /// <see cref="RequestVerb.VoiceInstall"/>, ignored by everything else.
    ///
    /// <para>A field rather than an implicit yes because the voices are a second
    /// licence axis (trap 7) and each one differs — 33 upstream voices are absent
    /// from the catalog entirely for being unable to state theirs, and three of
    /// the ones present are NonCommercial. The daemon has no screen, so the only
    /// thing it can enforce is that the client claims to have shown one.</para>
    /// </summary>
    public bool? AcceptLicence { get; init; }
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

    /// <summary>
    /// Present on the final reply to <see cref="RequestVerb.Benchmark"/>, and the
    /// marker that the sweep is over.
    /// </summary>
    public BenchmarkPayload? Benchmark { get; init; }

    /// <summary>
    /// Present on the intermediate replies to <see cref="RequestVerb.Benchmark"/>
    /// — one per row measured.
    ///
    /// <para>This is what makes a multi-reply verb legible without a second
    /// channel: a client reads lines until one arrives with this unset, which is
    /// either the profile or a failure. Older clients are unaffected because no
    /// other verb ever sets it.</para>
    /// </summary>
    public BenchmarkProgress? Progress { get; init; }

    /// <summary>Present on <see cref="RequestVerb.Voices"/>.</summary>
    public VoicesPayload? Voices { get; init; }

    /// <summary>
    /// Present on the intermediate replies to <see cref="RequestVerb.VoiceInstall"/>
    /// — the same "read lines until one arrives without this set" contract
    /// <see cref="Progress"/> established for the sweep, so a client that already
    /// streams one verb streams the other with the same loop.
    /// </summary>
    public VoiceProgress? VoiceProgress { get; init; }

    /// <summary>
    /// Present on every reply to <see cref="RequestVerb.Render"/>. The first has
    /// a format and no <see cref="AudioChunk.Pcm"/>; the rest have samples.
    /// </summary>
    public AudioChunk? Audio { get; init; }

    /// <summary>Present on the final reply to an install or a remove.</summary>
    public VoiceActionPayload? Voice { get; init; }

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
/// <param name="Engine">
/// Which engine would speak the current voice — "supertonic" or "piper" — or
/// null from a daemon that has only one. Reported because <c>Inference</c>
/// describes the SUPERTONIC session's provider and thread count, which is not
/// what renders while a Piper voice is selected; without this field a status
/// line reads as if it were.
/// </param>
/// <param name="Language">Configured default language.</param>
/// <param name="Version">Daemon version, so a stale client is diagnosable.</param>
/// <param name="Text">
/// The utterance being spoken, or null when idle. Present so a subscriber that
/// arrives mid-read knows what the offsets index into.
/// </param>
/// <param name="SourceOffset">Where in <paramref name="Text"/> the reader is, or null.</param>
/// <param name="SourceLength">How many characters that covers, or null.</param>
/// <param name="Inference">
/// What the next utterance will render on, and why — "CUDA, 4 threads (benchmark
/// 2026-08-24)" or "CPU, 2 threads (on battery, benchmark 2026-08-24)".
///
/// <para>Here as well as in <c>config</c> because this is the field that changes
/// while the daemon runs. Phase 8b re-decides at the start of every utterance, so
/// a user who unplugs their laptop and wants to know whether the product noticed
/// should not have to ask a different verb than the one they already use.</para>
/// </param>
public sealed record StatusPayload(
    SpeechState State,
    bool Paused,
    bool ModelLoaded,
    string Voice,
    string Language,
    string Version,
    string? Text = null,
    int? SourceOffset = null,
    int? SourceLength = null,
    string? Tray = null,
    string? Inference = null,
    string? Engine = null);

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
/// <param name="Provider">Execution provider in force — "cpu" until Phase 8b.</param>
/// <param name="IntraOpThreads">
/// Threads the loaded session was built with. 0 is ORT's own pick.
///
/// <para>Reported because it is decided at startup and cannot be moved by
/// <c>reload</c> — ORT sizes its pool when the session is created — so "what is
/// actually in force" and "what the settings file says" are legitimately
/// different questions, and only the daemon can answer the first.</para>
/// </param>
/// <param name="ThreadsReason">
/// Where that number came from, in one phrase: a benchmark and its date, or the
/// percentage fallback and why the stored measurement did not apply. A number in
/// a settings file with no provenance is a number nobody dares change.
/// </param>
/// <param name="Benchmark">The stored profile, or null when this machine has never been swept.</param>
/// <param name="StoreRoot">
/// The directory holding <c>models/</c> and <c>data/</c>, followed by the reason
/// it is that one, in parentheses.
///
/// <para>Equal to <c>BaseDir</c> for every install that is not an AppImage, and
/// reported separately precisely because of the case where it is not: an
/// AppImage runs from a read-only mount at a path that changes every start, so
/// "where is my install" and "where is my 383 MB of models" stop being the same
/// question. A client that needs to write beside the models — the first-run
/// download is the only one — must use this and not <c>BaseDir</c>.</para>
/// </param>
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
    IReadOnlyList<string> Notes,
    string Provider = "cpu",
    int IntraOpThreads = 0,
    string ThreadsReason = "",
    BenchmarkSummary? Benchmark = null,
    string StoreRoot = "",
    VibeSuperTonic.Core.Install.InstallCheck? Install = null);

/// <summary>
/// The stored profile as <c>config</c> reports it — enough to judge it without
/// opening the file, and no more.
/// </summary>
/// <param name="MeasuredUtc">When the sweep ran.</param>
/// <param name="Threads">What it picked.</param>
/// <param name="Provider">Which provider it picked.</param>
/// <param name="Applied">
/// False when a profile exists but something about the machine has changed since.
/// This is the field that turns "my benchmark did nothing" into a question with
/// an answer.
/// </param>
/// <param name="Staleness">Why not, when <paramref name="Applied"/> is false.</param>
public sealed record BenchmarkSummary(
    string MeasuredUtc,
    int Threads,
    string Provider,
    bool Applied,
    IReadOnlyList<string> Staleness);

/// <summary>
/// The result of a sweep, and what became of it.
/// </summary>
/// <param name="Profile">The measurement, including every row.</param>
/// <param name="Saved">False on a read-only install — the answer is still correct, it just could not be kept.</param>
/// <param name="Path">Where it was written, when it was.</param>
/// <param name="AppliesNow">
/// False whenever the running daemon is already using a different number.
///
/// <para>It usually is: ORT builds its thread pool with the session, so a profile
/// measured at 11:00 governs the daemon started at 12:00, not the one that
/// measured it. Saying so is the difference between a verb that appears to do
/// nothing and one that reports honestly.</para>
/// </param>
/// <param name="Notes">The load guard, an unwritable data directory, rows that failed.</param>
public sealed record BenchmarkPayload(
    BenchmarkProfile Profile,
    bool Saved,
    string? Path,
    bool AppliesNow,
    IReadOnlyList<string> Notes);

/// <summary>
/// One voice, as the list reports it. Deliberately one shape for both engines.
///
/// <para><b>Why not two records.</b> The decision that the voice selects the
/// engine only pays off if a client can hold them in one list and set one
/// setting from either. A Supertonic style and a Piper voice differ in what they
/// can offer, not in what they are for, so the differences are nullable fields
/// rather than a second type — and the fields a Supertonic row leaves empty
/// (quality, licence, size) are exactly the ones it genuinely has nothing to
/// say about, because it is one of five styles over a shared 383 MB set rather
/// than a download.</para>
/// </summary>
/// <param name="Id">The engine-qualified id — <c>piper:de_DE-thorsten-high</c>, <c>supertonic:M1</c>.</param>
/// <param name="Engine">"piper" or "supertonic".</param>
/// <param name="Name">Display name: the voice's own name, or the style id.</param>
/// <param name="Installed">Whether the bytes are on this machine.</param>
/// <param name="IsDefault">Whether the next press would use this one.</param>
/// <param name="LanguageCode">"en_US" for Piper; null for Supertonic, which takes a language per utterance.</param>
/// <param name="Language">The language spelled for a person, or a count for Supertonic.</param>
/// <param name="Quality">Piper's tier — <c>high</c>, <c>medium</c>, <c>low</c>, <c>x_low</c>. Null for Supertonic.</param>
/// <param name="SampleRate">
/// The voice's native rate. Since P3 the sink follows it, so this is what the
/// machine will actually play rather than a property of the file.
/// </param>
/// <param name="Bytes">Download size when available, on-disk size when installed.</param>
/// <param name="Licence">The terms, per voice, from the voice's own MODEL_CARD.</param>
/// <param name="LicenceClass">Normalised family, so a client can warn on <c>nc</c> without parsing prose.</param>
/// <param name="LicenceUrl">Where those terms live.</param>
/// <param name="Speakers">
/// How many voices this one file holds. Greater than 1 means the graph takes a
/// <c>sid</c> and the picker has something to pick.
/// </param>
/// <param name="SpeakerNames">Ordered by id, so index N IS <c>sid</c> N.</param>
/// <param name="Calibrated">
/// Whether this voice's rate curve has been measured on this machine. False means
/// a requested rate is served by the reciprocal until the measurement finishes —
/// accurate to within a few per cent rather than 2.8%, and worth saying because
/// it is a difference a user can hear and would otherwise have no name for.
/// </param>
public sealed record VoiceEntry(
    string Id,
    string Engine,
    string Name,
    bool Installed,
    bool IsDefault,
    string? LanguageCode = null,
    string? Language = null,
    string? Quality = null,
    int? SampleRate = null,
    long? Bytes = null,
    string? Licence = null,
    string? LicenceClass = null,
    string? LicenceUrl = null,
    int? Speakers = null,
    IReadOnlyList<string>? SpeakerNames = null,
    bool? Calibrated = null);

/// <summary>
/// The answer to <see cref="RequestVerb.Voices"/>: what is here, and what could be.
/// </summary>
/// <param name="Installed">Every voice that can speak right now, both engines.</param>
/// <param name="Available">Catalog voices that are not installed. Empty when no catalog shipped.</param>
/// <param name="StoreRoot">
/// Where Piper voices live. Reported for the same reason <c>config</c> reports
/// <c>StoreRoot</c>: under an AppImage this is not beside the executable, and
/// "where did my 114 MB go" deserves an answer that is not a guess.
/// </param>
/// <param name="CatalogRevision">The upstream revision the catalog is pinned to, or null when there is none.</param>
/// <param name="Notes">Why the catalog is missing, or anything else non-fatal.</param>
public sealed record VoicesPayload(
    IReadOnlyList<VoiceEntry> Installed,
    IReadOnlyList<VoiceEntry> Available,
    string StoreRoot,
    string? CatalogRevision = null,
    IReadOnlyList<string>? Notes = null);

/// <summary>
/// How far an install has got — one file at a time, since a voice is two.
/// </summary>
/// <param name="VoiceId">The voice being installed.</param>
/// <param name="File">Which of its files is in flight.</param>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="BytesTotal">The size the catalog pinned, known before the first byte arrives.</param>
/// <param name="Message">A line for a log, when there is one instead of a number.</param>
/// <summary>
/// One piece of a <see cref="RequestVerb.Render"/> reply.
///
/// <para><b>Why the rate is on every chunk and not just the first.</b> It costs
/// eight bytes and it makes a chunk self-describing, which matters because the
/// sink follows the voice ([P3](../../../docs/PIPER-PLAN.md#p3)): a render that
/// switched engines mid-stream would otherwise hand the caller samples at a rate
/// it had already committed to a WAV header. The daemon refuses that switch, and
/// a reader that checks this field will see the refusal rather than a chipmunk.
/// </para>
/// </summary>
/// <param name="SampleRate">Hz. 44100 for Supertonic, 16000 or 22050 for Piper.</param>
/// <param name="Channels">Always 1. Present so a WAV header can be written from this record alone.</param>
/// <param name="Pcm">
/// Base64 of little-endian 16-bit mono samples, or null on the opening reply and
/// on the final one. Null is not the same as empty: empty would be a chunk that
/// rendered to silence, which is a thing that can legitimately happen.
/// </param>
/// <param name="Final">True on the last reply, so a reader stops without guessing.</param>
public sealed record AudioChunk(
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("channels")]   int Channels,
    [property: JsonPropertyName("pcm")]        string? Pcm,
    [property: JsonPropertyName("final")]      bool Final);

public sealed record VoiceProgress(
    string VoiceId,
    string? File = null,
    long BytesReceived = 0,
    long BytesTotal = 0,
    string? Message = null);

/// <summary>What an install or a remove did.</summary>
/// <param name="VoiceId">The voice acted on.</param>
/// <param name="Installed">Whether it is in the store now.</param>
/// <param name="Path">Its directory.</param>
/// <param name="Message">One sentence for a user.</param>
/// <param name="Calibrating">
/// True when a rate calibration was started in the background. It runs off the
/// press path and takes about 47 seconds for a <c>high</c> tier, so an install
/// that reported nothing about it would leave the first minute of a new voice
/// unexplained — the rate is served by the reciprocal until it lands.
/// </param>
public sealed record VoiceActionPayload(
    string VoiceId,
    bool Installed,
    string? Path = null,
    string? Message = null,
    bool Calibrating = false);

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
// Phase 8a. Six types for one verb, which is exactly the shape that fails under
// AOT and nowhere else: BenchmarkPayload nests a BenchmarkProfile, which nests a
// BenchmarkMachine and a list of BenchmarkRow. The generator walks that graph, so
// missing one of them is not caught by the outermost type being present.
[JsonSerializable(typeof(BenchmarkPayload))]
[JsonSerializable(typeof(BenchmarkProgress))]
[JsonSerializable(typeof(BenchmarkSummary))]
[JsonSerializable(typeof(BenchmarkProfile))]
[JsonSerializable(typeof(BenchmarkMachine))]
[JsonSerializable(typeof(BenchmarkRow))]
// P4. VoicesPayload nests two lists of VoiceEntry, and VoiceEntry nests a list
// of string — the generator walks that graph, so the outer type being present is
// not enough, which is the lesson the six benchmark types above already paid for.
[JsonSerializable(typeof(VoicesPayload))]
[JsonSerializable(typeof(VoiceEntry))]
[JsonSerializable(typeof(VoiceProgress))]
[JsonSerializable(typeof(AudioChunk))]
[JsonSerializable(typeof(VoiceActionPayload))]
public sealed partial class ProtocolJson : JsonSerializerContext
{
}

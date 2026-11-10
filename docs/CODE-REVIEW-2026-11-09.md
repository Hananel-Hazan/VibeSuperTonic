# Code review, 2026-11-09: bugs, gaps and improvements

Tree reviewed: branch `Dev` at `7bfb0f1`, clean. Build of the Linux solution: 0 errors.
Tests: Core 1691, Daemon 220, SpeechD 116, all passing. The Windows projects were read, not
built (no Windows machine here).

How it was done: five parallel read-only reviews (Core; daemon and ctl; SpeechD, RenderHost
and UI; Windows engine and launcher; build scripts and CI), each asked to quote the code and
label its confidence. Items marked **measured** were run here against the shipped espeak
payload or a publisher's metadata. Items marked **re-checked** had their code quoted and
re-read by the merging session. Everything else carries the reviewer's label: CONFIRMED means
the code path was traced end to end, PLAUSIBLE means the code is as described but the
user-visible effect was not proven, SPECULATIVE means a guess worth one check.

The two things that already appear in README's open items (stale `XAUTHORITY` for X11
selection capture, AutoCopy inside the sandboxes) and in WINDOWS-PLAN are not repeated.

## 1. Fix first

These reach a user as silence, a wrong voice, or a release that cannot be built.

1. **The screen-reader fallback voice is silent in Hebrew, Arabic and every language the
   payload has no dictionary for.** Measured. `src/VibeSuperTonic.SpeechD/Voices.cs:212-216`
   passes `-v <language>` to the shipped espeak-ng. With `-v he` or `-v ar` the binary prints
   `Can't read dictionary file: .../he_dict` to stderr, exits 0 and writes a 13,318-byte WAV
   of nothing; `-v de` and `-v en` give 60 to 65 KB for the same sentence. `SpeakWith`
   (`SpeechdModule.cs:498-529`) sees a header and samples, answers `702 END`, and stderr is
   read only on failure (`:473, :504, :516`). So an Orca user whose language is Hebrew hears
   nothing for every character echo, and for every sentence while the daemon is down, with no
   log line. The payload ships 31 dictionaries; `he`, `ar`, `ja`, `zh` are not among them.
   Fix: drain espeak's stderr on every run; on `Can't read dictionary file` rerun without
   `-v` (or with `-v en`) and log once per language; add a packer check in 3e that drives
   `-v he` against the payload and expects the retry.

2. **Key echo of punctuation and spaces is silent, and capitals are not announced.**
   Measured silence; upstream comparison PLAUSIBLE. `SpeechdModule.cs:292-307` returns
   BEGIN/END with no audio for whitespace; espeak is run without `--punct`
   (`Voices.cs:195-225`). A comma gives the same 13 KB of nothing, a full stop gives a
   352-byte header. `cap_let_recogn` is ignored (`:204`). speech-dispatcher's own espeak
   module says "comma", "space", "capital a". Fix: for `CHAR` add `--punct`, map whitespace
   to a word before the empty check, prefix "capital" when asked.

3. **The AppImage cannot be built from a clean tree: the runtime pin is stale and the
   cache that hid it is wiped.** Measured. `build/pack-appimage.sh:77-82` pins the
   `continuous` asset at `156f4bdb...`; GitHub's digest for it is now `617aa773...`
   (updated 2026-10-19). `build/pack-tar.sh:120` runs `rm -rf dist/build-linux`, where the
   cached runtime lives, so the documented sequence (tarball then AppImage) downloads a fresh
   runtime that fails the pin. 0.2.19's AppImage was built by the hand step in
   `docs/RELEASING.md:90-94`. Fix: fetch from a fixed release tag (for example
   `releases/download/20251108/runtime-x86_64`) and pin that; keep the cache outside the
   directory `pack-tar.sh` empties.

4. **Every Linux package's licence file names the wrong first GPL release.** Re-checked.
   `build/pack-tar.sh:842`: "These terms apply from 0.2.13 onward". CLAUDE.md, README and the
   0.2.11 notes say 0.2.11; 0.2.13 was never published. Fix: `0.2.11`.

5. **The daemon loads the whole Supertonic model before it binds its socket, on every
   start.** Re-checked. `src/VibeSuperTonic.Daemon/Program.cs:375` calls
   `engines.Select(...)`; `server.Bind()` is at `:455`. The fast path in
   `EngineRoutingSynthesizer.cs:218-219` returns `_current.SampleRate`, and
   `OrtSynthesizer.cs:119` is `SampleRate => EnsureLoaded().SampleRate`. The routing class's
   own comment (`:182-200`) says this loads the model; the 0.2.11 fix made it not throw on a
   fresh install, it did not stop the load. So the first hotkey press after login waits for a
   380 MB load before the socket exists, `Program.cs:25-29`'s "loaded lazily by default" is
   not true, `--preload` changes nothing, and a Piper-only user carries Supertonic resident.
   Fix: answer the rate without loading (Supertonic's rate is a constant, Piper's comes from
   its JSON), or move the startup selection after `Bind()` and off the startup path.

6. **The daemon's startup ignores the voice the user chose.** Re-checked.
   `Program.cs:375` uses `config.Settings.DefaultVoice`; the configured voice is
   `HostConfig.ConfiguredVoice` (`HostConfig.cs:629-630`), which prefers `VoiceId`.
   A user on `piper:de_DE-thorsten-high` gets Supertonic selected and loaded at every start,
   and the Piper session plus its first calibration land on the first press, which is what
   the comment above that line says it exists to avoid. Fix: `engines.Select(voice ??
   config.ConfiguredVoice)`.

7. **A failed `accept()` kills the daemon with nothing in its log.** CONFIRMED type
   hierarchy, trigger frequency PLAUSIBLE. `DaemonServer.cs:330-331` catches only
   `OperationCanceledException`; a `SocketException` (EMFILE, ECONNABORTED) derives from
   `Win32Exception`, not `IOException`, so `Program.cs:515` misses it too. Exit 134, socket
   file deleted by the `finally`, one press lost. Fix: catch `SocketException` in the accept
   loop, log, pause briefly, continue.

8. **With AutoCopy on, pressing the read key to stop first sends Ctrl+C to the focused
   window.** CONFIRMED ordering. `DaemonServer.ReadSelection` captures (`:1304`) before it
   decides "same text, so stop" (`:1334-1341`); `WaylandSelectionSource.Capture` runs
   `TryAutoCopy` (`:138`), which fires exactly when PRIMARY equals the previous press
   (`:184`), then waits up to about 1.25 s. Stopping a reading while a terminal has focus
   sends SIGINT to whatever runs there; in an editor it overwrites the clipboard. Each half
   has a test; the pair has none. Fix: compare PRIMARY with the text being read before any
   key press.

9. **Windows: the Language setting probably never reaches untagged text.** PLAUSIBLE,
   needs one log line. `src/VibeSuperTonic.Engine/SapiEngine.cs:1075-1081` treats
   `LangID == 0` as "no tag". SAPI documents the untagged value as the voice's language, and
   the token lists `409` first (`Registration.cs:292`), so untagged text arrives as English
   and the user's German setting is skipped. The harness cannot see it (`TestHarness/
   Program.cs:1571` expects `en` either way). Verify: set Language to `de`, speak plain text,
   read the `frag LangID=` line in `engine.log`. If it says `0x0409`, treat a LangID equal to
   the token's first LCID as untagged, and add a harness case whose default is not `en`.

10. **Windows: the optimised-graph cache is never invalidated, so a model update keeps
    the old weights on CPU.** Re-checked. `Engine/Synth/SupertonicSdk.cs:782-790` treats
    "the four files exist" as valid; nothing deletes `models/onnx-optimized` and
    `ModelSet.Fingerprint` looks only under `models/onnx`. After "Repair all" re-downloads a
    changed model, CPU sessions still run the old graph while DirectML runs the new one.
    Two hosts starting together can also read a half-written cache (protobuf error,
    `E_FAIL`, one silent utterance). **This is not Windows-only**: `SupertonicSdk.cs` is
    compiled into the Linux backend (`Onnx.Ort.csproj:75`), so the daemon's CPU path has the
    same cache and the same missing invalidation. Fix: key the cache on source name, size and
    ORT version; write to a temp name and rename; have model repair delete the directory.

11. **Windows: voices registered in HKLM, engine class in HKCU, so another account on the
    machine sees voices it cannot create.** PLAUSIBLE. `Launcher/Registration.cs:271-283`
    versus `:308-316` and `:321-326`. A second user, the sign-in screen's Narrator or an
    elevated reader lists the voice and gets a silent failure on select. Fix: register the
    CLSID under HKLM when already elevated and read `BaseDir` from HKLM as a fallback, or
    state the single-user scope in `INSTALL.txt`.

## 2. Bugs by area

### Text pipeline (Core)

12. **No sentence split in Japanese (and Chinese if added).** Re-checked regex. The
    break regex at `Core/Text/SentenceChunker.cs:81-83` ends in `(?<=[.!?])\s+`;
    `FindSoftBoundary` (`:340-359`) looks for `, ; : —` then whitespace, then returns
    `text.Length`; `TextCap.cs:25` has the same ASCII terminators. A 2,000-character `。`
    paragraph is one chunk: first audio waits for the whole render, stop cancels one huge
    inference, one word-boundary event. Hindi and Arabic have spaces so they get cut
    mid-sentence at 200 characters instead. Fix: terminators `[.!?。！？।؟]` with optional
    whitespace after fullwidth ones, `、，` as soft boundaries, a hard cut at `maxChars` on a
    non-surrogate boundary as the last resort; same list in `TextCap`; a chunker test with a
    long `。` string.

13. **Curly apostrophe splits words for highlighting and defeats pronunciation
    rules.** Re-checked. `Core/Audio/BoundaryPlanner.cs:35` accepts only `'`;
    `Core/Text/Pronunciations.cs:144` matches the rule text literally after `Regex.Escape`.
    Text from Word, browsers and PDFs uses `’`: "don’t" highlights as two words, and a rule
    typed as `don't` never fires on real documents, with no diagnostic. No test mentions `’`.
    Fix: treat U+2019 and U+02BC as apostrophes in `IsWordChar` (both platforms, for
    parity); in `Compile`, replace the escaped `'` with `['’ʼ]`.

14. **The dehyphenator misses "word- \nrest" (trailing space) and the Unicode
    hyphen U+2010.** CONFIRMED code. `Core/Text/Dehyphenator.cs:115-120` requires `-`
    immediately before the newline. Both shapes are common in text copied from browsers
    and LaTeX PDFs, the case the class was written for. Fix: accept `-`, `‐`, soft hyphen;
    skip spaces and tabs before the newline; add the test.

15. **A seek or a cap can land between the halves of a surrogate pair.** PLAUSIBLE.
    `BoundaryPlanner.cs:61` treats a low surrogate as a separator; `SpeechSession.cs:485`
    slices there; `TextCap.cs:64-69` cuts at `maxChars` blindly. The fragment then starts
    with a lone surrogate, which reaches espeak as U+FFFD. Fix: step back one when the
    offset is a low surrogate, in both places.

16. **SSML with an XML prolog is read aloud as text.** PLAUSIBLE. `Core/Text/Ssml.cs:43-50`
    requires the text to start with `<speak`; `SsmlFragments.cs:357` handles a prolog only
    inside a document. `<?xml ...?><speak>Hello</speak>` is spoken as "xml version one point
    zero speak hello speak". No test has `<?xml`. Fix: skip `<?...?>` and `<!DOCTYPE>` in
    `IsDocument`.

### Export (Core)

17. **An ffmpeg that exits early is reported as "Broken pipe".** CONFIRMED.
    `Core/Export/ExportRunner.cs:336-340` and `:365`: when our write to its stdin faults, the
    stderr branch is skipped and the user sees the EPIPE message instead of ffmpeg's reason.
    Fix: when ffmpeg exited non-zero, prefer its last stderr line.

18. **`.aac` output is an MP4 container with the wrong extension.** CONFIRMED code.
    `Core/Export/ExportFormat.cs:66` maps `.aac` to the same format as `.m4a`, and
    `FfmpegTools.cs:94` always emits `-f ipod`. Fix: `-f adts` for `.aac`, or drop `.aac`.

### Daemon and vst-ctl

19. **The per-utterance provider decision uses the global `TotalStep`, not the scoped
    one.** CONFIRMED. `Daemon/ProviderSwitchingSynthesizer.cs:270-273` passes
    `settings.TotalStep` where startup uses `config.TotalStepFor(voice)`. With a per-engine
    step of 6 under a global 12, the first press declares the benchmark profile stale,
    drops its thread count and GPU pick, and rebuilds the session once. `HostConfig.cs:451-460`
    lists the four places this was fixed; this is the fifth. Fix: use `TotalStepFor` and
    `BenchmarkVoiceFor` as startup does.

20. **`vst-ctl subscribe` cannot be stopped with Ctrl+C while the daemon is idle.**
    CONFIRMED. `Ctl/Program.cs:624-635` cancels a token that the blocking `ReadLine()` never
    checks. `RunRender` (`:1146-1152`) does it right by shutting the socket down. Same fix.

21. **vst-ctl drops unknown and `--key=value` options without a word.** CONFIRMED.
    `Ctl/Program.cs:283-288` filters every `--` argument out of the positionals.
    `vst-ctl speak --voice=M4 hello` speaks with the default voice. Fix: a whitelist and
    "unknown option", exit 2.

22. **The tray's Toggle sends the daemon's own stale `$DISPLAY`.** CONFIRMED code versus
    comment. `Daemon/Tray/TrayIcon.cs:327` fills `Display` for Toggle; `DaemonServer.cs:585-587`
    prefers a non-empty request display, and its comment says the tray sends none. Only
    bites when the new X session has a different display number. Fix: send null.

23. **`/tmp` fallback: a socket directory owned by someone else crashes Bind.** PLAUSIBLE.
    `DaemonServer.cs:261-267` throws `UnauthorizedAccessException`, which `Program.cs:457`
    does not catch. Only when `$XDG_RUNTIME_DIR` is unset. Fix: catch it beside `IOException`.

### SpeechD module and UI

24. **Tune tab: the three checkboxes, the provider and the voice picker revert on a tab
    switch.** CONFIRMED. `Ui/TuneTab.cs:324` refreshes on every attach; `:434-440` assigns
    the controls from the file unconditionally; `:476-477` resets `_voiceTouched`. The
    touched guard covers only the text boxes. Tick "GPU on battery", look at Voices, come
    back: unticked. Pick a voice, switch tabs, Save: not written. Fix: the same pending-edit
    flag the rate already has, on those five controls.

25. **An unguarded one-shot `SendAsync` inside async-void handlers can end the
    window.** PLAUSIBLE. `Ui/DaemonClient.cs:137-150` swallows connect failures but not an
    `IOException` from write or read when the daemon exits between them; the callers at
    `MainWindow.cs:206, :329`, `ReaderTab.cs:313`, `VoicesTab.cs:263`, `TuneTab.cs:220`,
    `ExportTab.cs:102` are all async void. Fix: catch and return null as the contract
    promises; subscribe `Dispatcher.UIThread.UnhandledException` as a backstop that logs.

26. **The About tab's licence text predates 0.2.11.** CONFIRMED. `Ui/Tabs.cs:205-209`
    says "The program is MIT" and names only OpenRAIL-M; nothing about GPL-3.0-or-later,
    `LICENSE-PHONEMIZER.txt` or the Piper voices' own licences.

27. **`StopProcess` says SIGTERM and sends SIGKILL.** CONFIRMED. `SpeechdModule.cs:762-779`
    uses `Kill(entireProcessTree: true)`; vst-ctl's SIGTERM handler (`Ctl/Program.cs:1154`)
    never runs from here. Harmless today; fix the code or the comment.

28. **`SET language` reaches espeak but not the neural voice when no voice
    resolves.** PLAUSIBLE. `SpeechdModule.cs:344-346` passes null when `Resolve` finds no
    voice, while `:390` still hands `_language` to espeak, so a fallback changes language as
    well as timbre. Fix: give both engines the same language in that case.

29. **RenderHost: a control-file "stop" yields a truncated file reported as done;
    an unknown `--format` writes WAV under the asked-for name.** PLAUSIBLE, dead today
    (the launcher writes only pause and resume). `RenderHost/Program.cs:87-90, :348`.

### Windows engine and launcher

30. **Bookmark strings leak on every bookmark event.** CONFIRMED. `SapiEngine.cs:874-888`
    allocates with `StringToCoTaskMemUni` and never frees, on the belief that SAPI takes
    ownership. SAPI copies the string inside `AddEvents` (the SDK sample passes stack
    memory). A reader that bookmarks every sentence leaks all day. Fix: free after the call.

31. **`--set` without `=` and a lone value flag open the GUI.** CONFIRMED.
    `Launcher/Program.cs:165-168, :219-222, :126-127`: `--set onnxthreads` passes validation,
    `TryGetSet` returns false, and the default branch runs the window. The defect W2 fixed
    for unknown flags, again. Fix: exit 2 on a malformed pair or a value flag with no command.

32. **Advanced-tab (Tier B) changes are ignored for any voice with a Tune-tab
    preset.** Re-checked. `TuneTab.cs:276-285` copies `MaxChunkChars` and four more globals
    into every per-voice snapshot; `Engine/Settings/EngineSettings.cs:84-88` prefers the
    per-voice copy whenever it is non-zero, which it always is. Raising Max chunk chars on
    Advanced leaves F1 at the old value with nothing on screen. Fix: make the Tier B fields
    nullable in per-voice entries, or have Advanced write them into every entry.

33. **`SynthesisSilenceSec` is on the Advanced tab but never reaches the model.**
    CONFIRMED. `SupertonicAdapter.cs:566-567` calls `Call(...)` without `silenceDuration`,
    so the default 0.3 s always applies. Fix: pass it, or remove the knob on Windows.

34. **The hang-recovery path can discard a working CPU retry and re-enable the GPU the
    watchdog just latched off.** PLAUSIBLE. Adapter watchdog at 30 s plus a CPU reload of up
    to 20 s versus the engine's 45 s ceiling (`SapiEngine.cs:34, :402-445`); the ceiling's
    `RequestReset()` clears the latch (`SupertonicAdapter.cs:68, :140-146`). Fix: ceiling at
    least watchdog plus retry, and a reset that keeps the latch.

35. **Audio-byte total is wrong after a mid-chunk abort.** CONFIRMED, cosmetic.
    `SapiEngine.cs:963-966, :525-527` add the whole chunk after a partial write.

### Build scripts

36. **`speechd-install.sh` word-splits the module path.** CONFIRMED by reading.
    `build/speechd-install.sh:358, :464` expand `$module_cmd` unquoted; `:351, :458` use
    `${module_cmd%% *}`. An install under a path with a space dies with "the module does not
    answer INIT" instead of saying why. Fix: refuse a path with whitespace explicitly and
    quote the rest.

37. **`pack-snap.sh --test-install` can die silently when the private dbus-daemon has
    already exited.** CONFIRMED shape. `build/pack-snap.sh:488`: `[[ -n "$bus_pid" ]] &&
    kill "$bus_pid"` as the last command of an `&&` list under `set -e`, unlike `:433` which
    has `|| true`.

38. **The per-voice phonemiser failure message always prints "exited 0".**
    CONFIRMED. `build/pack-tar.sh:604-605` reads `$?` inside the `then` of `if !`. The check
    still fails; only the message lies. Fix: `rc=0; out=$(...) || rc=$?`.

39. **`INSTALL.txt` in every Linux package is stale.** CONFIRMED. `pack-tar.sh:870` "All
    three binaries" (four are listed), `:978-979` "43 Piper voices" (the catalog ships 65),
    `:308` "the three binaries do not agree".

40. **`release.yml` publishes a body that contradicts the product.** CONFIRMED.
    `.github/workflows/release.yml:63, :72`: ten English voices, one runtime. Harmless only
    because RELEASING step 6 overwrites it by hand. Fix: cut the body from `RELEASE_NOTES.md`.

41. **`appimage-bind.sh` breaks on a quoted Exec line.** PLAUSIBLE. `build/appimage-bind.sh:164`
    splits on space; `keybindings.sh:333-340` quotes a path with spaces, so the "linked it to
    the new client" branch never fires for such a path.

## 3. Gaps a user hits

42. **A selection that sanitises to nothing finishes silently.** PLAUSIBLE.
    `Core/Session/SpeechSession.cs:495-502` emits Finished from Preparing with no notice.
    Icon-font glyphs or zero-width characters only: the key does nothing. Fix: a Notice
    event ("the selection contained nothing to read"); the daemon already has
    `Response.Notice`.

43. **Pause during Preparing is ignored.** `SpeechSession.cs:345`. On a cold load the user
    presses pause and speech starts anyway seconds later. The writer already waits on
    `_unpaused` before its first write (`:592`), so allowing Pause in Preparing is small.

44. **No R-6 guard on Wayland: selecting text in our own window and pressing the key
    re-reads it.** PLAUSIBLE. X11 refuses our own window (`X11SelectionSource.cs:123-126`);
    `WaylandSelectionSource.Capture` has no equivalent, and KDE/Wayland is the live path.
    Fix: compare the capture with the text the UI shows, or make the Reader text
    non-selectable.

45. **No automation names anywhere in the window.** Names CONFIRMED missing.
    `grep AutomationProperties src/VibeSuperTonic.Ui` finds nothing: every box reads as
    "edit" to UIA, the default-voice marker is a bare "●" TextBlock. Whether Avalonia 11.3
    exposes AT-SPI on Linux at all was not verified; if it does not, `INSTALL.txt` should
    say the accessible surfaces are `vst-ctl` and the speechd module.

46. **Click-to-seek has no keyboard path, and the "Recently read" list does
    nothing.** CONFIRMED. `Ui/ReaderTab.cs:102, :313-328` and `:69, :226-228`.

47. **Model download has no resume and no early size check.** CONFIRMED code.
    `Core/Models/ModelDownloader.cs:174-221` deletes `.part` on any failure and never
    compares `Content-Length` with the manifest size before copying. Fix: refuse a wrong
    length up front; `Range` resume on a short `.part`.

48. **Windows: `SPF_NLP_SPEAK_PUNC` has no effect and `SPVA_SpellOut` is spoken as a
    word.** CONFIRMED. `SapiEngine.cs:176-183, :1054-1057`. A screen reader asking for all
    punctuation hears none. Fix: expand punctuation to words before chunking; split
    spell-out fragments into letters through `TextOffsetMap`.

49. **Windows: rate and volume are read once per Speak; `SPVES_RATE`/`SPVES_VOLUME` are
    never polled.** CONFIRMED. `SapiEngine.cs:220-223, :359-369`. A change during a long
    passage waits for the next Speak.

50. **Windows: SSML `<prosody volume>`, `<prosody pitch>`, `<emphasis>` are dropped.**
    CONFIRMED. `SPVSTATE.Volume`, `PitchAdj`, `EmphAdj` are never read (`:1052-1094`). Volume
    could use the existing `ApplyVolume` (`:780-790`).

51. **Windows: at least 700 ms of appended silence plus a 500 ms drain floor on every
    utterance.** CONFIRMED. `SapiEngine.cs:556-557, :586-590`. Menu items and typed-character
    echo pay about 1.2 s each. Fix: scale the pad to utterance length and expose it.

52. **Windows: the launcher is a GUI-subsystem exe, so a shell does not wait for it and
    `--sweep`'s exit codes are not observable interactively.** PLAUSIBLE.
    `Launcher.csproj:4`, `Program.cs:19-20`. Fix: a tiny console stub, or document
    `start /wait`.

53. **Windows: the ZIP packer has none of the tarball's assertions and tolerates a
    missing manifest.** CONFIRMED. `build/pack-zip.ps1:164-166` copies the manifest only if
    present (the Linux packer dies); no version read-back of the staged exe, no check that
    `engine\x86\onnxruntime.dll` exists in the composed tree, no "models empty" check.

54. **CI: the snap can publish to `edge` after a failed smoke test; the Flatpak is built
    regardless of `smoke`, `linux` and `speechd`.** CONFIRMED. `build.yml:500` (snap
    `needs: [pack, linux, speechd]`), `:589` (flatpak `needs: pack`). Add `smoke`.

## 4. Improvements and hygiene

55. **STOP during a stalled body read waits for the renderer.** `SpeechdModule.cs:556`
    blocks in `Read`; stdin is checked only between blocks. The header wait was already
    converted to a polled Task (`:486-494`) for this reason; do the same for the body.

56. **No catch-all around a module command.** `SpeechD/Program.cs:72` and the code outside
    `SpeakWith`'s try. An unexpected exception kills the module mid-utterance and speechd
    loses the voice until next login. Fix: per-command try, `702 END` for an open utterance,
    keep the loop alive.

57. **Live Wayland proxies leak once per capture.** PLAUSIBLE. `Interop/WaylandNative.cs:
    156-232` never destroys registry, manager, seat, device or offers before
    `wl_display_disconnect`; AutoCopy's poll does 25 captures per press. Measure RSS over a
    thousand captures, then send the destroy requests.

58. **Tray menu clicks run the selection capture on the D-Bus receive loop.**
    `Tray/StatusNotifierItem.cs:39`, `Tray/TrayMenu.cs:313`: Toggle can hold the loop for
    several seconds. `Task.Run` the invoke.

59. **Subscriber inbound bytes accumulate without bound.** `DaemonServer.cs:501`
    `ReadToEndAsync` into a `StringBuilder`; `:437` has no line cap. Same-user socket, so
    low risk; read into a discard buffer and cap request lines.

60. **`TrayIcon.Status` stays "registered" after the watcher disappears**
    (`Tray/TrayIcon.cs:448-452`), and the `ContinueWith` at `:161-165` reads `t.Result` on a
    faulted task.

61. **X11 capture waits up to 6 s per press against 2 s on Wayland.**
    `X11SelectionSource.cs:149, :173, :272`: three sequential 2 s deadlines. Issue the
    conversions together under one deadline. No `XSetIOErrorHandler` either (`:466-475`),
    so an X server vanishing mid-capture exits the daemon through Xlib's default.

62. **Renderer can outlive the session's teardown.** `SpeechSession.cs:707-709` waits 10 s
    then disposes the collection; a renderer that ignores cancellation longer faults into a
    disposed collection while the next utterance starts beside it. Both ORT synthesizers set
    `RunOptions.Terminate`, so rare. Emit an Error after the timeout instead of continuing.

63. **`Stop()` and `Speak()` hold `_emitGate` across subscriber callbacks**
    (`SpeechSession.cs:239-270, :295-306`). Worth one check that the daemon's subscriber
    write cannot block on a full socket, or a stop press waits behind a stalled client.

64. **`HttpClient` created per `ModelDownloader` and never disposed**
    (`ModelDownloader.cs:66`; one per voice install via `PiperVoiceInstaller.cs:60`).

65. **`TimeStretch` skips chunks under about 93 ms, so "Yes." at 2x plays at 1x**
    (`Core/Audio/TimeStretch.cs:60-64`, documented). Pad to the Sonic floor, stretch, trim.

66. **A broken `settings.json` on Windows falls back to engine defaults, not the last
    good settings** (`Engine/Settings/EngineSettings.cs:142-156`), and the launcher's
    delete-then-move makes the file briefly absent. Keep the previous cache on failure.

67. **The engine writes to the host's stdout** (`SupertonicSdk.cs:814, :818`,
    `Helper.Timer`). Inside a console SAPI host that streams audio to stdout this corrupts the
    output. Route through a callback; `ThreadSweep.cs:351-354` depends on these lines.

68. **Small leaks and dead code.** `Process.GetCurrentProcess()` per telemetry tick
    (`Engine/Telemetry/TelemetryWriter.cs:93`); `DaemonClient.TryStartDaemon` never disposes
    its `Process` (`Ui/DaemonClient.cs:309-315`); `ClampMp3Bitrate` is dead
    (`Launcher/Export/MfAudioEncoder.cs:233-237`); the first telemetry snapshot shows
    `OnnxThreads = -1` (`SapiEngine.cs:242`); `LineReader` is quadratic on one long line
    (`SpeechD/LineReader.cs:217-227`); `_installed.Read()` walks two directories on every
    neural SPEAK (`SpeechdModule.cs:331-332`); `Enum.TryParse<RequestVerb>` accepts numbers
    (`Ctl/Program.cs:335`).

69. **Compiler warnings.** A full rebuild prints about 1,400 `CA1416` lines, nearly all in
    the test projects calling Linux-only APIs, which buries the five in product code:
    `Core/SpeechD/WavHeader.cs:62` (`stackalloc` inside the chunk loop, CA2014; bounded by
    the input but trivially hoisted), `Core/Session/SpeechSession.cs:579` and
    `Core/Text/Dehyphenator.cs:64` (nullable), `Daemon/PortalCopyKey.cs:178` and
    `Tray/UpdatePrompt.cs:54` (obsolete Tmds.DBus overload). Mark the test projects
    `SupportedOSPlatform("linux")` and fix the five.

70. **Build hygiene.** Tarball records the packer's uid and user name (`pack-tar.sh:1307`;
    add `--owner=0 --group=0 --numeric-owner`). Assertion 4 checks the tree root only
    (`:676-677`) where the other packers use `find`. Assertion 5 passes with no measurement if
    `objdump` returns nothing for every ELF (`:769-790`; add a `die` on an empty maximum).
    Workflows declare no `permissions:` and pin actions by major tag; `release.yml:116-117`
    interpolates inputs into a pwsh script. All three workflows ask `setup-dotnet` for
    `dotnet-quality: preview` with no `global.json`. The snap's licence string includes LGPL,
    the metainfo's does not (`snapcraft.yaml.in:39`). Two csproj files repeat `<Version>`
    that `Directory.Build.props:65` already sets. `keybindings.sh:622, :640` leak `alt` and
    `written` from a function.

71. **Leftover artifacts.** `dist/` still holds 0.2.12 to 0.2.16 packages that were never
    released (CLAUDE.md and RELEASING step 9 say to delete them) and `dist/nuget-official.json`,
    referenced by nothing. Not deleted here; they are yours.

## 5. Documentation that disagrees with the code

72. CLAUDE.md:457-458 says `<VstVersion>` is `0.2.17`, under development; it is `0.2.19`,
    shipped 2026-11-08. The paragraph already admits going stale once.
73. Nobody agrees how many assertions `pack-tar.sh` runs. Labelled blocks: 1, 2, 3, 3b, 3c,
    3d, 3e, 4, 4b, 4c, 5, 6, twelve. The file header says six, CLAUDE.md says nine and
    eleven, TESTING-PLAN says 11 and nine, `build.yml` says ten and eleven, `smoke-test.sh`
    says nine, README says eleven. Count once in CLAUDE.md and point the others at it.
74. CLAUDE.md:148 "Every instance in build/ was converted" (early-exiting pipeline
    consumers); five remain: `pack-tar.sh:333, :351, :358` and `pack-appimage.sh:115, :120`
    (`file | grep -q`, `find -print -quit | grep -q .`). Not reachable in practice (one short
    line each) but it is the shape the rule forbids.
75. README's open item on stale `XAUTHORITY` for selection capture looks partly done:
    `Interop/NativeXAuthority.cs` plus `DaemonServer.CaptureSelection` (`:588`) already
    applies the latest client's cookie before an X11 capture. Re-check the wording.
76. The `_assert_catalog` comment at `pack-tar.sh:379` says 43 voices.

## 6. Test gaps worth closing

77. `vst-ctl` has no tests at all: 1,250 lines on the hotkey's first 10 ms, including the
    argument parser (items 20 and 21 would have been caught). Move the parsing into a
    testable class.
78. No test drives espeak with a language outside the payload (item 1); the hermetic
    packer check 3e is the right home. None for CHAR punctuation (item 2).
79. No test for: a long CJK paragraph's chunk count, `’` in highlighting or rules, a hyphen
    with trailing space, a seek inside a surrogate pair, an SSML prolog, AutoCopy combined
    with press-to-stop, the Tune tab's tab-switch revert, `LogRotation` (none), the model
    downloader's mirror fallthrough after a hash mismatch and `.part` cleanup on cancel.
80. Windows harness never asserts audio byte offsets, only character offsets
    (`TestHarness/Program.cs:71-141, :285-365`); a regression putting every boundary at
    offset 0 would pass.

## 7. Performance

81. **Startup pays the full Supertonic load before the socket exists** (item 5). The
    first press after login waits seconds for a load the design calls lazy, and a
    Piper-only user keeps Supertonic resident. Same fix as item 5.

82. **Piper sessions run with ORT's default graph optimisation and no cache.**
    CONFIRMED code. `Onnx.Ort/PiperSynthesizer.cs:239-243` sets only the two thread counts.
    The Supertonic loader (`Engine/Synth/SupertonicSdk.cs:792-800`, compiled into the Linux
    backend) uses `ORT_ENABLE_ALL`, sequential execution and a persisted optimised graph,
    and its own comment measures 1 to 3 s of optimisation saved per model. Every Piper voice
    pays basic-level optimisation at each daemon start and gets none of the fusions. Fix:
    `ORT_ENABLE_ALL` plus `OptimizedModelFilePath` beside the voice's `calibration.json`,
    keyed to the `.onnx` size and mtime so item 10's staleness cannot recur.

83. **The screen-reader path has no lead chunk, so Orca's first audio waits for a
    full chunk.** CONFIRMED code. The hotkey path chunks with `LeadChunkChars = 64`
    (`Core/Session/SpeechSession.cs:60, :492-494`) so the first audio comes from a short
    render. The render path that `vst-speechd` and `vst-ctl render` use calls
    `SentenceChunker.Chunk` without a lead (`Daemon/DaemonServer.Render.cs:327-328`), so the
    first sound of every Orca sentence waits for a render of up to 200 characters. Fix: pass
    the same lead on the render path.

84. **Render-ahead depth is one chunk.** `SpeechSession.cs:475`
    `BlockingCollection(1)`: one chunk renders while one plays. On a CPU slower than real
    time, a chunk whose render takes longer than its predecessor's playback leaves a gap.
    SPECULATIVE on how often it is audible; the depth was chosen for stop latency and
    memory. Measure the real-time factor from the benchmark profile and allow depth two only
    when it is below about 1.5.

85. **Two processes per screen-reader utterance.** `SpeechD/Voices.cs:109, :180` spawn
    `vst-ctl render` (neural) or `espeak-ng` (fallback) for every SPEAK and every CHAR. At
    typing speed that is a fork and exec plus a socket round trip per keystroke; espeak-ng
    also reloads its dictionaries each time. vst-ctl is NativeAOT so the neural half is a few
    milliseconds, but the fallback is not measured. Fix: a persistent connection to the daemon
    for the neural path, and `libespeak-ng` called in-process for the fallback (the Piper
    project already P/Invokes it, and the module is AOT, so it adds no runtime).

86. **AutoCopy's clipboard poll opens a new Wayland connection 25 times per press.**
    `Daemon/WaylandSelectionSource.cs:201-207` polls every 40 ms for up to 1 s and each
    capture (`Interop/WaylandNative.cs:156-232`) connects, walks the registry and binds
    anew, then leaks the proxies (item 57). Keep one connection open across the poll.

87. **X11 capture spends up to 6 s in three sequential 2 s waits** (item 61). Issue the
    TIMESTAMP and UTF8_STRING conversions together under one deadline.

88. **Windows: about 1.2 s of padding and drain on every utterance** (item 51). Menu
    echo in a screen reader pays it every item.

89. **Model download runs one file at a time with no resume** (item 47). Two files in
    flight and `Range` resume would shorten the 380 MB first run on a slow link and make a
    dropped connection cost seconds instead of minutes.

90. **Small per-utterance costs** (items 64 and 68): a directory walk on every neural
    SPEAK, a quadratic line reader on long lines, a process handle per telemetry tick, an
    `HttpClient` per download. Each is cheap alone; together they are the module's per-key
    overhead.

## 8. Features that would help users

91. **Hebrew has no voice in the product at all.** CONFIRMED by listing. Supertonic's
    language list (`Core/Synthesis/SupertonicLanguages.cs`) has `ar` but no `he`; the Piper
    catalog's 30 languages have neither `he` nor `ar`; the espeak payload has no `he_dict`
    (item 1). So a Hebrew-speaking user gets English and 30 other languages, and silence from
    the screen-reader fallback. Cheapest step: add espeak-ng's `he` and `ar` dictionaries to
    the payload (`build-espeak.sh` prunes to the catalog's languages; two more cost under
    1 MB), so the fallback speaks and any later Hebrew Piper voice has its phonemiser. Whether
    a Piper-compatible Hebrew model exists upstream was not checked beyond the shipped catalog.

92. **Keys and tray items for pause, resume and speed.** The protocol has `Pause`,
    `Resume` and `Seek` (`Core/Ipc/Protocol.cs`), and `vst-ctl` exposes pause and resume
    (`Ctl/Program.cs:19`), but `keybindings.sh:956-957` binds only read and stop, and the tray
    offers "Read the selection", "Stop reading" and "Open the window" (`Tray/TrayIcon.cs:277-280`).
    Add a pause/resume toggle key and tray item, and faster/slower keys through a rate-delta
    verb applied per utterance the way the per-utterance rate already is.

93. **Sentence skip.** `Seek` takes a character index (`Protocol.cs:286`); chunk offsets
    already exist (`ChunkWithOffsets`), so a relative `seek --sentence +1/-1` and two keys are
    cheap. Orca users and long-article readers use this constantly.

94. **Read typed or pasted text, and files, from the Reader.** Only the Export tab has a
    text box and a file picker (`Ui/ExportTab.cs:45-50, :175`); the Reader shows captured text
    only. Add a "Read" button beside Export's box and drop of a `.txt` onto the Reader.

95. **An explicit clipboard read.** `vst-ctl read --clipboard` and a key. Today the
    clipboard is reachable only as a fallback setting when PRIMARY is empty.

96. **Speak from stdin.** `vst-ctl speak -` is refused on purpose (`Ctl/Program.cs:229-235`,
    because "-" is text a reader does read); a `--stdin` flag lets scripts pipe a file.

97. **A voice per language, chosen by script.** No language or script detection exists in
    Core or the daemon (grep), and settings hold one `VoiceId`. Mixed Hebrew and English, or
    English and German, is read by one voice in one language. The render path already carries
    a per-fragment language (`DaemonServer.Render.cs:322-325`); add a per-language default
    voice map and a Unicode-script detector per chunk.

98. **Opt-in daemon autostart.** `INSTALL.txt` says nothing autostarts it (`pack-tar.sh:874`).
    A systemd user unit or XDG autostart entry with `--preload` makes the first press after
    login instant. Honest only once item 5 is fixed, since today the load happens on every
    start anyway.

99. **Pronunciation rules you can share.** One file, `data/pronunciations.json`
    (`Ui/PronunciationsTab.cs:163`); no import, export or per-language sets. Item 13 is also
    why rules written on a keyboard miss real documents.

100. **Export quality and long texts.** Bitrate is fixed at 128k (`Core/Export/FfmpegTools.cs:
    90, :94`), and a text over the size limit is refused with "split it first"
    (`Ui/ExportTab.cs:192-200`) instead of being split into one file per chapter or paragraph
    block. A quality choice is one argument here and in `check-ffmpeg-bundle.sh`.

101. **Reader history that does something, and keyboard seek** (item 46).

102. **Screen reader: punctuation, spaces and capitals in key echo** (item 2). The single
    most noticeable gap for a daily Orca user, since key echo is most of what a screen reader
    says while typing.

103. **Windows parity for screen readers**: punctuation mode and spell-out (item 48), live
    rate and volume changes (item 49), SSML volume (item 50), shorter utterance padding
    (item 51).

### Added after batch 1's review pass (2026-11-09)

104. **SSIP `KEY` echo still speaks English key names in any voice.** Batch 1 fixed `CHAR`
    only (`SpeechdModule.cs:355-365`); `KEY` messages (`space`, `shift_a`, `Return`) take
    the old path, so a German voice says "space" rather than "Leerzeichen".
105. **A language is demoted for the module's life on one stderr string.** `Lacks()`
    (`SpeechdModule.cs:617-623`) treats any "Can't read dictionary file" line as a missing
    language. No shipped voice prints it for an auxiliary file; a language outside the
    catalog could. PLAUSIBLE, low.
106. **The first espeak run per language holds the whole WAV** (`SpeechdModule.cs:651-665`)
    to read stderr before sending audio. With the daemon down, the first long SPEAK in a
    language waits for its full render. Once per language per login; STOP is honoured.
107. **`HandleInit` (`SpeechdModule.cs:202`) owes no reply**, so an exception there leaves
    INIT unanswered and speechd drops the module. Not a regression of batch 1.

## 9. Checked and found sound

Protocol parsing and peer-credential checks on the daemon socket; stop and interrupt
ordering; PulseAudio reconnect after device loss; ORT provider fallback and the GPU latch;
Piper speaker ids and BOS/EOS handling; NativeAOT safety of every type that crosses vst-ctl's
socket; the SAPI interop layouts (`SPVSTATE`, `SPVTEXTFRAG`, `SPEVENT`, `WAVEFORMATEX`, both
vtables) on x86 and x64; HRESULT discipline at the COM boundary; the launcher's atomic
settings writes. All hash pins other than the AppImage runtime were re-derived from their
publishers and match (both wheels, the ORT nupkg, LAME, the ffmpeg tag commit, the sonic
pin). `check-piper-catalog.py` passes on the shipped catalog; `models-manifest.json` is
internally consistent.

## Suggested order

1. Items 1 and 2 (screen-reader fallback silence), 5 and 6 (startup load and voice), 3 and 4
   (AppImage pin, licence text): one patch release.
2. Items 7, 8, 19, 24, 25: daemon robustness and the Tune tab.
3. Items 12 to 16: the text pipeline's non-Latin edges, each with its test.
4. Items 9 to 11, 30 to 34, 48 to 53: the Windows catch-up, best folded into WINDOWS-PLAN.
5. Sections 5 and 6: documentation and tests, cheap and worth doing alongside each fix.
6. Performance: items 82 and 83 are small and measurable (a Piper start and Orca's first
   audio); 85 and 86 need a measurement first.
7. Features: 91 (Hebrew dictionaries) and 92 to 93 (pause, speed, sentence keys) are the
   ones a daily user would feel first; 97 (voice per script) is the largest.

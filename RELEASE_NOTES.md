# VibeSuperTonic v0.2.9

*Linux gets a second front door: one file you download, make executable, and run.*

The tarball is unchanged and is still the canonical Linux artifact — `install.sh`,
the optional GPU pack and all six packer assertions are verified against it, and a
headless or server install has no use for a single-file GUI bundle. The AppImage
is for everyone else.

```
VibeSuperTonic-0.2.9-x86_64.AppImage      48 MB
VibeSuperTonic-0.2.9-linux-x64.tar.gz     52 MB
```

## The AppImage

- **One file, no .NET, no unpacking.** `chmod +x` and run it. The window opens;
  `./VibeSuperTonic-0.2.9-x86_64.AppImage bind` sets up the two hotkeys.
- **Models and data live beside the file**, in a `VibeSuperTonic/` folder. Keep
  the two together — moving the AppImage on its own leaves the store behind, and
  the next run starts a new one. If the directory is not writable (`/opt`, a
  read-only medium), everything moves to `~/.local/share/vibesupertonic` instead
  and the Status tab says so.
- **The hotkeys do not go through the AppImage.** `bind` installs `vst-ctl` into
  `~/.local/bin` and points the keys there, because reaching it through the image
  costs a filesystem mount on every press — 20.0 ms against 5.3 measured, which
  is inside budget and still not worth paying forever for a 4 MB copy. Both the
  daemon and the window re-check that copy at startup and replace it when it is
  out of date, so upgrading is still replacing one file.
- **Runs on Ubuntu 22.04+, Debian 12+, RHEL 9+, Fedora 35+** — glibc 2.34 and
  above. Measured rather than assumed, and the packer now refuses to build an
  archive whose floor has risen.
- **Needs FUSE**, like every AppImage. Without it the image cannot be mounted and
  nothing inside it runs; `vst-ctl` and `bind` both say so now, and name
  `--appimage-extract-and-run` as the way through.

## Also on Linux since 0.2.7.5

These shipped in 0.2.7.6 and 0.2.8 and had no notes of their own.

- **An NVIDIA GPU can do the inference.** Measured on an RTX A2000: the wait
  before the first word goes from **802 ms to 77 ms**. It is not in the download —
  `./install-gpu.sh` fetches about 3.1 GB on request — and **on battery the engine
  stays on the CPU** unless you set `GpuOnBattery`, because a discrete GPU is the
  difference between a laptop that lasts an afternoon and one that does not.
- **The engine measures this machine and uses what it measured.** `vst-ctl
  benchmark`, or the button on the Tune tab, tries every thread count worth
  trying and keeps the whole table. It applies to the next thing you ask it to
  read, with no restart — as does unplugging the power lead.
- **A press is no longer silenced by the stop before it.** Reported twice from
  daily use: the text appeared, the highlight never moved, nothing was spoken,
  and the next press worked.
- **KDE and Wayland are supported properly.** Hotkeys are bound the way Plasma
  binds them, and the selection is read from the compositor rather than from X11 —
  on a Wayland session the old path could see almost nothing.

## Windows

Unchanged by this release. The AppImage is a Linux artifact; the Windows ZIP
continues to ship from the same version number.

---

# VibeSuperTonic v0.2.7.5

*The harness found a bug on its first run. This is that bug.*

0.2.7.4 shipped the verification harness. Step 8 failed immediately on the
Windows VM — two of its five whitespace layouts reported word-boundary offsets
that drifted one character per separator:

```
FAIL double space     events=10  off-word=7  max drift=2 chars
    pos=21 len=6 -> " Secon"   <-- not a word start
    pos=43 len=5 -> "  Thi"    <-- not a word start
FAIL paragraph break  events=10  off-word=7  max drift=2 chars
```

Single space, newline and tab passed. That pattern is the diagnosis: `\n` and
`\t` each collapse to one space, so nothing shifts, while `"  "` and `"\n\n"`
lose a character each.

---

## Fixed

- **Word-boundary offsets no longer drift when sentences are separated by more
  than one whitespace character.** The R-14 fix in 0.2.7 corrected each chunk's
  *start* position but not the whitespace the chunker collapses *inside* a chunk.
  Callers then added an index-within-chunk to that start, which is correct only
  while every separator happens to be exactly one character long.

  `SentenceChunker` now returns a per-character map from chunk text back to its
  input, and the engine composes it with the pronunciation/sanitizer map so a
  chunk index resolves to a source position in one lookup. There are two rewrites
  between what the model speaks and what the client holds, and both are now
  undone rather than one.

  Affects any client that highlights while reading, on text with double spaces
  after full stops or blank lines between paragraphs — which is to say, most
  pasted prose.

## Verified

Step 8 now passes all five layouts, plus a deliberately ragged sixth. Step 9 —
offsets under a length-changing pronunciation rule — passed in 0.2.7.4 already
and still does, including the check that a boundary on `kilograms` reports a
length of **2**, the `kg` the user typed.

Core.Tests is 187, up from 179. Eight of the new ones cover this directly,
including the harness's failing input reduced to a unit test and a test that
composes the chunk map with a pronunciation map in the same order the engine
does.

## Worth saying

This bug was live in 0.2.7, 0.2.7.1, 0.2.7.2, 0.2.7.3 and 0.2.7.4 — every
release that claimed R-14 was fixed. Nothing on the Linux side could see it: the
Core tests passed because they tested the chunk map in isolation, and the
end-to-end render sounds identical either way. It took a real SAPI client
reporting real offsets, which is exactly what the harness was built for and why
it ships in `tools\`.

---

# VibeSuperTonic v0.2.7.4

*Ships the harness that proves the last two releases were right.*

0.2.7 fixed two bugs about **where a word-boundary event says the spoken word
is** — a highlight that drifts away from the voice. Working audio does not
demonstrate either fix. Neither does a green build. The only evidence that
counts is a client reporting offsets that land on the right characters, and
until now there was no way to check that without watching a screen reader and
squinting.

Nothing in the engine changes behaviour in this release.

---

## Added

- **`tools\VibeSuperTonic.TestHarness.exe`** — the System.Speech verification
  suite, in the ZIP for the first time. It existed in the source tree and was
  never packaged, so anyone without a build environment could not run the one
  thing that tests the engine end to end through a real SAPI client.

      tools\VibeSuperTonic.TestHarness.exe

  Exit code 0 means everything passed. Run it after the voices are registered and
  the models have downloaded. You will hear it speak — the word-boundary checks
  need real audio timing to fire, so the output cannot be sent to a null device.

- **Step 8 — word-boundary offsets land on real words.** Speaks the same three
  sentences separated five different ways (single space, newline, double space,
  paragraph break, tab) and checks every reported offset against the actual word
  starts of the source text. The separator is the point: the chunker normalises
  whitespace, and that is what made chunk positions unrecoverable by search
  (R-14).

- **Step 9 — the same with a length-changing pronunciation rule.** Installs a
  `kg` → `kilograms` rule, speaks two sentences using it, and checks that
  nothing drifts by the seven characters the substitution adds — and that a
  boundary landing on `kilograms` reports a length of **2**, the source token the
  user actually typed, not 9 (R-2). Your `pronunciations.json` is backed up and
  restored, including if the step fails.

  These two are not circular, though they look it. System.Speech builds
  `SpeakProgress.Text` *by substringing your prompt at the offset the engine
  reported*, so comparing those two always agrees and proves nothing. The ground
  truth here is computed independently from the source string.

## Under the hood

- **`VibeSuperTonic.Core` grew to fourteen files** — the DSP (`Sonic`,
  `TimeStretch`), the language table, log rotation, the model manifest and
  downloader, and the telemetry DTO all moved in. Three duplicated types are gone
  with them, including a twenty-property telemetry DTO that existed once in the
  engine and once in the launcher with a "keep in sync" comment on each.
- **`RenderSanity`** — decides whether a buffer is speech without ears: pauses at
  phrase boundaries, and amplitude modulation in the 2–8 Hz syllable band. Phase 0
  of the Linux port raised the objection that a fast render of garbage passes RTF,
  peak, RMS and duration; this is the answer. 179 tests now, up from 90.
- The harness ships **framework-dependent**, like RenderHost and for the same
  reason: SAPI loads the engine into it, so ONNX Runtime ends up hosted there, and
  a self-contained single-file host crashes it. A verification tool shaped
  differently from a real SAPI client would not be verifying much.

## Upgrading

Drop the ZIP over your install. If you only want to run the checks, `tools\` is
self-contained apart from the .NET 10 x64 runtime you already need.

---

# VibeSuperTonic v0.2.7.3

*Voices listed, voices silent. The missing piece was never .NET.*

A field report against 0.2.7.2: 32-bit readers now listed all ten voices, the
user selected one, and nothing was ever heard — while the Control Panel spoke
normally. `engine.log` had the answer and phrased it as badly as Windows knows
how:

```
Unable to load DLL 'C:\...\engine\x86\onnxruntime.dll' or one of its
dependencies: The specified module could not be found. (0x8007007E)
```

That file was present. `0x8007007E` is `ERROR_MOD_NOT_FOUND` and the load that
failed was a **dependency** — the **Visual C++ runtime**, which ONNX Runtime's
native library links against and which the message never names. The x64
redistributable was installed (something else had brought it along); the x86 one
was not. So the 64-bit Control Panel worked and every 32-bit reader was mute.

---

## Added

- **The Status tab now checks the Visual C++ runtime**, x64 and x86, alongside
  the .NET rows — and can install either for you, same as the .NET ones: confirm,
  UAC, winget, verified by re-probing rather than by exit code.
- **The "32-bit SAPI clients" row distinguishes listed from working.** With
  registration complete but the C++ runtime missing it now reads *"voices are
  listed but produce NO audio — Visual C++ runtime (x86) is missing"*. Reporting
  "all 10 voices visible" there was true and useless.
- **The startup warning covers both prerequisites** and names which one is
  missing, because they fail in opposite ways:

  | Missing | Symptom |
  | --- | --- |
  | .NET 10 runtime (x86) | reader lists **no** voices |
  | Visual C++ runtime (x86) | voices **are** listed, and silent |

## Fixed

- **The engine no longer passes `0x8007007E` along unedited.** A native load
  failure is caught at the ONNX Runtime load site and rethrown naming the likely
  cause, the host's bitness, and the exact winget command. That text reaches you
  in two places you would actually be looking: `engine.log`, and the Monitor
  tab's **Last error** column.

  The old message was worse than unhelpful — it named `onnxruntime.dll`, which is
  present, so everyone who read it went and checked that file, found it, and
  started hunting a corrupt download.

## Documentation

- README and `INSTALL.txt` now list **both** runtimes, in both architectures,
  with a table of what each failure looks like. Previously they named only .NET —
  correct as far as it went, and it did not go far enough.
- Both now end with the step that is easy to miss: after installing anything,
  press **Repair all** *and restart your reader*. A reader that was already
  running keeps the old, failed engine loaded until it restarts.

## Why this keeps happening

The engine is a COM in-process server, so it runs inside somebody else's process
and inherits that process's bitness and its runtime search. Every dependency it
has must therefore exist twice, and the 32-bit half of the pair is the one no
modern machine has by default. The engine cannot ship its own copies either —
`dotnet publish --self-contained` with `EnableComHosting` fails outright with
`NETSDK1128`.

What is left is detection. Three releases in, the Status tab now checks all four
prerequisites, states which is missing, describes the symptom it produces, and
offers to install it.

---

# VibeSuperTonic v0.2.7.2

*Fixes a warning that told you the opposite of the truth, and lets the app
install the prerequisite itself.*

0.2.7.1 added a startup warning for the case where 32-bit readers cannot see the
voices. It fired too widely and then described the wrong cause — including,
absurdly, telling a user who had just installed the 32-bit runtime that it was
not installed, while the Status tab two inches away reported it present.

---

## Fixed

- **The 32-bit warning no longer fires on a freshly extracted folder.** It was
  triggered by "32-bit clients cannot see the voices", which is the ordinary
  state of an install that has not been registered yet — so a normal first run
  produced an alarming modal about a problem that pressing **Repair all** was
  about to solve. It now fires only for the missing x86 runtime: the one cause
  you cannot discover for yourself and cannot fix from inside the app's normal
  flow.
- **…and no longer states a cause it has not checked.** The text hardcoded "the
  32-bit .NET 10 runtime is not installed" regardless of why 32-bit was not
  working. A diagnostic that contradicts the evidence on screen is worse than no
  diagnostic at all. It now reports what was actually detected, naming the
  versions it found.

## Added

- **The Control Panel can install the .NET runtime for you.** If one is missing,
  the launch warning offers to install it, and either runtime row on the Status
  tab can be double-clicked to do the same. It runs `winget`, so the package is
  verified against Microsoft's own manifest — the app never downloads an
  executable itself and runs it elevated.
  - **Never silent.** A machine-wide runtime install is always confirmed first,
    with the exact command shown, and always raises UAC. "Repair all" skips it
    unless you confirm, because registry writes and model downloads are what that
    button promises and installing a system runtime is not. `--repair` on the
    command line skips it too and prints the command instead.
  - Success is judged by re-probing for the framework, not by winget's exit code:
    winget returns non-zero for benign outcomes like "already installed", and a
    zero exit does not prove the runtime landed where the engine will look.
  - If winget is absent, it says so and points at the download page rather than
    failing obscurely.

## Documentation

- **The README now leads with the prerequisite** instead of mentioning it in a
  parenthetical. It explains why 32-bit matters even on 64-bit Windows, gives
  both winget commands, and states plainly that skipping the x86 runtime means
  32-bit readers show *no* entries at all while the Control Panel's own Test
  button keeps working — the combination that reads as "the app is fine, my
  reader is broken".
- Corrected throughout: the engine needs the plain **.NET Runtime**, not the
  **Desktop Runtime**. It targets `net10.0-windows` but uses neither WinForms nor
  WPF, so its runtimeconfig asks only for `Microsoft.NETCore.App`.
- The README now answers "why not just bundle the runtime?" with the actual
  build error — `NETSDK1128: COM hosting does not support self-contained
  deployments` — rather than leaving it as an obvious-looking omission.

## A note on fresh installs

A newly extracted folder legitimately shows several red rows: no voice tokens, no
CLSID, no models. That is the pre-registration state, not damage. Press **Repair
all** — it registers the voices (one UAC prompt) and downloads the models. The
README now says so at the step where you will see it.

---

# VibeSuperTonic v0.2.7.1

*Hotfix. The voices were fine; the Control Panel was lying about them.*

If you installed 0.2.7, opened Balabolka or Lingoes, and found no VibeSuperTonic
voices at all — while the Control Panel's own Test button spoke perfectly — this
is the release that explains why, in words, the moment you open it.

Nothing about speech changed. This is entirely about the product telling the
truth when something is missing.

---

## The problem

The engine is a COM in-process server: it loads *inside* your reader, so it needs
the .NET 10 runtime **in the same bitness as that reader**. Most SAPI clients are
still 32-bit — Balabolka, Lingoes, many NVDA setups — and the 32-bit runtime is a
separate download that almost nobody has.

Without it, the launcher registered nothing for 32-bit. Not a partial install —
*nothing*, so those programs showed no VibeSuperTonic entries whatsoever. Then:

- The Status tab reported **"all 10 voices registered"**, because it only
  inspected the 32-bit registry view when it had already decided to populate it.
- The x86 CLSID row rendered **"(skipped — no x86 engine or runtime)"** as a
  green pass.
- The one amber row that did mention it was truncated mid-sentence by the column
  width.
- And the Control Panel's own voices worked, because it is 64-bit and
  self-contained.

Eleven green ticks and working audio, on a machine where no reader could find a
single voice. The natural conclusion is that the readers are broken, which is the
most expensive possible way for this to fail.

## Fixed

- **The Status tab now says it plainly.** A new **32-bit SAPI clients** row states
  the outcome rather than the steps: either "all 10 voices visible to 32-bit
  hosts", or "NO voices — the 32-bit .NET 10 runtime is not installed". The voice
  token row now names which bitnesses it actually found.
- **The fix instructions are visible.** Every check has always carried a
  `FixHint` — the exact winget command — and the Status grid had no column for it,
  so it was computed and thrown away on every refresh. The log pane below the grid
  now lists each failing check with its fix, and you can select and copy the
  command straight out of it.
- **A warning when you open the Control Panel**, if and only if 32-bit clients
  cannot see the voices. It says so, gives the command, and offers to open the
  download page. It also warns you not to read the working Test button as proof
  that your reader will work — that inference is exactly the trap.
- **Runtime detection actually checks the runtime.** It tested whether
  `dotnet.exe` existed under Program Files, which any .NET install of any version
  satisfies — including .NET 8, or an SDK. On such a machine the launcher would
  have registered the 32-bit voices and let them **fail on first Speak**, which is
  worse than not appearing: it sends you hunting an engine bug. It now looks for
  an actual `Microsoft.NETCore.App` 10.x shared framework, per architecture, via
  the location the .NET installer recorded.
- **The prerequisite is named correctly.** The engine needs the plain **.NET 10
  Runtime**, not the **Desktop** Runtime — it targets `net10.0-windows` but uses
  neither WinForms nor WPF, so its runtimeconfig asks only for
  `Microsoft.NETCore.App`. The old advice worked but sent you after a
  substantially larger download than you need.
- **INSTALL.txt leads with it.** It previously said "open any SAPI client
  (Balabolka, NVDA, Narrator)" without ever mentioning a runtime — naming, as the
  examples, the very programs that would fail.

## Upgrading

Drop the ZIP over your install and run `VibeSuperTonic.exe`. If 32-bit clients
are affected you will be told immediately.

To make 32-bit readers work:

    winget install Microsoft.DotNet.Runtime.10 --architecture x86 --force

then press **Repair all** on the Status tab. Installing both architectures is
fine and is the safe default.

## Not fixed, because it cannot be

Shipping the runtime inside the ZIP would remove the prerequisite entirely, and
it is not available to us: `dotnet publish --self-contained` on a project with
`EnableComHosting` fails with **NETSDK1128 — COM hosting does not support
self-contained deployments**. The runtime is a genuine prerequisite, so the
product's job is to say so clearly and at the right moment. That is what this
release does.

## Known limit

If you set `HKCU\SOFTWARE\VibeSuperTonic\SuppressX86Warning` to 1, the startup
warning stops appearing. There is deliberately no checkbox in the dialog — the
failure is total and silent, so the default has to be to keep saying it.

---

# VibeSuperTonic v0.2.7

*The release that makes the underline stop lying.*

Two of these fixes are the same bug wearing different hats: the engine knew which
word it was speaking, and told your screen reader a slightly different number.
The third is a setting that has had sliders, persistence, and per-voice overrides
since the day it shipped, and has never once been read.

Drop-in replacement for v0.2.5. No schema migration, no re-registration.

---

## Fixed

- **Word-boundary offsets no longer drift under pronunciation rules.** This was
  documented as a "Known quirk" in the v0.2.2 notes and is now simply gone. Rules
  and the invisible-character sanitizer both change the length of the text before
  it reaches the model, and the engine was reporting positions by adding an index
  into the *rewritten* text to the *source* offset of the fragment. One `kg` →
  `kilograms` rule shifted every following word in that sentence by seven
  characters, and the error accumulated. Highlights now resolve through a proper
  offset map.
  - The highlight *length* is right too. `kilograms` is nine characters of spoken
    text covering the two you typed, so the underline is two wide — not nine
    characters of whatever came next.
- **…and no longer drift on text with line breaks in it.** A second, independent
  offset bug found while fixing the first, and the more common of the two: chunk
  positions were recovered by searching for the chunk inside the sentence it came
  from, but the chunker normalises whitespace as it works. Any text separated by
  a newline, a tab, or two spaces — which is to say most prose — failed that
  search and fell back to a running estimate that drifted by one character per
  collapsed separator. Measured at 5 of 6 chunks wrong on newline-separated text.
- **`Max chunk chars` and `Min chunk chars` now do something.** Both have been
  sliders in **Advanced**, per-voice overrides in **Tune**, and saved to
  `settings.json` — and the chunker has always ignored them in favour of two
  hardcoded constants. They are now read. The defaults are unchanged (200 and
  100, exactly what the constants were), so if you never moved these sliders you
  will not hear any difference; a regression test pins that. **If you did move
  them, your chunk boundaries will change**, which is audible at the seams. That
  is the setting finally working, but it is worth knowing before you wonder why
  a voice you had tuned sounds slightly re-paced.
  - Values are now clamped to a sane range at the point of use. The two sliders'
    ranges overlap, so a minimum above the maximum was always selectable, and the
    settings-file import path never range-checked anything at all.
- **`settings.json` keys the Control Panel doesn't recognise are no longer
  deleted.** Settings are parsed through a source-generated serializer, which
  discards unknown properties; the launcher then writes the whole object back, so
  anything it hadn't heard of was quietly erased on the next save. Harmless while
  only one program writes the file. It stops being harmless the moment two do —
  and it already bites anyone who runs a newer build, goes back to an older one,
  and loses their newer settings with no error anywhere.

## Under the hood

For the curious, and the people who scrolled this far on principle:

- **`VibeSuperTonic.Core`.** A new platform-neutral assembly, shipped inside
  `engine\x64` and `engine\x86`, holding the text pipeline: offset mapping, the
  sanitizer, the pronunciation rule types, and the sentence chunker. It takes no
  NuGet dependencies and never will.
  - The launcher's hand-maintained *copy* of the pronunciation types is gone with
    it. There were two `Apply` implementations that were only ever *intended* to
    agree, and they compiled their regexes with different options — so the
    Pronunciations tab's **Test** pane could and did disagree with what the engine
    actually spoke. Both now run the same code.
  - Also the reason those offset bugs were found at all. Moving the code turned
    two silent arithmetic errors into 148 assertions.
- **Offsets are mapped, not computed.** `TextOffsetMap` stores one source index
  per rewritten character rather than a list of length deltas. Rules apply in
  sequence, each rewriting the previous one's output, and composing delta lists
  across passes is exactly where the subtle version of this bug lives. An array
  is obviously correct, and it is allocated only when a rule actually fires — text
  nothing touches, which is nearly all of it, allocates nothing.
- **The chunker reports its own spans.** `SentenceChunker.ChunkWithOffsets`
  returns each chunk with its position in the input, replacing the search that
  whitespace normalisation defeated. It leans on one invariant — the three passes
  only ever drop or substitute whitespace — which is pinned by a test over the
  same corpus, so a future change that breaks it fails loudly rather than
  mis-highlighting quietly.
- **Chunk boundaries are pinned to the previous implementation** by a reference
  oracle: a verbatim copy of the pre-move chunker, kept in the test project
  precisely so it can never be tidied up, run against a twelve-input corpus plus
  generated prose. Chunk seams are audible, so "it still chunks about the same"
  was not good enough.

## Upgrading

1. Drop the new ZIP over your existing install.
2. Run `VibeSuperTonic.exe` once if you want the registry's launcher path to
   refresh.

If you had customised **Max/Min chunk chars**, this is the release where those
values start taking effect — set them back to 200 and 100 to keep the old
pacing.

---

# VibeSuperTonic v0.2.2

*Still cooking — more in the oven.*

This release teaches the engine that "NOT" is not, in fact, an acronym; lets the Monitor tab count higher than one (yes, really); gives the GPU permission to fail gracefully instead of dragging your whole audio session down with it; and finally answers the question "how do I get this voice onto my phone?" with an **Export** tab that writes proper MP3 and AAC files.

Drop-in replacement for v0.2.1. No schema migration, no re-registration, no apology required.

---

## Added

- **Export tab.** A new tab between **Pronunciations** and **Benchmark** for converting text to a portable audio file you can send to a phone, tablet, or anything that plays MP3/AAC. Paste text, pick a voice, hit **Preview** to hear the first few seconds, then **Save…** to render the whole thing. Because this is an offline render, the defaults are pinned to *max quality*: TotalStep 16 (slider goes to 24), DirectML force-enabled regardless of the global GPU toggle, and the export's quality knobs don't touch your global Tune/SAPI defaults — they apply for the render and restore on completion. Pronunciation rules are honored automatically.
  - **Formats:** MP3 (up to 320 kbps) and AAC/.m4a (up to 192 kbps, the Microsoft AAC encoder's ceiling). Both are universally playable. Encoding goes through Media Foundation — zero extra binaries shipped.
  - **Chapter-length text supported.** Synthesis streams to a temp WAV and is transcoded on the fly; RAM stays bounded.
  - **GPU first, every time.** The engine's normal CPU fallback still kicks in if DirectML init genuinely can't proceed, but the export never *chooses* CPU on its own.
- **Pronunciations tab.** A new tab between **Tune** and **Benchmark** where you can tell the engine "say *this* instead of *that*" before the words ever reach the model. Per-rule **Whole word** and **Case** toggles, a master kill-switch, and a live **Test** pane so you can verify your fix before saving it. Rules persist to `<DataDir>\pronunciations.json` — share it, back it up, mail it to your friend with the same TTS quirks.
  - Stops the engine from spelling all-caps words letter by letter ("NOT" → set the replacement to `not!` and breathe a sigh of relief).
  - Handles glyphs the model has never heard of (`Bᵠ` → `B phi`).
  - Hot-reloaded on every sentence — no restart of the engine, the Control Panel, or your screen reader.

## Changed

- **The Monitor tab now shows every running engine, not just whichever one spoke last.** Telemetry moved from a single shared-memory slot to per-PID JSON files under `<DataDir>\sessions\`. Lingoes, Balabolka, NVDA, Word — all visible at once with their own live state.
- **The "Currently synthesizing" pane is no longer truncated at 4 KB.** A limit nobody asked for, finally retired alongside the shared-memory backend.
- **Reset selected** (Monitor tab) now writes a small sentinel file the engine watches for, so it can drop and rebuild its ONNX session on demand. The host process (Lingoes, etc.) does **not** need to restart.

## Fixed

- **A GPU hiccup no longer means audio death until restart.** When DirectML loses the device — Windows TDR, sleep/resume, the usual GPU drama — the engine used to politely report the error and then produce silence forever after. Now it dusts itself off, rebuilds the ONNX session, and retries the sentence. You hear a one-time blip; everything keeps working.
- **…unless the GPU is genuinely having a day.** A second device-loss inside 60 seconds flips a process-local latch that quietly rebuilds on CPU instead. Your saved settings stay GPU-on (a driver tantrum shouldn't downgrade your config); use **Reset selected** to give the GPU another shot once the driver has had its coffee. The Monitor tab's `LastError` column shows what happened.

## Upgrading

None of the work, all of the benefit:

1. Drop the new ZIP over your existing install (or use the pre-staged folder under `dist\release\VibeSuperTonic\`).
2. Run `VibeSuperTonic.exe` once if you want the registry's launcher path to refresh; otherwise, just keep going.

That's it. No `--unregister`, no UAC prompt, no clicking through migration dialogs.

---

## Under the hood

For the curious, and the people who scrolled this far on principle:

- **Pronunciations.** Rules are read by `PronunciationsCache` once per `pronunciations.json` mtime change, regexes pre-compiled, applied inside `BuildSpeakPlan` *before* chunking — so a rule can never be split across two chunks. Whole-word matching wraps the escaped match in `\b…\b`; .NET's regex treats Unicode letters (including U+1D60 modifier-letter small Greek phi) as word characters, so boundaries land correctly around exotic glyphs.
- **Export.** Two-stage pipeline. Stage 1 drives `SAPI.SpVoice` with its audio output bound to a `SAPI.SpFileStream` writing 44.1 kHz 16-bit mono WAV — the standard SAPI-to-file path, which means pronunciations, the engine's DirectML self-heal, and every other Speak-path behavior come along for free. Settings are mutated through `EngineSettingsRegistry` for the render and restored in a `finally` block (same pattern as Benchmark), so a crash mid-export never leaves your SAPI clients running at TotalStep 16. Stage 2 transcodes the WAV via Media Foundation's `SourceReader` → `SinkWriter` to MP3 (`MFAudioFormat_MP3`) or AAC (`MFAudioFormat_AAC`, raw payload type 0, AAC LC profile 0x29). Bitrates are clamped to the encoders' supported sets — the MS AAC encoder only accepts 96/128/160/192 kbps, so a request for 256 lands on 192. Zero NuGet adds: `MfApi.cs` declares the minimal COM interfaces (`IMFMediaType`, `IMFSourceReader`, `IMFSinkWriter`) with stub slots for the vtable methods we don't call.
- **Telemetry.** Per-PID JSON files at `<DataDir>\sessions\<pid>.json`, atomic tmp+rename per write. Mtime doubles as a liveness signal (>5 s old = host probably crashed). Graceful process exits delete their own file via `AppDomain.ProcessExit`.
- **DirectML self-heal.** Device-loss exceptions are caught in `SupertonicAdapter`, the shared session is disposed and rebuilt, and the failing synthesis retries once. A sliding window of loss timestamps drives the latch (threshold = 2 inside 60 s). Never persisted to disk.
- **Reset signal.** The launcher writes `<pid>.reset` next to the session file; the engine polls for it on each telemetry tick and deletes the marker after acting on it.

## Known quirk

SAPI word-boundary events (used by some screen readers to underline the spoken word) report offsets in the *original* source text. If a pronunciation rule changes the substituted text's length, the underline may land a few characters off the spoken word. Audio is unaffected — only the visual cursor tells a small lie.

---

## VibeSuperTonic v0.2.1

DSP-path overhaul. The phase vocoder shipped in v0.2.0 produced audible artifacts at higher DSP rates that no amount of parameter tuning could escape — five rounds of perceptual A/B converged on a configuration that was *the* best phase vocoder configuration for this material, and was still not good enough. This release swaps it out for **Sonic** (Bill Cook's BSD-licensed library, ported to pure C#), a pitch-synchronous overlap-add algorithm purpose-built for speech speedup. End result: the voice sounds dramatically closer to the original at 1.5×–2× DSP rate. Same SAPI surface, same install, same settings (minus the now-defunct `VocoderMode` knob).

This release is a drop-in replacement for `0.2.0`. **Everyone should re-run `VibeSuperTonic.exe` once after upgrading** — a one-shot schema migration (v4→v5) cleans the stale `VocoderMode` field out of your `settings.json`.

---

### Highlights (v0.2.1)

- **DSP path replaced: phase vocoder → Sonic (PSOLA)**. Each output pitch period is bit-perfect from the input, so the voice's spectral envelope (formants) is preserved sample-for-sample. No more "voice character changed" feeling at speed-up.
- **SIMD inner loop**. Sonic's AMDF pitch-detection inner loop (the hot path — ~225K abs-diff operations per pitch-period decision) is vectorized via `System.Numerics.Vector<int>` + `Vector.Widen` + `Vector.Abs`. 4–8× speedup on AVX2, 8–16× on AVX-512.
- **Source tree cleanup**. Phase vocoder, FFT, and the 28-mode `VocoderMode` registry knob are gone. `TimeStretch.cs` shrinks from ~500 lines to ~50 (thin wrapper over `Sonic`). Net deletion of ~700 lines.

---

### DSP (time-stretch)

#### Why the phase vocoder lost

Five rounds of user-driven perceptual A/B over the phase-vocoder configuration space:

- **Round 1**: 13 modes covering frame size, peak radius, locking on/off — winner was mode 14 (frame 2048, hop 256, radius 2, hard locking — 87.5% overlap version of Laroche-Dolson).
- **Round 2**: pushed overlap further (hop 128), tried large/small FFTs and soft Gaussian locking — saturation on overlap, distortion on the others.
- **Round 3**: peak-prominence filtering, transient-reset disable, low-magnitude bypass — the *transient reset detector was over-firing on voiced micro-onsets and adding artifacts*. Disabling it (mode 20) won.
- **Round 4**: combined "no transient reset" + "low-mag bypass" → mode 22 became the new top.
- **Round 5**: threshold sweeps below perceptual discrimination, peak tracking broke OLA continuity. Plateau confirmed.

The remaining "voice character changed" artifact was a fundamental phase-vocoder failure mode (formant smearing from bin-quantized FFT representation + harmonic-phase tracking). It is not fixable within the phase vocoder family.

#### Sonic — what changed

Pitch-synchronous overlap-add. For each detected pitch period P (via AMDF over the 65–400 Hz range covering all human speech):

- At speed *s* ≥ 2: emit one crossfaded block of length P/(s−1), consume P + P/(s−1) input samples → ratio 1/s.
- At 1 < *s* < 2: emit one crossfaded block of length P, consume 2P, then copy P·(2−s)/(s−1) input samples verbatim — interleaves compression with passthrough to land on ratio 1/s overall.
- Unvoiced regions (fricatives, breath, silence) produce a best-fit period; crossfading noise with noise is acoustically benign.

Why this beats phase vocoder for speech:

- No bin-quantized frequency representation → no inter-bin phasing.
- Each output pitch period is bit-perfect from the input → formants stay put.
- No phase-tracking math that drifts across frames.
- No analysis frame size to smear transients across.

#### SIMD pitch detection

`Sonic.AmdfDiff` widens int16 samples to int32 (`Vector<short>` arithmetic wraps modulo 65536 — would corrupt diffs above ±32767), then does abs-difference and accumulation in `Vector<int>`. JIT picks the widest SIMD register available (SSE2: 4 lanes, AVX2: 8 lanes, AVX-512: 16 lanes). Period range is 110–678 samples at 44.1 kHz, so the inner loop dwarfs the tail handler.

---

### Schema migration (v4 → v5)

- Removes the `VocoderMode` property from `EngineSettings` (was used to select among phase-vocoder variants; meaningless now). Existing `settings.json` files with `VocoderMode` and `PerVoice[*].VocoderMode` deserialize harmlessly — the field is ignored on read and dropped on next write.
- `EnsureMigrated` triggers one such write on first launch, so the user's `settings.json` sheds the stale field without waiting for a Tune-tab edit.

No other settings change. No re-registration needed. SAPI clients keep seeing the same 10 voices, same CLSID, same registry layout.

---

### Removed (v0.2.1)

- `Synth/TimeStretch.cs`: phase vocoder family (28 modes, transient detection, peak locking, low-magnitude bypass). Replaced with a ~50-line wrapper around `Sonic`.
- `Synth/Fft.cs`: in-house radix-2 Cooley-Tukey FFT. No longer used.
- `EngineSettings.VocoderMode`: the knob that selected among phase-vocoder configurations. Gone from the schema, the registry parser, the CLI `--set` parser, the Tune tab, and the launcher's preserve-on-save logic.
- Tune tab dropdown for vocoder mode (only briefly visible during the round-1 A/B work; dropped before this release).

---

### Added (v0.2.1)

- `Synth/Sonic.cs`: ~200-line port of Bill Cook's Sonic, single-stream mono int16, both speedup and slowdown branches. BSD license preserved (Cook's original C source: [waywardgeek/sonic](https://github.com/waywardgeek/sonic)).
- SIMD AMDF via `System.Numerics.Vector<int>`.

---

### Bug fixes (carried forward from v0.2.0)

See the v0.2.0 section below for the long-standing audio-buffer-cut hardening, telemetry timing, and Restart-Manager lock-probe fixes. v0.2.1 introduces no regressions in those areas.

---

## VibeSuperTonic v0.2.0

First non-spike release. The original CLI Launcher has grown into a full-featured Control Panel; the engine has user-tunable knobs, GPU acceleration, and a phase-vocoder DSP path; and the SAPI handoff is hardened against the audio-buffer-cut bugs that would clip the last word of long passages.

This release replaces the `0.1.0-spike` build entirely. **Everyone should re-run `VibeSuperTonic.exe` once after upgrading** — a one-shot schema migration runs on first launch (clears stale per-voice overrides, enables GPU by default, locks engine speed at 1.0×).

---

### Highlights (v0.2.0)

- **Control Panel GUI** replaces the CLI Launcher. Six tabs: Status, Tune, Benchmark, Monitor, Advanced, About.
- **GPU acceleration** via DirectML — opt-in, falls back to CPU automatically. Typical 2-5× speedup on iGPU, more on dGPU.
- **Phase-locked phase vocoder** for time-stretch — replaces WSOLA. Cleanly handles 0.5×–2× without the robotic / dropped-syllable artifacts the old algorithm had. *(Note: superseded by Sonic in v0.2.1.)*
- **User-tunable engine knobs** (totalStep, DSP rate, volume trim, chunking, ONNX threads…) exposed via the Tune and Advanced tabs and the registry-backed settings system.
- **Live engine telemetry** in the Monitor tab — current voice, RTF, latency, CPU%, RAM, dropped-frame counter, all updated at 5 Hz.
- **Self-fixing install** — Status tab runs 12 integrity checks (registry, model files, .NET runtime, voice tokens) with one-click Repair.

---

### Control Panel

#### New tabs

- **Status** — green/red checklist of registry and file-system integrity, with per-row Repair buttons. Identifies which processes are holding engine DLLs (Restart Manager) so you know what to close before re-installing.
- **Tune** — quality presets (Draft / Balanced / Quality / HiFi), DSP rate (0.5×–2.0×), volume trim, default voice. Per-voice overrides via a scope dropdown.
- **Benchmark** — A/B-compare presets on a passage of text. Shows RTF, CPU %, peak RAM, and a green/yellow/red verdict per preset, with an inline guide explaining what each metric means.
- **Monitor** — live engine telemetry via shared-memory ring buffer.
- **Advanced** — Tier B knobs (chunking, inter-chunk silence, rate-clamp, ONNX threads), GPU toggle, "Danger zone" with Unregister.
- **About** — version, runtime info, project link.

#### Self-fixing install flow

- Manifest-driven model downloader with SHA-256 verification (`models-manifest.json`).
- Lock-aware repairs: refuses to overwrite engine DLLs while a SAPI client is using them; tells you exactly which process to close.
- Schema migration on launch — cleans stale settings from earlier builds without surprising the user.

#### CLI preserved

- `VibeSuperTonic.exe --register` — silent install (UAC if needed)
- `VibeSuperTonic.exe --unregister` — clean uninstall
- `VibeSuperTonic.exe --repair` — runs the full integrity-check pass headless
- `VibeSuperTonic.exe --bench` — JSON-output benchmark
- `VibeSuperTonic.exe --set <key>=<value>` — script any single knob

---

### Engine

#### GPU acceleration (DirectML)

- ONNX Runtime DirectML provider, opt-in via Advanced tab. **GPU is the new default** for fresh installs; existing installs are flipped on by the v1→v2 schema migration.
- Adds ~30 MB to the engine folder (DirectML.dll). Falls back to CPU automatically if DirectML init fails (no DX12 GPU, driver too old, etc.).
- Adapter selection: due to inconsistent device-ID mapping between DXGI's `EnumAdapterByGpuPreference` and ORT's DirectML provider on some systems, the in-app adapter picker has been removed. To choose iGPU vs dGPU on a multi-adapter machine, configure **Windows Settings → System → Display → Graphics → Add VibeSuperTonic.exe → "High performance" / "Power saving"**.

#### CPU performance

- ORT SessionOptions tuned: `GraphOptimizationLevel.ORT_ENABLE_ALL`, sequential execution mode, explicit inter-op = 1.
- **Optimized-graph cache**: ORT writes its post-fusion graph to `models/onnx-optimized/` on first run. Subsequent cold starts skip the optimization pass entirely (saves 1–3 s per session load).
- ONNX intra-op and inter-op thread counts are user-tunable (Advanced tab).
- WSOLA / phase-vocoder correlation hot loops vectorized with `System.Numerics.Vector<float>` (auto-selects AVX2 / AVX-512 / NEON).

#### Settings system

- New registry-backed settings tree at `HKCU\SOFTWARE\VibeSuperTonic\Settings\` with monotonic version counter.
- Engine reads settings from a cached snapshot, invalidated only when the version DWORD bumps. Per-`Speak` cost is microseconds (one registry DWORD read).
- Per-voice override support: any knob can be overridden for a specific voice token. Auto-pruning when overrides match global defaults.
- Schema-aware migrations: each launcher build runs forward-only migrations (v0→v1 prune, v1→v2 GPU default + clear stale per-voice, v2→v3 force `engineSpeed=1.0`).

#### Telemetry (v0.2.0)

- Engine writes a snapshot to `Local\VibeSuperTonic.Telemetry` shared memory every chunk: voice, current text fragment, resolved knob values, first-byte latency, rolling RTF, pipeline depth, inter-chunk gap, CPU %, RSS, ONNX threads, underrun counter, last error.
- Monitor tab reads this at 5 Hz when active; otherwise the timer pauses.

---

### DSP (time-stretch — v0.2.0 implementation)

The DSP path is the most user-visible change in this release.

#### Phase-locked phase vocoder

- Replaces the WSOLA implementation, which dropped trailing syllables at chunk boundaries and produced robotic / metallic artifacts above 1.5× stretch.
- Operates entirely in the frequency domain (STFT → modify magnitudes / phases → ISTFT → overlap-add). No frame-similarity search, no boundary anchor, no robotic / metallic artifacts.
- **Phase locking** (Laroche-Dolson): identifies spectral peaks (typically the fundamental + its harmonics in voiced speech) and locks surrounding bins' phases to the peak's phase + the original input phase offset. Eliminates the "phasy/reverby" distortion that basic phase vocoders produce on speech.
- Frame size 2048 (~46 ms at 44.1 kHz), synth hop 512 (75% overlap, COLA-clean), peak detection radius 3 (chosen after A/B testing on speech).
- A custom radix-2 Cooley-Tukey FFT (`Synth/Fft.cs`) keeps the engine self-contained — no FFT NuGet dependency.

#### Engine speed locked at 1.0×

- The Supertonic model's duration predictor under-renders the trailing phoneme at engine speeds > 1.0×, manifesting as the last word being clipped on every chunk.
- The engine speed slider is now disabled at 1.0× and the v2→v3 migration forces existing installs to 1.0×.
- All speedup goes through the DSP rate, which the new vocoder handles cleanly across the full 0.5×–2.0× range.

#### SAPI audio handoff hardening

A long-standing class of bugs where the last word of long passages would intermittently drop is now fixed. Three changes work together:

- **Real-time write throttle** in `StreamPcm`: writes to SAPI are paced so the buffer never holds more than ~100 ms of audio. Without this, we'd hand SAPI 5+ seconds of PCM in tens of ms; the audio device pulled at real-time and couldn't catch up before SAPI's "done" signal.
- **Tail-flush silence (700 ms)** before signaling end-of-stream — pads the audio so any buffer-cut from the SAPI client lands in silence, not in the last word.
- **Drain wait** at end of `Speak`: don't return until elapsed wall-clock time ≥ total audio duration since the first SAPI Write. Floor at 500 ms.

---

### Settings & defaults

- **`engineSpeed=1.0`** locked (was per-preset varying). All speedup via DSP.
- **Default preset = Balanced** (totalStep = 6, was 4).
- **Draft preset totalStep = 4** (was 2 — too aggressive, caused syllable drops).
- **Balanced preset totalStep = 6** (was 4).
- **`UseDirectML=true`** by default (was false). Falls back to CPU automatically.

---

### Bug fixes (v0.2.0)

- Phase-1 launcher CLI works again from PowerShell — `VibeSuperTonic.exe --register` now writes its log to the parent console (was silent because of the WinExe regression).
- `MonitorTab.Refresh()` no longer overrides `Control.Refresh()`; renamed to `RefreshTelemetry()`.
- `LockProbe` Restart Manager session-key uses `StringBuilder` (correct out-buffer marshaling).
- `BenchmarkTab` always restores prior settings on cancel/error (was leaking the benchmark's preset overrides).
- `Benchmark` no longer relies on `voice.Status.RealtimePosition` (which it polled too eagerly, exiting before audio actually started).
- `MonitorTab` timer paused when the tab isn't visible; Restart Manager probe throttled to 0.2 Hz from 5 Hz.
- `EngineSettings.Save` is auto-pruning: per-voice entries that exactly match global are dropped to prevent the "stale snapshot shadowing the global" class of bugs.
- `Repair` UX: per-row Repair buttons disable the list during operation, preventing concurrent registry mutations.
- `BenchmarkTab` cancellation token sources properly disposed across runs.
- `TimeStretch` tail handling no longer drops the trailing 30–80 ms of every chunk at high stretch factors.
- GUI window default size raised to 980×740 (was 760×600); per-voice scope dropdown widened so full voice names show.
- About-tab Unregister button moved into the Advanced tab's Danger Zone, where destructive actions belong.

---

### Project links

- Repo: [Hananel-Hazan/VibeSuperTonic](https://github.com/Hananel-Hazan/VibeSuperTonic)
- Phase plan: see `lets-jump-to-phase-recursive-newell.md` (kept for historical reference; this release implements all phases through 3)

---

### Migration notes (upgrading from 0.1.0-spike)

1. Close every SAPI client (Lingoes, Balabolka, NVDA, Narrator, etc.) and any old `VibeSuperTonic.exe`.
2. Replace the entire folder with the new ZIP contents (or run `pack-zip.ps1` from this commit).
3. Run `VibeSuperTonic.exe` once. The schema migration runs silently:
   - All per-voice overrides are cleared (legacy snapshots can shadow global with stale values).
   - `UseDirectML` is set to `true` (engine falls back to CPU if DML init fails).
   - `EngineSpeed` is forced to `1.0`.
4. Open the Status tab; click **Repair All** if anything is red.
5. (Optional) Open Windows Settings → System → Display → Graphics → Add `VibeSuperTonic.exe` → set **High performance** if you want the dGPU on a multi-adapter machine.

No breaking changes to SAPI clients — they continue to see 10 voices (M1–M5, F1–F5) registered identically.

---

### Known limitations (v0.2.0 — see v0.2.1 for updates)

- DSP rate >2.0× is clamped — phase vocoder quality degrades sharply past that. *(v0.2.1: same cap, different reason — Sonic's crossfade quality degrades past 2×.)*
- DirectML adapter selection is "the one Windows picks" — multi-adapter laptops use Windows Graphics Settings to choose, not an in-app picker.
- First synthesis after a cold start takes 2–5 s (ONNX model load, ~380 MB) on CPU; less on GPU. Subsequent syntheses are fast.
- The Supertonic model has no pitch-control parameter; pitch follows the model's voice embedding. SSML `<prosody pitch>` is ignored.
- Phoneme overrides (`<phoneme>`) are not supported by Supertonic.

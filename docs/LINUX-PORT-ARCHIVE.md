# VibeSuperTonic on Mint — port archive

**You probably do not need to read this.** It is the completed record behind
[LINUX-PORT-PLAN.md](LINUX-PORT-PLAN.md): the build notes, measurements and
defect accounts for Phases 0–5, the full review write-ups, and two superseded
designs. The plan carries every conclusion that still binds future work; this
file carries the evidence for them.

Read a section here only when one of these is true:

- **A measurement in the plan is being questioned.** The numbers and the
  conditions they were taken under are here.
- **You are about to change something a completed phase built**, and want to
  know what it cost to get right the first time. Every phase record ends with a
  "N things this phase found" list, and those are the expensive parts.
- **You are taking [Way 3](#way-3) or [the Windows convergence]**, both of which
  are deferred in the plan and reasoned about here.
- **You want the full text of a review item (R-1 … R-16).** The plan keeps a
  one-line statement of each constraint that is still live; the reasoning is
  here.

Nothing here is a to-do. Everything below is done, superseded, or deliberately
not being done.

---

<a name="landing"></a>

## The landing — 2026-08-16

The plan warned for two days about an uncommitted 47-path working tree. This is
what closing it looked like, kept because the lessons transfer to any large
landing in this repository.

### Read this first: it all landed, and CI is green

**Done 2026-08-16.** The 47-path working tree this section used to warn about is
committed and pushed. `COMMIT-PLAN.md`, `TODO-TOMORROW.md` and
`commit-tomorrow.sh` did their job and were deleted with it; nothing references
them any more and links to them in this document have been removed.

```
ae8e271  Fix .gitattributes and .gitignore so git sees this tree correctly
5c72942  Add VibeSuperTonic.Core, the CPU backend, and the Linux daemon
88e2c88  Move both hosts onto Core, and fix what that surfaced
```

Seven more commits followed the same day: the Phase 5 keybindings script, a CI
fix, the selection-staleness work with `spike/x11-freshness`, the Phase 4
application pass, the CPU budget, and the Phase 8 write-up. `Dev` is pushed and
level with `origin/Dev`.

**CI has now run, and both jobs pass** — `windows-latest` (Core.Tests on
Windows, engine x64 + x86 publishes, the win-x86 `onnxruntime.dll` assertion,
release ZIP) and `ubuntu-latest` (Linux solution, Core.Tests, NativeAOT publish
of `vst-ctl` with the native-ELF assertion). See [trap 9](LINUX-PORT-PLAN.md#where-to-pick-up) for
what that clears and the one thing it caught.

Four lessons from the landing are worth keeping, because each cost real time and
none is visible from the result:

- **`.gitattributes` had to be first, and alone.** The tree was CRLF while HEAD
  was LF, so without it `git status` reported 47 changed files where 13 were
  real, burying every change under a whole-tree whitespace rewrite and
  destroying `git blame` for the engine. Anyone re-normalising line endings in
  this repository again should do the same thing: one commit, nothing else in
  it.
- **Three commits, not nine, and the reason is mechanical.** Git stages whole
  files, and several files carried two changes belonging to different commits
  (`Registration.cs` — a Core type and the new runtime probes; `Checks.cs` —
  Core's downloader and the four prerequisite checks; `SapiEngine.cs` — the
  offset fixes and the Core switch). A finer split needs `git add -p`, and the
  nine-commit draft would additionally have staged none of the seven files the
  move into Core *deleted*, leaving two definitions of `Sonic` and
  `SupertonicLanguages` in scope for both hosts.
- **A landing script must build each *commit*, not the working tree.** The
  first `--verify` did the latter, which does not change while the script runs:
  it produced the same answer three times and proved nothing. Rebuilt to check
  out each commit into a throwaway `git worktree` and build and test there — and
  that is what found the real defect, that `Linux.Audio`, `Daemon` and `Ctl`
  appeared in none of the three commits while `VibeSuperTonic.linux.slnx`, which
  names all three, was staged in commit 2.

  The guard that made that class of mistake impossible is worth reusing: a
  `check_complete` pass that **refuses to start unless every changed path
  appears in a step list or a declared holdout**, so the next directory anyone
  adds produces a loud failure rather than a silent omission.
- **There was no git identity on this machine**, local or global, so the first
  rehearsal died on commit 1 with a half-staged index. If a future script
  commits on this box, precheck it.

**Rehearsing a landing against a throwaway clone found three defects that
reading the script did not.** That is the transferable part.

---

<a name="phases"></a>

## Phases 0–5 — the build record

Dependency-ordered, as built. Phase 0 was a gate: everything after it assumes
its result. Each review finding was folded in as concrete work, tagged with its
ID.

<a name="phase-0"></a>

### Phase 0 — Prove the model runs · 0.5 day · **GATE**

**Goal.** Verify ONNX Runtime CPU renders Supertonic fast enough on linux-x64.
There is no DirectML on Linux, so CPU is the baseline case, not the fallback.

**Work.** Minimal console app referencing `Microsoft.ML.OnnxRuntime` 1.22.1 (CPU
package) plus `SupertonicSdk.cs` unchanged. Publish `-r linux-x64
--self-contained`, multi-file. Render a fixed sentence to WAV, play with
`paplay`. Report cold load time, RTF at totalStep 4 / 8 / 12, peak RSS, and the
effect of `OnnxThreads`.

**Exit criteria.** RTF ≤ 0.5 at totalStep 8. At 0.5 the synthesis pipeline stays
comfortably ahead of playback on a desktop that is also doing other things.

**Status — PASSED 2026-08-15.** Gate cleared on the Mint box itself (Mint 22.3
Zena, Cinnamon, X11, 20 logical CPUs, PipeWire's pulse server). See
[spike/phase0-linux/](../spike/phase0-linux/).

- ✅ `SupertonicSdk.cs` compiles and links against the **CPU-only** ORT package
  for `linux-x64` — but only after a real blocker was removed, see [R-13](#r-13).
- ✅ Self-contained multi-file `linux-x64` publish produces `libonnxruntime.so`
  and a working native host. 111 MB expanded, 46 MB packed. Runs on stock Mint
  with no .NET installed.
- ✅ **RTF 0.193 at totalStep 8 on Linux** (warm), against a gate of ≤ 0.5.
  0.102 at step 4, 0.278 at step 12. Load 0.43 s warm / 1.00 s cold.
- ✅ Manifest-driven model download works unmodified on Linux — all 16 entries,
  383 MB, first mirror, no fallbacks needed.
- ✅ Output is 44100 Hz mono s16le, 12.82 s, peak 0.46 FS, zero clipped samples
  — the engine's native format, so Phase 2's sink needs no resampling.
- ✅ **Audible, intelligible speech confirmed by ear**, and the render verified
  against the Windows output structurally — see the audio note below. The gate
  is closed with nothing outstanding.

| | Windows (warm) | Linux (warm) |
| --- | --- | --- |
| RTF @ step 4 | 0.130 | **0.102** |
| RTF @ step 8 | 0.252 | **0.193** |
| RTF @ step 12 | 0.356 | **0.278** |
| Load cold / warm | 3.35 s / 0.58 s | **1.00 s / 0.43 s** |
| Peak RSS cold / warm | 1154 MB / 642 MB | 1182 MB / **830 MB** |

Linux is **~25% faster than Windows on the same machine** at every step count,
and loads substantially quicker. The contingency ladder for RTF 0.5–1.0 (drop to
totalStep 4, raise `MinChunkChars`) is not needed; neither is quantization.
There is enough headroom that totalStep 12 would also be viable if quality
warrants it.

**Also measured: intra-op threads are not free — and Linux punishes them
harder.** At totalStep 8:

| `OnnxThreads` | Windows RTF | Linux RTF |
| --- | --- | --- |
| 0 (auto) | 0.248 | **0.189** |
| 10 (half the cores) | 0.240 | 0.393 |
| 20 (all cores) | 0.484 | 0.433 |

On Windows, half-the-cores was a wash with auto and only full pinning hurt. On
Linux **any** manual value is roughly 2× worse than letting ORT choose — even
the setting that was harmless on Windows. `OnnxThreads` defaults to 0 (auto),
which is right on both. The Linux Tune tab should not offer it as a knob at all;
the Windows one should keep its existing warning.

**✅ Audio confirmed 2026-08-15 — the last open item is closed.** The earlier
run of `paplay phase0-out.wav` exited 0 but was inaudible. Re-checked the same
day: a reference WAV and the Phase 0 render were played back to back to the
default sink and **both were heard as speech**. Nothing in the chain was
misconfigured — the machine has exactly one sink
(`alsa_output.pci-0000_00_1f.3.analog-stereo`, unmuted, 71%), its active port is
`analog-output-speaker`, and the headphones port reports *not available*. The
original silence was the audio arriving at the built-in speakers while nobody
was listening to them. No fix was required and none was made.

That was the cheap half. The expensive half was the plan's own objection — *a
fast render of garbage would still pass every number above* — which peak, RMS
and duration genuinely cannot answer. It was settled by comparing the Linux
render against the Windows render of the same text, voice and totalStep:

| Measure | Result |
| --- | --- |
| Envelope correlation, 500 ms windows | **0.93** (0.88 at 250 ms) |
| Pause structure (runs ≥ 120 ms below 3% peak) | **5 of 5** Linux pauses match a Windows pause within 300 ms |
| Amplitude-modulation rate | 3.59 Hz Linux / 3.67 Hz Windows — natural syllable range |
| Clipped samples | 0, peak −7.1 dBFS |

Correlation *rises* with window width (0.32 at 20 ms → 0.93 at 500 ms), which is
the signature of the same utterance carrying a different noise realization from
the stochastic sampler — not of two different renders. Prosody, sentence
boundaries and total duration are identical across platforms.

**Worth keeping: this is a render-sanity test that needs no ears.** Pause-position
alignment plus modulation rate distinguishes speech from plausible-looking
garbage, runs in a second with no dependencies, and would catch a silent
regression in the chunker or the vocoder that RTF and peak amplitude cannot.
Phase 1 should fold it into `Core.Tests` against a committed reference envelope
rather than a committed WAV.

**Contingencies.**

- *Single-file publish crashes ONNX* → publish self-contained **multi-file**.
  This exact failure already bit us on Windows and produced `RenderHost`. The
  portable-folder model wants a directory anyway, so multi-file is the default
  here, not the fallback.
- *RTF between 0.5 and 1.0* → drop the Linux default to totalStep 4, raise
  `MinChunkChars` to give the pipeline a longer lead, surface the tradeoff in
  the Tune tab.
- *RTF > 1.0* → **stop and decide before Phase 1.** Options in order: int8
  dynamic quantization of `vector_estimator.onnx` (the diffusion loop dominates
  cost), CUDA as an optional path for NVIDIA users, or reconsider the port.

<a name="phase-1"></a>

### Phase 1 — Extract `VibeSuperTonic.Core` · 2–3 days

**Goal.** One library both hosts share. This is a Windows refactor that happens
to enable Linux — worth doing on its own merits, and it carries a real bug fix.

**Status — slice 1 of 2 landed 2026-08-15, not yet shipped.** The R-2 fix is in,
with tests, as the smallest slice that stands the layout up end to end. Taking
it first was deliberate: the plan wanted R-2 shipped as its own bisectable patch
*before* the extraction, while R-3 wanted its regression test the day the fix
shipped — and `Core.Tests` did not exist until the extraction. Making R-2 the
first slice of Core satisfies both, and proves the Way 2 layout against two
files instead of twelve.

Landed:

- `VibeSuperTonic.Core` (`net10.0`, `VstPortable=true`, **zero** PackageReferences)
  with `TextOffsetMap`, `TextSanitizer`, `PronunciationsConfig`/`PronunciationRule`,
  and `SynthTextPipeline`.
- `VibeSuperTonic.Core.Tests` — **48 tests, green on Linux**, no native
  dependencies. Two of them failed on first run against a real bug in
  `TextOffsetMap.ToSource`, which short-circuited index 0 to source 0 and so
  reported the wrong offset whenever the *first* character was displaced.
- R-2 fixed at both sites: chunk construction no longer adds a rewritten-space
  index to a source-space offset, and word/sentence boundaries resolve through
  the map. Boundary *lengths* are now reported in source characters too, so
  "kilograms" highlights the two characters the user typed.
- The launcher's hand-maintained mirror of the pronunciation types is gone; the
  Test pane and the engine now run the same code. They previously had two
  `Apply` implementations that were only *intended* to agree — and compiled
  their regexes with different options.
- Engine x64 + x86, Launcher, RenderHost and TestHarness all build clean.

**Validated on Windows 2026-08-15.** 0.2.6 installed on a Win11 VM from the
Linux-built ZIP: all 10 voice tokens registered, both COM hosts resolved, 16
models fetched, and — after the .NET 10 Desktop Runtime was installed — speech
works through a real SAPI client. The engine is framework-dependent by design,
so the runtime is a prerequisite, not a defect; it is the one thing the portable
folder cannot carry. Worth noting that this is exactly what the Linux build
avoids by shipping self-contained, and worth asking later whether the Windows
engine should do the same.

**Slice 2 in progress — the backend split works end to end on Linux.**

- `SentenceChunker` moved into Core, pinned to the old implementation by a
  reference-oracle test over a 12-input corpus plus generated prose. Chunk
  boundaries are audible, so "it still chunks about the same" was not good
  enough.
- `ISynthesizer` + `SynthesisOptions` in Core, and `VibeSuperTonic.Onnx.Cpu`
  implementing it against the CPU ORT package, compiling `SupertonicSdk.cs` as
  linked source.
- `AudioBuffer` / `WavWriter` in Core — PCM conversion and a mono 16-bit writer
  both hosts need.
- [spike/linux-render/](../spike/linux-render/) renders through the real seam
  (Core chunker → `ISynthesizer` → Core WAV writer) with **nothing from the
  Windows engine referenced**. Measured 2026-08-15: load 0.56 s, **RTF 0.221–0.338
  at totalStep 8**, 12.89 s of audible, correct speech.
- Core.Tests now 90 tests, green.

**Three corrections to this plan, found by building it:**

1. **`SupertonicAdapter` does not move to shared code.** The plan assumed it went
   to `src/shared/` alongside the SDK. It is ~600 lines of which the great
   majority is DirectML device-loss recovery — TDR detection, the process-local
   GPU latch, the launcher reset channel, the CPU-retry path — plus a registry
   read. That is precisely the dead weight [R-12](#r-12) exists to keep out. What
   was genuinely shared inside it was the chunker and the PCM conversion, both
   now in Core; the rest stays Windows-side. `CpuSynthesizer` is what remains
   when it is removed, and it is a fraction of the size.
2. **The backends are named for the execution provider, not the OS.**
   `Onnx.Cpu` and `Onnx.DirectML`, not `Onnx.Linux`/`Onnx.Windows` — the CPU
   backend runs perfectly well on Windows, it simply has no DirectML. The
   provider is the axis that actually differs.
3. **`src/shared/` is deferred.** Linking `SupertonicSdk.cs` from its current
   location achieves the split today; moving a 990-line vendored file buys
   nothing until the Windows adapter is split too, and linked source is already
   the established pattern here (the launcher and the Phase 0 spike both use it).

**New finding for [R-8](#r-8): a terminated ONNX run does not report as
cancellation.** Setting `RunOptions.Terminate` makes ORT throw
`OnnxRuntimeException: [ErrorCode:Fail] Exiting due to terminate flag being set
to true`, not `OperationCanceledException`. Left alone, every stop would surface
in the daemon's log as an engine fault — and with one key doing both, stop is the
most-travelled path there is. `CpuSynthesizer` translates it at the seam.
Measured: cancellation requested at 150 ms, `OperationCanceledException` observed
at **157 ms**, so inference is interrupted about 7 ms after the ask. That is the
inference half of Phase 3's 100 ms stop budget, comfortably.

**Unrelated defect surfaced while moving the chunker: `MaxChunkChars` and
`MinChunkChars` do nothing.** Both are persisted settings with sliders in the
launcher's Advanced tab and per-voice overrides in Tune, and the chunker has
always read private constants instead. `SentenceChunker.Chunk` now takes them as
parameters, but callers still pass nothing: making a dead setting live would
silently re-chunk every install whose `settings.json` already holds a non-default
value. Wiring it through is a one-line change and a deliberate decision, not a
tidy-up. **Still undecided** — see [Open decisions](LINUX-PORT-PLAN.md#open-decisions).

**Review pass 2026-08-15 — three defects found and fixed, none of them Linux.**
The slice built and its 90 tests passed; these were found by reading the seam
against what the phases after it will ask of it.

- **[R-14](#r-14) — chunk start offsets were recovered with `IndexOf` and missed
  on any text with a newline, a tab, or a double space between sentences.** Same
  family as R-2, one layer up, and it survived the R-2 fix because R-2 was about
  the *map* while this is about the *index handed to* the map. Fixed with
  `SentenceChunker.ChunkWithOffsets`, which reports each chunk's true span.
- **`CpuSynthesizer.Dispose` could free ONNX sessions with a `Run` still
  executing on them**, because the session was captured outside the lock and used
  after — the exact TOCTOU
  [SupertonicAdapter](../src/VibeSuperTonic.Engine/Synth/SupertonicAdapter.cs)
  documents having walked into once on the Windows side. It surfaces as a native
  access violation, not an exception, so it takes the daemon down rather than
  logging. Not hypothetical for Phase 3: releasing the session on an idle timeout
  is an open decision below, and stop is the most-travelled path in the product.
  Dispose now closes the door under the lock and drains in-flight renders before
  releasing anything.
- **`SupertonicAdapter.FloatToInt16` was a character-for-character duplicate of
  `AudioBuffer.FloatToPcm16`.** Two copies of the clamp-and-scale is precisely the
  drift Core exists to prevent — the platforms would have rendered the same model
  to different PCM the first time either changed rounding or clipping. The adapter
  now calls Core.

Re-verified after all three: engine x64 + x86, launcher, RenderHost and
TestHarness build clean; Core.Tests 135 green; `spike/linux-render` still renders
through the real seam at RTF 0.207 with cancellation observed at 161 ms.

**`MaxChunkChars` / `MinChunkChars` are now live — decided 2026-08-15.** The
settings are read by the chunker for the first time. Defaults are byte-identical
to the constants they replace (200 / 100) and a regression test pins that, so an
install that never touched the sliders chunks exactly as every prior release
did. An install that *did* will re-chunk, and chunk seams are audible — that is
a known, accepted consequence, called out in the release notes rather than
discovered by a user. Values are clamped at the point of use: the two sliders'
ranges overlap so `min > max` was always selectable, and the settings-file import
path range-checks nothing at all.

**Shipped 2026-08-15 and validated through real SAPI clients**, ending at
`dist/VibeSuperTonic-0.2.7.3-win.zip`. Speech works on the Win11 VM through both a
64-bit host (the Control Panel's own `SAPI.SpVoice` test) and 32-bit hosts
(Balabolka, Lingoes) — the first time any SAPI client has spoken through the Core
layout. Phase 1's exit criterion is met but for the *survives real daily use*
half, which is a matter of time rather than of work.

**Getting there took three hotfixes, none of them about speech**, and the pattern
is structural rather than bad luck. The engine is a COM in-process server, so it
inherits its host's bitness and every native dependency must exist *twice* — and
the 32-bit half is the half no current machine has by default. It cannot carry
its own copies either: `dotnet publish --self-contained` with `EnableComHosting`
fails outright with `NETSDK1128`.

| Release | Missing piece | How it presented |
| --- | --- | --- |
| 0.2.7.1 | .NET 10 runtime (x86) | no voices listed anywhere, and the Status tab reported success |
| 0.2.7.2 | *(the warning itself)* | told a user who had just installed it that it was missing |
| 0.2.7.3 | Visual C++ runtime (x86) | voices listed and **silent**; `0x8007007E` naming a file that was present |

Each was detectable before the user hit it, and each was found by reading their
log rather than by enumerating the dependency chain once. The Status tab now
checks all four prerequisites and can install any of them.

**The lesson lands on Phase 7.** The Linux daemon ships self-contained precisely
because it is *not* an in-process component — the one property that makes this
whole class of failure impossible there. Worth protecting when packaging, and
worth remembering if a speech-dispatcher module is ever built: that would put us
back inside somebody else's process.

**R-2 verified, R-14 verified after a second fix — 2026-08-15.** The TestHarness
now drives a real SAPI client and checks reported offsets against independently
computed word starts (steps 8 and 9, shipped in `tools/` from 0.2.7.4). R-2
passed first time, including the length check: a boundary on "kilograms" reports
2 characters, the "kg" the user typed.

**R-14 failed on first run, and the failure was real.** The 0.2.7 fix corrected
each chunk's *start* offset but not the whitespace the chunker collapses *inside*
a chunk, so callers adding an index-within-chunk to that start were right only
while every separator was one character. Single space, newline and tab passed;
double space and paragraph break drifted one character per separator. Fixed in
0.2.7.5 by having `ChunkWithOffsets` return a per-character map and composing it
with the pronunciation map in the engine.

Two things worth carrying forward. **Core.Tests could not have caught this** —
it tested the chunk map in isolation, and every unit-level assertion was true;
the defect only existed at the composition point in `SapiEngine`. And **the audio
is identical either way**, so no amount of listening would have found it. It took
a real client reporting real offsets, which is the entire argument for shipping
the harness rather than keeping it in the source tree.

**Slice 3 landed 2026-08-15 — the portable-file list is done.** Core now holds
fourteen files across Audio, Diagnostics, Models, Synthesis, Telemetry and Text.
Everything builds (engine x64 + x86, launcher, RenderHost, TestHarness) and
Core.Tests is **187 green** (see the render-sanity note below).

- `Sonic` and `TimeStretch` → `Core.Audio`, with the DSP coverage R-3 asked for:
  output length at 0.5×/0.75×/1.25×/1.5×/2.0×, energy preservation, and the
  pass-through cases. Three implementations of time-stretch have been judged by
  ear here; ears do not catch a length regression, and a length regression is
  what desynchronises word-boundary events from the audio.
- `SupertonicLanguages` → `Core.Synthesis`, `LogRotation` → `Core.Diagnostics`.
  Both were previously `<Compile Include>`-linked into the launcher; the links
  are gone now that Core is a real reference.
- `Manifest` and `ModelDownloader` → `Core.Models`. The only thing keeping them
  out was a direct call to `LockProbe` (Restart Manager), now an injected
  `FileLockDescriber` the launcher supplies and Linux simply omits — replacing an
  open file is legal there. The duplication was already costing: the Phase 0
  spike reimplemented manifest parsing and downloading from scratch, ~90 lines,
  precisely because it could not take the launcher's copy.
- The telemetry **DTO** → `Core.Telemetry`, with its serializer context. It
  existed twice — `SessionSnapshotDto` in the engine, `TelemetrySnapshot` in the
  launcher — twenty identical properties and a comment on each telling the reader
  to keep them in sync by hand. The same arrangement the pronunciation types had,
  failing the same way.

**Correction: `TelemetryWriter` should not move, and this plan was wrong to list
it.** Its entire design serves *N* SAPI hosts loading the engine at once: one
file per PID, liveness inferred from mtime, a reset marker polled per process.
The Linux daemon is a single process, and this plan already says so — Phase 6
collapses Monitor into Status because "with one daemon instead of N SAPI hosts,
per-host monitoring has lost its reason to exist". Moving the writer would carry
Windows-shaped multi-host machinery into Core for a consumer that will never want
it: R-12 pointed the other way. The DTO is the genuinely shared part, because it
is the on-disk contract any status UI reads.

**Still open before Phase 1 can close** — and both are a different kind of risk
from everything above:

- The `SpeechSession` extraction out of `SapiEngine`.
- An `Onnx.DirectML` backend so the Windows engine goes through `ISynthesizer`.
- 0.2.7.3 surviving real daily use.

**The render-sanity test is done** (2026-08-15), as `RenderSanity` in
`Core.Audio` plus `RenderSanityTests`.

Phase 0 asked for exactly this and named the reason: *a fast render of garbage
would still pass every number above.* RTF, peak, RMS and duration are all
satisfied by noise, by a stuck tone, and by a vocoder that has quietly
regressed into producing either. The checks are the two structural properties
speech has and those do not — irregular pauses at phrase boundaries, and
amplitude modulation in the 2–8 Hz syllable band.

Committed as an **envelope**, per Phase 0's instruction: 5.9 KB of RMS frames
rather than a 1.1 MB WAV, diffable in review, and the only part the checks read.

Two things the build of it turned up, both now pinned by tests:

- **Modulation rate alone is not a test.** The first version took the argmax over
  the band, which always returns something *inside* the band — white noise scored
  2.25 Hz and a 220 Hz sine scored 7.95 Hz, and both were declared speech. Every
  signal has a loudest bin; only speech has a loud one. The verdict now also
  requires modulation *strength* (share of envelope variance at that rate) and a
  minimum fraction of near-silent frames.
- **`Correlate` returns 0 for two identical inputs** when the window is wide
  enough to average their structure flat — Pearson is undefined without variance,
  and 0 reads as "unrelated" rather than "cannot tell". 500 ms against speech is
  safe because phrases are seconds long; it is not safe against anything slower.
  Documented on the method and pinned by
  `Correlation_is_undefined_not_zero_when_the_window_hides_the_structure`.

Thresholds are deliberately far from the measured values rather than tuned
between them — the real render scores 0.034 modulation strength and 29.8% silent
frames against floors of 0.005 and 3%, so each criterion has 6–10x headroom.
Splitting the difference with white noise would have given ~1.7x on a sample size
of one, which is how a test starts false-failing legitimate voices a year later.

| Signal | Verdict | Why |
| --- | --- | --- |
| the reference render | **speech** | 3.25 Hz, 7 pauses, 29.8% silent |
| white noise | rejected | no pauses, no silence, unprominent modulation |
| sustained tone | rejected | same |
| digital silence | rejected | silent, no modulation |
| full-scale square wave | rejected | clipped, no pauses |
| tone gated at exactly 4 Hz | rejected | textbook modulation, and never pauses |

Everything landed so far is a *move*: the code is byte-identical or nearly so,
and Core.Tests covers the behaviour that matters. Those first two are *rewrites*
— of the COM speak loop, and of ~600 lines of field-hardened DirectML
device-loss recovery — and neither can be executed from the Linux box. The
TestHarness is System.Speech-based, so this plan's own exit criterion ("the
existing TestHarness passes **unchanged** on Windows") is checkable only on the
VM. The contingency already anticipates this: *duplication you can see beats a
refactor you cannot verify.* Sequencing them behind a TestHarness run is the plan
following its own rule, not a reduction in scope.

**Work.**

1. New `src/VibeSuperTonic.Core`, TFM `net10.0` (not `-windows`),
   `VstPortable=true`, and **no `PackageReference`s** — see
   [Platform isolation](LINUX-PORT-PLAN.md#isolation) for why that constraint is the design.
2. Move unchanged: `Sonic`, `TimeStretch`, `SupertonicLanguages`,
   `Pronunciations`, `LogRotation`, telemetry DTO + writer, `Manifest`,
   `ModelDownloader`. **`SupertonicSdk` and `SupertonicAdapter` do not move into
   Core** — they hold all 32 ORT call sites, so they go to `src/shared/` and are
   compiled into `Onnx.Windows` and `Onnx.Linux` instead.
   > **Done, with three corrections.** The telemetry *writer* stayed behind (it is
   > multi-host machinery a single daemon will never want); `src/shared/` was
   > deferred in favour of linking `SupertonicSdk.cs` from where it sits; and the
   > backends are named `Onnx.Cpu` / `Onnx.DirectML`, for the execution provider
   > rather than the OS. `ModelDownloader` needed a `FileLockDescriber` seam to
   > cross. See the slice notes above.
3. Extract from `SapiEngine.cs` into a host-agnostic `SpeechSession`:
   `BuildSpeakPlan`, the sentence chunker and balancer, `ComputeSpeed`,
   `ApplyVolume`, word-boundary offset math, and the synth/write pipeline. It
   raises events; it knows nothing about COM.
   > **Not done — and now unblocked.** This was gated on a TestHarness baseline,
   > which exists as of 2026-08-15. Run `tools\VibeSuperTonic.TestHarness.exe`
   > before and after.
4. **Fix the pronunciation offset corruption (R-2).**
   `PronunciationsConfig.Apply` gains an out parameter: an ordered edit list of
   `(rewrittenStart, lengthDelta)`. `SanitizeForSynth` produces the same. Word
   and sentence boundary math maps rewritten offsets back through the edit list
   to true source offsets. This is a live Windows bug, not Linux prep — see
   [R-2](#r-2).
   > **Done and verified on Windows.** Implemented as a per-character
   > `TextOffsetMap` rather than the edit list sketched here — rules apply in
   > sequence, and composing delta lists across passes is where the subtle version
   > of this bug lives. R-14 turned up alongside it and took two attempts; see
   > both records below.
5. **Add `VibeSuperTonic.Core.Tests` (R-3)** — cross-platform, runs on both
   OSes. Minimum coverage: chunker boundaries and balancing, Sonic output
   length at 0.5×/1.0×/2.0×, pronunciation offset mapping (regression test for
   step 4, including length-increasing, length-decreasing, and overlapping
   rules), speak-plan construction from a fragment list.
   > **Done — 187 tests.** Everything on that list is covered except speak-plan
   > construction, which lives in `SapiEngine` and moves to Core only with
   > `SpeechSession` (step 3). Also carries the Phase 0 render-sanity checks.
6. **Leave the Windows-only machinery behind (R-12).** `LockProbe`,
   `GpuEnumeration`, `RenderHost`, and the DirectML device-loss latch stay on
   the Windows side. No `OperatingSystem.IsWindows()` branches inside Core.
   > **Done, and enforced rather than intended.** `VstPortable=true` promotes
   > CA1416 to an error, so Windows-only code in Core fails to compile. Core has
   > no `OperatingSystem.IsWindows()` branch anywhere.

New seams:

| Interface | Windows impl | Linux impl | State |
| --- | --- | --- | --- |
| `ISynthesizer` | `Onnx.DirectML` | `Onnx.Cpu` | **Linux side built and rendering.** Windows still calls `SupertonicAdapter` directly |
| `FileLockDescriber` | Restart Manager | omitted — replacing an open file is legal | **done**, and it is what let `ModelDownloader` move |
| `IHostConfig` | registry | `$XDG_CONFIG_HOME/vibesupertonic` | not built; `DataPaths` is still Windows-side. **This row was the only one assigned to no phase** — now [Phase 4b](#phase-4b) |
| `IAudioSink` | `ISpTTSEngineSite::Write` | libpulse | **Phase 2** |
| `ISpeechEvents` | SAPI event structs | socket broadcast | **Phase 3** |

`ISynthesizer` replaces the `IExecutionProviderPolicy` this table used to list.
The provider choice is not a runtime seam at all: the DirectML API does not
exist in the CPU package, so it is a *build-time* split, and the `#if ORT_DIRECTML`
guard from Phase 0 lives inside a backend that only ever compiles one way. See
[R-13](#r-13) for the discovery and [Platform isolation](LINUX-PORT-PLAN.md#isolation) for the
resulting layout.

`ISynthesizer` takes a `CancellationToken` from the outset — that is what R-8's
stop maps onto, and retrofitting cancellation through a synthesis interface is
considerably worse than starting with it.

**Exit criteria — fully cleared 2026-08-16.** The last item landed with the
first push, exactly as predicted.

- ✅ The existing TestHarness passes **unchanged** on Windows. All nine steps
  green against 0.2.7.5 on the Win11 VM — the original seven unchanged, plus two
  new offset steps. Note the baseline is **CPU-provider only**: the VM has no GPU
  and DirectML falls back on every run.
- ✅ `Core.Tests` passes on Windows and on the Mint box. **818 green locally on
  Mint, and green on `windows-latest` in CI** as of 2026-08-16 — this was the
  one ⚠️ on the list and it cleared itself on the first push, as expected. It is
  also the first of the three conditions on [the Way 3 gate](LINUX-PORT-PLAN.md#the-gate).
- ✅ A Windows patch release built from Core — carrying the R-2 fix — ships and
  survives real daily use before Linux work starts. 0.2.7.5 shipped from the
  Way 2 layout and has been in use since. Skip this and the first Core regression
  gets misdiagnosed as a Linux bug.

**Contingencies.**

- *Harness can't be kept green* → the extraction is too aggressive. Reduce scope
  to moving the already-portable files and let the Linux host duplicate the
  `SapiEngine` logic for v1. Duplication you can see beats a refactor you can't
  verify.
- *Core drags in Windows-only types* → it will be `Registry` leaking through
  `DataPaths`. That is the seam; fix it there rather than widening the TFM.
- *The R-2 fix turns out to be invasive* → ship it as its own Windows patch
  **before** the extraction starts, so the two changes are separately
  bisectable. Do not bundle a behavior fix with a large file move.

<a name="phase-2"></a>

### Phase 2 — Audio out and the playback clock · 1 day

**Status: done 2026-08-15, exit gate cleared.** Measurements and the three
things this phase found are at the end of the section.

**Goal.** PCM to the speakers, plus an accurate answer to "what has the user
actually *heard*?" The second half is what makes the Reader tab work.

**Work.** `PulseAudioSink` P/Invoking `libpulse-simple.so.0`: `pa_simple_new`,
`pa_simple_write`, `pa_simple_get_latency`, `pa_simple_drain`,
`pa_simple_flush`, `pa_simple_free`. Format 44100 Hz mono s16le — the engine's
native output, so there is no resampling anywhere in the chain.

Playback clock: `playedFrames = writtenFrames − latencyUsec × rate / 1e6`,
sampled at ~50 Hz. A word boundary fires when `playedFrames` crosses that word's
frame offset. Calibrate against a known click track under pipewire-pulse and
record the residual.

**Suppress the clock until the stream primes (R-7).** Reported latency is zero
or nonsensical before the buffer fills, which would throw the highlight to a
wrong word at every utterance start — the most visible moment there is. Hold
boundary events until ~200 ms has been written *and* latency reads sane, then
fast-forward to the correct position.

`Flush()` on the sink drops buffered audio immediately; it is one of the three
things stop has to do (see Phase 3).

**Exit criteria.** Highlight-to-audio drift ≤ 80 ms sustained across a 2-minute
read, and no visible jump in the first second.

**Contingencies.**

- *`pa_simple_get_latency` is inaccurate under pipewire-pulse* → move to the
  async `pa_stream` API and use `pa_stream_get_time`. More code, authoritative
  clock.
- *Both are inaccurate* → degrade the highlight to sentence granularity, where
  drift is invisible. Audio quality is unaffected; only the follow-along
  resolution drops.
- *`libpulse-simple.so.0` missing* → it ships with `libpulse0`, present on any
  Mint desktop. dlopen-probe at startup and fail with an install hint rather
  than crashing.
- *Flush doesn't kill buffered audio fast enough on stop* → shrink
  `pa_buffer_attr.tlength`, trading a little underrun headroom for stop latency.

#### What was built

| Where | What |
| --- | --- |
| `Core/Audio/PlaybackClock.cs` | The subtraction, R-7 priming suppression, the monotonic guard |
| `Core/Audio/BoundaryScheduler.cs` | Queue of planned events, released as their audio is heard |
| `Core/Audio/BoundaryPlanner.cs` | Chunk + offset map → boundary events, in frames |
| `Core/Audio/BoundaryEvent.cs`, `IAudioSink.cs` | The two coordinate spaces, and the sink seam |
| `src/VibeSuperTonic.Linux.Audio` | `PulseAudioSink` + the P/Invoke surface |
| `spike/linux-play` | End to end: render → plan → play → read the words back |

Only the sink is Linux-only. The clock, the scheduler and the planner are all in
Core, are platform-neutral, and have **41 new tests** — Core.Tests went from 187
to 228. That was deliberate: the clock is the piece most likely to be subtly
wrong and the least likely to look wrong, so it holds no wall-clock and starts no
timer, and is driven entirely by what it is told. Every pathological latency
curve a real sink only produces on someone else's machine is therefore a unit
test here, and the whole thing runs on a Windows CI box with no speakers.

#### Measured on the Mint box, 2026-08-15

| Check | Result |
| --- | --- |
| Clock residual vs real time, 120 s | **worst 24.2 ms**, gate is 80 ms |
| Residual trend | **none** — oscillates ±20 ms, does not accumulate |
| Drain accuracy | 120.032 s wall for 120.000 s audio (32 ms over) |
| Priming | 111–156 ms, i.e. one buffer |
| Stop | `Flush` returns in **0.6 ms** |
| Word readout, 21 s of speech | 56 of 56 events fired, every one on a real word |
| Core.Tests | 229 passed |

#### Verified on the Win11 VM, 2026-08-15

The harness grew two steps and ran **11 of 11 green** against 0.2.7.5.

**Step 10 is the one that could not be written anywhere else.** It reads that
machine's `pronunciations.json` and chunk-size settings, predicts every word
boundary with the same `BoundaryPlanner` the Linux daemon uses, and holds the
prediction against what the live engine reported over five text layouts:
`engine=10 predicted=10 unmatched=0` on all five. The unit oracle proves the
planner matches a *transcription* of the engine's source; this proves it matches
the engine as installed and running. Both platforms now demonstrably place the
same boundaries on the same sentences.

Step 11 covers the clock and scheduler, which need no audio device.

**It failed on its first run, and the failure was in the test, not the product.**
Step 11 seeded the scheduler from frame 0 and then made four `Advance` calls
against one shared instance, so the first event was already due on the "releases
nothing early" check and every later count was off by what the previous call had
consumed. Worth recording because the unit tests could not have caught it — each
of those starts with a clean scheduler, and the defect lived in the *sequence*.
`BoundarySchedulerTests.The_harness_step_11_sequence_holds` now pins the exact
sequence on Linux, so the harness's arithmetic is checkable on the machine that
writes it rather than only on the VM.

**The 80 ms gate is met with room to spare, and the contingency does not need
spending.** `pa_simple_get_latency` is accurate enough under pipewire-pulse that
moving to the async `pa_stream` API would buy nothing — the residual is bounded
by the 20 ms poll interval, not by the reading. Sentence-granularity degradation
is likewise off the table.

One thing the measurement cannot see: it compares the clock against the *wall
clock*, not against a microphone, so a **fixed** offset between what libpulse
reports and when sound leaves the speakers would be invisible. That is the
benign case — it shifts every highlight by the same amount — and drift, the case
that would make a paragraph unreadable by the end, is exactly what is measured.
Worth re-reading `spike/linux-play --calibrate` output on any machine that
behaves oddly rather than assuming this generalises.

#### Three things this phase found

1. **A terminated `pa_simple` stream cannot be flushed from another thread.**
   `pa_simple` is not thread-safe, so the obvious stop implementation — call
   `pa_simple_flush` from the hotkey thread while the writer sits inside
   `pa_simple_write` — is a data race in native code, which presents as the
   daemon vanishing rather than as an exception. `PulseAudioSink.RequestFlush`
   sets a flag the writer observes between blocks instead. This is why `Write`
   feeds the device in 20 ms pieces rather than handing over a whole chunk: a
   sentence is seconds of audio and a stop that waited for it would be no stop.

2. **The write loop *is* the clock's poll loop.** `pa_simple_write` blocks until
   the device has room, so the loop is already paced by playback at exactly the
   rate the clock wants to be sampled. The plan's "sampled at ~50 Hz" was
   written assuming a separate timer; a timer would add jitter, a second thread
   and a race, and measure nothing better. Phase 3's daemon should keep this
   shape.

3. **Boundary placement had to be *ported*, not reimplemented.** The Windows
   engine places a word proportionally within its chunk — 40% of the way through
   the characters is 40% of the way through the audio. It is crude, and it is
   what five shipped releases do. A better rule on Linux would make the two
   platforms disagree about the same sentence with no way to say which was
   wrong, and every future field report would have to establish which product it
   came from before it could be read. `BoundaryPlannerTests` holds the port
   against a transcription of `SapiEngine.EmitWordBoundaries`, and harness step
   10 holds it against the running engine.

   (The rule's real weakness is that it assumes an even speaking rate within a
   chunk, so a chunk containing a long pause drifts. Chunks are sentence-sized,
   which bounds it. Fixing it needs per-token durations out of the model, which
   the vendored SDK does not surface.)

#### Not done here, deliberately

`SapiEngine` still has its own copy of the boundary-emitting code, now provably
equivalent to `BoundaryPlanner`. Adopting it is real work with a real reason and
belongs to [the Windows convergence](LINUX-PORT-PLAN.md#convergence), not to a Linux phase.

<a name="phase-3"></a>

### Phase 3 — Daemon and IPC · 1–2 days

**Status: built 2026-08-15. Two of three exit criteria met; the third was wrong
and is restated below with the measurement that shows why.**

**Goal.** One process holding the warm model, a client that feels instant, and a
stop that actually stops.

**Work.** `vibesupertonicd` listening on
`$XDG_RUNTIME_DIR/vibesupertonic/ctl.sock`, mode 0600, single-instance lock.
JSON-lines protocol: `toggle`, `speak {text}`, `stop`, `pause`, `resume`,
`status`, `subscribe`. `vst-ctl` is NativeAOT — connect, write one line, exit.
Optional D-Bus `org.vibesupertonic.Daemon` mirroring the same verbs, which makes
the whole thing scriptable.

**The state machine from [the hotkey contract](LINUX-PORT-PLAN.md#the-hotkey-contract) lives
here**, in the daemon — not in the client. The client is stateless and sends
`toggle`; the daemon decides whether that means speak or stop. Debounce lives
here too, so it works identically whether the request arrives from the hotkey,
the tray menu, or D-Bus.

**Stop is three operations, not one (R-8).** The pipeline renders ahead, so on
stop there is typically an ONNX `Run` in flight that can take seconds:

1. `RunOptions.Terminate` on the in-flight inference — the mechanism already
   exists in `SupertonicAdapter` for the watchdog path, reuse it.
2. `Flush()` the pulse buffer.
3. Discard queued PCM and the remaining exec plan.

Stopping the audio feed alone leaves the daemon busy and queues the next speak
request behind a dead utterance.

**Define `subscribe` before anything consumes it (R-1).** The event stream —
Started, WordBoundary, SentenceBoundary, Finished, StateChanged, Error — is the
*only* way anything learns what the daemon is doing, including the in-process
UI in Phase 6. Building it here, with a `vst-ctl subscribe` that just prints
events, forces it to be a real interface rather than a convenience wrapper over
internal state.

**Client auto-starts a dead daemon (R-5).** If autostart didn't fire or the
daemon crashed, `vst-ctl` finds no socket, spawns the daemon, and retries within
a 5 s budget. The user then waits through a cold load — slow, but audibly
*something*, versus a hotkey that does nothing at all.

**Exit criteria.** Hotkey to first audible feedback under 150 ms with the model
warm. Stop silences audio within 100 ms from any state, including mid-inference.
`vst-ctl subscribe` prints a coherent event stream for a full utterance.

**Contingencies.**

- *NativeAOT is a build hassle* → a ~40-line C client, or ship the .NET client
  non-AOT and accept ~70 ms. Do **not** depend on `socat`, `xclip`, or `xsel` —
  none are installed on stock Mint, and a hotkey tool that requires
  `apt install` before it works has already lost.
- *`$XDG_RUNTIME_DIR` unset* (rare — some `su`'d sessions) →
  `/tmp/vibesupertonic-$UID`, mode 0700.
- *D-Bus slips* → the socket alone is sufficient for v1. D-Bus is additive.

#### What was built

| Where | What |
| --- | --- |
| `Core/Session/SpeechSession.cs` | The pipeline: chunk, render ahead, write, clock, boundaries, stop |
| `Core/Session/ToggleGate.cs` | The hotkey contract and debounce, with time as a parameter |
| `Core/Session/SpeechState.cs`, `SessionEvent.cs` | The four states and the one event channel |
| `Core/Ipc/Protocol.cs` | JSON-lines wire format, socket path, source-generated serializer |
| `src/VibeSuperTonic.Daemon` | `vibesupertonicd` — socket, dispatch, subscriber fan-out |
| `src/VibeSuperTonic.Ctl` | `vst-ctl` — NativeAOT, stateless, auto-starts a dead daemon |

The protocol is JSON, one object per line, both directions:

```
$ vst-ctl subscribe                      # opened while a read is in progress
{"State":"Speaking","Text":"The sea is everything. It covers...","SourceOffset":39,"SourceLength":6,...}
{"Kind":"WordBoundary","SourceOffset":46,"SourceLength":2,"AudioSeconds":3.48}
{"Kind":"WordBoundary","SourceOffset":49,"SourceLength":3,"AudioSeconds":3.69}
```

The first line is the snapshot, the rest are events — see finding 6.

The session and the gate are in Core and platform-neutral, so the stop sequence
and the state machine are driven by fakes in **31 new tests** rather than by
getting lucky with real hardware. Core.Tests is now **273**.

D-Bus was not built. The socket is sufficient for v1 and D-Bus is additive; it
belongs with Phase 6, where something other than a hotkey wants to drive it.

#### Measured on the Mint box, 2026-08-15

| Check | Budget | Result |
| --- | --- | --- |
| Press → daemon acknowledges | — | **28 ms** |
| Press → first audio, warm | 150 ms | **792 ms** — see below |
| Press → silence, mid-utterance | 100 ms | **40 ms** |
| Stop, daemon side only | — | 18 ms |
| `vst-ctl` process start | — | **6 ms** (107 ms before AOT) |
| Auto-start a dead daemon and answer | 5 s | **228 ms** |
| `vst-ctl subscribe` over a full utterance | coherent | 18 events, ordered, no gaps |

#### The 150 ms exit criterion was wrong

**First audio cannot be 150 ms, and no amount of engineering gets it there.**
Measured by sweeping the length of the first chunk:

| First chunk | Press → first audio |
| --- | --- |
| 3 characters | 707 ms |
| 22 characters | 747 ms |
| 61 characters | 1112 ms |

Extrapolating to zero gives a **fixed floor of roughly 600 ms** per utterance
that has nothing to do with how much text was asked for. It is the model's
minimum cost for one inference at `totalStep` 8 — voice styles are already
cached, and this is with the session preloaded and warm. Above that floor the
cost is about 6 ms per character.

So the criterion conflated two things the product needs separately:

- **Acknowledgement**, which is what stops a key feeling dead, and which is
  **28 ms**. That is the number the tray blip and icon state in Phase 6 hang
  off, and it comfortably beats 150 ms.
- **First speech**, which is bounded by the model and is ~750 ms for a short
  opening sentence.

**Revised criterion: acknowledgement within 150 ms, first speech under 1 s.**
Both are met. If first speech has to come down further, the only lever left is a
lower `totalStep` for the opening chunk alone — a quality trade on one sentence,
and a product decision rather than an implementation one. Recorded under
[Open decisions](LINUX-PORT-PLAN.md#open-decisions).

#### Six things this phase found

1. **Chunk size is press-to-speech latency, and nothing in the chunker says so.**
   The merge pass exists to keep chunks evenly sized, and it does that by gluing
   short sentences together — so a passage opening "The sea is everything."
   produced a first chunk of three sentences and **1.45 s** of silence after the
   press. `SentenceChunker` now takes a `leadChars` cap that splits the first
   chunk back at a sentence boundary the text already had, which cost nothing
   audible and took first audio to 792 ms. Off by default, so the Windows engine
   — which hands SAPI a whole stream and has no press to answer — is unchanged,
   and the reference-oracle test confirms it.

2. **NativeAOT for the client was not optional, and it needs source-generated
   JSON.** The same client starts in **107 ms** on the runtime and **6 ms**
   compiled. 107 ms is most of the acknowledgement budget spent before any work
   begins, and it is comparable to the 150 ms debounce window — a hotkey would
   have spent most of its life waiting for a process to exist. AOT disables
   reflection-based serialization, so the first AOT build compiled cleanly and
   then aborted on its first message; `ProtocolJson` is the fix, and it lives in
   Core because the daemon must serialize identically. **Add new protocol types
   to it** — a missing one fails only on the shipped binary, never in a test.

3. **A debounced press is a success that does nothing**, which is correct for a
   finger and indistinguishable from a press that worked for everything else. It
   made "the key sometimes does nothing" undiagnosable. `Response.Action` now
   reports which of speak/stop/ignored a toggle turned out to mean.

4. **`ToggleGate`'s first press was silently dropped.** The debounce timestamp
   started at `long.MinValue`, so `now - last` overflowed to a negative number,
   read as "well inside the window", and dropped the first press of every
   session — a freshly started daemon whose key did nothing until pressed twice.
   Found by the unit tests, which is the argument for the gate being a class with
   time as a parameter rather than three lines inside the daemon.

5. **The daemon shut down cleanly and then core-dumped.** An `AppDomain
   ProcessExit` handler cancelled the lifetime token — but it runs *after* `Main`
   has returned and disposed it, so it threw `ObjectDisposedException` on a
   thread with no handler. The socket was removed, the log said "stopped", and
   the exit code was 134 with a core file: a clean shutdown that every tool
   downstream would read as a crash, and that a systemd restart policy would act
   on. Removed; `SIGTERM`/`SIGINT`/`SIGHUP` are handled explicitly instead.
   **`SIGHUP` matters** — the daemon is often started from a shell that then
   exits, and the default action for it is to terminate, which killed the daemon
   mid-utterance with no cleanup and cost half an hour of confusion during
   development.

6. **An event stream alone cannot serve a subscriber that arrives late** — and
   Phase 6's exit criterion is exactly that subscriber. "Open the window mid-read
   and the highlight snaps to the correct word" was unsatisfiable as built: the
   stream carries only what happens after you connect, so a window opened during
   a read would receive `WordBoundary offset 46` with nothing to index it into.
   Fetching state first and *then* subscribing does not fix it either — at three
   to four boundaries a second, everything in the gap is lost.

   The `subscribe` reply now carries a snapshot — state, the full utterance text,
   and the current offset — on the same connection, before the first streamed
   event, so there is no gap to race. Found by asking what Phase 6 would actually
   need, which is the entire reason [R-1](#r-1) says to build this stream before
   anything consumes it.

#### Not done here

- **D-Bus.** Additive; the socket covers v1.
- **Selection capture.** Phase 4. `ISelectionSource` is the seam and ships with a
  null implementation that says so, so `toggle` with no text reports a sentence
  the user can act on rather than speaking nothing.
- **`SpeechSession` is not yet used by the Windows engine.** It was extracted so
  the daemon would not duplicate `SapiEngine`'s speak loop, and it does not — but
  pointing the engine at it belongs to [the Windows convergence](LINUX-PORT-PLAN.md#convergence),
  which is now a single named job rather than a deferral repeated per phase.

<a name="phase-4"></a>

### Phase 4 — Selection capture · 0.5 day

**Status: built 2026-08-15.** The protocol half is verified against a synthetic
owner; the application half — Firefox, a GTK editor, the terminal, a PDF viewer
— still needs a person. See [What was built](#phase-4-built) at the end of this
section.

**Goal.** Read the highlighted text without touching the clipboard.

**Where it plugs in (new, from Phase 3).** `ISelectionSource` in the daemon is
the seam; it ships with a `NullSelectionSource` that reports "not implemented
yet" rather than speaking nothing. Phase 4 is one implementation of one
one-method interface, and `DaemonServer.Toggle` already handles the failure path
— including putting the gate back to Idle so an empty selection is a no-op that
does not consume the next press.

**The seam was widened before starting, 2026-08-15, because it could not express
what this phase is required to report.** It was
`bool TryGetSelection(out string text, out string reason)` — two outcomes, where
there are three. R-9 caps a selection at 100 KB, truncates at a sentence
boundary and says to *report what was dropped*: that is a capture that
succeeded and was changed, and the old shape had nowhere to put it. Reporting it
through `reason` would have meant failing a request that in fact started
speaking; reporting nothing would leave a user watching the reading stop early
with no explanation. Phase 6's R-9 tray tooltip would then have had to reach
around the interface to find out, which is the coupling [R-1](#r-1) exists to
prevent.

Now `SelectionResult Capture(string? display)` returning
`Ok` / `Text` / `Reason` / `Notice`, with `Response.Notice` carrying the third
outcome to the client — distinct from `Error`, and pinned by
`ProtocolTests.A_notice_rides_on_a_successful_response_without_becoming_an_error`.
Found by writing Phase 4's exit criteria against the interface it plugs into
rather than against the prose, which is worth doing for every remaining phase.

**The daemon has no X11 connection today, and may not be able to get one.** It is
a background process that can legitimately start before or without a session
bus and display — from a shell, from systemd, or from `vst-ctl`'s autostart
(R-5), which inherits whatever environment the caller had. So:

- Open the display lazily, on first selection request, not at startup. A daemon
  that refuses to start without `$DISPLAY` cannot be used to render to a file or
  driven from a script, and it would turn a missing variable into "the hotkey is
  broken".
- Treat a missing or unopenable display as a selection failure with a readable
  reason, which the existing seam already carries to the client.
- **The client sends the display; the daemon does not look it up.** This
  sentence used to read "re-check per request rather than caching a failure,
  because the daemon outlives individual X sessions" — which sounds right and
  accomplishes nothing. A process's environment is fixed for its lifetime, so
  re-reading `$DISPLAY` returns the same answer forever: a daemon started by
  `systemd --user` or from a display-less shell would fail every selection
  until someone restarted it, and the re-check would faithfully produce the
  same failure each time. The session identity has to travel *with the
  request*, from a process that by construction runs inside the session.
  `Request.Display` carries it and `vst-ctl` populates it on `toggle`; the
  daemon's own environment is the fallback, not the source.

  **`XAUTHORITY` is a trap if it turns out to be needed.** `.NET`'s
  `Environment.SetEnvironmentVariable` updates a managed copy and does not call
  `setenv`, so libX11 — which reads the real environment with `getenv` at
  `XOpenDisplay` time — will not see a value set that way. If a cookie path has
  to be forwarded too, it needs a `setenv` P/Invoke, and the failure without one
  is an authorisation error that looks exactly like a missing display.

**Work.** In-process X11 against `libX11.so.6`: `XOpenDisplay`,
`XConvertSelection(PRIMARY, UTF8_STRING)`, wait for `SelectionNotify`,
`XGetWindowProperty`. Roughly 150 lines of P/Invoke. 300 ms timeout when no
owner answers. Empty selection is a no-op — never fall back to re-reading the
last thing, which is surprising and feels broken.

**Ignore our own windows (R-6).** Reader-tab text is selectable, so selecting
any of it makes us the PRIMARY owner and the next press re-reads our own window.
Check the owner via `XGetSelectionOwner`; if it belongs to this process, treat
the request as "no new selection" and leave the current text alone.

**Cap the selection (R-9).** 100 KB of text, roughly a long article. Beyond
that, truncate at a sentence boundary and report what was dropped. Ctrl+A in a
book should not produce an unbounded queue.

**Exit criteria.** Works from Firefox, a GTK text editor, the Cinnamon terminal,
and a PDF viewer. Selecting text inside our own Reader tab does not hijack the
next press. `vst-ctl toggle` with no display available reports why instead of
failing silently.

**Contingencies.**

- *App doesn't export PRIMARY* (some Electron, some Java/Swing) → documented
  limit. An opt-in Ctrl+C fallback via XTEST sits behind a setting, **off by
  default**, text-only, with the clipboard caveat stated plainly where the user
  enables it.
- *INCR protocol* — selections above ~256 KB arrive in chunks. The 100 KB cap
  makes this mostly moot; truncate with a visible notice rather than
  half-implementing the protocol.
- *Wayland later* → same interface, a `wl-paste --primary` /
  `ext-data-control-v1` implementation behind it. Nothing else changes.

<a name="phase-4-built"></a>

#### What was built

| Where | What |
| --- | --- |
| `Daemon/Interop/X11Native.cs` | The libX11 surface — twelve entry points, no more |
| `Daemon/X11SelectionSource.cs` | `ISelectionSource` over PRIMARY: owner check, request, 300 ms wait, property read, R-9 cap |
| `Core/Text/TextCap.cs` | The R-9 cap, with 8 tests. Core.Tests now **286** |
| `spike/x11-select` | A synthetic PRIMARY owner, so any of this can be tested at all |

**A folder in the daemon, not a `Linux.X11` project — decided on evidence.**
`Linux.Audio` is a separate project and this is not, which is the first thing a
reader will ask about. Three things settled it. Every *other* native surface in
this repository is already a folder inside its owning project — `Engine/Interop`
for SAPI, `Launcher/Export` for Media Foundation, `Launcher/Integrity` for DXGI
and Restart Manager — so `Linux.Audio` is the exception rather than the rule,
and a folder is also the ordinary .NET convention. `Linux.Audio`'s own stated
reason ("Phase 6's app is a second consumer") does not transfer: [R-1](#r-1)
requires the Phase 6 UI to drive the daemon over the socket like any external
client, so nothing else will ever capture a selection. And the isolation that
actually matters is `ISelectionSource`, which already exists — it is what makes
Wayland an implementation swap and what lets the daemon run headless. A project
boundary would have added a compile-time barrier on top, at the cost of moving
that seam out of its only consumer.

The condition on that decision was `unsafe`: promoting `AllowUnsafeBlocks` to
the whole daemon would have been a real widening, and would have made the
separate project the better trade. **It was not needed.** `XGetWindowProperty`'s
`unsigned char**` marshals as `out IntPtr` and is read back with `Marshal.Copy`;
the 192-byte `XEvent` union is declared with explicit field offsets rather than
a `fixed` buffer. The daemon has no `unsafe` anywhere.

**Xlib's default error handler calls `exit()`, and that would have killed the
daemon in normal use.** `XGetSelectionOwner` followed by a property read on that
window is a race the user wins routinely — select text, close the window, press
the key. The owner is gone by the second call, Xlib raises `BadWindow`, and the
default handler prints to stderr and terminates the process: the daemon
disappears mid-utterance, with no exception to catch and nothing in its own log.
Exactly the shape of the `pa_simple` threading hazard from [Phase 2](#phase-2),
and just as invisible to testing, since nothing about a deliberate test closes a
window at the wrong microsecond. `XSetErrorHandler` is installed once, process
wide. *Phase 6 note: Avalonia installs its own and whichever runs last wins.*

**Measured on the Mint box, 2026-08-15**, against `spike/x11-select`:

| Case | Result |
| --- | --- |
| Ordinary selection → toggle | captured and spoken |
| **Daemon started with no `$DISPLAY` at all**, client forwards its own | **captured and spoken** |
| Neither has a display | "no X display. The daemon was started outside a graphical session…" |
| 200,000-character selection (R-9) | cut at a sentence boundary: read 102,338, dropped 97,662, reported as a `Notice` |
| Owner refuses every target | "the application holding the selection could not provide it as text." |
| Nothing owns PRIMARY | "nothing is selected." |
| Whitespace-only selection | "the selection is empty." |
| Daemon survived all of the above | clean shutdown, no core dump |

The second row is the one worth keeping: that daemon has no `$DISPLAY` in its
environment and never will, and it read the selection anyway. It is the case the
plan's original "re-check per request" wording could not have handled.

**`spike/x11-select` exists because stock Mint has no `xclip` and no `xsel`** —
this plan refuses to depend on them at runtime, which leaves nothing on the
machine able to put text on PRIMARY on demand. It owns PRIMARY and serves
`UTF8_STRING`, with `--size N` for the R-9 case and `--refuse` for the
Java/Swing behaviour. Roughly eighty lines, and it turns "select something and
press the key" into a scripted check.

#### Stress and fuzz, 2026-08-15 — four defects, none of them in Phase 4

`spike/daemon-stress/` holds the two scripts, because none of this is reachable
from `Core.Tests`: the protocol lives behind a socket, the selection path behind
a display, and both are in the daemon, which `Core.Tests` deliberately does not
reference. Everything the scripts do is something a real client does.

Every one of these had passed every unit test:

- **A UTF-16 byte-order mark hung the connection.** `StreamReader` defaults to
  `detectEncodingFromByteOrderMarks: true`, so a request beginning `0xFF 0xFE`
  switched that connection's decoder to UTF-16 and the newline that frames the
  protocol never appeared again. Out of nineteen malformed inputs it was the
  only one that produced *no reply at all* rather than "unparseable request" —
  which is how it was noticed.
- **The accept backlog was 16.** A unix socket whose queue is full fails
  `connect()` with EAGAIN immediately; it does not wait. Twenty threads issuing
  status calls lost **158 connections out of 400**.
- **`vst-ctl` treated every connect failure as "no daemon is listening"** — the
  trigger for auto-starting one ([R-5](#r-5)). So a *busy* daemon produced a
  *second* daemon.
- **`ClearStaleSocket` deleted the socket file on any `SocketException`**, which
  is where that second daemon went next: unlink the live daemon's socket, bind
  its own, and now there are two processes with one holding the audio device and
  no client able to reach it. The method's own comment already said that outcome
  was "far worse than refusing to start". Only `ConnectionRefused` proves nobody
  is listening, and it is now the only error that justifies the delete.

The last two compose into one chain, which is the interesting part: none of the
four is visible from inside its own file, and the pair that matters only becomes
dangerous when a load condition meets an error-handling shortcut two projects
apart.

After the fixes — 400/400 concurrent calls with zero failures, every malformed
input answered, 50 connect-and-abandon rounds, 1,800 subscribers opened and
dropped mid-utterance, 120-press toggle storms, and 40 rounds of the selection
owner being killed mid-transfer. The daemon survived all of it and shut down
clean.

**Worth knowing: these four have no unit coverage and cannot have any** at the
current layout — they live in `VibeSuperTonic.Daemon`, and `Core.Tests` is
platform-neutral by construction. The stress scripts are the regression test,
and they only run when someone runs them. Same argument the TestHarness won on
the Windows side.

**What is still unverified**, and cannot be verified from here:

- ~~The exit criteria proper — Firefox, a GTK editor, the Cinnamon terminal, a
  PDF viewer.~~ **Done 2026-08-16** — see [the application pass](#phase-4-apps)
  below. All four pass, and the sweep found one limit worth knowing about.
- **R-6 has no real test.** `spike/x11-select` sets no `_NET_WM_PID`, so it
  exercises the "treat as foreign" fallback rather than the match. The match
  cannot be exercised until Phase 6 gives this process a window of its own,
  which is also the first moment the bug it prevents becomes possible.
- The INCR path reports honestly rather than being implemented; the R-9 cap
  makes it reachable only for a selection above 4 MB.

<a name="phase-4-apps"></a>

#### The application pass — 2026-08-16

The exit criteria, run by the user against live applications. **All four pass**,
and so does an application that was not on the list.

| Application | Result |
| --- | --- |
| Firefox | Reads the selection |
| xed (GTK editor) | Reads the selection |
| Cinnamon terminal | Reads the selection |
| xreader (PDF) | Reads the selection |
| Brave — Gmail message body | Reads the selection. Instrumented: PRIMARY owned by Brave, `UTF8_STRING` offered, and the daemon's text matched PRIMARY byte for byte at 580 and 2150 characters |

**The limit: an in-frame document viewer never claims PRIMARY.** Selecting
inside Gmail's attachment preview in Brave and pressing the key reads the
*previous* selection. The product is reading PRIMARY correctly — the viewer
renders selectable text and never takes ownership, and X11 has no empty state
for a selection, so whatever the last owner put there stays until someone else
claims it.

**The failure mode is worse than wrong text.** The stale content is usually the
utterance already playing, so re-reading it is indistinguishable from the hotkey
doing nothing. Pressed repeatedly it reads as a dead key, and the field report
that produces is "the hotkey stops working in Gmail" — which points at the
hotkey, the daemon and the socket, none of which are involved.

**The route that works: `Ctrl+C`, then the hotkey.** Verified. Brave claims
PRIMARY *and* CLIPBOARD in the same instant on copy — both showed the same
owner, the same ownership timestamp and the same 856 bytes — so the ordinary
path reads the right text with no fallback involved. This is the documented
answer for any viewer of this kind.

**What was built in response** — see `SelectionFreshness` in Core:

- **Staleness is now detectable, via ICCCM's `TIMESTAMP` target.** Every owner
  must answer it with the time it acquired the selection, so "has this been
  re-established since I last looked" has an answer. An unchanged selection is
  reported through the `Notice` channel and still read, because pressing the key
  twice on purpose is legitimate and X11 cannot distinguish that from an
  application which published nothing.
- **`ClipboardFallback`, off by default.** Reads CLIPBOARD when the selection is
  provably stale and the clipboard was claimed more recently. It is *not* what
  makes the Gmail case work — Brave updates PRIMARY on copy — and it carries a
  real surprise for a user who copies something unrelated between presses, which
  is why it is opt-in.
- **A latent bug fixed on the way in:** `SelectionNotify` replies are matched on
  target as well as requestor. A capture now makes two conversions on one window,
  and matching the requestor alone hands the text read four bytes of server time
  that decode as plausible garbage rather than as an error.

**What the detection does *not* cover, measured rather than assumed.** It
compares against *the daemon's previous read*, which is not the same as *the
user's last selection*. If any other window claims PRIMARY in between, a stale
read looks fresh and no notice fires — observed directly: one press had PRIMARY
held by an unrelated window with 1393 bytes of unrelated content while the user
had just selected inside the viewer. Closing that gap is not possible from X11:
the daemon cannot know what was highlighted in a window that published nothing.

**The notice is invisible in the hotkey path.** `vst-ctl` writes it to stderr and
the hotkey runs detached from any terminal. Phase 6's tray is the right home for
it; a `notify-send` call in the client would do until then.

**Two facts for Phase 6, both from instrumenting rather than reading code:**

- **Chromium's PRIMARY owner is an unmapped window with no `WM_CLASS` and no
  `_NET_WM_PID`.** Any check that compares the selection owner against the
  focused window is unusable there — which bears directly on **R-6**, whose
  `_NET_WM_PID` match has the same blind spot for a Chromium-based owner.
- **The X server's own selections cannot be tested on the live display.** The
  user selecting text while a test runs takes PRIMARY away mid-case; the
  end-to-end cases run on a nested `Xephyr` display for that reason.

<a name="phase-4b"></a>

### Phase 4b — Linux host config · 0.5–1 day · **added 2026-08-15**

**Status: built 2026-08-15**, and it changed shape on the way in — see
[The product is portable](#portable) immediately below, then
[What was built](#phase-4b-built).

**Why this exists.** It is the one seam in the table above that is listed
as *not built* and was assigned to no phase, while two later phases assume it
works. Found by reading Phase 6's tab list against what the daemon can actually
do.

<a name="portable"></a>

#### Correction: the product is portable, and this phase had it wrong

**Stated by the user, 2026-08-15, and it invalidates what this phase originally
specified.** VibeSuperTonic is a *portable* application: everything it saves
lives under the directory of the executable, and on first run it picks up
whatever the last usage left there. Copy the folder to a USB stick or another
machine and it resumes with every setting intact.

This phase as written said `$XDG_CONFIG_HOME/vibesupertonic` for the config and
`$XDG_DATA_HOME/vibesupertonic` for models and logs. Those are the correct
answer for a distro-packaged application and the **wrong** answer for this one:
state under `$HOME` does not travel with the folder, so a portable install would
have silently lost every setting the moment it moved — which is the one thing
the product promises. **No XDG path is used for data anywhere.** (`$XDG_RUNTIME_DIR`
still holds the control socket, which is correct and unrelated: a socket is
per-boot session state, not saved state, and it must not travel.)

Worth recording that the Windows side has been portable since long before this
plan existed, and nobody checked: `DataPaths.BaseDir` is `AppContext.BaseDirectory`
and `DataDir` is `<BaseDir>\data`, with the registry holding only an *optional*
override pointer. So this was never a new requirement — it was an existing
product contract that the Linux plan failed to read before specifying a
different one. The layout is now mirrored exactly, taken from a real portable
install rather than from the packer:

```
VibeSuperTonic/
  vibesupertonicd            <- BaseDir
  vst-ctl
  models/onnx/               <- ModelsRoot
  models/voice_styles/
  data/settings.json         <- DataDir
  data/pronunciations.json
  data/logs/
```

**The daemon's own default was worse than the plan's.** It resolved models to
`Environment.SpecialFolder.LocalApplicationData/vibesupertonic/models` — i.e.
`~/.local/share` — so a portable folder carrying 383 MB of models beside the
binary would have ignored them and looked in the home directory. That is now
`<BaseDir>/models`, and `VST_MODELS` / `--models` remain as escape hatches
rather than as the default.

`AppContext.BaseDirectory`, never `Environment.CurrentDirectory`: the daemon is
started from a hotkey, from `vst-ctl`'s autostart, or from a shell sitting
anywhere at all, so the working directory is whatever it happened to inherit.
Verified by running the daemon from `/` and watching it resolve its folder
correctly.

The daemon has **no configuration at all**. Voice, language and step count come
from `argv`; `DaemonServer.StartSpeaking` constructs
`new SynthesisOptions(voice, language, totalStep)` and leaves `Pronunciations`
null. Three consequences, none of them visible until the phase that trips over
them:

- **The Linux product applies no pronunciation rules.** `SpeechSession` already
  threads them through `SynthTextPipeline` — the wiring is there and nothing
  fills it in. So the two platforms speak the same text differently, and harness
  step 10's cross-platform boundary agreement holds only while the Windows side
  has no rules either. That is the drift Core exists to prevent, arriving through
  the one door Core does not cover.
- **Phase 6's Tune and Pronunciations tabs have nothing to read or write**, and
  no way to tell a running daemon that anything changed.
- **`settings.json` round-tripping unknown keys** — [mechanic 4](LINUX-PORT-PLAN.md#mechanics),
  built and tested — currently protects a file the Linux side never opens.

**Work.**

1. ~~`LinuxDataPaths` on the daemon side: `$XDG_CONFIG_HOME/vibesupertonic` …
   `$XDG_DATA_HOME/vibesupertonic` for models and logs.~~ **Superseded — see
   [the portability correction](#portable).** `LinuxDataPaths` resolves
   everything against `AppContext.BaseDirectory`: `<BaseDir>/data` for
   `settings.json` and `pronunciations.json`, `<BaseDir>/models` for the model
   set, with `--data` / `VST_DATA_DIR` and `--models` / `VST_MODELS` as explicit
   overrides. It is the `IHostConfig` row of the seam table. **Not in Core** —
   it is the counterpart of the registry reader that [R-12](#r-12) keeps out,
   and Core already refuses to express a platform.
2. Load both files at startup and on demand, through Core's existing
   `PronunciationsConfig` types — the same code the Windows engine runs, which is
   the entire point of Phase 1 having collapsed the launcher's duplicate.
3. Feed `SynthesisOptions.Pronunciations` / `CompiledPronunciations` from it.
   Compile once and cache; recompiling per utterance would put a regex compile
   inside the press-to-speech budget.
4. **A protocol verb, and this is the part to get right first.** There is no way
   to change a setting on a running daemon: the verbs are `toggle`, `speak`,
   `stop`, `pause`, `resume`, `status`, `subscribe`. [R-1](#r-1) says the UI
   "sends the same verbs any external client would" — and for settings there are
   none, so Phase 6 would have reached into daemon state exactly as R-1 predicts,
   because the alternative would be no Tune tab. Add `reload` (re-read both
   files) at minimum; `config` get/set if the tab is to write through the daemon
   rather than to the file. Decide which **before** Phase 6, for the same reason
   `subscribe` was built before anything consumed it.

   **Add new types to `ProtocolJson`.** Phase 3, finding 2: NativeAOT disables
   reflection-based serialization, so a type the generated context does not know
   about fails only on the shipped `vst-ctl` and never in a test.
   `ProtocolTests.New_protocol_fields_reach_the_source_generated_serializer`
   pins the mechanism; extend it alongside the verb.

**Exit criteria.** A pronunciation rule set on Windows and a rule set on Linux
produce the same spoken text and the same word-boundary offsets for the same
input — which is checkable without a Windows machine, because
`BoundaryPlanner` is in Core and harness step 10 already holds it against the
running engine. Changing a rule and sending `reload` takes effect on the next
utterance without restarting the daemon.

**Contingency.** *This turns out to be bigger than a day* → ship Phase 5 first
and let the hotkey work against `argv` defaults. Nothing in Phases 4 or 5 needs
config; only Phase 6 does. It is placed here because doing it before Phase 6
starts is what keeps the Tune tab from being written against daemon internals,
not because Phase 5 waits on it.

**The one rule that carries over.** Linux never writes `UseDirectML`,
`DirectMLDeviceId`, or `OnnxThreads` — see [mechanic 4](LINUX-PORT-PLAN.md#mechanics). Phase 0
measured that any manual `OnnxThreads` value is about 2× worse on Linux, so the
Tune tab should not offer it as a knob at all. As built, Linux does not even
*read* those four keys.

<a name="phase-4b-built"></a>

#### What was built

| Where | What |
| --- | --- |
| `Daemon/LinuxDataPaths.cs` | Portable path resolution — `BaseDir`, `data/`, `models/`, `~`/`$VAR` expansion, a writability probe |
| `Daemon/HostConfig.cs` | Loads both files, compiles the rules once, builds `SpeechSessionOptions`, mtime-cached |
| `Core/Session/SpeechSession.cs` | `Options` is now settable, and snapshotted per utterance |
| `Core/Ipc/Protocol.cs` | `reload` and `config` verbs, `ConfigPayload`, both in `ProtocolJson` |
| `Ctl/Program.cs` | The two verbs; `config` prints one JSON line for `jq` |
| `Core/Audio/SpeechRate.cs` | The rate split and volume trim, ported from `SapiEngine` |
| `Core/Session/SpeechSession.cs` | DSP stage in the renderer: time-stretch + gain |
| `Core.Tests` | `SessionOptionsReloadTests`, `SpeechRateTests` — Core.Tests is now **768** |

**The defect this phase existed for, in one line:** `SpeechSession` had threaded
pronunciation rules through `SynthTextPipeline` since Phase 3 and **nothing ever
filled them in**, so the Linux product applied no rules at all. Every unit test
passed, because each component was correct in isolation and the gap was that no
caller supplied the input — the same shape as [trap 11](LINUX-PORT-PLAN.md#where-to-pick-up), one
layer out. `Pronunciation_rules_reach_the_model` is the test that would have
failed.

**Reload is both automatic and explicit, and the automatic half is the one that
matters.** The Windows engine re-reads on mtime, once per Speak, for the cost of
one `stat`. Matching that means an edit saved in the UI applies on the next
utterance on *both* platforms with no verb involved and no way to forget. The
`reload` verb sits on top for scripts, and because "did it pick up my change"
deserves an answer that is not "speak something and listen".

**Decided: there is no `config set`.** The plan left this open. The Windows
Control Panel writes `settings.json` and the engine only reads it; Phase 6's app
is the same shape, so keeping the UI as the single writer costs nothing and
leaves the daemon with no concurrency story to get wrong. `config` is
**read-only** and reports where configuration came from and what was made of it
— which is chiefly a *portability* answer, since "which data directory is this
instance actually using, and is it writable" is the first question when a copied
folder misbehaves.

**`SpeechSession.Options` is snapshotted, not re-read.** A reload landing
between the pipeline's rewrite and the chunker's would let the offset maps
disagree about the same text — R-2 and R-14 with a new cause. One utterance sees
exactly one generation of config; a change takes effect on the next one.
`An_utterance_in_flight_keeps_the_options_it_started_with` pins it.

**The DSP stage landed here too, on the user's call.** It was written up as a
known parity gap and then closed the same day rather than deferred, because the
gap was audible: the real portable install carries `DspRate: 1.35`, and Linux
was ignoring it, so the two platforms spoke the same `settings.json` at
noticeably different speeds.

`SpeechRate` is a **port** of `SapiEngine.ComputeSpeed` and `ApplyVolume`, not
an improvement on them — the identical argument Phase 2 settled for boundary
placement, and `SpeechRateTests` holds it against a transcription of the
engine's version across a 450-case grid. The model is driven only in its safe
range and a pitch-preserving time-stretch absorbs whatever was asked for beyond
it, which is how the DSP knob reaches past the model's 1.3× ceiling at all.

**The stage runs in the renderer, and that placement is the whole design.** The
stretch *changes the frame count*, and the consumer plans word boundaries from
`pcm.Length` — so stretching before the queue means boundaries are planned
against the audio that will actually be heard, and nothing needs retiming.
Applying it after planning would desynchronise every highlight by the stretch
ratio, silently, because [a wrong offset sounds exactly like a right
one](LINUX-PORT-PLAN.md#where-to-pick-up). It also keeps CPU work off the write path, where it
would present as an underrun rather than as slowness.

**Still unread: `InterChunkSilenceMs`.** The Windows engine writes explicit
silence between chunks through the SAPI site; the Linux session has no
equivalent step, and adding one changes chunk timing and therefore boundary
scheduling. Audible only as slightly tighter gaps, and worth doing deliberately.

> **Decided 2026-08-16: implement it, for Windows parity — and before Phase 6**,
> so that boundary scheduling is verified once against its final timing rather
> than twice. Scheduled in
> [what to do next](LINUX-PORT-PLAN.md#next).

#### Verified on the Mint box, 2026-08-15

Against a published portable folder — daemon, client, `models/` and `data/`
beside each other — with **no arguments at all**:

| Case | Result |
| --- | --- |
| Portable resolution | voice `F1`, `TotalStep` 6, chunk 180/90, 2 rules — every value from the folder |
| Run from `/` | `BaseDir` still resolved from the executable, not the working directory |
| Edit `pronunciations.json`, ask anything | rule count went 2 → 3 with **no verb sent** (mtime) |
| Edit `settings.json`, `vst-ctl reload` | voice changed `F1` → `M2` on a running daemon |
| **The real Windows `settings.json`** (copied from the portable install on this machine) | parsed on Linux: `TotalStep` 6 preserved, both rules loaded, absent `Language` key defaulted cleanly |
| Fresh install, no files | defaults, no crash, `SettingsFound: false` |
| Malformed `settings.json` | defaults **and a note naming the file and the parse error**, on stderr and in `config` |
| Read-only data directory | `DataDirWritable: false` with a note — **and the settings still loaded and applied** |
| `SIGTERM` | exit 0, "stopped", no core dump |
| **Speech, by ear, with the real rules loaded** | "3 **equal** 4", "pi **about** 3.14" — the rules audibly reach the model. This is also what found [R-15](#r-15) |

The read-only row is the one worth keeping: a portable folder can legitimately
sit on a read-only mount, and the failure mode to avoid is refusing to start,
which would turn it into a hotkey that does nothing — the same rule
[Phase 7](LINUX-PORT-PLAN.md#phase-7) already applies to missing models.

**Exit criteria.** *"Changing a rule and sending `reload` takes effect on the
next utterance without restarting"* — met, and it also takes effect without
sending anything. *"A rule set on Windows and a rule set on Linux produce the
same spoken text and the same word-boundary offsets"* — the text half is met and
is now covered by tests through the real `SynthTextPipeline`; the offsets half
follows from `BoundaryPlanner` already being shared and pinned by harness step
10, but **has not been measured with rules active on both platforms**, and the
`DspRate` gap above means the audio is not yet identical regardless.

### Phase 5 — Hotkeys · 0.25 day

**Status: built 2026-08-15, on estimate.** Deliverable is
[build/keybindings.sh](../build/keybindings.sh). Everything except the key press
itself is verified; see [What was built](#phase-5-built) at the end of the
section.

**Goal.** A global toggle key that survives desktop upgrades and a possible
Wayland future.

**Halved by Phase 3.** All the behaviour — the state machine, debounce, the
decision that a press means speak or stop — is in the daemon and tested. What is
left is writing two `gsettings` entries and checking for conflicts. There is no
new logic here at all; if this phase starts growing behaviour, something has been
put in the wrong place.

**Point the binding at an absolute path.** `vst-ctl` is NativeAOT and lives in
the install directory; a bare `vst-ctl` in a keybinding depends on the desktop
session's `PATH`, which is not the shell's and is a classic source of "works in
the terminal, does nothing on the key".

**Where the code lives — settled 2026-08-15, because two phases claimed it.**
This phase says it writes the keybindings; [Phase 7](LINUX-PORT-PLAN.md#phase-7) says `install.sh`
writes "the autostart entry, keybindings, and desktop file". One file, two
owners, and the absolute path above is only knowable at install time — so the
answer follows from the requirement: **`build/keybindings.sh`, written here,
sourced by Phase 7's `install.sh` and `uninstall.sh`.** Two functions,
`vst_bind <install-dir>` and `vst_unbind`, plus a `main` so it can be run
standalone for anyone installing by hand or on a desktop other than Cinnamon.
Phase 5's deliverable is that file and the conflict scan; Phase 7 calls it and
does not reimplement it.

**Work.** Write Cinnamon custom keybindings through the `gsettings` CLI: append
to `org.cinnamon.desktop.keybindings custom-list`, then set `name`, `command`,
and `binding` on the new path.

- `Ctrl+`` → `vst-ctl toggle` — the primary interaction.
- Ctrl+~ → `vst-ctl stop` — unconditional silence.

The client sends the verb and nothing else; all state lives in the daemon.

Scan existing bindings first and warn on conflict instead of stealing the key.
Uninstall removes exactly the entries we added, by name.

**Append, never rewrite** the `custom-list` array. Clobbering a user's existing
shortcuts is both unforgivable and very easy to do by accident.

**Exit criteria.** Shortcuts appear in System Settings → Keyboard, are editable
there, and survive a reboot. Toggle behaves per [the contract](LINUX-PORT-PLAN.md#the-hotkey-contract)
from all four states, including a double-tap during model load.

**Contingencies.**

- *Cinnamon's `binding` key type differs from GNOME's* (array-of-strings vs
  string; it has changed across versions) → detect the schema type at write
  time, fall through to the manual path on mismatch.
- *gsettings schema path differs on this Cinnamon version* → show the exact
  command to paste into System Settings → Keyboard → Custom Shortcuts. This path
  gets documented regardless: it is the supported route for anyone on KDE, XFCE,
  or any other desktop.
- Explicitly **not** building an `XGrabKey` grabber. It dies on Wayland, fights
  other apps for keys, and can't be discovered or changed in system settings.

**Known limit (R-11).** Screen lockers, some fullscreen games, and open menus
take an X11 keyboard grab, and global shortcuts don't fire under one. Not
fixable, and not worth chasing as a bug.

<a name="phase-5-built"></a>

#### What was built

One file, [build/keybindings.sh](../build/keybindings.sh): `vst_bind
<install-dir>`, `vst_unbind`, a `vst_kb_status`, and a `main` so it runs
standalone. `VST_KB_DRY_RUN=1` prints every mutating call instead of making it,
which is how the conflict path below was exercised without touching the desktop.

No behaviour, as the phase required. The script contains no rule about what a
press means — it writes two `gsettings` entries pointing at `vst-ctl toggle` and
`vst-ctl stop`, and the daemon decides the rest.

**Three mechanics were read out of Cinnamon's own settings code**
(`/usr/share/cinnamon/cinnamon-settings/bin/KeybindingTable.py`) rather than
guessed. Each would have produced a shortcut that does not work, and none is
discoverable from the schema:

- `custom-list` holds **bare ids** (`custom0`), not paths. The path is built
  from the id and read through a relocatable schema.
- The id is the **lowest free integer**, which is how Cinnamon allocates.
  Appending `custom<count>` collides the first time a user deletes a shortcut
  from the middle of their list.
- **`keybindings.js` rebuilds its grabs only when `custom-list` changes
  *value*.** Re-binding an already-registered shortcut therefore writes an
  identical list and appears to succeed while doing nothing until the next
  login. Cinnamon forces the change by toggling a `__dummy__` entry in and out
  of the list; so do we, and skip it everywhere we read.

The entry is filled in **before** its id joins `custom-list` — the reverse of
Cinnamon's own order. Cinnamon can do it the other way because its GUI creates
an entry and then waits for the user to type an accelerator; a script doing both
at once would otherwise expose a half-built entry to a rebuild triggered by its
own list write.

**The contingency about the `binding` key type was real.** On Cinnamon 6.6.9 it
is `as`, an array of strings; GNOME's equivalent is `s`. The type is detected at
write time, and anything unrecognised falls through to printed manual
instructions — the route that is documented regardless, because it is the
supported one on KDE, XFCE and everything else.

**Conflicts warn instead of stealing.** An accelerator already spoken for leaves
the entry registered with *no* accelerator, so it still appears in System
Settings for the user to assign a key to. Accelerators are normalised before
comparison, since Ctrl+~ and `<Shift>Ctrl+`` are the same key to
the desktop and different strings to a comparison — verified against real data,
where a query for `<Shift><Super>Left` correctly matched the stored
`<Super><Shift>Left`.

Neither of our two keys is taken on a stock Mint 22.3 Cinnamon session.
`<Alt>Ctrl+`` is the screen reader and is a different accelerator.

#### One bug, found by the script disagreeing with itself

`status` passed the "are we on Cinnamon" check while `bind` **failed it in the
same second**. `gsettings list-schemas | grep -q X` under `set -o pipefail` is a
race: `grep -q` exits the moment it matches, `gsettings` is still writing and
takes SIGPIPE, and the *pipeline* reports failure depending on which finished
first. The symptom is an installer intermittently telling a Cinnamon user they
are not on Cinnamon, and then printing the manual fallback.

This is the same shape as the `pa_simple` and Xlib hazards from Phases 2 and 4:
correct-looking code whose failure depends on timing, invisible to a single
test run. Reading into a variable removes the pipeline and the race with it;
20 consecutive runs then pass.

#### Verified on Mint 22.3 / Cinnamon 6.6.9

| Case | Result |
| --- | --- |
| `bind` from a clean list | two entries, correct ids, `binding` written as `as` |
| Re-`bind`, twice | **updates in place** — no duplicates, dummy toggles each run |
| Conflict against real bindings (`<Super>d`, `<Alt>Ctrl+``) | warned, named the holder, left the accelerator unassigned |
| `unbind` | removed exactly ours, keys reset to defaults |
| `unbind` with an unrelated user shortcut present | **the user's shortcut survived untouched** |
| An entry of ours the user had renamed | still found, by command rather than name |
| Missing install dir / no `vst-ctl` in it | refused, exit 64 |
| Accelerator normalisation | order, case, `<Primary>`/`<Control>`, `<Mod4>`/`<Super>` all fold correctly |

**Not verified, and it needs a person:** that the shortcuts appear in System
Settings → Keyboard, are editable there, survive a reboot, and that a real press
drives the toggle contract from all four states including a double-tap during
model load. The storage is `dconf` and the schema/path/id convention is
Cinnamon's own, so all four follow from what is verified above — but "follows
from" is not "observed", and this plan has been wrong that way before
([trap 13](LINUX-PORT-PLAN.md#where-to-pick-up)).

**Since observed.** The keys have been bound in the user's own portable install
at `~/Apps/VibeSuperTonic` and in daily use since 2026-08-15, which settles all
four. What remains unverified is nothing to do with the script: it is the
[R-11](#r-11) limit, and that is not fixable.

**Nothing is bound on this machine right now.** The bind was tested against the
scratch `vst-ctl` from a NativeAOT publish and then unbound, because binding a
key to a binary in a temporary directory is precisely the silent-no-op this
phase exists to avoid. The real bind belongs to Phase 7's `install.sh`, which
sources this file.


---

## Phase 6 preparation — the full 2026-08-15 account

The plan keeps the conclusions. This is how they were arrived at.

<a name="phase-6-prep"></a>

#### Preparation done 2026-08-15 — three things this phase would have hit on day one

The first two were found by reading the stream Phase 6 consumes against what the
Reader tab actually has to do; the third by running the daemon. All three fixed
*before* the phase rather than during it. The first two are the class the
readiness review names: seams never asked what the next phase would need from
them.

**1 · The event stream never said what its offsets indexed into.** Every
boundary reports a `SourceOffset`, and nothing on the stream carried the text.
The `subscribe` snapshot answers the *arriving mid-read* case — the one the plan
designed for — but not the *staying subscribed across utterances* case, which is
the normal one for a window left open. Confirmed against the live daemon: a
subscriber saw `Preparing`, `Speaking`, then word boundaries into a string it had
never been shown. The only workaround was a `status` round trip on every
`Preparing`, which races the next utterance.

`SessionEvent.Text` now rides on the `Preparing` transition, and on that
transition only — a selection may be 100 KB ([R-9](#r-9)) and repeating it on
Speaking and Idle would triple that on the wire to say nothing new. It is also
the earliest possible moment: it arrives with the 28 ms acknowledgement rather
than the ~750 ms first audio, so the Reader tab can paint text during exactly the
gap the tray's Preparing state exists to cover.

A latent ordering bug came with it. `CurrentText` was assigned *after* the
Preparing event was emitted, so a subscriber whose handler asked the session what
it was speaking got the **previous** utterance — or null, on the first. Harmless
while nothing subscribed; not harmless the moment a UI does.

**2 · `ApplySkip` is not reusable, and this plan was wrong to say it was.**
The text above said click-a-word-to-jump could reuse it. It cannot:
[SapiEngine.cs:678](../src/VibeSuperTonic.Engine/SapiEngine.cs#L678) is
`private static`, lives in the Windows COM host, and walks the SAPI exec plan
(`List<object>` of `SpeakChunkExec` and bookmark items, re-aligning a prefetch
task). Nothing in Core resembles it and `SpeechSession` had no seek at all.

What made it cheap anyway is that **`BoundaryPlanner.PlanChunk` already takes a
`sourceBase` that nothing had ever passed anything but `0`**. So seeking is:
render `text[offset..]`, pass the offset as `sourceBase`, and keep the whole text
as `CurrentText`. Boundaries stay in **whole-text coordinates** — there is still
exactly one coordinate space on the wire, which is the [R-2](#r-2)/[R-14](#r-14)
discipline. Speaking the fragment and letting the UI add the offset back would
have put a second space on the wire, which is precisely what both of those bugs
were.

New `seek` verb, `Request.Offset`. The daemon snaps to the start of the word
containing the offset via `BoundaryPlanner.SnapToWordStart`, which shares
`IsWordChar` with the highlighter — so a click lands on the word the highlight
drew, hyphens and apostrophes included ("well-known" is one highlight, so it is
one click target). Snapping is daemon-side for the reason the state machine is:
every client gets the same behaviour.

Seek works **after** a reading has finished as well as during one, via
`SpeechSession.LastText`, which survives the terminal event that clears
`CurrentText`. That is the ordinary Reader gesture — the text is still on screen
and the user clicks a word in it.

`seek` is deliberately **not debounced**, the one place it departs from the other
speaking verbs. The 150 ms window exists because a finger double-taps a key; a
click is aimed at a specific word, and dropping it silently would leave the user
having clicked and heard nothing.

**3 · The daemon core-dumped when the audio device was unavailable** — found by
running it against an unreachable PulseAudio server while testing the above, not
by reading anything. `PulseAudioSink` was constructed in `Main`, so the throw was
unhandled: **exit 134, a core file, and no control socket**. Every verb that
could have explained it died with the daemon, and with [R-5](#r-5)'s autostart on
top that is precisely a hotkey that silently does nothing — the failure
[Phase 7](LINUX-PORT-PLAN.md#phase-7) already fixed for a missing model set and then generalised:
*anything the daemon refuses to start for is a hotkey that silently does nothing,
and the set of such conditions should stay at "the socket is already held by
another daemon"*. An absent audio device had quietly joined that set.

`LazyAudioSink` (Core, so `Core.Tests` covers it) defers the open to the first
request that needs to play. The daemon starts, `status` / `config` / `subscribe`
answer, and `speak` fails with libpulse's own reason — "Connection refused" and
"No such entity" send a reader somewhere different, and that is the first thing a
field report needs. Retried per request rather than latched, because a server
that was down at start may be up by the time anyone presses a key. It also
refuses a device that opens at an unexpected sample rate: the clock is built from
the declared rate before the first write, and a mismatch would desynchronise every
boundary by the ratio — which [trap 12](LINUX-PORT-PLAN.md#where-to-pick-up) says nobody would hear.

Verified both ways against the real daemon: with `PULSE_SERVER` pointed at a
nonexistent socket it **stayed up**, answered `status`, and refused `speak` with
`no audio device available: could not open a PulseAudio playback stream:
Connection refused`; with a working server, speech and seek are unchanged.

The three speak paths' duplicated model check collapsed into one
`NotReadyToSpeak` on the way past — it was about to become three copies of two
conditions.

**Measured end to end** against the real daemon and a real model. Speaking
"Alpha beta gamma. Delta epsilon zeta. Eta theta iota." reported word offsets
0, 6, 11, 18, 24, 32, 38, 42, 48. Then `vst-ctl seek 20` — mid-word, inside
"Delta" at 18 — snapped to 18 and reported 18, 24, 32, 38, 42, 48: **identical to
the corresponding offsets of the full read**, which is the property that matters
and the one a fragment coordinate space would break. 785 Core tests green (17
new), and the whole-text-coordinates test was confirmed to fail when `sourceBase`
is reverted to `0`, so it is testing the mechanism rather than agreeing with it.

**The tray has 28 ms to work with, and ~750 ms to cover.** The daemon
acknowledges a press in 28 ms but first speech is ~750 ms (Phase 3, and it is
model-bound). That gap is precisely what the icon state and blip exist for, and
it is larger than the plan originally assumed — so the Preparing state is not a
detail, it is the thing standing between the user and "did that even register?".
Drive the icon off `StateChanged`, which arrives immediately, never off the first
audio.


---

<a name="one-key"></a>

## Superseded: the original one-key hotkey contract

Replaced 2026-08-15 by the two-key contract in the plan, on the user's call
after using the product. Kept because `toggle` still implements exactly this,
for the tray menu and for anything scripted against it.

### The original one-key contract, kept for `toggle`

One key does everything. **Press to speak, press again to stop.**

| State | Press does | Next state |
| --- | --- | --- |
| Idle | capture PRIMARY, start speaking | Preparing |
| Preparing (model load / first chunk) | cancel | Idle |
| Speaking | stop | Idle |
| Stopping | ignored | Stopping |

Three details this table is hiding, all of which matter:

- **Preparing counts as active.** A cold model load is 2–5 s. If the second press
  only worked once audio started, the key would feel dead for exactly the window
  where the user is most likely to press it again. Cancelling during load is the
  whole point.
- **Presses inside 150 ms of the last are ignored.** Without debounce, an
  accidental double-tap reads as speak-then-immediately-stop, which looks
  identical to "broken."
- **Selecting new text while speaking does not switch to it.** Press stops the
  current read; a second press speaks the new selection. Two presses, not one.
  This is the accepted cost of one-key operation — it is a choice, not an
  oversight. *(Superseded for the primary key by the `read` verb above, which is
  exactly this cost being declined once a second key existed to pay it with.)*

An explicit stop binding (Ctrl+~) stays available for "I don't care
what state it's in, be quiet." It is bound by default and costs nothing.

Empty selection in Idle is a no-op with a tray blip — it does not change state.
Phase 3 implements that: a failed selection puts the gate back to Idle, so the
no-op does not silently consume the next press.

**Pause is deliberately not in this table (decided in Phase 3).** The daemon has
`pause` and `resume` verbs — they cost almost nothing, since not feeding the sink
lets it drain and the playback clock stays correct by itself — but pausing is
*not* a fifth state and the key does not reach it. A press while paused reads as
Speaking and therefore stops.

The reasoning is that a five-state toggle is not a toggle. With one key, the user
has to hold a model of what the next press will do, and "speak / stop" is a model
that survives being wrong; "speak / pause / resume / stop, depending" is not. So
pause stays available to the tray menu, to D-Bus and to scripts, where there is a
distinct control to point at, and the key keeps its two meanings. Revisit only
with a second binding, never by adding a state to this table.

---


---

<a name="way-3"></a>

## Deferred: Way 3, and the option that was rejected

Way 2 is what ships. Way 3's gate is open as of 2026-08-16 and the plan
recommends waiting until after v1 — see *The gate* there. This is the design it
would move to, and the cheaper option that was considered and rejected.

### Way 3 — the end state

Identical file layout; the Core *assembly* dissolves. `src/shared/**` is
compiled into each platform's host by `<Compile Include>`, and the two trees
share no restore graph, no assembly, no version, no NuGet resolution.

The argument for going further is written in the codebase already:
[DataPaths.cs:22-26](../src/VibeSuperTonic.Engine/Settings/DataPaths.cs#L22-L26)
instructs the reader to *manually keep the file in sync* with the launcher's
mirror copy. That comment is the current isolation strategy, and it is a comment.
Linked source makes divergence impossible where a shared binary makes it merely
discouraged — and unlike a shared binary, it lets the 1.22.1 pin outlive any
decision Linux ever makes, because nothing connects them.

Cost: drift moves from compile time to CI time, and the R-2 fix must be proven
on both platforms rather than once. Both are acceptable *given* `Core.Tests`
runs on both runners, which is why Way 3 comes second.

### Why not the third option — one Core, two target frameworks

`<TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>` with conditional
package references is the cheapest change and Phase 1 would barely notice it.
It is rejected because the Windows and Linux builds stay in one project, so the
ORT bump that breaks win-x86 still lands on both flavors and CI is the only
thing standing between that and a shipped ZIP. It optimizes the step we are not
worried about.


---

<a name="review"></a>

## Review — the full write-ups

Every item below is either done or is a live constraint restated in the plan.
Kept here as the record of *why* each exists, since the reasoning is not obvious
from the task list.

## Review — issues found, and where each is handled

Every item below is now planned work in a phase above. Kept here as the record
of *why* each exists, since the reasoning won't be obvious from the task list.

<a name="r-14"></a>

### R-14 · Chunk offsets were recovered by `IndexOf`, which the chunker's own whitespace normalisation defeats — **found and fixed during Phase 1**

R-2 fixed the *map* from spoken text back to source text. It did not fix the
*index handed to* that map, and the two failures compose: a correct map applied
to a wrong index is still a wrong highlight.

[SapiEngine.cs](../src/VibeSuperTonic.Engine/SapiEngine.cs) located each chunk in
the fragment with `speakItem.Text.IndexOf(chunkText, offsetWithin)`, falling back
to a running `offsetWithin` when that returned −1. But the chunker normalises
whitespace — it splits on `\s+`, rejoins sentences with a single space, and trims
paragraphs — so a chunk built from text separated by anything other than one
space is **not a substring of its own input**. `IndexOf` then misses every time
and the fallback accumulates in rewritten coordinates, drifting against the
source by one character per collapsed separator.

Measured on the real reconstruction, against an independently-computed ground
truth:

| Input | Chunks with a wrong start offset |
| --- | --- |
| 12 sentences, single-space separated | 0 of 6 |
| 12 sentences, newline separated | **5 of 6** |
| 12 sentences, double-space separated | **5 of 6** |

Newline-separated is not an edge case; it is what a paragraph of prose looks like
in most SAPI clients, and it is what a Linux PRIMARY selection will almost always
contain.

**Fixed** with `SentenceChunker.ChunkWithOffsets`, which returns each chunk
alongside its span in the input. It relies on one invariant of the three passes —
they only ever drop or substitute *whitespace*, so the non-whitespace characters
of the chunks, concatenated, are exactly those of the input — and walks both
strings skipping whitespace to recover the true span. `Chunk` is unchanged, so
the reference-oracle tests still pin the boundaries; the invariant itself is
pinned by `ChunkWithOffsets_spans_are_exact` over the same corpus, so a future
chunker change that breaks it fails loudly instead of silently mis-highlighting.

Worth noting for Phase 6: this is exactly the bug class the Reader tab would have
made obvious and self-inflicted-looking, which is the same argument R-2 was
written on. Finding it before the Reader tab exists cost an afternoon.

<a name="r-16"></a>

### R-16 · A stop during *Preparing* left the sink holding a flush request, killing the next utterance — **found in real use 2026-08-15**

Reported as *"Ctrl+` works, Ctrl+~ stops, then Ctrl+` again does nothing until I
press it a couple more times."* The key was not unreliable; it was reliably
eating exactly one press after every stop.

`SpeechSession.Stop` sets the sink's flush flag from the *stopping* thread —
`PulseAudioSink.RequestFlush`, which exists precisely because `pa_simple` is not
thread-safe and the flush cannot happen on the caller ([Phase 2](#phase-2),
finding 1). Only the writer consumes that flag, between blocks, inside `Write`.

But a stop arriving during **Preparing** unwinds through the renderer and the
cancellation token **without ever entering `Write`**, so nothing consumed the
flag and it survived the utterance. The next utterance's first write then
tripped it, flushed, and threw `OperationCanceledException` — so that utterance
died silently, reporting `Stopped` rather than an error, and the press *after*
it worked, because tripping the flag is also what clears it.

The observable sequence, from `vst-ctl subscribe`:

```
{"Kind":"StateChanged","State":"Preparing"}
{"Kind":"Stopped"}                             <- never spoke
{"Kind":"StateChanged","State":"Idle"}
```

**Fixed** by clearing the sink in the worker's teardown on the cancelled path —
the writer thread, after the write loop has finished, which is the one place it
is both safe and guaranteed to run.

Two things worth carrying forward. **The existing tests could not have found
this**: every stop test drives the session through `Speaking`, where `Write`
does the clearing for you as a side effect, so the leak only exists on the path
the fakes never took. And it is the *third* defect in this family — a flag or
handle set on one thread and consumed on another, where the consuming path
turns out to be skippable. The others are the `pa_simple` flush that started it
and the Xlib error handler in [Phase 4](#phase-4-built). `SessionOptionsReloadTests`
now pins both halves, and both were confirmed to fail without the fix.

<a name="r-15"></a>

### R-15 · `WholeWord` silently kills every symbol rule — **existing Windows bug, found from Linux 2026-08-15**

`PronunciationsConfig.Compile` wrapped a `WholeWord` rule as `\bMATCH\b`. But
`\b` is a *transition* between a word and a non-word character, so it cannot
anchor a side that is not a word character to begin with. `\b=\b` therefore
never matches `3 = 4` — the spaces on either side are already non-word, and
there is no transition to find. It matches only `3=4`, where the digits supply
the boundaries.

The Pronunciations tab **defaults `WholeWord` to true**
([PronunciationsTab.cs:232](../src/VibeSuperTonic.Launcher/Ui/PronunciationsTab.cs#L232)),
so every symbol rule any user has ever added was born dead — and appeared to
work intermittently, on exactly the inputs where the symbol was jammed against
text.

Found by running the Linux daemon against the **real** `pronunciations.json`
from the portable install on the development machine. Both of its rules are
symbols:

| Rule | Compiled to | Fires on `3 = 4`? |
| --- | --- | --- |
| `≈` → ` about ` | `\b≈\b` | no |
| `=` → ` equal ` | `\b=\b` | no |

The user heard `3 = 4` spoken as "three dot four" — the raw `=` reaching the
model, which vocalises it as "dot". Not a Linux defect at all: this is shared
Core code and has been live on Windows for as long as the feature has existed.

**Fixed** by anchoring each side independently — `\b` is prepended only when the
match *starts* with a word character and appended only when it *ends* with one.
Word rules are completely unaffected (`kg` still compiles to `\bkg\b`, and still
does not match inside `kgs`). Covered by eight cases in
`PronunciationOffsetTests`, including the mixed case (`%s`, symbol one side and
word the other) where exactly one anchor is correct.

**Confirmed by ear, 2026-08-15**, against the real rules on the real machine:
`It weighs 5 kg and 3 = 4 and pi ≈ 3.14` was spoken with "equal" and "about" in
place, where the same sentence had previously produced "three dot four". Worth
noting how it was found — not by a test, and not by reading the code, but by a
person listening to one sentence. Every unit test passed both before and after,
because the rules under test were word rules and the defect only exists for
symbol ones. [Trap 12](LINUX-PORT-PLAN.md#where-to-pick-up) says the audio is no guide to *offset*
bugs; the converse also holds, and this is the case for it.

**This changes what the Windows product speaks**, for any install carrying a
symbol rule — in the direction the rule's author obviously intended, but it is a
behaviour change and belongs in the release notes rather than being discovered.
It wants a TestHarness run before the next Windows release; the offsets are
already covered, since a length-changing replacement is exactly what
[R-2](#r-2)'s `TextOffsetMap` handles.

<a name="r-13"></a>

### R-13 · The DirectML seam is build-time, not runtime — **found and fixed during Phase 0** → Phase 1

The plan listed `IExecutionProviderPolicy` alongside the other seams as though
the DirectML/CPU choice were a runtime decision. It is not.
`SessionOptions.AppendExecutionProvider_DML` is declared **only** in
`Microsoft.ML.OnnxRuntime.DirectML`; the CPU-only package a Linux build must use
does not have the method at all. So
[SupertonicSdk.cs:799](../src/VibeSuperTonic.Engine/Synth/SupertonicSdk.cs#L799)
does not merely take a dead branch on Linux — the file **cannot compile**.

Discovered on the first `dotnet build -r linux-x64` of the Phase 0 spike, which
is exactly the kind of thing a half-day gate is for: as written, Phase 1 would
have hit a wall on its first hour.

**Fixed already**, minimally: the DML block sits behind `#if ORT_DIRECTML`, and
the symbol is defined in
[VibeSuperTonic.Engine.csproj](../src/VibeSuperTonic.Engine/VibeSuperTonic.Engine.csproj),
so the Windows engine compiles to the same IL as before. The `#else` branch logs
and falls through to the CPU provider. Windows engine build verified green after
the change.

**Consequence for Phase 1:** `IExecutionProviderPolicy` cannot be a plain
interface with two implementations in one assembly. Either Core multi-targets
with per-RID `PackageReference` and compilation symbols, or EP selection is
hoisted out of Core into the two host projects. Decide before starting the
extraction, not during it.

**Resolved 2026-08-15 → [Platform isolation](LINUX-PORT-PLAN.md#isolation).** Hoisted: Core takes
no ORT reference at all, and the two backend projects own both the package and
the provider choice. `IExecutionProviderPolicy` does not survive as a named
seam — there is nothing left for it to abstract once the EP decision lives
entirely inside the assembly that can compile it.

<a name="r-2"></a>

### R-2 · Pronunciation rules silently corrupt word-boundary offsets — **existing Windows bug** → Phase 1

`BuildSpeakPlan` rewrites the text through the pronunciation rules and the
sanitizer, then stores the *original* source offset alongside the *rewritten*
string:

- [SapiEngine.cs:957](../src/VibeSuperTonic.Engine/SapiEngine.cs#L957) —
  `spoken = SanitizeForSynth(pron.Apply(text, pronCompiled))`
- [SapiEngine.cs:966](../src/VibeSuperTonic.Engine/SapiEngine.cs#L966) —
  `new SpeakTextItem(spoken, frag.ulTextSrcOffset, …)`
- [SapiEngine.cs:770](../src/VibeSuperTonic.Engine/SapiEngine.cs#L770) —
  `lParam = chunk.SourceCharOffset + (uint)start`, where `start` indexes the
  rewritten string

`PronunciationsConfig.Apply` returns a plain `string` with no offset map, so any
length-changing rule ("kg" → "kilograms", +7 chars) shifts every subsequent word
boundary in that fragment by the accumulated delta. `SanitizeForSynth` has the
same problem in the other direction when it strips characters.

This is live on Windows today — highlight-while-reading drifts in any SAPI
client once a length-changing rule is enabled. It has gone unreported because
few users combine both features and the drift starts small. Our Reader tab makes
it obvious and self-inflicted-looking.

<a name="r-4"></a>

### R-4 · One key, not two — **superseded by product decision** → Phase 5

Originally raised as an objection to a same-key toggle: if the key means "speak
what's highlighted now," then a second press is ambiguous when the selection has
changed.

**Overruled, correctly.** The state is not hidden — the user can *hear* whether
it is speaking. The rule is simply: speaking → stop, not speaking → speak. The
one genuine gap in the original objection was the Preparing window, which is now
explicit in [the hotkey contract](LINUX-PORT-PLAN.md#the-hotkey-contract): a press during model
load cancels, and presses inside 150 ms are debounced.

<a name="r-8"></a>

### R-8 · Stop must cancel in-flight inference, not just stop feeding audio → Phase 3

The pipeline renders ahead, so on stop there is typically an ONNX `Run` in
flight that can take seconds. Stopping the audio feed without cancelling it
leaves the daemon busy and the next request queued behind a dead utterance.
`RunOptions.Terminate` already exists in `SupertonicAdapter` for the watchdog
path — reuse it. This gets sharper with a toggle key, where stop-then-speak is
the normal two-press pattern rather than a rare one.

<a name="r-7"></a>

### R-7 · The playback clock is garbage during the initial buffer fill → Phase 2

Before the stream primes, reported latency is zero or nonsensical, so the
highlight jumps to a wrong position at every utterance start — the most visible
moment possible.

<a name="r-3"></a>

### R-3 · Core would have zero automated coverage after extraction → Phase 1

The TestHarness is System.Speech-based, so it is Windows-only and tests the
*SAPI* path. Once shared logic moves into Core, that logic has no coverage on
Linux at all, and Phase 1's own exit criteria depend on the harness. `Core.Tests`
has to land inside Phase 1, not "later" — R-2's fix needs a regression test the
day it ships.

<a name="r-5"></a>

### R-5 · Hotkey with a dead daemon is silence → Phase 3

If the daemon crashed or autostart didn't fire, the key does nothing at all,
with no feedback anywhere. Worse with a toggle: the user presses again, still
nothing, and concludes the feature is broken.

<a name="r-1"></a>

### R-1 · In-process UI will quietly couple itself to the daemon → Phase 3 + Phase 6

Putting the tray and window in the daemon process is right (the tray must
outlive the window), but it makes it trivially easy for the UI to reach into
daemon state directly instead of going through the event stream. Then splitting
them later — or adding the speech-dispatcher front-end, or any external
subscriber — turns into a rewrite. Enforced by building `subscribe` in Phase 3
and the Reader tab against it in Phase 6, in that order.

<a name="r-6"></a>

### R-6 · The Reader tab will steal its own PRIMARY selection → Phase 4

Reader text is selectable. Select any of it and our own window becomes the
PRIMARY owner, so the next press re-reads our own window instead of the document
the user was on.

<a name="r-10"></a>

### R-10 · The estimate has no slack → Effort table

The range assumes Phase 0 passes cleanly and the first tray option works.

<a name="r-9"></a>

### R-9 · Nothing stops someone selecting a whole book → Phase 4 + Phase 6

Ctrl+A in a long document then hotkey produces an unbounded queue and a lot of
RAM.

<a name="r-11"></a>

### R-11 · X11 grabs will swallow the hotkey sometimes → Phase 5, documented limit

Screen lockers, some fullscreen games, and open menus take a keyboard grab, and
global shortcuts don't fire under one. Not fixable and not worth fixing.

<a name="r-12"></a>

### R-12 · Dead weight must not follow us across → Phase 1

`LockProbe` (Restart Manager), `GpuEnumeration` (DXGI), `RenderHost`, and the
DirectML device-loss latch have no meaning on Linux, and would arrive in Core as
`OperatingSystem.IsWindows()` branches if nobody is watching.

---


---

<a name="baseline"></a>

## The Windows TestHarness baseline

What the nine steps cover, and why that decided the order of the two Phase 1
items still listed in [the convergence](LINUX-PORT-PLAN.md#convergence).


The TestHarness ran all nine steps green against 0.2.7.5 on the Win11 VM on
2026-08-15 — the Phase 1 exit gate, and it is cleared. The harness ships in
`tools/`, so re-running it before and after any change to the speak path costs
one command. It earned its keep on the first run: step 8 failed against 0.2.7.4
and the bug was real — see the [R-14](#r-14) note. Five releases had claimed
that fix.

**What the baseline does and does not cover.** Steps 1–9 exercise COM activation,
voice enumeration, sync and async speech, cancellation, SSML bookmarks, prosody,
language tags, and word-boundary offsets under five whitespace layouts and a
length-changing pronunciation rule — **all on the CPU execution provider**. The
VM has no GPU, so DirectML falls back on every run:

```
DirectML init failed, falling back to CPU:
  ... C0262002 Specified display adapter handle is invalid.
```

That distinction decides the order of the two remaining items:

- **`SpeechSession` is now safe to attempt.** It is the COM speak loop, which is
  exactly what the harness drives end to end. Run it before and after.
- **`Onnx.DirectML` is still not verifiable here.** Its entire substance is the
  ~600 lines of device-loss recovery — TDR detection, the process-local GPU
  latch, the launcher reset channel, the CPU-retry path — and none of it executes
  on a machine whose adapter handle is invalid. Refactoring it against a harness
  that silently takes the CPU branch would prove nothing. Either do it on real
  hardware, or leave it: Way 2 is a perfectly good place to stop, and this is the
  one piece with no test to stand on.


---

<a name="readiness"></a>

## Readiness reviews — 2026-08-15 and 2026-08-16

Twice, the phases ahead were re-read against what the daemon could actually do
rather than against what this document claimed. Both passes are recorded because
the *method* is the transferable part: ask each seam what the next phase will
need from it, and check every claim in the code rather than in the prose.

Eleven findings between them, all fixed or scheduled before the phase they would
have hit. The conclusions live in the plan; this is the ledger.


**Readiness review, 2026-08-15 — the code is ready, the plan was not.** Phases
4–7 were re-read against what the daemon can actually do, and four things were
found and fixed before starting rather than during:

| Found | Where it is now |
| --- | --- |
| Phase 4's "re-check `$DISPLAY` per request" cannot work — a process's environment is fixed for its lifetime, so a daemon started without a session fails every selection forever | The client forwards it: `Request.Display`, populated by `vst-ctl` on `toggle`. See [Phase 4](#phase-4) |
| `ISelectionSource` had two outcomes where R-9 needs three — a truncated selection is a success that must still report what was dropped | Widened to `SelectionResult` with a `Notice`, carried to the client as `Response.Notice` |
| **The whole config seam belonged to no phase.** The daemon reads no `settings.json` and no `pronunciations.json`, so Linux applies no pronunciation rules and Phase 6's Tune tab would have had nothing to talk to — and no protocol verb to talk with | New [Phase 4b](#phase-4b), 0.5–1 day |
| A missing model set made the daemon exit 2, so a fresh install's first key press was 5 s of silence and an unhelpful message — the exact shape of the failure [R-5](#r-5) exists to prevent | The daemon starts anyway and the speak verb explains itself. See [Phase 7](LINUX-PORT-PLAN.md#phase-7) |

Two of the four are in the class [trap 13](LINUX-PORT-PLAN.md#where-to-pick-up) already names:
criteria and mechanisms written before anything measured them. The other two are
seams that were never asked what the *next* phase would need from them, which is
cheap to check and was not done.

Two more came out of **rehearsing the landing** against a throwaway clone rather
than reasoning about it (the third, the missing git identity, is recorded with
the landing above):

- **`vst-ctl`'s NativeAOT publish was exercised nowhere.** `PublishAot` only
  applies to `dotnet publish`, so CI's `dotnet build` compiled the client
  without ever touching the native toolchain — and [Phase 7](LINUX-PORT-PLAN.md#phase-7) names the
  release run as the first place that can fail. CI now publishes it and asserts
  the result is a native ELF, which is the same guard shape as the win-x86 ORT
  assertion. It catches both a missing clang and the worse case: a silent
  fallback to a managed binary that works and costs 100 ms on every press.
- **The daemon hardcoded its version string** as `"0.2.7.5"` while
  `<VstVersion>` is documented as the single source of truth. `status` reports
  it so a stale client is diagnosable, which a hand-maintained copy defeats the
  first time it drifts. Now read from the assembly, and the fallback is
  `"unknown"` rather than a plausible-looking number.

`Onnx.DirectML` remains untouchable without real GPU hardware — see the Windows
baseline above. The RTX A2000 confirmed on the Mint box 2026-08-16 does not
change that: DirectML is a Windows API and the GPU is on the Linux host. It is
[Phase 8](LINUX-PORT-PLAN.md#phase-8)'s CUDA question, not this one.

**Readiness review, 2026-08-16 — the same exercise, one phase later.** Phases
6–8 were re-read against what the daemon and the protocol can actually do, with
every claim checked in the code rather than in this document. Seven things:

| Found | Where it is handled |
| --- | --- |
| **`Notice` never reaches a subscriber.** It exists only on `Response`, so the tray — named in `Protocol.cs` as its intended reader — cannot see it. Identical in shape to the `SessionEvent.Text` gap found in the previous pass | [Phase 6 pass 2](LINUX-PORT-PLAN.md#phase-6-pass2), seam fix 1 |
| **The process topology is unwritten, and the document assumes both answers.** `SessionEvent.cs` says the tray and window live in the daemon process; Phase 6's exit criteria describe killing them independently | [Phase 6 pass 2](LINUX-PORT-PLAN.md#phase-6-pass2), decision 1 |
| **[R-6](#r-6) may be defending against something that cannot happen** — nobody has checked whether an Avalonia window claims PRIMARY on X11 at all. Its `_NET_WM_PID` mechanism also assumes in-process, so decision 1 decides it | [Phase 6 pass 2](LINUX-PORT-PLAN.md#phase-6-pass2), measure before building |
| **The Tune tab makes the UI the second writer of `settings.json`, and Linux has none of the round-trip discipline [mechanic 4](LINUX-PORT-PLAN.md#mechanics) added for exactly this.** `HostConfig` documents that the problem "cannot arise here" *because the daemon never writes* | [Phase 6 pass 2](LINUX-PORT-PLAN.md#phase-6-pass2), seam fix 3 |
| **Nothing on Linux can obtain the models.** `ModelDownloader` is in Core, works on Linux (Phase 0 fetched all 16 entries), and its only caller anywhere is the Windows launcher. Phase 7 says the app downloads them; Phase 6's task list never mentions it, and a headless install has no path at all | [Phase 7](LINUX-PORT-PLAN.md#phase-7), and it wants a verb |
| **Two builds cannot be told apart.** `<VstVersion>` is the last *shipped* version by design, so `status` reported `0.2.7.5` from both a daemon built before the CPU-budget fix and one built after | [Phase 7](LINUX-PORT-PLAN.md#phase-7), build provenance |
| **Phase 8's benchmark verb is ordered after the tab that calls it** | [The next three things](LINUX-PORT-PLAN.md#next), item 3 |

Five of the seven are the class this document keeps rediscovering: **a seam that
was never asked what the next phase would need from it.** The cost of asking is
an hour; the cost of not asking has twice been a defect found in use.

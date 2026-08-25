# Piper as a second engine — plan

Status: **Not started. Investigation done 2026-08-19; the open decisions settled
2026-08-24. Nothing written, no dependency added, no file in `src/` touched.**
This document is the handoff. It is written so the next agent can start at
[Phase P0](#p0) without re-reading the upstream repository. **It is now the next
thing to do** — 0.2.8 (the tarball) and 0.2.9
([the AppImage](LINUX-PORT-PLAN.md#phase-9)) both shipped 2026-08-24, which was
the whole of what stood in front of it.

The proposal is to add [Piper](https://github.com/OHF-Voice/piper1-gpl) **beside**
Supertonic, not in place of it. Supertonic stays the default and keeps the
product its name. Piper buys three things Supertonic cannot: a maintained
upstream, ~130 voices across 40+ languages instead of ten styles, and a ~63 MB
model where Supertonic needs ~830 MB resident.

The route chosen is **not** to embed Piper the program. It is to run Piper's
`.onnx` on the ONNX Runtime this repository already ships, from C#, and to keep
espeak-ng — the only GPL-3.0 component, and the only hard part — at arm's length.
Everything below follows from that.

Written 2026-08-19. Not yet worked.

**Six decisions the investigation deliberately left open were settled by the user
on 2026-08-24, and one of them changes the route.** The licence posture is
**GPL-3.0-or-later**, which collapses [D1 to D2](#routes): espeak-ng is
P/Invoked, not held at arm's length in a sidecar, and the plan gets *shorter*
rather than longer. The rest — native sample rates, Piper's own speed control,
which voice is default, what the catalog contains — are in
[Decisions](#decisions) and each one removes work. **This ships as 0.3, after
[Phase 9](LINUX-PORT-PLAN.md#phase-9) — the AppImage — shipped as 0.2.9**, which
it did on 2026-08-24. The order was never arbitrary: the per-voice store has to
be designed against the data directory that phase decides on, and now that
directory is decided.

---

<a name="decisions"></a>

## Decisions — settled. Do not re-litigate these

| Decision | What it means in practice |
| --- | --- |
| **Piper is added, never substituted** — 2026-08-19 | Supertonic remains the default voice family and the product identity. A user who never opens the voice list must not be able to tell this landed |
| **We run the model, not the program** — 2026-08-19 | A Piper voice is a VITS graph with four inputs. We call it on our own ORT session. We do **not** link `libpiper`, spawn `piper`, or ship a Python interpreter. [Why](#why-not-libpiper) |
| **espeak-ng is a separate process, not a linked library** — 2026-08-19 | It is GPL-3.0-or-later and it is statically linked into everything upstream ships. Isolating it in its own executable is what keeps [LICENSE](../LICENSE) MIT. If the licence posture changes ([open](#open)), this collapses to a P/Invoke and the plan gets *shorter* — so build the seam, not the assumption |
| **Licence posture is deliberately undecided** — 2026-08-19, by the user | Do not pick one. [Phase P0](#p0) and [P1](#p1) are pure research and commit us to nothing; the posture is decided after P1 produces a parity number and P2 produces an RTF. Anyone who "resolves" this before then has skipped the only two phases that could kill the project |
| **One seam, both platforms** — 2026-08-19, by the user | `PiperSynthesizer` is written once, in a platform-neutral project, against the existing [ISynthesizer](../src/VibeSuperTonic.Core/Synthesis/ISynthesizer.cs). Not a Windows feature ported later, and not a Linux-only experiment. This is why P0 is a spike and not a branch of the engine |
| **Phoneme parity is the go/no-go, and it is measured second** | Not last. See [P1](#p1) and [trap 1](#traps) |
| **Voices download per-voice, on demand, and we ship none** | Same contract as the Supertonic weights: the user accepts the terms, the bytes arrive from Hugging Face, the archive contains nothing. [CLAUDE.md](../CLAUDE.md) already binds this for both packers |
| **The licence posture is GPL-3.0-or-later** — 2026-08-24, by the user | Settled ahead of [P2](#p2), which was where this plan parked it. It selects [route D2](#routes): `libespeak-ng` is bundled and P/Invoked, there is no sidecar process and no IPC protocol to design, and the phonemiser seam has one implementation instead of two. What it costs, precisely, is in [What GPL-3.0 actually binds](#gpl) — and it is a **one-way door**, which the user was told before answering |
| **Piper voices render at their own sample rate** — 2026-08-24, by the user | 16 kHz or 22.05 kHz, straight to the sink, with no resampling to 44.1 kHz. The CPU cycles are the reason and they are the right reason: a resampler on every chunk buys nothing a person can hear from a voice that was trained at that rate. The audio sink follows the voice; [what that touches](#p3) is one class |
| **Speed comes from Piper, not from our DSP** — 2026-08-24, by the user | `length_scale` is the model's own rate control and it was trained with it. [SpeechRate](../src/VibeSuperTonic.Core/Audio/SpeechRate.cs)'s split between a model speed and a pitch-preserving time-stretch exists because Supertonic degrades outside roughly [0.9, 1.3]; that is a fact about Supertonic, not about VITS. On the Piper path the stretch is off by default and the whole rate lands on `length_scale` — cheaper *and* better, and measured in [P2](#p2) rather than assumed |
| **The default Piper voice is the highest quality tier available** — 2026-08-24, by the user | When a language offers `high`, that is what the catalog offers first and what "install this voice" means. The tier is a control the client can change, never a decision made for them silently — the smaller tiers stay one click away, with their size and rate shown. Supertonic remains the *product* default; this is about which Piper voice a person who wants Piper gets |
| **The catalog is a curated, hash-pinned list we ship** — 2026-08-24, by the user | Roughly thirty voices, chosen for language coverage and quality, pinned by size and SHA-256 exactly as [models-manifest.json](../models-manifest.json) pins the Supertonic weights, generated by a build script and shipped in the archive. Not the live 1000-entry upstream index: 130 voices "is not a dropdown" was already this document's own open question, integrity would depend on an index we do not control, and a curated list is the only version of this that can be *reviewed* — [trap 7](#traps) requires reading each voice's MODEL_CARD, which is possible for thirty and theatre for a thousand |
| **The first-run screen does not change** — 2026-08-24, by the user | It still downloads Supertonic behind the OpenRAIL-M acceptance and says nothing about Piper. A second engine offered to someone who has not heard the first one speak is a choice with no basis. Piper is found where voices are found: the [Voices tab](#ui) |
| **Linux first; Windows adoption stays design-only** — 2026-08-24, by the user | Unchanged in substance from *one seam, both platforms* above, and narrowed in scope: `PiperSynthesizer` is still written once against `ISynthesizer` with no Linux-only types, and the catalog still lives in Core — but the SAPI token work in [P4](#p4) is not done this round. What we owe Windows now is that adopting it later is *additive*, not a rewrite |

---

<a name="gpl"></a>

## What GPL-3.0 actually binds — and what it does not

Written 2026-08-24, when the posture was decided. Precision here is cheap and
the alternative is discovering it during P5.

- **The repository's own source can stay MIT.** MIT is GPL-compatible: our files
  keep their licence and their headers, and the *combined work we distribute*
  goes out under GPL-3.0-or-later because it links a GPL-3.0-or-later library.
  Nobody has to relicense a single `.cs` file.
- **The release archive is what changes**, and only from 0.3 onward. The 0.2.8
  tarball and the 0.2.9 AppImage contain no espeak-ng and are unaffected — which
  is worth stating in their release notes, because "the project became GPL" is
  the thing a reader will otherwise conclude retroactively.
- **The obligation is source availability**, discharged the way everything else
  here is: the exact espeak-ng source we built, offered from the same place the
  binary is, plus a `COPYING`/`LICENSE-PHONEMIZER.txt` in the archive naming the
  version and the terms. A written offer valid three years is the fallback, not
  the plan.
- **The Windows ZIP is unaffected until it ships a phonemiser.** Piper on
  Windows is not this round; the day it is, that archive inherits the same terms.
- **What the user bought with this**, and it is the practical half: no external
  dependency. A sidecar meant "install espeak-ng first", surfaced in the UI,
  refused at the point of choosing a voice, and different on every distro. A
  bundled library means the AppImage speaks Piper out of the box. That is the
  trade — a licence obligation we can discharge mechanically, against a support
  burden we could not.

The voices are a **separate** licence axis and are unchanged by any of this —
[trap 7](#traps).

---

<a name="where"></a>

## Where to pick up

### Status by phase

| Phase | State |
| --- | --- |
| P0 · Prove the graph runs | **Next.** Half a day. Kills routes A/B/C permanently if it passes |
| P1 · Phoneme parity | Not started. **The go/no-go.** Everything after it is ordinary work |
| P2 · Measure | Not started. RTF, cold load, resident set, on both boxes. **No longer ends with the licence decision** — that is [settled](#decisions). What it settles instead is the speed question: `length_scale` against our time-stretch, on real audio |
| P3 · `PiperSynthesizer` + the options refactor | Not started. Forces the `SynthesisOptions` question, and carries the one class the [native-rate decision](#decisions) touches — [see below](#p3) |
| P4 · Catalog, download, and the [Voices tab](#ui) | Not started. The bulk of the calendar time, and the least risky part. SAPI tokens are **out of this round** |
| P5 · Packaging | Not started. **Unblocked:** [pack-tar.sh](../build/pack-tar.sh) landed 2026-08-22 and the AppImage lands in [Phase 9](LINUX-PORT-PLAN.md#phase-9). What is left here is the GPL obligations and a second `LICENSE-MODELS` story |

<a name="next"></a>

### What to do next, in order

**Do P0 and P1 before planning anything else.** They are cheap, they are pure
research, and between them they answer the only two questions that can end this.
Every estimate in P3–P5 is worthless until P1 has a number.

**Before either, check the ground has not moved.** This document's
[upstream facts](#reference) were checked 2026-08-19 and are dated for that
reason: confirm the voice JSON still carries `phoneme_id_map` in the shape P0
assumes, and that the voice you fetch still exists at the pinned revision. Ten
minutes, and it is the difference between a spike that fails on the model and one
that fails on a URL.

> **Re-checked 2026-08-25, and it has not moved.** `rhasspy/piper-voices` at
> `v1.0.0` still resolves — both `en_US-lessac-medium.onnx` and its `.onnx.json`
> answer 200 at the URL [P0](#p0) names. The JSON is the shape P0 assumes:
> `audio.sample_rate` 22050, `inference` carrying `noise_scale` 0.667,
> `length_scale` 1 and `noise_w` 0.8, `num_speakers` **1** — so the `sid` input
> is absent for this voice and the graph has three inputs, not four — and a
> `phoneme_id_map` of 154 entries. Re-take it rather than trust it if this line
> is more than a month old.

**And do not start before 0.2.9 has shipped.** ~~The per-voice store is a
directory under the models root, and [Phase 9](LINUX-PORT-PLAN.md#phase-9) is the
phase that decides where the models root *is* when the product is an AppImage.~~
**Satisfied 2026-08-24.** Both artifacts shipped, and the store's rule is now a
decided thing to design against rather than a moving one: `--data` /
`$VST_DATA_DIR`, then an existing store beside the AppImage, then an existing
`$XDG_DATA_HOME/vibesupertonic`, then created beside the image if that directory
is writable, else under XDG. A per-voice directory hangs off whichever of those
won — never off a path computed a second time.

<a name="p0"></a>

### Phase P0 — Prove the graph runs · half a day

**Work.** A new spike beside the existing ones — `spike/piper-render`, modelled on
[spike/linux-render](../spike/linux-render), which is platform-neutral for the
same reason this one must be. It references `Microsoft.ML.OnnxRuntime` directly
(spikes may; [Core may not](#traps)).

Fetch one voice by hand — `en_US-lessac-medium.onnx` and its `.onnx.json` from
`https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium/`.
Do **not** wire it to the downloader yet.

Hardcode a phoneme-id array captured out of `python -m piper` or
`espeak-ng -q --ipa`. Run the session. Write a WAV. Listen.

The graph is trivial and is the whole reason this route exists:

| Input | Type | Notes |
| --- | --- | --- |
| `input` | `int64[1,N]` | Phoneme ids |
| `input_lengths` | `int64[1]` | `N` |
| `scales` | `float32[3]` | `noise_scale`, `length_scale`, `noise_w` — **in that order**; defaults live under `inference` in the voice's `.onnx.json` |
| `sid` | `int64[1]` | **Only** when `num_speakers > 1` |

Output is `float32` audio at `audio.sample_rate` from the same JSON — 22050 for
medium, 16000 for low. Upstream then peak-normalises and clips; reproduce that or
you will chase a level difference that is not a bug.

**Exit criteria.**

- A WAV that sounds like the voice, rendered by our ORT, with no Piper code in
  the process.
- The same spike runs on **both** boxes — Windows and Mint — from the same
  source. This is the "one seam" decision being tested on day one rather than
  assumed.
- Written down: does DirectML accept this graph, and is it faster than CPU? Free
  to learn here, expensive to discover in P3.

**If this passes**, `libpiper`, a native host process, and portable Python are all
off the table permanently and this document's [route comparison](#routes) becomes
history rather than a decision.

<a name="p1"></a>

### Phase P1 — Phoneme parity · the go/no-go

**This is the project.** Everything else is ordinary engineering.

**Work.** Build espeak-ng and **P/Invoke `espeak_TextToPhonemes` directly** —
the [licence decision](#decisions) removed the sidecar, so there is no process to
launch, no protocol to design and no serialisation to get wrong. Read upstream's
`espeakbridge` C extension anyway: it is ~200 lines and it is the specification
for what to call and in what order. What it does, and what a
reimplementation gets wrong:

- Clause terminators: `,` `;` `:` are appended to the phoneme output with a
  trailing space and do **not** end a sentence. Everything else does. Text with
  no final punctuation is still one complete sentence.
- Language-switch markers matching `\([^)]+\)` are stripped.
- Phonemes are NFD-normalised, so accents decompose into separate characters
  **before** the id lookup.
- Ids are assembled as BOS, PAD, then each phoneme followed by PAD, then EOS —
  the interleave is not optional and a model fed a bare id sequence produces
  audio, just wrong audio.

Then **diff id sequences against `python -m piper`** over a corpus of several
hundred sentences across several languages, and pin it as a test.

**Exit criteria.**

- A byte-identical id sequence for every sentence in the corpus, or a written
  account of every divergence and why it is acceptable.
- The comparison is a **test in the repository**, not a session's transcript.
  This is the one piece of this project that can rot silently.

**If this fails**, stop. There is no fallback: no non-GPL phonemiser produces the
phoneme set these voices were trained on. The `phoneme_type: "text"` voices are a
handful, and Misaki / openphonemize target different models entirely. A partial
pass is not a pass — see [trap 1](#traps).

<a name="p2"></a>

### Phase P2 — Measure · half a day

**Work.** RTF, cold-load time and resident set per voice, on both boxes, against
the Supertonic numbers already recorded: **RTF 0.193 on Mint, 0.294 on Windows,
~830 MB resident.** Warm and cold, x64 and — if P0 got that far — x86. Add
first-audio latency, because that is the number [8b](LINUX-PORT-PLAN.md#phase-8b-landed)
had to buy a GPU to move: **802 ms on CPU, 77 ms on CUDA.** A VITS render is one
`Run`, so the honest expectation is that Piper beats the *GPU* number on the CPU —
and an expectation that specific is worth writing down before it is measured.

**And settle the speed question with audio, not reasoning.** The
[decision](#decisions) is that `length_scale` carries the whole rate on this path
and our time-stretch is off. Verify it the only way it can be verified: the same
sentence at 1.0, 1.35 and 1.6, through `length_scale` alone and through the
stretch, listened to. If `length_scale` degrades somewhere the way Supertonic
does past 1.3, the ceiling belongs in the voice's metadata rather than in
`RateClampCeiling`, which is a Supertonic constant.

**Exit criteria.** A table, and a stated speed ceiling per quality tier or a
statement that there is not one.

<a name="p3"></a>

### Phase P3 — `PiperSynthesizer` and the options refactor

**Work.** A second `ISynthesizer`. The interface is two members and already says
what it needs to: cancellation is part of the contract, and `SampleRate` may
require loading the model to answer — both of which Piper satisfies more easily
than Supertonic does, because a VITS render is one `Run` of well under a second
rather than a multi-second diffusion loop.

**The native-rate decision lands here, and it is one class.** Piper voices are
16 kHz or 22.05 kHz and go to the sink unresampled. Nothing above the sink needs
changing, which is worth checking rather than hoping: `PlaybackClock` is built
per utterance from `_sink.SampleRate`
([SpeechSession.cs:344](../src/VibeSuperTonic.Core/Session/SpeechSession.cs#L344)),
and the write block and the inter-chunk silence are computed from it too
([:453](../src/VibeSuperTonic.Core/Session/SpeechSession.cs#L453),
[:461](../src/VibeSuperTonic.Core/Session/SpeechSession.cs#L461)). So the clock
and every scheduled boundary already follow the sink.

What does not follow is
[LazyAudioSink](../src/VibeSuperTonic.Core/Audio/LazyAudioSink.cs): its rate is
fixed at construction — 44100, from
[Program.cs](../src/VibeSuperTonic.Daemon/Program.cs) — and it *asserts* that the
device it opened agrees, which is a guard worth keeping. It needs to be able to
re-tune: drop the open sink and reopen at the new rate. **Between utterances,
never inside one** — the third case of the rule that already governs the
device-loss reconnect and the provider switch, for the identical reason, and the
place to enforce it is the same idle check
([ProviderSwitchingSynthesizer](../src/VibeSuperTonic.Daemon/ProviderSwitchingSynthesizer.cs)).

**Speed is `length_scale`.** `SpeechRate.Compute` splits a requested rate between
a model speed and a pitch-preserving stretch because Supertonic degrades outside
roughly [0.9, 1.3]. On this path the whole rate goes to the model and
`StretchFactor` is 1.0, so `Sonic` never runs — which is the CPU the
[no-resampling decision](#decisions) was protecting, given back a second time.

The unavoidable design work is
[SynthesisOptions](../src/VibeSuperTonic.Core/Synthesis/ISynthesizer.cs#L52): it
is Supertonic-shaped. `TotalStep`, `Language` and `Speed` have no Piper meaning;
`length_scale`, `noise_scale`, `noise_w` and `speaker_id` have no Supertonic
meaning. Doing this with a real second implementation in hand is the point of
doing P3 after P0 rather than before it.

<a name="p4"></a>

### Phase P4 — Catalog, download, and the Voices tab

The bulk of the time, and the least likely to surprise anyone. Per-voice
on-demand download through Core's own `ModelDownloader` — resume, mirrors and
hash checking already exist and get one caller more, not a second implementation.
**SAPI tokens are out of this round** ([decision](#decisions)); what remains of
[the token explosion](#casualties) is that the catalog must not be shaped in a
way that makes per-voice opt-in registration harder later.

Where the pieces go, and this is the part that keeps Windows additive:

| Piece | Where | Why there |
| --- | --- | --- |
| Voice id, parsing, formatting | Core | `supertonic:M1` / `piper:en_US-lessac-high`. A string that crosses the settings file, the protocol and two platforms belongs where both can reach it |
| The pinned catalog and its integrity | Core | Same shape as `Manifest`/`ModelDownloader`, and pointedly not a second copy of them |
| Install / remove / what-is-present | Core | Pure filesystem work over a models root. `models/piper/<id>/` keeps the packer's *no models in the archive* assertion true by construction |
| `PiperSynthesizer`, the ORT session, the phonemiser | the backend project | [R-13](LINUX-PORT-ARCHIVE.md#r-13). Core takes no `PackageReference`, and that is not negotiable |

<a name="ui"></a>

#### What the window becomes

Designed 2026-08-24 with the user, against the window
[Phase 6](LINUX-PORT-PLAN.md#phase-6-landed) built. Tabs go from **Reader | Tune
| Pronunciations | Status | About** to **Reader | Voices | Tune | Pronunciations
| Status | About**.

```
Voices

INSTALLED
 ●  Supertonic        M1 … F5 · 31 languages · 383 MB · OpenRAIL-M   [voice ▾][language ▾]
 ○  en_US-lessac      piper · high · 22 kHz · 110 MB · MIT           [Use][Sample][Remove]

AVAILABLE                                   [search][language ▾][quality ▾]
    de_DE-thorsten    piper · high · 22 kHz · 110 MB · CC-BY-4.0     [Download]
    …
```

**Every row is a verb**, because that is the rule the window is built on
([MainWindow](../src/VibeSuperTonic.Ui/MainWindow.cs)): `vst-ctl voices`,
`vst-ctl voice install <id>`, `vst-ctl voice remove <id>` — the install streams
progress the way `benchmark` already does, so the tab reuses that client path
rather than inventing one — and `vst-ctl speak --voice <id>` behind *Sample*.
*Use* writes `settings.json`, which is the one stated exception: configuration is
a file with exactly one writer.

**Quality tier is shown, never chosen silently.** The catalog offers `high` first
per the [decision](#decisions); the other tiers are listed under the same voice
with their size and rate, because 110 MB against 20 MB is a choice a person on a
metered connection is entitled to make.

**Each voice's licence is shown before its bytes arrive**, and the download is
gated on it — the same principle as the OpenRAIL-M screen, and a *different*
licence, per voice ([trap 7](#traps)).

Changes elsewhere, all of them small and all of them required by the parity rule:

- **Tune** — the Voice text box becomes a picker over *installed* voices.
  `Language` and `Model steps` grey out with a sentence when the selected voice is
  a Piper voice: one voice, one language, no diffusion steps. A control that
  cannot apply must say so; the tab already does this for `MaxCpuPercent`.
- **Status** — the engine, the voice's native sample rate (which is now the
  sink's rate, and therefore a fact worth reporting), and the provider.
- **Settings key** — a new engine-qualified `VoiceId` that supersedes
  `DefaultVoice` when present. The Windows engine reads the same file and keeps
  speaking `M1`, carrying the new key through `[JsonExtensionData]` untouched.
  That is exactly what the extension-data discipline was landed for, one release
  before there was a writer.
- **`benchmark`** — `MachineFacts` records the *engine*, so a profile measured on
  Supertonic reports "measured on supertonic, this daemon runs piper" and falls
  back, instead of silently applying a thread count from a different cost curve.

<a name="p5"></a>

### Phase P5 — Packaging

Both Linux artifacts — [pack-tar.sh](../build/pack-tar.sh) landed 2026-08-22 and
the AppImage lands in [Phase 9](LINUX-PORT-PLAN.md#phase-9), which builds from
the tarball's composed tree, so this phase edits one packer and inherits the
other. `LICENSE-PHONEMIZER.txt`, the espeak-ng source offer and the archive's
GPL-3.0-or-later notice are [the obligations](#gpl), discharged mechanically.

Two assertions to add beside the existing five, both in the shape those five
already have — *the failure it catches is silent*:

- **No voice models in the archive.** Free if voices live under
  `models/piper/<id>/`, and worth asserting anyway, because the day someone
  stages a voice for testing is the day it ships.
- **The bundled `libespeak-ng` and its data are both present and agree.** A
  library without its `espeak-ng-data` produces no phonemes and therefore no
  audio, from an install that looks complete.

---

<a name="traps"></a>

## Traps

Found during investigation, before any code was written. Each is a specific way
this goes wrong quietly.

1. **A phoneme mismatch sounds like a voice, not like a bug.** Wrong phonemes
   produce fluent, confident, wrong audio — never an exception, never a log line.
   This is the same class as [trap 12](LINUX-PORT-PLAN.md#traps) in the port plan:
   *the audio is no guide*. P1 is a numeric diff for exactly this reason, and it
   is scheduled second so it cannot become the thing nobody got to.
2. **Two `onnxruntime.dll` in one process, and the wrong one wins silently.**
   [Engine.csproj](../src/VibeSuperTonic.Engine/VibeSuperTonic.Engine.csproj)
   pins ORT DirectML **1.22.1**; `libpiper` downloads and links ORT **1.22.0
   shared**, same file name. In an NVDA process whichever loads first serves
   both. The C API is stable enough that it would probably *work*, which is worse
   than failing. The chosen route has one ORT and never meets this — do not
   reintroduce it by "just linking libpiper for now".
3. **`libpiper` has no win-x86 target.** Its CMake handles win-x64, linux-x64,
   linux-arm, macOS. The engine ships x86 for Balabolka and some Narrator paths,
   and [CLAUDE.md](../CLAUDE.md) calls skipping it a silent break. Our route
   inherits x86 for free because ORT 1.22.1 still ships `win-x86`
   ([trap 4](LINUX-PORT-PLAN.md#traps)) — that pin is now load-bearing for two
   reasons, not one.
4. **There are no prebuilt `libpiper` binaries.** Releases carry Python wheels
   only. Anyone planning to "just drop in the DLL" is planning to add CMake and
   MSVC to a build that is `dotnet publish` end to end, plus an espeak-ng source
   build during configure.
5. **`alignments` requires a patched model.** The `piper_audio_chunk.alignments`
   field is the thing that could finally replace the proportional rule in
   [BoundaryPlanner.cs:18-23](../src/VibeSuperTonic.Core/Audio/BoundaryPlanner.cs#L18-L23)
   — which says so itself, in a comment written before anyone had heard of this.
   Upstream's own C docs say stock voices do not carry it. Treat per-phoneme
   highlighting as a *later, conditional* prize, never as a P3 exit criterion.
6. **Core still takes zero `PackageReference`s.** Unchanged and unnegotiable —
   [R-13](LINUX-PORT-ARCHIVE.md#r-13). `PiperSynthesizer` lives with the backends,
   not in Core, for the same reason `CpuSynthesizer` does. Spikes are exempt.
7. **Two licence axes, not one.** The engine is GPL-3.0-or-later; the *voices*
   are separate, vary per voice, and upstream's own VOICES.md says Piper is
   "intended for personal use and text to speech research only". Read each
   voice's `MODEL_CARD` before it goes in a catalog. This maps onto the existing
   OpenRAIL-M download-on-first-run machinery, but it is a **second**
   `LICENSE-MODELS.txt` story, not the same one.
8. **Upstream's normalisation is per-chunk peak normalisation.** Enabled by
   default. Chunk-to-chunk level jumps are a plausible symptom of reproducing it
   naively across our own [SentenceChunker](../src/VibeSuperTonic.Core/Text/SentenceChunker.cs)
   boundaries. [RenderSanity](../src/VibeSuperTonic.Core/Audio/RenderSanity.cs)
   is where that would surface.

---

<a name="casualties"></a>

## What breaks regardless of route

Not risks — consequences. They are the actual work in P3/P4, and none of them are
avoided by choosing a different integration route.

- **Mid-utterance language switching cannot survive on the Piper path.**
  [SupertonicLanguages.cs](../src/VibeSuperTonic.Core/Synthesis/SupertonicLanguages.cs)
  exists because *one* Supertonic model speaks 31 languages, so a SAPI
  `SPVSTATE.LangID` becomes a `<de>…</de>` wrapper inside one render. Every Piper
  voice speaks exactly one language. SSML `xml:lang` mid-text would need a voice
  swap mid-stream. Decide this deliberately and write it in the release notes; do
  not let it be discovered.
- **SAPI token explosion — deferred, not solved.**
  [Voices.cs](../src/VibeSuperTonic.Launcher/Voices.cs) is a static array of ten
  and `Registration` writes them all. 130 voices cannot all become registry
  tokens; it needs per-voice opt-in install, a dynamic token writer, and a
  `TokenSchemaVersion` bump — which by design marks every existing install as
  needing repair. **Out of this round** ([decision](#decisions)). What it still
  binds today: the catalog's shape must not make that work harder, which means an
  installed voice has to be enumerable without a running daemon.
- **Sample rate becomes per-voice and changes mid-session** — 16000 or 22050 by
  quality tier — **and it is not resampled** ([decision](#decisions)).
  `ISynthesizer.SampleRate` is already per-instance and `PlaybackClock` is already
  built from the sink's rate per utterance, so the interface and the clock hold.
  [LazyAudioSink](../src/VibeSuperTonic.Core/Audio/LazyAudioSink.cs) is the one
  that does not: its rate is fixed at construction and it asserts the device
  agrees. [P3](#p3) has the shape of the change and the rule it must obey.
- **The manifest schema is wrong for this.**
  [models-manifest.json](../models-manifest.json) is a flat, pinned, hashed list
  for exactly one model set. Piper needs per-voice on-demand fetch against
  upstream's own voice index. The integrity discipline generalises well; the
  schema does not.
- **Text normalisation moves.** espeak-ng expands numbers, dates, currency and
  abbreviations in 40+ languages as part of phonemisation — which
  [SynthTextPipeline](../src/VibeSuperTonic.Core/Text/SynthTextPipeline.cs) does
  not do at all today. A genuine gain, and also a behaviour difference between
  the two engines that users will hear and report as an inconsistency.

---

<a name="routes"></a>

## The routes, and why this one

Kept because the next agent will be asked "why not just link it?" at some point,
probably by a future version of me.

| | Route | GPL | win-x86 | ORT clash | Build cost |
| --- | --- | --- | --- | --- | --- |
| A | P/Invoke `libpiper` in-proc | Infects everything | ✗ | Yes | CMake + MSVC |
| B | Native host process + IPC | Arm's length | ✓ | Avoided | CMake + MSVC |
| C | Portable Python + `piper-tts` | Arm's length | ✓ | Avoided | None |
| D1 | C# inference + espeak-ng sidecar | Arm's length | ✓ | None | espeak-ng only |
| **D2** | **C# inference + P/Invoke espeak-ng** | **Accepted, deliberately** | **✓** | **None** | **espeak-ng only** |

<a name="why-not-libpiper"></a>

**Why not `libpiper` (A/B).** It brings a second ORT, no 32-bit target, and a
C++ toolchain, to run a graph we can already run. B pays all of that and adds an
IPC hop.

**Why not portable Python (C).** Embeddable Python ~15 MB, `piper-tts` wheel 34
MB, onnxruntime ~15 MB, numpy ~25 MB — roughly **110 MB onto an 80 MB ZIP**, plus
interpreter startup and a second ONNX stack we do not control. Fine as a
reference implementation to diff against in P1. Wrong to ship.

**D2 is D1 with the sidecar removed**, and it became correct on 2026-08-24 when
the [licence posture was decided](#decisions). D1 is kept in this table because
the reasoning that produced it is still the reasoning that justifies D2: the
sidecar was never about performance, only about the licence, and paying for it
after the licence question is answered would be paying for nothing.

---

## Non-goals

- **Replacing Supertonic.** Stated at the top; restated here because it is the
  thing most likely to drift.
- **Piper's own sentence splitting.** We keep
  [SentenceChunker](../src/VibeSuperTonic.Core/Text/SentenceChunker.cs) and
  [TextOffsetMap](../src/VibeSuperTonic.Core/Text/TextOffsetMap.cs) in charge,
  because the entire highlight feature is built on them. Handing text to
  something that splits it differently is how offsets break, and
  [R-14](LINUX-PORT-PLAN.md#traps) is the precedent for how long that hides.
- **Training or fine-tuning voices.** Upstream supports it; we are a consumer.
- **The `zh` / `ja` / `he` phonemisers.** Upstream needs g2pW, OpenJTalk and a
  rule-based Hebrew path — three more dependencies for three more languages. Not
  in the first cut; espeak languages only.

---

<a name="open"></a>

## Open decisions

- ~~**Licence posture.**~~ **Decided 2026-08-24 by the user: GPL-3.0-or-later**,
  ahead of P2 rather than after it. Route D2. [What it binds](#gpl).
- **Whether `alignments` is worth a patched model** — and whether we would then be
  shipping voices upstream does not, which is a hosting and provenance question,
  not a technical one. Revisit only after P4.
- ~~**Which voices ship in the catalog's default view.**~~ **Decided 2026-08-24:
  a curated, hash-pinned ~30, highest quality tier first.** What is still open is
  *which* thirty — a product answer, made once, with each MODEL_CARD read
  ([trap 7](#traps)).
- **Whether Piper eventually becomes the Linux default** on resident-set grounds
  alone. 830 MB against ~63 MB is the kind of gap that deletes the port plan's
  open question about releasing the ONNX session on an idle timeout. Not for this
  round.

---

<a name="reference"></a>

## Reference — upstream facts, checked 2026-08-19

Recorded so nobody re-fetches them, and dated so nobody trusts them forever.

| | |
| --- | --- |
| Repository | `github.com/OHF-Voice/piper1-gpl` — `rhasspy/piper` was MIT, archived Oct 2025 |
| Latest | v1.7.0, published 2026-08-15. Three releases in the preceding month |
| Licence | **GPL-3.0-or-later** |
| PyPI | `piper-tts`, wheels ~34 MB (espeak-ng data included). Base deps: `onnxruntime<2,>=1`, `pathvalidate` |
| `libpiper` | espeak-ng **static**, onnxruntime **1.22.0 shared**, win-x64 / linux-x64 / linux-arm / macOS. No prebuilt binaries in releases |
| C API | `piper_create` · `piper_synthesize_start` · `piper_synthesize_next` · `piper_free`. Pull-based iterator, no cancellation parameter |
| Voices | `huggingface.co/rhasspy/piper-voices`, `.onnx` + `.onnx.json`, 40+ languages, ~20–110 MB each |
| Voice JSON | `audio.sample_rate`, `espeak.voice`, `inference.{noise_scale,length_scale,noise_w}`, `phoneme_type`, `phoneme_id_map` (~154 entries), `num_speakers`, `speaker_id_map` |

# Piper as a second engine — plan

Status: **Not started. Investigation done 2026-08-19; nothing written, no
dependency added, no file in `src/` touched.** This document is the handoff. It
is written so the next agent can start at [Phase P0](#p0) without re-reading the
upstream repository.

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

---

<a name="where"></a>

## Where to pick up

### Status by phase

| Phase | State |
| --- | --- |
| P0 · Prove the graph runs | **Next.** Half a day. Kills routes A/B/C permanently if it passes |
| P1 · Phoneme parity | Not started. **The go/no-go.** Everything after it is ordinary work |
| P2 · Measure, then decide | Not started. RTF, cold load, resident set, on both boxes. Ends with the licence decision |
| P3 · `PiperSynthesizer` + the options refactor | Not started. Forces the `SynthesisOptions` question — [see below](#casualties) |
| P4 · Voice catalog, download, tokens, UI | Not started. The bulk of the calendar time, and the least risky part |
| P5 · Packaging both platforms | Not started. Blocked on [Phase 7](LINUX-PORT-PLAN.md#phase-7) existing at all on the Linux side |

<a name="next"></a>

### What to do next, in order

**Do P0 and P1 before planning anything else.** They are cheap, they are pure
research, and between them they answer the only two questions that can end this.
Every estimate in P3–P5 is worthless until P1 has a number.

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

**Work.** Build espeak-ng. Wrap `espeak_TextToPhonemes` the way upstream's
`espeakbridge` C extension does — it is ~200 lines and it is the specification,
so read it rather than inventing a wrapper. What it does, and what a
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

### Phase P2 — Measure, then decide · half a day

**Work.** RTF, cold-load time and resident set per voice, on both boxes, against
the Supertonic numbers already recorded: **RTF 0.193 on Mint, 0.294 on Windows,
~830 MB resident.** Warm and cold, x64 and — if P0 got that far — x86.

**Exit criteria.** A table, and then the licence decision, which is the user's and
nobody else's. Present it as: *stay MIT and pay for a sidecar process and a
parity test*, versus *relicense to GPL-3.0 and P/Invoke espeak-ng directly*. Both
are legitimate; the second is materially less code; the second is a one-way door.
eSpeak NG ships its own GPL SAPI5 engine, which is the precedent worth putting in
front of the user, not a lawyer's opinion invented here.

<a name="p3"></a>

### Phase P3 — `PiperSynthesizer` and the options refactor

**Work.** A second `ISynthesizer`. The interface is two members and already says
what it needs to: cancellation is part of the contract, and `SampleRate` may
require loading the model to answer — both of which Piper satisfies more easily
than Supertonic does, because a VITS render is one `Run` of well under a second
rather than a multi-second diffusion loop.

The unavoidable design work is
[SynthesisOptions](../src/VibeSuperTonic.Core/Synthesis/ISynthesizer.cs#L52): it
is Supertonic-shaped. `TotalStep`, `Language` and `Speed` have no Piper meaning;
`length_scale`, `noise_scale`, `noise_w` and `speaker_id` have no Supertonic
meaning. Doing this with a real second implementation in hand is the point of
doing P3 after P0 rather than before it.

<a name="p4"></a>

### Phase P4 — Catalog, download, tokens, UI

The bulk of the time, and the least likely to surprise anyone. Per-voice
on-demand download through Core's downloader; a voice list that is no longer a
static array of ten; opt-in SAPI token installation on Windows. Details under
[what breaks anyway](#casualties).

<a name="p5"></a>

### Phase P5 — Packaging

Both packers. A `LICENSE-PHONEMIZER.txt` and a written GPL source offer if the
sidecar shipped. Note that [pack-tar.sh does not exist yet](../CLAUDE.md) — this
phase cannot precede Linux [Phase 7](LINUX-PORT-PLAN.md#phase-7).

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
- **SAPI token explosion.** [Voices.cs](../src/VibeSuperTonic.Launcher/Voices.cs)
  is a static array of ten and `Registration` writes them all. 130 voices cannot
  all become registry tokens. Needs per-voice opt-in install, a dynamic token
  writer, and a `TokenSchemaVersion` bump — which by design marks every existing
  install as needing repair, so it is a release-notes item too.
- **Sample rate becomes per-voice and changes mid-session.** 16000 or 22050
  depending on quality tier. `ISynthesizer.SampleRate` is already per-instance, so
  the interface holds; the audio sink and `PlaybackClock` are what to check.
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
| **D1** | **C# inference + espeak-ng sidecar** | **Arm's length** | **✓** | **None** | **espeak-ng only** |
| D2 | C# inference + P/Invoke espeak-ng | Infects everything | ✓ | None | espeak-ng only |

<a name="why-not-libpiper"></a>

**Why not `libpiper` (A/B).** It brings a second ORT, no 32-bit target, and a
C++ toolchain, to run a graph we can already run. B pays all of that and adds an
IPC hop.

**Why not portable Python (C).** Embeddable Python ~15 MB, `piper-tts` wheel 34
MB, onnxruntime ~15 MB, numpy ~25 MB — roughly **110 MB onto an 80 MB ZIP**, plus
interpreter startup and a second ONNX stack we do not control. Fine as a
reference implementation to diff against in P1. Wrong to ship.

**D2 is D1 with the sidecar removed**, and becomes correct the moment the licence
posture changes. This is why P3 must not hard-code "the phonemiser is a process" —
it is an interface with one implementation today.

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

- **Licence posture.** Deliberately deferred to after P2, by the user, 2026-08-19.
- **Whether `alignments` is worth a patched model** — and whether we would then be
  shipping voices upstream does not, which is a hosting and provenance question,
  not a technical one. Revisit only after P4.
- **Which voices ship in the catalog's default view.** 130 is not a dropdown.
  Needs a product answer, not an engineering one.
- **Whether Piper eventually becomes the Linux default** on resident-set grounds
  alone. 830 MB against ~63 MB is the kind of gap that deletes the port plan's
  open question about releasing the ONNX session on an idle timeout. Not for this
  round.

---

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

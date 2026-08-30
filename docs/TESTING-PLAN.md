# Testing — safe, slim, fast, valid

Written 2026-08-27, alongside [SPEECHD-PLAN.md](SPEECHD-PLAN.md). Linux only:
Windows advances later and nothing here changes a Windows path.

**This is not a proposal to write more tests.** It is an audit of which of four
properties the existing checks actually defend, and it finds that one of them is
defended very well, one is defended by accident, and two are not defended at all.

---

## What exists today, honestly

| Layer | What it is | What it catches |
| --- | --- | --- |
| **Unit tests** | 1159, ~0.8 s, no network, no models, no native libraries | Logic. The bulk of the product's behaviour |
| **Packer assertions** | 11 in [pack-tar.sh](../build/pack-tar.sh), run against the *composed tree* — and two of them are scripts of their own, [check-speechd-payload.sh](../build/check-speechd-payload.sh) and [check-budgets.sh](../build/check-budgets.sh) | Silent packaging defects — a stale binary, a managed apphost, 330 MB of CUDA, a missing dictionary, an archive that doubled, a vst-ctl that got slow |
| **The parity spike** | [spike/piper-phonemes](../spike/piper-phonemes) — 327 sentences against piper's own output, with five deliberate sabotages that must all be caught | Phoneme divergence, which is wrong audio rather than a failure |
| **CI** | [build.yml](../.github/workflows/build.yml): five jobs — `build` (Windows), `linux`, `pack` and `smoke` since 2026-08-27, and `speechd` since 2026-08-30 | Compile breaks, `Core.Tests` on both platforms, an AOT publish that silently went managed — **every packer assertion, "does the archive run on Ubuntu 22.04", and "does espeak-ng still answer after we install"** |

**The gap this document opened with — *CI runs none of the packer's nine
assertions, never builds espeak-ng, and never runs the parity spike* — is
two-thirds closed as of 2026-08-27.** `pack` builds espeak-ng against a
pin-keyed cache and runs the packer; `smoke` extracts the tarball it produced
into a bare `ubuntu:22.04` container and starts the daemon there. The parity
spike in CI is [item 8](#the-order-to-do-it-in) and still open.

[What that cost, and what it caught on its first run](#what-2-found).

---

<a name="safe"></a>

## Safe

**What it means here.** Data the product reads must not be able to write,
delete, or execute outside the places it owns; downloaded bytes must be what
they claim; and nothing may widen what a local process can already do.

The product reads three kinds of untrusted-ish input: **files beside the
binaries** (`models-manifest.json`, `piper-voices.json` — ours, but in a
user-writable portable install), **bytes from the network**, and **strings from
a command line**.

### What is defended

| Surface | Defence | Tested |
| --- | --- | --- |
| Model and voice downloads | SHA-256 pinned in the manifest, re-verified after *each* mirror | Yes — a mirror serving a truncated file must fall through, not leave it on disk |
| The daemon's socket | `0600` inside a `0700` directory, so no other local user can speak or read your selection | Yes, as of 2026-08-30 — [SocketModeTests](../src/VibeSuperTonic.Daemon.Tests/SocketModeTests.cs), four checks including the `/tmp` fallback, and four sabotages |
| Catalog → disk path | `StorePath.Under` + `IsSafeVoiceId`, at every point a string becomes a path | Yes, as of today — 39 tests, plus the same rule in [check-piper-catalog.py](../build/check-piper-catalog.py) |
| `vst-ctl voice remove <id>` | Same guard. The path that ends in `Directory.Delete(recursive: true)` | Yes |
| Archive extraction | Nothing extracts archives. `tar` is the user's | n/a |
| The user's speech-dispatcher config | `speechd-install.sh` re-declares every module it found, copies the system file before creating a user one, and backs up an existing one | Yes, as of 2026-08-28 — [restore-test.sh](../spike/speechd-s4-install/restore-test.sh) drives a private speechd through six scenarios, 40 checks, and 13 sabotages of the installer are all caught |

**The catalog guard was added on 2026-08-27 because this audit found it
missing.** `Path.Combine(root, entry.Path)` had no traversal check, in two
places — one that writes and one that *deletes* — and `Path.Combine` discards
the base entirely when the second argument is rooted, so `/etc/cron.d/x` was
never a traversal to catch, just an instruction to obey. See
[StorePath](../src/VibeSuperTonic.Core/Models/StorePath.cs).

### What is not defended, and what to do

1. **`install-gpu.sh` fetches ~3.1 GB with no hash pin.** The ORT provider comes
   from nuget.org by version, and CUDA/cuDNN come from PyPI through `pip`. TLS
   and the registries' own integrity are the whole defence — which is the same
   trust model as `dotnet restore`, so it is not unreasonable, but it is the one
   download path in this product that does not follow its own rule. These
   libraries are `dlopen`ed into the daemon.

   **Do**: pin the nupkg's SHA-256 and check it, in the shape assertion 5 already
   has (it compares the *version* in that script against the csproj, so both
   numbers are already visible in one place). `pip --require-hashes` for the
   wheels is the same idea and more work; decide it separately.

2. ~~**The speechd module puts arbitrary desktop text into a shell command.**~~
   **Gone, and worth saying why rather than deleting.** That was
   [trap 9](SPEECHD-PLAN.md#t9) under route A, where `sd_generic` builds a shell
   command and hands it to `system()` — safe *only* while the config wraps
   `$DATA` in single quotes, which looks like a style convention and is not.
   Route B ([reversed 2026-08-27](SPEECHD-PLAN.md#route-b)) is a module binary
   speaking a line protocol on stdin: **no shell is involved at any point**, and
   the utterance never becomes part of a command line. The security check that
   was owed here is not needed, and the decision that removed it was made for an
   unrelated reason — routing keystroke echo — which is the sort of thing worth
   noticing when a design changes.

   What replaced it is a different assertion about the same file: the installer
   writes the config, so [check-speechd-payload.sh](../build/check-speechd-payload.sh)
   checks that what it writes names a path that exists, and that it refuses to
   write at all when there is nothing to say.

3. ~~**Nothing asserts the socket's mode.**~~ **Done 2026-08-30.** A daemon test
   binds a real listener and asserts `0600` on the socket and `0700` on its
   directory — in both places the path can land, because under
   `$XDG_RUNTIME_DIR` the modes would come out right by INHERITANCE and the
   `/tmp` fallback is the case the explicit `chmod` exists for. So the version
   of this code that relies on inheritance passes the first check and fails the
   second, which is what the sabotage run confirmed. A third check narrows a
   directory somebody else left at 0755, and a fourth connects to what was
   bound, because a mode on a leftover file proves nothing about a listener.

### The test to write first

A **hostile-input suite** that treats `piper-voices.json` as attacker-controlled
and asserts the product refuses rather than obeys. It exists now for paths; it
should grow an entry every time a new field of that file reaches a syscall.

---

<a name="slim"></a>

## Slim

**What it means here.** The archive is downloaded by people on domestic
connections, and the daemon is resident for a login session. Both have budgets;
neither has a check.

### The numbers, measured 2026-08-27

| | Now | Note |
| --- | --- | --- |
| Tarball, 0.2.11 (no espeak) | **54.1 MB** | |
| Tarball with the P5 phonemiser | **59 MB** | +7.1 MB compressed, of which `ru_dict` is 4.9 |
| AppImage | **47 MB** | |
| Composed tree, uncompressed | **140 MB** | |
| Warm daemon RSS | **~830 MB** | Measured in Phase 0; a Piper `high` session adds ~350 MB on GPU |

Biggest files in the tree: `libonnxruntime.so` 22 MB, `System.Private.CoreLib.dll`
15 MB, `libSkiaSharp.so` 8.9 MB, `ru_dict` 8.7 MB.

### What is defended

Assertions 3 and 4 catch **two named files** — any `.onnx`, and
`libonnxruntime_providers_cuda.so`. Both exist because those two specific things
once appeared. **Nothing catches growth in general**, which is the shape most
size regressions actually have: a package reference that drags in a native blob,
a publish that stops trimming, a payload that quietly ships `--full-data`.

### What to do

1. ✅ **A total-size budget in the packer** — **done 2026-08-30**. 75 MiB against
   60.7 measured, checked against the finished tarball, printing the ten biggest
   files in the tree on failure. A tarball that fails it is **deleted**: an
   artifact that exists is an artifact somebody can ship.
2. ✅ **A per-payload budget for `espeak/`** — **done 2026-08-30**. 18 MiB
   against 13.2 measured, which is on the useful side of the ~25 MB an unpruned
   build costs.
3. **An RSS budget**, which is the harder one because it needs a model. Put it in
   [spike/daemon-stress](../spike/daemon-stress) rather than in CI, and record
   the number in the release notes the way P2's table does. **Still open.**

Both budgets live in [check-budgets.sh](../build/check-budgets.sh) rather than
inside the packer, for the reason `check-speechd-payload.sh` does: a check that
can only run inside a four-minute pack is one nobody sabotages.
[budget-sabotage.sh](../build/budget-sabotage.sh) breaks each of them against a
copy of a composed tree — a 20 MB blob, an unpruned payload, a slow vst-ctl, a
missing phonemiser, a missing binary — and all five were caught the day they were
written.

**And the honest note**: `ru_dict` is 4.9 MB of a 7.1 MB payload, and dropping
`EXTRA_ru` would recover it at the cost of Russian stress placement diverging
from what the model was trained against. That is a budget decision with a real
price, which is exactly why it should be visible as a number rather than left
implicit.

> **Decided 2026-08-30: keep it.** The archive is 61 MB against a 75 MiB budget,
> so nothing forces the question, and the price is the wrong kind — a Russian
> voice that sounds subtly wrong reaches a user as a quality complaint with no
> cause they can name, not as an error. Revisit it if the budget is ever the
> thing standing between a release and a feature; the number is in
> [check-budgets.sh](../build/check-budgets.sh) and the switch is one flag in
> [build-espeak.sh](../build/build-espeak.sh).

---

<a name="fast"></a>

## Fast

**What it means here.** Four numbers, three of which have budgets already
written down somewhere and none of which is checked automatically.

| Number | Budget | Where it came from | Checked? |
| --- | --- | --- | --- |
| `vst-ctl` startup | 6 ms AOT vs 107 ms managed | Phase 7 | ✅ **Measured**, 2026-08-30 — median of 21 against this machine's own fork/exec floor + 35 ms. 7 ms observed |
| Pipeline, request → first sample | 250 ms ceiling, 25 ms growth | This document | ✅ **Measured**, 2026-08-30 — 1 ms for a 27 KB document, and two of the four checks do not look at a clock at all |
| Hotkey → acknowledgement | 150 ms | The port plan | No |
| First word, Supertonic, warm | ~750 ms, ~600 of it one inference | Phase 3 | No — and it needs a model, so it stays `vst-ctl benchmark`'s |
| First word, Piper | 802 ms CPU / 77 ms GPU | P2 | No — `vst-ctl benchmark` measures it on demand, by hand |

### The insight that makes this testable

**Separate pipeline latency from model latency.** The model's cost needs
hardware, models on disk, and an idle machine — it belongs in a benchmark a
human runs. What creeps is everything *around* it: a chunker that got quadratic,
a settings reload on every utterance, an extra IPC round trip.

That part can be measured with a **synthetic `ISynthesizer`** that returns
silence in a fixed 1 ms — the interface is already small enough
([`Synthesize` returns `short[]`](../src/VibeSuperTonic.Core/Synthesis/ISynthesizer.cs)),
and `Fakes.cs` in the test project already does this shape. Then "time from
request to first sample" is a pure-code number, stable in CI, and a regression in
it is a regression the model would otherwise hide.

### What to do

1. ✅ **A pipeline-latency test** with the synthetic synthesizer and a budget —
   **done 2026-08-30**,
   [PipelineLatencyTests.cs](../src/VibeSuperTonic.Core.Tests/PipelineLatencyTests.cs).
   Four checks, and the two that matter most **never look at the clock**: how
   many chunks were rendered before the first sample (2–3, whether the document
   is one sentence or four hundred) and whether the first chunk is the opening
   sentence rather than a merged one. Those are properties of the pipeline's
   shape, so they cannot flake and a fast machine cannot paper over them. The
   timed pair are the catastrophe net behind them.

   **The growth budget was tightened by its own sabotage run**, which is the
   point of doing one: 50 ms was the comfortable number, a deliberate quarter of
   a millisecond per chunk — 46 ms across a 184-chunk document, invisible on one
   sentence — passed it, and 25 ms catches it with 25x headroom still.
2. ✅ **Measure `vst-ctl` startup for real** — **done 2026-08-30**, in
   [check-budgets.sh](../build/check-budgets.sh), so it runs on every pack and
   therefore on every push. The budget is **relative**: this machine's own
   fork/exec floor, measured with `/bin/true` at the same moment, plus 35 ms. An
   absolute budget would fail on a loaded runner while shipping a fast binary,
   and the sabotage — an ELF that sleeps 100 ms, still native, still passing
   assertion 2 — is caught either way.
3. **[SPEECHD-PLAN.md's S0](SPEECHD-PLAN.md#s0) is the first place a real
   first-word budget gets written down**, because it is the first feature where
   the number decides whether to ship. Whatever it measures should become the
   recorded baseline.

---

<a name="valid"></a>

## Valid

**What it means here.** Correct output for correct input — and, in this product
specifically, *refusal* rather than plausible-wrong output. Almost every bug this
codebase has found was a thing that worked and was wrong.

**This is the strongest axis and it is worth saying why**, because the method is
reusable: every check is *behavioural* and every check has been **seen to fail**.
The parity spike runs five deliberate sabotages before it reports a pass. The
nine packer assertions were each sabotaged on the day they were written, and P5's
three again on 2026-08-27 — a removed dictionary, a wrong pin, the distro
library, missing data, no payload, a library without the export. Six sabotages,
six refusals.

**The rule, stated so it survives: a check that has never been observed failing
is not evidence. Sabotage it once, at the time you write it.**

### The gap

The artifact-level checks only ever run on the machine that packs. Concretely:

- CI does not run `pack-tar.sh`, so **none of the nine assertions run in CI**.
- CI never builds espeak-ng, so P5's payload is unverified until a release run.
- CI never runs the parity spike, so a phonemiser regression is invisible until
  someone re-runs it by hand.
- **Nothing smoke-tests the finished archive.** No check anywhere proves the
  composed tree runs on a machine that is not this one — which is precisely the
  claim the glibc-floor assertion is trying to make on paper.

### What to do

1. **A CI job that packs.** Build espeak-ng once and cache it on the pin (it is a
   fixed commit, so the cache key is exact and it rebuilds only when the pin
   moves), then run `pack-tar.sh`. This turns nine hand-run assertions into nine
   continuous ones and is the single highest-value item in this document.
2. **A smoke test in a clean container**, on the tarball that job produces:
   extract it into `ubuntu:22.04` (the glibc floor's own claim — 2.34), and run
   `vst-ctl --version`, `vibesupertonicd --version`, and one espeak phonemisation
   through the shipped library. No models, no audio device, no GPU. It answers
   "does this run on a supported distro", which nothing answers today.
3. **The parity spike in CI**, using the payload the pack job already built. It
   is 327 sentences and needs no network.
4. **Keep the sabotage discipline** for everything above: each new check gets
   broken once, deliberately, and the breakage recorded in the commit.

---

## Where a check belongs

| If it… | Put it in | Because |
| --- | --- | --- |
| is about logic, and needs no model, network or device | `Core.Tests` / `Daemon.Tests` | 1159 of them run in 0.8 s; that is the budget being protected |
| is about what is **in the box** | a packer assertion | The composed tree is the thing that ships, and it differs from the build output |
| is about **generated data** | a checker script the packer calls | [check-piper-catalog.py](../build/check-piper-catalog.py) is the pattern: runnable by hand at the moment of regeneration |
| is about **agreeing with an external implementation** | a spike with a committed corpus | The parity spike needs neither Python nor network, which is what makes it re-checkable |
| needs a GPU, an audio device, or a human ear | a spike or a documented manual step | Say it is manual rather than pretending; P2's table is the format |

---

## The order to do it in

Ranked by defect-caught per hour, not by axis.

| # | Item | Axis | Cost |
| --- | --- | --- | --- |
| 1 | ✅ **CI runs `pack-tar.sh`** with a cached espeak build — **done 2026-08-27** | valid | half a day |
| 2 | ✅ **Clean-container smoke test** on Ubuntu 22.04 — **done 2026-08-27**, and it found a release blocker on its first run | valid | half a day |
| 3 | ✅ **Archive size budget** with a ten-biggest-files report on failure — **done 2026-08-30**, [check-budgets.sh](../build/check-budgets.sh) | slim | an hour |
| 4 | ✅ **Socket mode test** — **done 2026-08-30**, [SocketModeTests.cs](../src/VibeSuperTonic.Daemon.Tests/SocketModeTests.cs) | safe | an hour |
| 5 | **Pin the ORT nupkg hash** in `install-gpu.sh` | safe | an hour |
| 6 | ✅ **Pipeline-latency test** with a synthetic synthesizer — **done 2026-08-30**, [PipelineLatencyTests.cs](../src/VibeSuperTonic.Core.Tests/PipelineLatencyTests.cs) | fast | half a day |
| 7 | ✅ **`vst-ctl` startup measured**, not inferred — **done 2026-08-30**, in the same script as 3 | fast | an hour |
| 8 | **Parity spike in CI** | valid | an hour, once 1 exists |
| 9 | ✅ **espeak payload size budget** — **done 2026-08-30**, in the same script as 3 | slim | 15 minutes |
| 10 | **RSS budget** in `spike/daemon-stress` | slim | half a day |

**Three of the ten are left, and none is a Speech Dispatcher blocker**: the ORT
hash pin is the last of the two *safe*-axis items this audit opened with, the
parity spike in CI is cheap now that the pack job builds the payload, and the RSS
budget needs a model and therefore a spike rather than a job.

Items 1 and 2 are worth more than the other eight together: they take every
artifact-level check that exists and make it continuous, and they answer the one
question nothing currently answers — *does the thing we ship run somewhere that
is not this laptop?*

---

<a name="what-2-found"></a>

## What item 2 found, in its first run

**The daemon could not start on a fresh install.** It logged, correctly, `no
onnx/ under …/models — starting anyway; speech will fail until the models are
downloaded`, and then died with an unhandled `DirectoryNotFoundException`.

The mechanism is worth keeping, because it is a shape rather than an incident.
`EngineRoutingSynthesizer.Select` has a fast path whose own comment reads
*"cheap and by far the common case — one directory stat and a dictionary
lookup"*. It reads `_current.SampleRate`. The Supertonic engine answers that
question **by loading the model**. So a routing decision that changed nothing
performed a model load, and with `models/onnx` absent it threw — from the
daemon's *startup* call, where nothing stood between it and `Main`.

A fresh install could not start the daemon it needs in order to stop being a
fresh install.

| Release | State |
| --- | --- |
| 0.2.10 | **Passes.** Predates the startup call |
| 0.2.11 | **Fails.** Packed 2026-08-27, never published — the first artifact to carry it |
| Introduced by | `3d5e8ff`, P3, *"A Piper voice speaks through the product"* |

**Why nothing caught it for two phases**: every machine that has ever run this
product has models on it. 1159 unit tests, nine packer assertions and a CI job
all pass against a tree that cannot start. It took an empty directory on a
machine that had never seen the product.

Fixed by making a routing decision able to **fail but never throw** — the press
path already refuses on `Selection.Error` before it touches the rate, and the
startup call already logs it. Two regression tests, both observed failing
against the unfixed code first.

**This is the argument for items 1 and 2 in one paragraph.** They cost a day and
the first run of one of them found a release blocker in an artifact that was
already built.

### And what the first run *in CI* found

The local run found the fresh-install crash. The first run in a real
`ubuntu:22.04` container found a second thing, of exactly the shape the job was
built to catch: **`vibesupertonic-ui` could not start at all.**

It was the one binary without `<InvariantGlobalization>`, so .NET probed for
`libicuuc.so.78`, `.79`, `.80` and `.81` at process start and aborted when the
container had none — before `Main`, so the program itself said nothing. Every
desktop has ICU, which is why it had never been seen; `INSTALL.txt` promises
*"No runtime to install. All three binaries carry what they need"*, and for that
one it was false.

Fixed by matching the daemon and `vst-ctl`, plus **assertion 3d**: every
`*.runtimeconfig.json` in the archive must declare invariant globalization. The
smoke test is what found it, and the packer assertion is what makes it a
five-second failure on any machine instead of a container-only one.

### And the third, which was not in the archive at all

The same CI run had a second red job: `dotnet test`, failing on the runner and
green on the developer's machine, deterministically, for three runs. Reproduced
by running the suite in a container with **two CPUs**:

```
Assert.EndsWith("…, 20% of 20 logical processors, never benchmarked", reason);
```

**Twenty is this laptop's core count.** The assertion could only ever pass on the
machine it was written on. It went in with P3 on 2026-08-25 and CI had not run
the Linux suite since, so it sat red for two days while the suite was green
locally — and a green suite locally is exactly what stops anyone looking.

Fixed by computing the clause the way the product computes it, from
`Environment.ProcessorCount`. The test's own comment already said the thread
count "is not this test's business"; the assertion had made it so.

**All three findings are the same lesson.** A check that only ever runs where the
product already works cannot find what the product needs — and that applies to
the test suite itself, not only to the archive.

**The cheapest guard against the third kind**: run the suite in a
CPU-constrained container occasionally. `docker run --cpus 2` found in one
attempt what three CI runs had only reported as "exit code 1".

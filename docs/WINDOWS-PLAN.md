# VibeSuperTonic on Windows — convergence plan

Status: **W0 done 2026-08-19** — one version, stamped on every assembly and read
back off the shipped binaries. **[W1](#w1) is built and has been run on hardware
the same day**: it works end to end, it answered the DirectML question, and it
**fails its reproducibility criterion on the CPU rows** for a reason it diagnoses
itself. 914 Core tests pass. Nothing else has started.

Written 2026-08-19, from an audit of what the Linux port built that the shipping
Windows product does not have.

**Why this file exists.** Windows is the shipping platform and has never had a
plan document — its history lives in commit messages, in
[CLAUDE.md](../CLAUDE.md)'s release rules, and as a single orphan section at the
end of [LINUX-PORT-PLAN.md](LINUX-PORT-PLAN.md). That was fine while Linux was
catching up. It stopped being fine on 2026-08-16, when
[Phase 8a](LINUX-PORT-PLAN.md#phase-8a) shipped a per-machine measurement that
replaced a guess Windows is **still making** — and made Linux the platform that
knows more about the hardware it runs on. This file is where the Windows side's
work is written down, in the same shape, so the two can be compared rather than
remembered.

**The rule this document is written under.** Every change here lands on software
people already use, through a COM in-process server loaded into *someone else's*
program. [Trap 11](LINUX-PORT-PLAN.md#traps) is the standing warning: R-14
shipped broken in five consecutive releases while every unit test passed, because
the defect lived at a composition point Core.Tests cannot see. A Windows phase is
not done when the tests are green. It is done when the TestHarness passes **in
both bitnesses** on a real install.

---

<a name="decisions"></a>

## Decisions — settled. Do not re-litigate these

| Decision | What it means in practice |
| --- | --- |
| **The engine cannot be self-contained** | `EnableComHosting` + `--self-contained` fails `NETSDK1128`. This is why the four prerequisites exist and cannot be bundled — [trap 6](LINUX-PORT-PLAN.md#traps) |
| **ORT is pinned at 1.22.1** | 1.23.0 dropped the win-x86 native. Publish still succeeds and 32-bit dies on first Speak — [trap 4](LINUX-PORT-PLAN.md#traps) |
| **Core takes zero `PackageReference`s** | Same assembly name, different managed surface between the DirectML and CPU ORT builds. R-13, a hard constraint |
| **Rendering happens out of process** | The Control Panel is self-contained single-file; ONNX access-violates and Media Foundation returns `MF_E_INVALIDMEDIATYPE` inside it. `render\VibeSuperTonic.RenderHost.exe` deliberately mirrors an ordinary SAPI host's shape. Do not make it single-file |
| **`data\settings.json` has one writer** | The Control Panel writes; the engine reads, cached on mtime. Identical to the Linux arrangement, arrived at independently |
| **The product is portable** | All state under `<BaseDir>\data`. A USB-stick install travels with its settings, its telemetry and (from W1) its benchmark |
| **Version bumps are the user's decision** | Never inferred. `<VstVersion>` is the *last shipped* version, updated after a release — [CLAUDE.md](../CLAUDE.md) |
| **One version for every binary** — 2026-08-19 | `Directory.Build.props` stamps `$(VstVersion)` on the whole tree, Windows included. This **reverses** the earlier scoping decision; see [W0](#w0) |
| **The next release is 0.3.0** | Shared with the first Linux release. A deliberate jump from 0.2.8 |

---

<a name="findings"></a>

## What the audit found — 2026-08-19

Ordered by what it costs the user, not by what it costs to fix. Every row was
verified against the source or by building it; none is a suspicion.

| # | Finding | Severity | Phase |
| --- | --- | --- | --- |
| 1 | **Windows guesses its thread count and always has.** `OnnxThreads` defaults to `0` — ORT's own pick, every core. Linux measured that pick as **eleven times the machine for a 1.6x worse answer** on the same hardware family. Windows has the apply-side plumbing and no measurement | High | [W1](#w1) |
| 2 | **`UseDirectML` defaults to `true` and has never been shown to win.** 2026-08-17 proved the *append* works on real hardware and passed all eleven harness steps at engine RTF 0.28. Nothing anywhere compared it against CPU on the same box | High | [W4](#w4) |
| 3 | **The Benchmark tab runs each preset once.** No median, no spread, and verdict bands at 0.5/1.0 RTF that were never checked against the noise floor. Linux measured **15–18% run-to-run** on *identical* configurations. The tab is capable of ranking two presets on noise and colouring the answer green | High | [W3](#w3) |
| 4 | **Nothing in the product says what inference is doing or why.** The Status tab is registration checks only. Linux `config` reports provider, threads, and the *reason* — "benchmark 2026-08-17" versus "20% of 20 logical processors, never benchmarked" | Medium | [W2](#w2) |
| 5 | **`VibeSuperTonic.exe --version` opens the GUI.** So does any unrecognised flag: [Program.cs:19](../src/VibeSuperTonic.Launcher/Program.cs#L19) treats "has arguments" as "is CLI", then falls through to `Application.Run`. This is the exact input that crashed `vst-ctl` on Linux, fixed there 2026-08-18 with one sentence and exit 2 | Medium | [W2](#w2) |
| 6 | **The two copies of the settings schema already disagree.** `TotalStep` defaults to **8** in [the engine's](../src/VibeSuperTonic.Engine/Settings/EngineSettings.cs#L25) and **6** in [the launcher's](../src/VibeSuperTonic.Launcher/EngineSettings.cs#L20). `Load()` does not write, so a fresh install with no `settings.json` shows 6 in the Tune tab while the engine synthesises at 8, until the user saves anything | Medium | [W6](#w6) |
| 7 | **An in-place upgrade can leave a split-bitness install** — [trap 16](LINUX-PORT-PLAN.md#traps), found 2026-08-17. A running 32-bit reader holds `engine\x86` open; the copy fails there and succeeds for x64. Nothing detects it. W0 built the detector; nothing yet routes a user to it | Medium | [W5](#w5) |
| 8 | **The shipped `tools\` harness is x64 only**, so a user diagnosing a 32-bit reader cannot test the engine that reader actually loads — which is the engine most users run | Low | [W5](#w5) |
| 9 | **The models download with no acceptance step.** "Repair all" fetches them; `LICENSE-MODELS.txt` in the ZIP states the user agrees to OpenRAIL-M *by* accepting the download. Linux Phase 6 built an explicit screen because the acceptance has to be a human agreeing to something | Low, but a compliance question | [W5](#w5) |
| 10 | **The Advanced tab's thread tooltip is wrong.** It states `0 = auto (cores/2)`; ORT's auto is every core. It then guesses "1–4 often beats auto" — which is right, and is exactly what W1 would stop guessing about | Low | [W1](#w1) |

---

## Phases

<a name="w0"></a>

### Phase W0 — one version, and it is visible · **DONE 2026-08-19**

Every Windows assembly built with the .NET default `1.0.0.0`, for every release
up to and including 0.2.8. Only the two Linux projects stamped `<VstVersion>`, so
the About tab reported a version that had never been shipped and could not be
matched to any ZIP.

| Change | Where |
| --- | --- |
| `<Version>$(VstVersion)</Version>` set repo-wide | [Directory.Build.props](../Directory.Build.props) |
| Redundant per-project copies removed | Daemon and Ui csproj |
| About reports Control Panel, **Engine x64, Engine x86 and the render helper separately**, read off the files with `FileVersionInfo`, and flags disagreement in red | [AboutTab.cs](../src/VibeSuperTonic.Launcher/Ui/AboutTab.cs) |

The reversal is deliberate and recorded in both files. The old reasoning —
changing assembly versions on the shipping platform is not a side effect a shared
props file deserves to have — was correct as far as it went, and the price of
honouring it was a version number nobody could read off the product they were
holding. Nothing binds against these versions: SAPI registration is path-keyed
([Registration.cs:230](../src/VibeSuperTonic.Launcher/Registration.cs#L230)), the
SAPI token's own `Version` attribute is the unrelated literal `"1.0"`, and no
assembly is strong-named.

**Why About compares rather than just prints.** The four binaries ship together
and are extracted separately. Reading the versions back off disk is the only way
this window can tell that a reader is loading last month's engine — which is
[finding 7](#findings)'s symptom, and W5 is where it gets a route to the user.

Verified: launcher publishes as `0.2.8+3421cfa`, all six projects build clean,
881 Core tests pass.

<a name="w1"></a>

### Phase W1 — measure this machine · 1.5–2 days · **built and run on hardware 2026-08-19; one exit criterion is NOT met**

> **Status.** Everything is written, both solutions build clean, Core.Tests is at
> **914 passing** (up from 881), and it has been run three times against the live
> install on the i7-12800H. It works end to end — and the runs found four things
> that no amount of reading the code would have, which is
> [trap 15](LINUX-PORT-PLAN.md#traps) doing its job for the third time.
>
> **The reproducibility criterion fails**, and the honest summary is: *this sweep
> can tell CPU from GPU on this machine, and cannot tell the CPU rows apart.* See
> [what the hardware said](#w1-hardware) below.
>
> **What landed**
>
> | Piece | Where |
> | --- | --- |
> | `BenchmarkStore` and the model-set fingerprint lifted into Core, so one file format serves both platforms | [BenchmarkStore.cs](../src/VibeSuperTonic.Core/Synthesis/BenchmarkStore.cs), [ModelSet.cs](../src/VibeSuperTonic.Core/Synthesis/ModelSet.cs) |
> | `Measurement.Median` / `.Spread`, shared by both sweeps | [Measurement.cs](../src/VibeSuperTonic.Core/Synthesis/Measurement.cs) |
> | Every row carries the spread of its own runs; every profile records the tie band it was picked under, and whether that band cleared the noise | [BenchmarkProfile.cs](../src/VibeSuperTonic.Core/Synthesis/BenchmarkProfile.cs) |
> | Windows machine facts — `MachineGuid` keyed-hashed, CPU name, `GetSystemTimes` load guard at the same 15% as Linux, `GetSystemPowerStatus` | [MachineFacts.cs](../src/VibeSuperTonic.Launcher/Bench/MachineFacts.cs) |
> | The sweep: one render-helper process per candidate, three timed runs each, rows built from engine telemetry | [ThreadSweep.cs](../src/VibeSuperTonic.Launcher/Bench/ThreadSweep.cs) |
> | `--runs N` in the render helper, and it now reports the thread count the engine *actually built with* | [RenderHost/Program.cs](../src/VibeSuperTonic.RenderHost/Program.cs) |
> | The engine applies an applicable profile when `OnnxThreads` is 0, mtime-cached, never throwing | [BenchmarkProfileCache.cs](../src/VibeSuperTonic.Engine/Settings/BenchmarkProfileCache.cs), [SupertonicAdapter.cs](../src/VibeSuperTonic.Engine/Synth/SupertonicAdapter.cs) |
> | `LoadTextToSpeech` reports whether DirectML *actually appended*, rather than whether it was asked for | [SupertonicSdk.cs](../src/VibeSuperTonic.Engine/Synth/SupertonicSdk.cs) |
> | Benchmark → "This machine", and `VibeSuperTonic.exe --sweep` for the headless case | [MachineSweepPanel.cs](../src/VibeSuperTonic.Launcher/Ui/MachineSweepPanel.cs), [Program.cs](../src/VibeSuperTonic.Launcher/Program.cs) |
> | 33 new Core tests: the median and spread, the file format across versions, the fingerprint's stability | Core.Tests |
>
> **Three things the writing changed from what this section predicted.**
>
> 1. **The engine already had a session-reset mechanism** — the Monitor tab's
>    `<pid>.reset` sentinel. It does not change the recommendation (a fresh
>    process per candidate is still simpler and cannot carry a stale static), but
>    the "expose a session reset" option below was wrong to call it new surface.
> 2. **Telemetry reported the settings value, not the effective one**, so the
>    verification guard as designed would have compared a number against itself.
>    `SupertonicAdapter` now publishes what the session was actually built with,
>    which is also what [W2](#w2) needs to report provenance at all.
> 3. **A DirectML row could have been a CPU row wearing the wrong label.** The
>    engine caught its own DML failure, logged it, and continued on CPU without
>    telling the caller — so the GPU question would have looked answered when it
>    had not been asked. The provider is now reported, and a DirectML row that did
>    not get DirectML is recorded as failed.

<a name="w1-hardware"></a>

#### What the hardware said — 2026-08-19, i7-12800H, three sweeps

Run against the live install. The Control Panel and render helper were replaced;
**the engine could not be**, because Firefox held `engine\x64` and Lingoes held
`engine\x86` — [trap 16](LINUX-PORT-PLAN.md#traps) demonstrating itself, and worth
recording that **Firefox is a holder**, which nothing had noticed. The trap names
Lingoes. Nobody closes a browser.

So the engine-side half — applying a profile, and the effective-threads telemetry
— is still unverified. Everything else ran.

| # | Finding | What it cost |
| --- | --- | --- |
| 1 | **One warm-up utterance is not enough.** Seven consecutive timed runs in one warm process read 0.287, 0.261, 0.250, 0.248, 0.233, 0.248, 0.232 — the first two still settling. A median of three taken from run 1 measures the decay curve, identically in every row, and orders the rows by nothing | Two untimed settling runs of the real sample now precede timing (`--warmups`). Spread on a quiet process fell from 104% to 6.4% |
| 2 | **The load guard fired on an ordinary desktop.** 15% was inherited from Linux; two of the first three sweeps were refused at 17%. Twelve resting samples through the guard's own instrument: 8.4–16.6%, median 11.6 — so 15 sits *inside* the resting distribution | Raised to 25, derived from that measurement. Perfmon read 10.3% at the same moment `GetSystemTimes` read 11.6%, so the number is instrument-specific as well as platform-specific |
| 3 | **The CPU rows do not reproduce.** Three sweeps, three different fastest CPU rows — 2 threads, then 3, then 4 — with row spreads of 3–556% | Nothing yet. This is the failed criterion |
| 4 | **DirectML wins on this machine, and is the steadiest row on the board.** 1405, 1370, 1292 ms across the three sweeps against a CPU pack scattered over 1012–2613, and fastest overall in two of them, using 0.6 cores against 1.9–12.6 | Answers an [open decision](#open-decisions) that has been open since DirectML shipped on by default |

**Finding 4 is the useful one and finding 3 is why.** The GPU is the only part of
this machine nothing else on the desktop competes for, so it is the only row whose
measurement is quiet. The CPU rows are contending with the browser, the dictionary
and Windows' own background work, and the sweep takes minutes — long enough for
that load to drift, which is why **raising the sample count made it worse**: five
runs produced the worst row of all three attempts (556%, one run stalled outright)
and took 442 s against 327 s. It is back at three.

**The real fix is the pacing.** The engine paces its writes to real time and cannot
tell a file render from an audio device, so a 4.9 s sample costs ~6 s of wall clock
to measure ~1.2 s of compute. An unpaced bench path would buy fifteen runs in less
time than three cost here, and shrink the window over which the machine can change
its mind. That is engine work on the shipping platform and it belongs in its own
change — **it is now the first thing W1 needs, ahead of anything else in this
document.**

**What is honestly true today:** the sweep is trustworthy about the provider and
is not trustworthy about the thread count, on a machine in use. It says so itself
— every profile records its own worst spread against the tie band, and all three
runs printed *"the runs varied by up to N% while the tie band is 15%"*. That the
warning fires is the design working; that it fires every time is the work left.

Port [8a](LINUX-PORT-PLAN.md#phase-8a). The premise transfers wholesale — **the
cost curve is not monotonic, so the right thread count cannot be derived from the
core count** — and Windows is where that premise was first measured, by hand, on
2026-08-16.

**Half of this already exists.** `OnnxThreads` / `OnnxInterOpThreads` flow from
`settings.json` through
[SupertonicAdapter](../src/VibeSuperTonic.Engine/Synth/SupertonicAdapter.cs#L197)
into `IntraOpNumThreads` at
[SupertonicSdk.cs:789](../src/VibeSuperTonic.Engine/Synth/SupertonicSdk.cs#L789).
What is missing is measuring, picking, storing, and applying with provenance.

#### What ports for free

`BenchmarkRow`, `BenchmarkMachine`, `BenchmarkProfile`, `CpuProfileDecision` and
`BenchmarkSweep.Pick` are in Core, are `VstPortable`, carry no packages, and are
already referenced by the Launcher. The tie band, the staleness rules and the
"fastest, then least of the machine" ordering come across unchanged and
already-tested.

#### What does not port, and this is the phase's real content

**`BenchmarkSweep.Run` cannot be reused.** It times `synth.Synthesize()` with a
`Stopwatch`, which requires a synthesizer this process can call. Windows has no
such thing: the engine is a COM in-process server, the Control Panel is
self-contained single-file, and driving ONNX in it access-violates. Worse, wall
clock is not even the right instrument here — **the engine paces its writes to
real time** so live playback keeps its trailing words, and it cannot tell a file
render from an audio device, so wall-clock RTF sits near 1.0 for every
configuration. The existing benchmark already knows this and reports the
engine's own RTF from telemetry instead
([Benchmark.cs:89-98](../src/VibeSuperTonic.Launcher/Bench/Benchmark.cs#L89-L98)).

So Windows supplies `BenchmarkRow`s measured its own way and hands them to Core's
`Pick`. That seam is exactly where Core was cut, and it holds.

| Row field | Windows source |
| --- | --- |
| `MedianWallMs` | **Not wall clock.** Engine-reported synthesis time from the telemetry session, median of three |
| `Rtf` | Engine-reported RTF — the number the pacing does not touch |
| `AvgCores` / `CoreSeconds` | The render helper's own process CPU time, which it already reports as `cpuPercent` |
| `Provider` | `"cpu"` or `"directml"` — see [W4](#w4). Linux has a CPU-only table; Windows can fill the provider column on day one |

**The trap that would make the whole table wrong.**
`SupertonicAdapter._sharedTts` is a **process-static**, built once from
`settings.json` at first activation and never rebuilt when settings change
([SupertonicAdapter.cs:173](../src/VibeSuperTonic.Engine/Synth/SupertonicAdapter.cs#L173)).
A sweep that rewrites `OnnxThreads` between rows inside one host process would
measure the first configuration seven times and produce a confident, plausible,
entirely fictional table — with a timestamp on it. This is the same class of
defect as 8a's tie band: correct code, wrong about the world.

Three ways out, and the recommendation is the boring one:

| Option | Verdict |
| --- | --- |
| **One `RenderHost` process per candidate** | **Take this.** Costs one model load per row (~1 s × 7). The preset sweep already spawns a process per preset, so the pattern is proven and the settings-restore sidecar already covers a crash mid-sweep |
| A `--mode sweep` that loops inside one helper | Does not work. The static is per *process*, so the loop measures row 1 seven times — the defect this trap describes, hidden one layer deeper |
| Expose a session reset on the engine | New public surface on the COM engine to serve a benchmark. Changes shipping code loaded into other people's programs, for a second of sweep time. No |

#### What it writes, and where

`data\benchmark.json` — same filename, same schema, beside `settings.json`, for
the same reason Linux keeps it separate: `settings.json` has exactly one writer
by design, and a measurement is the *tool's* output, not the user's input.

**Lift `BenchmarkStore` and `ModelSetFingerprint` into Core.** Both are
platform-neutral already — JSON over a stream, and a names-and-sizes hash over a
directory — and both live in the Linux daemon only because they were written
beside `MachineFacts`, which genuinely is platform policy. Sharing them means the
two platforms cannot drift about the file format or about what "the model set
changed" means, which matters the first time a portable folder crosses platforms.

#### Windows `MachineFacts`

Same record, different sources. Nothing here may throw — a profile whose CPU
name is "unknown" is still usable; a Control Panel that will not open because a
WMI call was slow is not.

| Fact | Linux | Windows |
| --- | --- | --- |
| Machine id | keyed hash of `/etc/machine-id` | keyed hash of `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid`. Hash it for the same reason: equality is the only property needed and the file travels on a USB stick |
| CPU name | `/proc/cpuinfo` | `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString` |
| Logical processors | `Environment.ProcessorCount` | same — and it honours affinity on both, which the staleness check already covers |
| Idle CPU before the sweep | `/proc/stat` over 1 s | `GetSystemTimes` over 1 s. **Keep the 1 s window**: 8a shipped 300 ms and it refused two legitimate sweeps off an idle desktop |
| Power state | `/sys/class/power_supply` | `GetSystemPowerStatus` |
| Model set | names + sizes hash | identical, once lifted to Core |

#### How the pick is applied

The precedence question is new, because Windows stores an absolute count where
Linux stores a percentage. Proposed, and this is the one decision in W1 that
wants the user's agreement before it is built:

- `OnnxThreads = 0` — today's default, meaning "auto" — becomes **"use the
  profile if one applies to this machine, else ORT's pick"**. Existing installs
  change behaviour only after they run a sweep.
- `OnnxThreads` non-zero means **the user has decided**, and the profile is
  reported but not applied. A knob that silently loses to a measurement is a knob
  that generates bug reports.

Reading `benchmark.json` happens inside every SAPI host, so it is cached on mtime
exactly like `settings.json` and parsed once per session build — not per
utterance.

#### Exit criteria

- Three consecutive sweeps on an idle machine agree on the same row. *This is the
  criterion 8a's tie band failed first, and it is the one that catches a band
  narrower than the noise.*
- The sweep reports its own run-to-run spread, and the tie band is at or above
  it — **measured on this hardware, not inherited from Linux's 15%.** Different
  instrument (engine telemetry, not wall clock), different noise.
- A profile carrying another machine's identity, another model set, or another
  `TotalStep` is refused with the reason stated.
- The Advanced tab's thread tooltip is replaced by what was measured.
- Timing is **stated as measured**, never promised in advance — [trap
  13](LINUX-PORT-PLAN.md#traps) has claimed two criteria already.
- TestHarness green in both bitnesses afterwards, because this changes engine
  code.

<a name="w2"></a>

### Phase W2 — say where the number came from · 0.5 day

A number in a settings file with no provenance is a number nobody dares change.
Windows currently shows no number at all.

- **Status tab gains an Inference group**: provider in force, threads in force,
  the reason, and the profile's date with any staleness sentence. It sits beside
  the existing checks because that is where users are already sent, and it uses
  the same row shape so a stale profile can carry a "re-measure" action the way a
  broken registration carries "repair".
- **`VibeSuperTonic.exe --config`** prints the same thing to stdout, which is
  `vst-ctl config` parity and is what a field report should contain.
- **`--version` prints the version and exits 0.** Any unrecognised flag prints
  usage to stderr and exits 2. Today both open the GUI, which is the same input
  that took `vst-ctl` down with SIGABRT — Linux answered it in one sentence and
  Windows should not answer it with a window.

**Exit criterion:** a user can answer "what is my machine doing and why" from
either the tab or the CLI, and both give the same answer.

<a name="w3"></a>

### Phase W3 — the preset benchmark, made trustworthy · 0.5 day

The Benchmark tab predates everything Linux learned about measuring this engine.
It runs each preset **once**, prints an RTF to two decimal places, and colours it
green below 0.5 and amber below 1.0. Those bands may well be right. Nothing has
ever checked them against the noise, and the noise is known to be large: 8a
measured **15–18% run-to-run on identical configurations, with one row moving
39%**.

Two presets whose true RTFs differ by 10% will therefore swap places between
runs, and the tab will present whichever won as a fact.

- Median of three, like the sweep, with the **spread shown**. A user comparing
  Balanced and Quality needs to know when the difference is smaller than the
  measurement.
- Verdicts stay, because "will it keep up" is a real question with real
  thresholds — but a row whose spread straddles a band boundary must say so
  rather than pick a colour.
- The word cap goes to 100,000, which at RTF 0.2 is hours. Show the estimate
  before starting, from the row already measured.

This phase shares the median/spread machinery with W1 and should be built with
it, but it is listed separately because it changes an existing surface people
already use and can ship on its own.

<a name="w4"></a>

### Phase W4 — provider policy on evidence · 1 day + the battery rule

`UseDirectML` defaults to `true`. On 2026-08-17 the harness printed *"DirectML
provider appended (device 0)"* on real hardware, in **both bitnesses**, and
passed everything at engine RTF 0.28. That proves the path is live. It proves
nothing about whether it is better — a clean append is not evidence about where
the ops ran, and nothing has ever compared it against CPU on the same box.

W1's provider column answers this the first time anyone runs it. What W4 does
with the answer:

| Decision | Shape |
| --- | --- |
| Provider selection | `auto` means "what the benchmark chose". An explicit `cpu` or `directml` means the user decided, same rule as threads |
| The battery rule | Straight from [Phase 8b](LINUX-PORT-PLAN.md#phase-8). A discrete GPU on a laptop is the difference between an afternoon and a lunchtime, and CPU at the measured knee is fast enough that nothing is lost. `GetSystemPowerStatus`, read per decision — no polling, no service, always current |
| When it switches | At the start of an utterance, never during one. Switching disposes `_sharedTts` and rebuilds, roughly a second, and doing that mid-sentence to chase a power event the user did not notice is indefensible |
| What the user sees | **Three causes, one symptom, and only the engine can tell them apart**: "CPU (on battery)", "CPU (no DirectML device)", "CPU (DirectML lost — disabled for this process)". The third already happens today via `_useDmlLatchedOff` and is invisible outside the log |

**Windows is the platform where this is answerable now.** Linux Phase 8b holds
GPU behind a gated spike because CUDA is gigabytes against a 76 MB ZIP and no
provider pack exists. DirectML already ships in the engine, already appends, and
already falls back. The only missing piece is evidence.

<a name="w5"></a>

### Phase W5 — upgrade safety · 0.5 day

[Trap 16](LINUX-PORT-PLAN.md#traps)'s Windows half, plus two things the same
audit turned up.

- **Route the version mismatch to a user.** W0 put the detector in About; About
  is not where anyone looks when something is wrong. The Status tab is, and it
  already knows how to present a failed check with a fix hint. Add the mismatch
  check there — including **engine x86 versus engine x64**, which is the split
  the trap describes and which comparing each against the launcher alone would
  miss when both are stale.
- **`INSTALL.txt` must say to close every SAPI client before extracting over an
  existing install.** It currently says "Move folder anywhere. Re-run
  VibeSuperTonic.exe" and never mentions the sharing violation that produces a
  half-upgraded engine. One paragraph in [pack-zip.ps1](../build/pack-zip.ps1).
- **Ship the x86 harness in `tools\`**, and publish it in CI beside the existing
  x86 assertion. A user diagnosing a 32-bit reader currently has no way to
  exercise the engine that reader loads, which is the half of the product most
  people run and the half that was never executed by anything until 2026-08-17.
- **Decide what the model download is agreeing to.** "Repair all" fetches the
  models; `LICENSE-MODELS.txt` says the user accepts OpenRAIL-M by accepting the
  download; there is no screen where that acceptance happens. Linux built one
  because the acceptance has to be a human agreeing to something. Either Windows
  gets the same screen or the reasoning for the asymmetry gets written down —
  **this is the user's call, not an engineering one.**

<a name="w6"></a>

### Phase W6 — the Core convergence · own branch, nothing else in the diff

Moved here from [LINUX-PORT-PLAN.md](LINUX-PORT-PLAN.md#convergence), which is
where it was written and where it does not belong: every item is a
behaviour-affecting change on the *Windows* product.

| Core has | Windows still has its own | Proven equivalent by |
| --- | --- | --- |
| `BoundaryPlanner` | `SapiEngine.EmitWordBoundaries` / `EmitSentenceBoundary` | unit oracle + harness step 10 |
| `SpeechSession` | the COM speak loop | — |
| `AudioBuffer.FloatToPcm16` | (already adopted) | Phase 1 |
| — | **two copies of the settings schema, already disagreeing** | [finding 6](#findings) |

The settings-schema row is new and is the cheapest of the four to act on. The
engine's `EngineSettings` and the launcher's are hand-maintained copies of one
file format, and their `TotalStep` defaults are already 8 and 6. It is masked in
practice — the Control Panel writes `settings.json` during migration before any
SAPI host can be registered — and it stops being masked the moment someone
deletes the file, ships a preset-less install, or adds a field to one copy. Linux
reads the same file through a *third* implementation
([SettingsFile.cs](../src/VibeSuperTonic.Ui/SettingsFile.cs)). Three readers, one
format, no shared type.

**Why this has not been done, and it is still the right reason.** Every row is a
change to shipping behaviour to no user-visible end, at the seam
[trap 11](LINUX-PORT-PLAN.md#traps) exists to warn about. Do it on its own
branch, with a TestHarness run in both bitnesses before and after, and nothing
else in the diff. Not while another phase is open. `Onnx.DirectML` as a separate
backend project is explicitly **not** part of this.

---

## Features worth having

Separate from convergence: things neither platform has, ranked by what they buy
against what they cost. Nothing here is committed.

**1 · A diagnostics bundle — `VibeSuperTonic.exe --diag` · half a day.** One ZIP
containing the version table, `settings.json`, `benchmark.json`, the effective
configuration, the last N session telemetry files, `launcher.log`, and the
registration state. [CLAUDE.md](../CLAUDE.md) describes triaging field reports as
a real activity; today that means asking a user to find and paste four things
from a folder they have never opened, and getting three of them. This is the
highest ratio of value to effort in the document and it becomes more valuable
after W1 and W2 give it something to say.

**2 · An idle session release · half a day, engine code.** The ONNX session is
~830 MB resident and lives as long as its host process. On Windows that host is
frequently NVDA or Balabolka, open all day. Releasing after an idle timeout and
paying the ~0.4 s warm reload on the next Speak is the same trade Linux is
weighing for its daemon, and Windows has the *more* sympathetic case: several
hosts can hold several sessions at once. Held back only because it touches
`SupertonicAdapter`, and because the right timeout is a measurement.

**3 · Per-voice benchmark rows — probably not, and here is why.** The profile is
keyed to the model set, and all ten voices share one model set; only the style
vector differs. Unless a sweep shows voice-to-voice variation above the noise
floor, this is seven times the sweep time for a number that does not change.
Worth **one** measurement to close the question, not a feature.

**4 · A Reader tab for Windows — deliberately not.** Linux built one because
nothing on Linux can push a cursor into someone else's window. Windows does not
have that problem: the engine emits real SAPI word boundaries and the host
highlights. Building a Reader here would duplicate what Balabolka already does
better. Noted so nobody ports it for symmetry.

**5 · Audio-device-loss recovery — checked, and not needed.** The Linux daemon's
worst defect was a dead PulseAudio stream that went unnoticed for five hours. On
Windows the SAPI *host* owns the audio device, so the equivalent failure is the
host's to handle. The analogous engine-side failure — DirectML device loss —
already has detection, a CPU retry, a watchdog and a latch, all exercised by
`--stress` in both bitnesses. Listed so the audit is honest about what does
*not* need porting.

---

## Effort

| Phase | Days | State |
| --- | --- | --- |
| W0 · One version, visible | 0.25 | **done, 2026-08-19** |
| W1 · Measure this machine | 1.5–2 | **built and run 2026-08-19, on estimate; the unpaced bench render is what is left** |
| — · Unpaced bench render (engine) | 0.5 + a harness run | not started, and it now gates W1 — [why](#w1-hardware) |
| W2 · Provenance | 0.5 | not started |
| W3 · Preset benchmark, made trustworthy | 0.5 | not started |
| W4 · Provider policy + battery | 1 | not started |
| W5 · Upgrade safety | 0.5 | not started |
| W6 · Core convergence | 1–2 | not started, own branch |
| **Total** | **5.25–6.75** | |

**W1 is estimated above 8a's one day on purpose.** 8a came in on estimate because
it was new code against interfaces that already existed, with a scripted fake
standing in for a model. W1 has a real obstacle 8a did not — the process-static
session, and an instrument that has to be engine telemetry rather than a
stopwatch — so it is new code against a seam that has to be *designed*, not
merely used. What it keeps from 8a is the part that overran there: **budget for
thresholds that are wrong about the machine rather than about the code.** Both of
8a's were found in the first five minutes of running it against real hardware and
neither was findable any other way.

**Every phase that touches engine code costs a TestHarness run in both
bitnesses** on a real install with registered voices. That is not in the numbers
above because it is not development time, but it is wall-clock time and it cannot
be skipped — see [trap 11](LINUX-PORT-PLAN.md#traps).

**Plan against the top of the range.** The one Windows phase whose output a
person uses directly is W3, and [Phase 1's overrun](LINUX-PORT-PLAN.md) is the
standing evidence that those are the ones that surprise you.

---

## Open decisions

- **Does an explicit `OnnxThreads` beat a measured profile?** [W1](#w1) proposes
  yes — a knob that silently loses to a measurement generates bug reports — but
  it is the opposite of the Linux precedence, where the profile beats
  `MaxCpuPercent`. The two are not really in conflict (a percentage is a guess, an
  absolute count is a decision), and saying so out loud is what stops the next
  reader from "fixing" the inconsistency.
- **Is there a licence acceptance screen on Windows, or is there a documented
  reason there is not?** [W5](#w5). The user's call.
- **What is the right idle timeout for releasing the session**, if feature 2 is
  taken. Needs a measurement, not an opinion.
- ~~**Does DirectML win anything?**~~ **On the development machine, yes —
  measured 2026-08-19.** 1292–1405 ms against a CPU pack scattered over
  1012–2613, fastest overall in two of three sweeps, using 0.6 cores against
  1.9–12.6. It is also the *steadiest* row, because the GPU is the one part of a
  working desktop nothing else is competing for. One machine is not a
  generalisation — but the shipping default of `UseDirectML: true` now has a
  measurement under it rather than nothing. [The record](#w1-hardware).
- **New, and it now blocks W1: the bench render is paced to real time.** ~6 s of
  wall clock to measure ~1.2 s of compute, which is why the sweep takes minutes
  and why its own duration lets the machine's load drift underneath it. An
  unpaced path in the engine is the fix; it is shipping-platform engine work and
  needs its own change with a TestHarness run either side.

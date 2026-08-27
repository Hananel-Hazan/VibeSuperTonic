# Speech Dispatcher — SAPI's equivalent on the Debian family

Status: **planned 2026-08-27, nothing built.** Target `0.2.12`. Every fact below
was measured on this machine on the day it was written, and the measurements are
what the plan is made of — the [traps](#traps) in particular were found by
running things, not by reading about them.

---

## What this is, in one paragraph

On Windows this project already *is* a system voice: it registers as a SAPI 5
engine and every SAPI client — Balabolka, NVDA, Narrator, Edge Read Aloud — can
use it without knowing it exists. On Linux there is no SAPI. There is
**Speech Dispatcher**, and it occupies the same place: Orca talks to it, `spd-say`
talks to it, and applications that speak at all speak through it. Right now
VibeSuperTonic on Linux is reachable only through its own hotkey, its own window
and its own `vst-ctl`. **Nothing that already speaks on this machine can use it.**

`0.2.12` is that: a Speech Dispatcher **output module**, so `spd-say -o
vibesupertonic`, Orca's voice list, and anything else that speaks, can reach
these voices.

---

## What was measured, 2026-08-27

On this box: **speech-dispatcher 0.12.1**, Ubuntu 26.04, one output module
installed (`espeak-ng`).

| Question | Answer | How it was found |
| --- | --- | --- |
| Can a module be installed **without root**? | **Yes.** A config under `~/.config/speech-dispatcher/` is enough | Wrote one, restarted speechd, `spd-say -O` listed it and `spd-say -o` ran its command |
| Is there a precedent for a **neural** TTS here? | **Yes** — `mimic3-generic.conf` ships with the distro and drives mimic3 through `sd_generic` | It is in `/etc/speech-dispatcher/modules/` |
| What does `sd_generic` hand a command? | `$DATA $VOICE $LANGUAGE $RATE $PITCH $PITCH_RANGE $VOLUME $PUNCT $PLAY_COMMAND $TMPDIR $HOMEDIR` | `strings` on the shipped binary |
| How is rate mapped? | `(speechd_rate × GenericRateMultiply) + GenericRateAdd`, speechd's rate normalised to −1…1 | The generic module confs state the formula |
| Do the system modules survive adding ours? | **No, not by default** — see [trap 1](#t1), the one that matters | Measured both ways |

**The binary modules directory is root-owned** (`/usr/lib/speech-dispatcher-modules/`,
holding `sd_dummy`, `sd_espeak-ng`, `sd_espeak-ng-mbrola`, `sd_generic`). We put
nothing there. `sd_generic` is already installed and is what we drive.

---

<a name="inherited"></a>

## What earlier plans deferred into this one

**This feature was analysed and deferred once already**, in
[LINUX-PORT-PLAN.md's non-goals for v1](LINUX-PORT-PLAN.md#non-goals-for-v1), and
the reason it gave is the reason it is cheap now:

> *A speech-dispatcher module. Deferred, not rejected. It is what Orca and
> Firefox talk to, so it is the right way to serve screen-reader users — but it
> needs the same warm-model daemon this plan builds, so it becomes a thin
> front-end later at low cost. Building it first would serve a narrower audience
> for more work.*

That daemon exists. **The prediction holds and this plan is the thin front-end** —
one verb and a config file. What follows is everything else the earlier plans put
down that this release is the first to actually need.

### Deferrals that become decisions here

| Deferred | Where from | Why 0.2.12 is where it lands |
| --- | --- | --- |
| **Should the daemon release its session after an idle timeout?** ~830 MB resident, and 0.43 s to warm back up — "probably yes" was the note | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) | **0.2.12 changes the answer to no, or to something cleverer.** A hotkey user pays 0.43 s once in a while and never notices. Orca speaks on every keystroke, so an idle release means the next keypress after a pause is 430 ms late — the worst possible moment. Whatever this becomes, it can no longer be "a timer". |
| **A lower `totalStep` for the opening chunk only.** First speech is ~750 ms, ~600 of it the model's fixed cost; halving it for the first short sentence roughly halves the wait, at an audible quality step | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) — *"the only lever left"* | [Trap 4](#t4) is exactly this question with a user attached. A screen reader's utterances are almost all short and almost all first. This is the lever, and 0.2.12 is where trying it is justified — the quality step nobody wanted for prose is much easier to accept for "Control_L". |
| **Does `install.sh` launch the first-run window, or does the user?** An installer over `ssh` must not try to open a window | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) | `speechd-install.sh` inherits it **and is worse**: the person configuring a screen reader is disproportionately likely to be in a TTY or over ssh, and disproportionately unable to see a window that did open. It must never require one. |
| **The Piper session is always CPU**, and `vst-ctl benchmark` measures Supertonic only | [P3](PIPER-PLAN.md#p3-open), [P4](PIPER-PLAN.md#p4-open), [P5](PIPER-PLAN.md#p5-open) | Still not a blocker, and now it has a number attached: whatever [trap 4](#t4) measures is measured on the CPU path, so a "too slow" verdict may be a provider verdict rather than a product one. Say which was measured. |
| **There is no CLI way to choose a voice** — the window is the only writer of `VoiceId` | [P5](PIPER-PLAN.md#p5-open) | `render --voice` settles the per-utterance half, because speechd sends `$VOICE` on every call and never touches a setting. The *default* stays the window's, and that is now a defensible line rather than an omission. |
| ⚠ **The Voices tab has never been looked at** | [P4](PIPER-PLAN.md#p4-open), [P5](PIPER-PLAN.md#p5-open) | [S3](#s3) generates the module's voice list from the same installed-voice state the tab shows, so they are one surface. The tab is owed a human eye before the 0.2.12 pack regardless — it has been owed for two releases. |
| **`ru_dict` is 4.9 MB of a 7.1 MB espeak payload** | [P5](PIPER-PLAN.md#p5-open) | Not this release. Recorded here only because 0.2.12 is the first release where "slim" is a stated goal — see [TESTING-PLAN.md](TESTING-PLAN.md#slim), which gives the archive a budget rather than an opinion. |

### Deferrals that stay deferred, and why

- **Everything Windows.** The Windows convergence, the Way 3 gate, `pack-zip.ps1`
  shipping a catalog, `Onnx.DirectML`, and the owed TestHarness run. Windows
  advances later; **0.2.12 is Linux only**, and nothing here touches a Windows
  path. The one thing to preserve is that Core stays platform-neutral, which the
  CI's Windows job already enforces by running `Core.Tests` on both runners.
- **`linux-arm64`.** Named a plausible later target in the port plan's non-goals.
  Speech Dispatcher is on ARM desktops too, so this feature would be wanted
  there — but the blocker is ONNX Runtime's ARM performance, which nobody has
  measured, and that is not this release's question.
- **Index marks and word highlighting.** See [the route decision](#decide).
- **32-bit.** No.

---

<a name="decide"></a>

## The decision: drive `sd_generic`, do not write a module binary

Two routes, and the second one is not the ambitious version of the first — it is
a different amount of work for a difference the first release does not need.

**Route A — `sd_generic` plus one new `vst-ctl` verb.** A config file names a
shell command; speech-dispatcher runs it per utterance and plays what it prints.
`mimic3` is shipped this way by the distro. No new binary, no new protocol, no
root, and the config is a file our installer writes.

**Route B — a native `sd_vibesupertonic`.** Speaks the module protocol on
stdin/stdout: `INIT`, `SPEAK`, `STOP`, `PAUSE`, `SET`, plus `BEGIN`/`END`/`STOP`
events and index marks. Buys precise pause/resume, index marks (a screen reader
highlighting the word being read), and lower per-utterance overhead. Costs a
binary that must live in a root-owned directory to be auto-detected, or an
explicit `AddModule` either way — so it does not even buy a simpler install.

**Route A, and B is not scheduled.** The thing B buys that A cannot fake is
**index marks**, and nothing in this product produces them today: the daemon
renders a chunk at a time and the word boundaries are not carried out of the
model. That is its own piece of work, it is not what makes these voices reachable,
and it can be added later without throwing A away — the `vst-ctl` verb A needs is
also what B would call.

### What Route A actually needs

**One new verb: `vst-ctl render`** — synthesise text and write a WAV to stdout
instead of playing it.

This is the whole product-side change, and it is small because the audio is
already there: [`ISynthesizer.Synthesize`](../src/VibeSuperTonic.Core/Synthesis/ISynthesizer.cs)
returns `short[]` PCM at a known rate. Nothing in the protocol can currently get
that out of the daemon — every path ends at the sink. So:

```
vst-ctl render --out - [--voice ID] [--rate R] "text"
```

and the module config becomes, in the shape `mimic3-generic.conf` already has:

```
GenericExecuteSynth "printf %s \'$DATA\' | $HOMEDIR/vst-ctl render --out - --voice \'$VOICE\' --rate $RATE | $PLAY_COMMAND"
```

**The daemon renders, speech-dispatcher plays.** That division is deliberate and
it is [trap 3](#t3): if our daemon played, speechd would lose the ability to stop
what it started, which is the single thing a screen reader needs most.

<a name="transport"></a>

### How the audio crosses the socket — decided, because it is a protocol change

**The control socket is line-delimited JSON**, source-generated for NativeAOT
([Protocol.cs](../src/VibeSuperTonic.Core/Ipc/Protocol.cs)). There is no binary
frame and there must not be an accidental one, so this is settled here rather
than by whoever reaches it first.

| Option | Why not |
| --- | --- |
| A temp WAV the daemon writes and `vst-ctl` cats | **Not streaming.** speechd would wait for the whole render before the first sample. `swift-generic.conf` does this and it is why swift feels slow; `mimic3-generic.conf` pipes `--stdout` instead |
| A second socket, or fd passing, for audio | A new IPC surface, new lifetime and cleanup rules, and a second thing to get wrong on every code path — for a saving that does not exist on a unix socket |
| `vst-ctl` loads the model itself | A cold load per utterance. This is the thing the daemon exists to avoid |

**Chosen: base64 PCM chunks as multi-reply JSON lines**, which is not a new
pattern — it is the one `benchmark` and `voice install` already use. A client
reads lines until one arrives without the progress field set
([`RequestVerb.VoiceInstall`](../src/VibeSuperTonic.Core/Ipc/Protocol.cs) states
the contract), and 43 voices of catalog already cross this socket as one ~40 KB
line, so a large line is precedented in kind.

What it costs and why that is acceptable: **33% encoding overhead on a local
unix socket**, which is not a number anyone can measure against a neural render.
What it buys: streaming from the first chunk, cancellation for free (the daemon
stops writing when the peer goes away), no new IPC surface, and the AOT
serializer unchanged.

**Two rules that come with it**, both of which are how this goes wrong:

- **Chunk, and bound the chunk.** `StreamReader.ReadLineAsync` has no length cap
  in either direction, so "one line per utterance" is unbounded memory driven by
  whatever a client sends. One chunk per rendered segment, and a stated ceiling.
- **`vst-ctl render` writes the WAV header itself**, then streams decoded chunks
  to stdout. The header needs the voice's real sample rate, which is in the first
  reply — so the first reply carries the format and no audio, exactly as the
  multi-reply pattern already does for a byte count.

---

<a name="traps"></a>

## Traps

Each was found by measurement on 2026-08-27, and each is a way this ships broken
while looking installed.

<a name="t1"></a>
### 1. Adding one module silently removes every other one

**This is the one that would have shipped.** speech-dispatcher 0.12 auto-detects
output modules when `speechd.conf` declares **none**: the distro's own file has
all nineteen `AddModule` lines commented out, and `espeak-ng` is offered anyway.

Add a single `AddModule` line and auto-detection stops. Measured, in this order:

| Config | `spd-say -O` says |
| --- | --- |
| Distro default, zero active `AddModule` | `espeak-ng` |
| Plus one `AddModule` for ours | **`vst-probe` — and nothing else** |
| Plus an explicit `AddModule` for espeak-ng as well | `espeak-ng`, `vst-probe` |

So a naive installer takes a blind user's working screen reader and removes its
only voice, in exchange for adding ours. **The installer must enumerate what
speechd currently offers, re-declare each one explicitly, and only then add
ours** — and it must be able to put it back.

<a name="t2"></a>
### 2. The user config replaces the system config, it does not extend it

`~/.config/speech-dispatcher/speechd.conf` is read *instead of*
`/etc/speech-dispatcher/speechd.conf`, not after it. Every setting the user had
from the distro file — audio backend, log level, default module, default rate —
is gone the moment we create one. So the installer copies the system file first
and appends, which is also what makes trap 1 recoverable.

And it must not clobber a user who already has one. On this machine there was
none; on a blind user's machine there almost certainly is, because that is what
`spd-conf` writes.

<a name="t3"></a>
### 3. Two audio paths that cannot stop each other

The daemon owns an audio device and plays what it renders. Speech Dispatcher
plays what a module prints. If the module asked the daemon to *speak*, then:

- `spd-say -C` (cancel) kills the module's child process, which is not the
  daemon, and the speech continues.
- Orca interrupting itself — which it does constantly, on every keystroke —
  would not interrupt.
- The daemon's own hotkey and speechd would fight over the device.

Hence `render`, not `speak`. The module prints a WAV and speechd plays it, so
cancelling is speechd killing a pipe it owns. The cost is that `vst-ctl stop`
and `spd-say -C` remain unrelated, which is correct and should be said out loud
in the docs rather than discovered.

<a name="t4"></a>
### 4. The first word is a latency budget, not a nice-to-have

Orca speaks on every keystroke. P2 measured a cold model load in **seconds** and
the first-word wait at 802 ms on CPU / 77 ms on GPU once warm. A module that
starts a daemon on its first utterance will look broken to the one class of user
this feature exists for.

So the install has to make the daemon warm and keep it warm, and `render` has to
autostart it the way the other verbs do — with the same 5 s budget `vst-ctl`
already uses. Worth measuring end to end before claiming this works: **the number
to beat is espeak-ng's, which is effectively zero.** It is entirely possible the
honest outcome is "excellent for reading documents, not for a screen reader's
keystroke echo", and if so that belongs in `INSTALL.txt` rather than in a bug
report.

<a name="t5"></a>
### 5. Rate does not mean the same thing on both sides

speechd normalises rate to −1…1 and applies `(rate × Multiply) + Add`. espeak
maps that onto words per minute, where 160 is normal. **This product's rate is a
multiplier with a calibration curve behind it** — P3 built it, and it is per
voice, because a Piper voice's `length_scale` is not linear in perceived speed.

`Multiply 0.5 / Add 1.0` gives 0.5×…1.5×, which is a starting point and not an
answer. Whatever is chosen has to be checked against the calibration curve, not
against the arithmetic — the point of that curve is that the naive mapping was
wrong by up to 2.8%.

Pitch has no equivalent at all: neither engine exposes it. Say so; do not fake it
by resampling.

<a name="t6"></a>
### 6. The module runs in a different environment from the window

speech-dispatcher spawns the module as its own child, in whatever environment the
session started it with. `VST_DATA_DIR` set in a terminal is not set there, and
an AppImage's portable store is found relative to *the AppImage's* location.
**0.2.10 already shipped one bug of exactly this shape** — a portable home that
made the hotkeys unbindable. So the module config must name an absolute path to
`vst-ctl`, and the installer must write the store location into the config
rather than assume the module will resolve it the same way the window does.

<a name="t7"></a>
### 7. A config change needs a restart, and the restart is not clean

speech-dispatcher caches its configuration for the life of the process. After
writing a config the daemon has to be stopped, and the **first** client call
after that hung long enough to hit a 20 s timeout in testing, twice; the second
call answered immediately. An installer that writes a config and prints "done"
without restarting leaves the user with no visible change and no error.

<a name="t8"></a>
### 8. `$VOICE` is speechd's name for a voice, not ours

`AddVoice "en" "FEMALE1" "en_US-ljspeech-high"` is the mapping, and speechd's
vocabulary is *language + one of MALE1..3 / FEMALE1..3 / CHILD\_*. Forty-three
Piper voices across 35 languages plus ten Supertonic styles do not fit that
vocabulary, and only the voices that are **actually installed** should appear —
a voice list offering 43 downloads to a screen reader is not a voice list.

So the module config is **generated from what is installed**, at install time and
again when a voice is added or removed, which makes it the first thing in this
product that has to be regenerated on a state change.

<a name="t9"></a>
### 9. `$DATA` must be inside single quotes, and that is a security rule

`sd_generic` builds a shell command and hands it to `system()`. It escapes the
utterance the standard way — `'` becomes `'\''`, confirmed in the binary's
strings along with its `child: escaped text is |%s|` log line — and **that
escaping only works if the substitution sits inside single quotes.** Every
shipped config writes `\'$DATA\'` for this reason; it looks like a style
convention and it is not.

A config that used double quotes, or none, would take **arbitrary text from any
application on the desktop** — which is what speechd carries — and pass it to a
shell. `$VOICE` is safer only because we generate it, and it should be quoted
identically anyway.

So: the generated config is generated by *us*, never hand-assembled from user
input, and [S4](#s4)'s packer assertion checks the quoting. This is the one
place in this feature where a mistake is a vulnerability rather than a defect.

<a name="t10"></a>
### 10. Orca's traffic is single characters, not sentences

The mental model of "a screen reader reads a paragraph" is wrong and it is the
one that would size this feature incorrectly. Orca's dominant traffic is **key
echo and character echo**: `a`, `Control_L`, `space`, one utterance per
keystroke, dozens per minute, each expected to start in tens of milliseconds and
to be interrupted by the next one before it finishes.

Three consequences, none of which the current product is shaped for:

- **A neural voice rendering the single letter "a" is the worst case for its
  fixed cost** — [trap 4](#t4), and the deferred `totalStep` lever is aimed at
  exactly this.
- **Chunking is wrong here.** `SentenceChunker` exists to split prose; a
  one-character utterance must skip it entirely.
- **It may simply be the wrong tool for echo.** A perfectly good outcome for
  0.2.12 is: excellent for reading a document or a web page, and a user who
  leaves espeak-ng as the echo voice. speechd supports different modules per
  message type, so that is a configuration, not a failure — and saying so in
  `INSTALL.txt` is more honest than pretending otherwise.

<a name="t11"></a>
### 11. SSML and punctuation modes arrive whether we handle them or not

speechd clients can set SSML mode, and `sd_generic` passes `$DATA` through
untouched. Fed to the model, `<speak>` and `<mark/>` are **spoken aloud** —
which is not a crash, is not a log line, and is unmistakably broken.

`$PUNCT` is the same shape from the other direction: speechd's four punctuation
levels mean "say the punctuation out loud", and this product has no concept of
it. `GenericPunctNone/Some/Most/All` map a level onto a command-line flag we do
not have.

**Decide both explicitly rather than by omission.** The cheap, honest answer for
0.2.12 is: `render` strips SSML tags (and says it does), and all four punctuation
levels map to nothing, with `INSTALL.txt` saying the module ignores punctuation
verbosity. The expensive answer is implementing them; neither is as bad as
shipping the tags into the audio.

<a name="t12"></a>
### 12. `render` and the hotkey share one synthesizer

`EngineRoutingSynthesizer` holds a single current `ISynthesizer`, and
[`EspeakPhonemizer` serialises every call on an instance lock](../src/VibeSuperTonic.Piper/EspeakPhonemizer.cs)
because espeak-ng keeps its voice and clause cursor in process-global state. So a
`render` arriving while the daemon is speaking does not race — it **queues**, and
the thing it delays is the hotkey the user just pressed.

Worse in the other direction: speechd calls `render` dozens of times a minute, so
a hotkey press can land behind a queue of key echoes.

This has to be a decided policy, not an emergent one. Options, cheapest first:
refuse `render` while a session is speaking (speechd sees an error, which it
handles); render on a second synthesizer instance (memory, and espeak is still
process-global); or a priority queue. **Whichever it is, `vst-ctl benchmark`
already sets the precedent** — it refuses concurrent runs with an `Interlocked`
compare-exchange and says so.

<a name="t13"></a>
### 13. The AppImage has no stable `vst-ctl` path — but it does have a verb

A module config names an absolute command. An AppImage's contents live in a
FUSE mount whose path changes every run, so `/tmp/.mount_XXXX/vst-ctl` is a
config that works once.

**Already solved by the AppImage's own dispatch**, which is worth knowing before
anyone designs around it:
[`pack-appimage.sh`](../build/pack-appimage.sh) makes `AppImage ctl …` exec
`vst-ctl`, and also dispatches on `ARGV0` so a symlink named `vst-ctl` pointing
at the AppImage works directly. So the config names the AppImage file, which is
where the user put it and does not move.

The cost is real and must be measured: **every utterance pays a FUSE mount**.
For a hotkey that is invisible; at Orca's utterance rate it may dominate
[trap 4](#t4)'s budget. If it does, the answer is a tiny extracted launcher, not
a redesign — but measure before writing one.

<a name="t14"></a>
### 14. A module that registers with nothing to say

Three states where the module installs perfectly and produces silence, all of
them likely on the machine of someone setting this up for the first time:

- **No models downloaded.** The first-run screen has not been accepted, so there
  is no voice at all. The installer must refuse, loudly, rather than write a
  config that cannot work.
- **`DefaultVoice` names a voice that is not installed.** [S3](#s3) generates the
  voice list, so this is a generation bug — and the failure is at speechd's
  `INIT`, which the user sees as the module missing from `spd-say -O`.
- **The daemon is not running and cannot start.** `render` autostarts it like the
  other verbs, but if that fails the module must exit non-zero with a message on
  stderr, because speechd logs stderr and a silent success is indistinguishable
  from a broken audio device.

**The general rule for this feature: never exit 0 having produced no audio.**
That is the same failure espeak-ng's missing dictionary has
([P5](PIPER-PLAN.md#p5-landed)), it is the failure this project keeps finding,
and here it has a screen-reader user on the other end of it.

---

## Phases

Deliberately small, and in this order because each one can fail the next. **S0 is
a gate**: it is allowed to end this plan.

### What is a human's, and cannot be handed to anyone else

Three items in this plan are **not** implementation, and a run that treats them
as implementation produces a confident wrong answer:

1. **S0's verdict.** The gate measures three numbers and then someone decides
   whether they are good enough to keep building. That is a product judgement
   about who this feature is for, not a threshold in a script. S0 *reports*; a
   person *decides*.
2. **The Voices tab's first human eye.** Owed since P4, and [S3](#s3) generates
   the module's voice list from the same state. Nothing automated can tell you a
   layout is wrong.
3. **Whether the honest sentence goes in `INSTALL.txt`** — [trap 10](#t10)'s
   "excellent for reading, leave espeak-ng for keystroke echo". That is a claim
   about the product, and it depends on 1.

Everything else in S1–S5 is ordinary work with testable exits.

<a name="s0"></a>
### S0 · Prove the pipe, and measure it · half a day · **GATE**

`vst-ctl render --out -` producing a WAV that plays, driven by a hand-written
module config. Then, before anything else is built, **the number**:

| Measure | Against | Why it gates |
| --- | --- | --- |
| First-word latency, warm, CPU | espeak-ng on the same machine | [Trap 4](#t4). If this is seconds, the feature is for reading and not for echo, and the rest of the plan changes shape |
| The same, with the utterance being `"a"` | espeak-ng | [Trap 10](#t10). The dominant traffic |
| The same again, invoked through the AppImage | The tarball's `vst-ctl` | [Trap 13](#t13). Isolates the FUSE mount cost |

**Exit criterion**: `spd-say -o vibesupertonic "hello"` speaks in a Supertonic
voice, `spd-say -C` stops it, and the three numbers above are written down.

**This gate can fail, and failing is a result.** If short-utterance latency is
hopeless on the CPU path, the honest outcomes are (a) ship it for document
reading and say so, (b) spend the [deferred `totalStep` lever](#inherited)
first, or (c) stop. Do not build S1–S5 to find out.

<a name="s0-result"></a>

#### S0 ran, 2026-08-27. The gate's numbers

`vst-ctl render --out -` is built and works: a warm daemon, CPU, Supertonic
`M1`, streaming a WAV to stdout. Median of seven runs each, wall time from
process spawn to the first PCM byte past the header, against the same machine's
`espeak-ng --stdout`:

| Utterance | VibeSuperTonic | espeak-ng | |
| --- | --- | --- | --- |
| `The quick brown fox jumps over the lazy dog.` | **718 ms** | 4 ms | 174× |
| `a` | **383 ms** | 5 ms | 71× |
| `Control_L` | **396 ms** | 4 ms | 99× |

**And the decomposition, which is what makes the verdict safe:**

| | |
| --- | --- |
| `vst-ctl --version` — process spawn alone | **2 ms** |
| `vst-ctl status` — spawn + connect + round trip | **6 ms** |
| So of the 383 ms for one letter, inference is | **~377 ms** |
| AppImage `ctl --version` — the same, through a FUSE mount | 22 ms |

**Four things follow, and three of them close open questions.**

1. **The pipeline is free and the model is everything.** 6 ms of 383. That
   settles [the route](#decide) beyond argument: a native module
   (route B) would save six milliseconds out of three hundred and eighty. It
   also means no amount of engineering on this side moves the number.
2. **[Open question 4](#open) is answered: the AppImage costs 20 ms**, which is
   5% of one inference. No extracted launcher is needed.
3. **[Trap 10](#t10) was right, and it is now measured.** 383 ms for a single
   character is not a keystroke echo. Orca users type at speed; the echo would
   fall behind within a sentence and never catch up.
4. **The deferred `totalStep` lever is the only lever left**, and it is not
   enough on its own. The port plan estimated halving it roughly halves the
   ~600 ms fixed cost, which would put a letter near 190 ms — still 40× espeak-ng.
   Worth trying for the reading case; it does not rescue echo.

**The verdict, which is a product judgement and not a threshold:** ship it for
**reading**, and let echo be handled by espeak. 718 ms before the first word of a
paragraph is the same order as the hotkey path this product already ships and
that people already use by choice; 383 ms per keystroke is not an echo.

<a name="whose-espeak"></a>

#### "Echo stays on espeak" — but *whose* espeak? Measured 2026-08-27

The first version of that sentence said "leave echo on espeak-ng" and meant the
distro's module, which would have been a mistake worth naming, because **we
already ship a complete espeak-ng and it is faster than the distro's.**

`build/build-espeak.sh` turns off `USE_LIBPCAUDIO`, `USE_KLATT` and
`USE_SPEECHPLAYER`, which reads like "no synthesis". It is not: those remove the
audio *device* backend and two optional synthesisers. eSpeak's own formant
synthesiser is the core of the library and is untouched, so `--stdout` produces
a WAV exactly as upstream does — checked, and it is what the numbers below were
taken from.

| First audio, median of nine | Ours (bundled) | The distro's |
| --- | --- | --- |
| `a` | **3.3 ms** | 6.6 ms |
| `Control_L` | **3.7 ms** | 5.0 ms |
| A sentence | **3.2 ms** | 4.0 ms |

**Ours wins because of the flags, not despite them** — there is no audio device
to open, so there is nothing to initialise before the first sample.

And the binary that drives it is **31 KB**, 27 KB stripped, 9 KB in the archive,
linking nothing but our own `libespeak-ng.so.1` and libc. We ship the library
already; shipping the binary beside it rounds to nothing.

**So the dependency question answers itself.** Routing echo to the distro's
module would reintroduce exactly the sidecar this project's
[GPL-3.0 decision](PIPER-PLAN.md#gpl) was made to remove — *"no apt package, no
version to detect, no distro variation"* — for a component we already carry, and
it would put our installer's [trap 1](#t1) mistake in front of the user's echo
as well as their reading. **0.2.12 should ship both voices: neural for reading,
our own espeak for echo, nothing installed.**

<a name="echo-cache"></a>

#### Could the neural voice do echo after all? A cache, measured

Worth answering with numbers rather than an opinion, because the appeal is real:
one voice for everything.

| | |
| --- | --- |
| `render` with empty text — the verb's floor, no synthesis | **4.6 ms** |
| A cache hit ≈ that floor + a 114 KB read and its base64 | **~6 ms, estimated** |
| A 56-entry echo vocabulary, rendered | 6.2 MB, 114 KB each, 21.7 s to build |
| Extrapolated to 500 entries | ~56 MB |

So **yes, it works**: the echo vocabulary is small and enumerable — letters,
digits, key names — and serving it from disk lands in espeak's own territory.

**But notice what it buys.** It is not speed: our espeak is already 3.3 ms. It is
**voice consistency**, and it costs 6–56 MB, a rebuild on every voice change
(~0.4 s per entry), and a miss policy for the unbounded case — word echo is not
enumerable, so a miss is either a 383 ms stall or a voice that changes
mid-stream. Both are worse than a user who deliberately chose a fast flat voice
for echo, which is what many screen-reader users do already.

**Recommendation: not in 0.2.12.** Ship our espeak for echo, and keep this
section so that whoever wants one voice everywhere has the measurements rather
than the argument.

**What all this changes below**: [S3](#s3) sets the module up for the message
types it is good at and declares an espeak-backed one for the rest, [S4](#s4)
ships the 31 KB binary and its `INSTALL.txt` sentence says what each voice is
for.

<a name="s1"></a>
### S1 · The `render` verb, properly · one to two days

- **Streaming, not buffered**, over [the transport decided above](#transport):
  base64 PCM chunks as multi-reply lines, bounded per chunk. A long document must
  not be one allocation and one silence. The WAV header takes the voice's real
  rate from the first reply — [P3](PIPER-PLAN.md#p3) made the sink follow the
  voice, and this must too.
- **`--voice` takes the qualified id**, so `piper:de_DE-thorsten-medium` works,
  and `#12` speaker selection comes along for free.
- **Cancellation on SIGTERM and on a closed stdout.** That is what `spd-say -C`
  becomes; a render that keeps going after its reader is gone is a CPU leak once
  per interruption, and Orca interrupts constantly.
- **Concurrency policy, decided and tested** — [trap 12](#t12).
- **SSML stripped, punctuation levels mapped to nothing, both documented** —
  [trap 11](#t11).
- **Never exit 0 with no audio** — [trap 14](#t14).
- Tests, per [TESTING-PLAN.md](TESTING-PLAN.md).

<a name="s2"></a>
### S2 · The installer, and trap 1 · one day

`speechd-install.sh` in the archive beside `install.sh`. **The enumerate-then
re-declare dance is most of it** ([trap 1](#t1)): read what `spd-say -O` offers
now, copy the system `speechd.conf` if the user has none, re-declare every
existing module explicitly, add ours, write the module conf with absolute paths
and correct quoting ([trap 9](#t9)), restart speechd ([trap 7](#t7)).

Also: `--remove` that restores what was there; refusal when no voice is installed
([trap 14](#t14)); and **no window, ever** — the inherited ssh/TTY constraint,
which for this installer is not a nicety.

The test that matters is not that ours works. **It is that espeak-ng still
answers afterwards, and again after `--remove`.**

<a name="s3"></a>
### S3 · The voice list · half a day

Generate `AddVoice` lines from installed voices, mapping 43 Piper voices and ten
Supertonic styles onto speechd's `language + MALE1..3 / FEMALE1..3` vocabulary —
[trap 8](#t8). Only what is installed appears.

This is the first thing in the product that must be **regenerated on a state
change**, so decide the trigger: a `vst-ctl speechd-sync` verb that
`voice install` / `voice remove` and the Voices tab all call, or regeneration
inside the installer only, with a documented "re-run it after adding a voice".
The first is better and is not much more work.

<a name="s4"></a>
### S4 · Packaging · half a day

The script into the tarball, a section in `INSTALL.txt` — including the honest
sentence [trap 10](#t10) may require — and packer assertions in the shape of the
nine that exist:

- the generated config names a `vst-ctl` (or AppImage) path **that exists**;
- `$DATA` and `$VOICE` are single-quoted in it ([trap 9](#t9) — this one is a
  security check, not a tidiness one);
- `speechd-install.sh` is executable and shellcheck-clean.

The failure being guarded is a module that registers, appears in Orca's voice
list, and says nothing.

<a name="s5"></a>
### S5 · The safety net · one day

New in this revision of the plan, and it is the half that keeps 0.2.12 from
being the release that broke somebody's screen reader. Everything in
[TESTING-PLAN.md](TESTING-PLAN.md) that this feature is the reason for:

- the **restore test** (S2's, automated),
- the **first-word latency budget** as a checked number rather than a memory,
- the **archive size budget**,
- the **no-audio-with-exit-0 test**,
- and CI actually running the Linux packer, which today it does not.

---

## Non-goals for 0.2.12

- **Index marks and word highlighting.** Route B territory, and the model does
  not emit word boundaries today.
- **A `.deb`.** "The Debian family" here means Speech Dispatcher, which is what
  Debian-family desktops actually speak through. The product stays portable;
  packaging it as a `.deb` is a separate argument with a separate answer.
- **Replacing espeak-ng as the system default.** Ours becomes *available*. Making
  it default is the user's choice and `spd-conf`'s job — and [trap 4](#t4) may
  well say it should not be.
- **Windows.** SAPI is already done there and this changes nothing about it, and
  **Windows advances later** — 0.2.12 is a Linux-family release. The constraint
  that survives is that Core stays platform-neutral, which CI enforces by running
  `Core.Tests` on both runners.
- **The daemon's idle-timeout question**, [inherited from the port
  plan](#inherited), is *answered* here (no timer) but the machinery is not
  built. Answering it is a sentence in a decision table; building an
  activity-aware release policy is its own piece of work.

---

<a name="open"></a>

## Open questions, to answer with a measurement rather than a preference

1. **Is the first-word latency good enough for a screen reader?** [Trap 4](#t4).
   Measure against espeak-ng on the same machine, on CPU, warm. This decides
   whether the feature is "a system voice" or "a system voice for reading, not
   for echo".
2. **What does `$RATE` have to be** for speechd's slider to feel linear against
   P3's calibration curve? [Trap 5](#t5).
3. **Does the Piper path hold up under a screen reader's utterance rate** —
   dozens of short utterances a minute, each one a process spawn? Route B exists
   for this answer being no.
4. **What does the FUSE mount cost per utterance** when the module is driven
   through the AppImage rather than the tarball? [Trap 13](#t13). If it is tens
   of milliseconds it is noise; if it is hundreds it decides that AppImage users
   need an extracted launcher.
5. **Does the deferred `totalStep` lever actually halve the first word**, and is
   the quality step audible on a one-word utterance? [Inherited](#inherited).
   Cheap to try, and it is the only lever left on the number [S0](#s0) gates on.
6. **Is concurrent `render` refused, queued, or given its own session?**
   [Trap 12](#t12). The answer is a measurement of how often a hotkey press
   actually lands behind key echo, and `vst-ctl benchmark`'s refusal is the
   precedent for the cheapest answer.

---

## How this gets verified

[TESTING-PLAN.md](TESTING-PLAN.md) is the companion to this document and was
written with it. Four properties, and this feature is the reason three of them
now have budgets rather than opinions: **safe** ([trap 9](#t9) is a shell
injection surface, and the daemon gains a second caller), **fast**
([trap 4](#t4) and [trap 10](#t10) are latency budgets S0 gates on), and
**valid** ([trap 14](#t14) — never exit 0 having produced no audio).

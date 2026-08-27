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

---

## Phases

Deliberately small, and in this order because each one can fail the next.

### S0 · Prove the pipe · half a day

`vst-ctl render --out -` producing a WAV that `paplay` plays, driven by a hand
written module config. **Exit criterion**: `spd-say -o vibesupertonic "hello"`
speaks in a Supertonic voice, and `spd-say -C` stops it. No installer, no voice
list, nothing generated.

If this is not achievable in a day the route is wrong and B needs revisiting
before anything else is built.

### S1 · The `render` verb, properly · one day

Streaming rather than buffered — a long document must not be one allocation and
one silence. WAV header with the voice's real rate ([P3](PIPER-PLAN.md#p3): the
sink follows the voice, and so must this). `--voice` accepting the qualified id,
so `piper:de_DE-thorsten-medium` works. Cancellation on SIGTERM and on a closed
stdout, which is what `spd-say -C` becomes. Tests.

### S2 · The installer, and trap 1 · one day

`speechd-install.sh` in the archive beside `install.sh`, and **the enumerate-then
re-declare dance is most of it**. Reads what `spd-say -O` currently offers,
copies the system `speechd.conf` if the user has none, re-declares every existing
module explicitly, adds ours, writes the module conf with absolute paths, and
restarts speechd. Plus `--remove`, which must put the file back the way it was —
and a test that proves espeak-ng still answers afterwards, because that is the
failure that matters.

### S3 · The voice list · half a day

Generate `AddVoice` lines from installed voices. Decide the mapping onto
speechd's MALE1/FEMALE1 vocabulary, and decide what happens when a voice is
installed later — regenerate on `voice install`, or a `vst-ctl speechd-sync`
verb the Voices tab calls.

### S4 · Packaging · half a day

The script into the tarball, a section in `INSTALL.txt`, and an assertion in
[pack-tar.sh](../build/pack-tar.sh) in the shape of the nine that exist: the
generated config must name a `vst-ctl` path that exists, since the failure is a
module that registers, appears in Orca's voice list, and says nothing.

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
- **Windows.** SAPI is already done there and this changes nothing about it.

---

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

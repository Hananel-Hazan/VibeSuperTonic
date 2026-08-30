# Speech Dispatcher — SAPI's equivalent on the Debian family

Status: **planned 2026-08-27, nothing built.** Target `0.2.13`. Every fact below
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

`0.2.13` is that: a Speech Dispatcher **output module**, so `spd-say -o
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

| Deferred | Where from | Why 0.2.13 is where it lands |
| --- | --- | --- |
| **Should the daemon release its session after an idle timeout?** ~830 MB resident, and 0.43 s to warm back up — "probably yes" was the note | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) | **0.2.13 changes the answer to no, or to something cleverer.** A hotkey user pays 0.43 s once in a while and never notices. Orca speaks on every keystroke, so an idle release means the next keypress after a pause is 430 ms late — the worst possible moment. Whatever this becomes, it can no longer be "a timer". |
| **A lower `totalStep` for the opening chunk only.** First speech is ~750 ms, ~600 of it the model's fixed cost; halving it for the first short sentence roughly halves the wait, at an audible quality step | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) — *"the only lever left"* | [Trap 4](#t4) is exactly this question with a user attached. A screen reader's utterances are almost all short and almost all first. This is the lever, and 0.2.13 is where trying it is justified — the quality step nobody wanted for prose is much easier to accept for "Control_L". |
| **Does `install.sh` launch the first-run window, or does the user?** An installer over `ssh` must not try to open a window | [Port plan, open decisions](LINUX-PORT-PLAN.md#open-decisions) | `speechd-install.sh` inherits it **and is worse**: the person configuring a screen reader is disproportionately likely to be in a TTY or over ssh, and disproportionately unable to see a window that did open. It must never require one. |
| **The Piper session is always CPU**, and `vst-ctl benchmark` measures Supertonic only | [P3](PIPER-PLAN.md#p3-open), [P4](PIPER-PLAN.md#p4-open), [P5](PIPER-PLAN.md#p5-open) | Still not a blocker, and now it has a number attached: whatever [trap 4](#t4) measures is measured on the CPU path, so a "too slow" verdict may be a provider verdict rather than a product one. Say which was measured. |
| **There is no CLI way to choose a voice** — the window is the only writer of `VoiceId` | [P5](PIPER-PLAN.md#p5-open) | `render --voice` settles the per-utterance half, because speechd sends `$VOICE` on every call and never touches a setting. The *default* stays the window's, and that is now a defensible line rather than an omission. |
| ⚠ **The Voices tab has never been looked at** | [P4](PIPER-PLAN.md#p4-open), [P5](PIPER-PLAN.md#p5-open) | [S3](#s3) generates the module's voice list from the same installed-voice state the tab shows, so they are one surface. The tab is owed a human eye before the 0.2.13 pack regardless — it has been owed for two releases. |
| **`ru_dict` is 4.9 MB of a 7.1 MB espeak payload** | [P5](PIPER-PLAN.md#p5-open) | Not this release. Recorded here only because 0.2.13 is the first release where "slim" is a stated goal — see [TESTING-PLAN.md](TESTING-PLAN.md#slim), which gives the archive a budget rather than an opinion. |

### Deferrals that stay deferred, and why

- **Everything Windows.** The Windows convergence, the Way 3 gate, `pack-zip.ps1`
  shipping a catalog, `Onnx.DirectML`, and the owed TestHarness run. Windows
  advances later; **0.2.13 is Linux only**, and nothing here touches a Windows
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

> **REVERSED 2026-08-27, after probing rather than reading. Route B.**
>
> A was chosen on the belief that B bought only index marks and six milliseconds.
> Then [trap 15](#t15) measured what a generic module actually receives: `SPEAK`,
> `CHAR` and `KEY` all arrive as bare `$DATA`, indistinguishable. Route A can only
> route echo by guessing from the text's shape — and after [S0](#s0-result),
> routing echo correctly is the difference between a module Orca can use and one
> it cannot.
>
> **B also turned out to be cheaper than assumed**, which is the other half of the
> reversal. See [what a native module actually is](#route-b), below: a line
> protocol on stdin/stdout, and — since speech-dispatcher 0.12 — the option of
> handing audio *back* to the server rather than playing it. No linking against
> speechd, no audio device, no `$PLAY_COMMAND`.
>
> **What A got right and B keeps**: `vst-ctl render` is unchanged. B calls the
> same verb over the same transport. S0's prototype is not wasted.

Kept below because the reasoning for A is still the reasoning for most of the
design, and because whoever revisits this should see what changed rather than
only the conclusion:

~~**Route A, and B is not scheduled.**~~ The thing B buys that A cannot fake is
**index marks**, and nothing in this product produces them today: the daemon
renders a chunk at a time and the word boundaries are not carried out of the
model. That is its own piece of work, it is not what makes these voices reachable,
and it can be added later without throwing A away — the `vst-ctl` verb A needs is
also what B would call.

<a name="route-b"></a>

### What a native module actually is — measured 2026-08-27

The word "native" suggests linking `libspeechd_module`, needing speechd's headers
at build time, and inheriting an ABI that varies by distro. **None of that is
required.** The shipped modules link it because they are C programs upstream
maintains; a module is a *process speechd spawns and talks to over stdin and
stdout*, and the protocol is text.

Read out of `libspeechd_module.so.0` on this machine:

| | |
| --- | --- |
| Commands in | `INIT`, `SET`, `AUDIO`, `SPEAK`, `CHAR`, `KEY`, `SOUND_ICON`, `STOP`, `PAUSE`, `QUIT`, `LIST VOICES`, `LOGLEVEL`, `DEBUG` |
| Replies out | `299 OK LOADED SUCCESSFULLY`, `200 OK SPEAKING`, `202 OK RECEIVING MESSAGE`, `203 OK SETTINGS RECEIVED`, `203 OK AUDIO INITIALIZED`, `301 ERROR CANT SPEAK`, `302 ERROR BAD SYNTAX`, `304 CANT LIST VOICES`, `401 ERROR INTERNAL` |
| Events out | `700 INDEX MARK`, `701 BEGIN`, `702 END`, `703 STOP`, `704 PAUSE`, `705 AUDIO`, `706 ICON` |

**`705 AUDIO` is the find.** speech-dispatcher 0.12 supports *server-side audio*:
the module describes a block and hands it back, and the server plays it.

```
705-bits=16
705-num_channels=1
705-sample_rate=44100
705-num_samples=<n>
705-big_endian=0
705-AUDIO
<the samples>
705 AUDIO
```

So our module **never opens an audio device**, never picks between PulseAudio and
PipeWire and ALSA, and never owns a `$PLAY_COMMAND`. It receives `SPEAK`/`CHAR`/
`KEY` distinctly, calls `vst-ctl render` or our espeak accordingly, and returns
blocks. `STOP` is an explicit command rather than a killed pipe — which is
strictly better than route A on the one thing [trap 3](#t3) was worried about.

<a name="b-gate"></a>

#### The one thing that must be checked before S1 — and it is a gate

**Server-side audio is not in every speech-dispatcher.** It is in 0.12.1, which
is what this machine runs. The archive claims Ubuntu 22.04+, and 22.04 ships an
older speech-dispatcher. **If 0.11 has no `705 AUDIO`, the module must play audio
itself on that distro**, which means an audio backend inside the module and a
choice between PulseAudio, PipeWire and ALSA — the complication route B was just
praised for avoiding.

Three possible answers, and the check is cheap because
[the smoke container is already `ubuntu:22.04`](TESTING-PLAN.md#what-2-found):

1. **0.11 has it** — nothing to do.
2. **0.11 lacks it** — the module negotiates: server-side audio when the `AUDIO`
   command offers it, otherwise pipe to a player, as route A would have.
3. **It is messier than either** — then route A with [trap 15](#t15)'s shape
   heuristic is the fallback, and S0's prototype already implements most of it.

**Do this first in S1. Do not write the module before knowing which.**

<a name="b-gate-result"></a>

#### The gate ran, 2026-08-27. **Answer 1: 0.11 has it**

[`spike/speechd-705-gate`](../spike/speechd-705-gate), against Ubuntu 22.04's
**speech-dispatcher 0.11.1** in a bare container and again on this machine's
0.12.1. The server sends `AUDIO`, then `audio_output_method=server`, and logs
`Initialized for server audio`. **Route B's module opens no audio device on the
oldest supported distro either**, so nothing in this plan changes shape — and
the distro's own `sd_espeak-ng` takes the same path on 0.11.1, which is the
strongest available evidence that this is the supported route rather than a
corner someone will close.

Three protocol facts the probe measured, all of which S2 must be built on:

1. **A command is answered before its parameters arrive.** `AUDIO` → reply →
   *then* the server sends the settings block and a lone `.` → reply again. A
   module that reads the block first **deadlocks**; one that answers out of order
   desynchronises, and the server then refuses to start *at all* rather than
   running without it. The first probe did exactly this.
2. `sd_espeak-ng` answers the command with `207 OK RECEIVING AUDIO SETTINGS` and
   the block with `203 OK AUDIO INITIALIZED`. `203` for both is also accepted.
3. `spd-say "hello"` reaches the module as **`<speak>hello</speak>`** — trap 11's
   SSML arrives whether or not anyone asked for it, which is what turned that
   trap from a worry into a measurement.

**And one thing left open, which is [S2](#s2)'s first bug.** With the audio block
returned, `spd-say -w` never returns: the end-of-utterance handshake after the
PCM is not right yet. Both `\n705 AUDIO\n` and `705 AUDIO\n` were tried as the
terminator. The server *plays* — 0.12.1 logs `Using pulse audio output method`
and then `speak_queue Playback` — so this is reply framing, not audio.

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

<a name="two-daemons"></a>

## Why two daemons, and why neither replaces the other

Asked 2026-08-27, and worth settling in writing because it looks like
duplication and is not. After 0.2.13 a machine runs **`vibesupertonicd`** and
**`speech-dispatcher`**, and the honest framing is: **ours is the engine, theirs
is the distribution channel.**

### What each one can do that the other cannot

| | `vibesupertonicd` | `speech-dispatcher` |
| --- | --- | --- |
| Holds the warm ONNX session (~830 MB) | **yes** | no — modules are slaves it starts and stops |
| Global hotkey, selection capture, tray, UI | **yes** | no concept of any of it |
| Reachable by Orca and other applications | no, and never will be | **yes** — that is the whole point |
| Routes per message type (echo vs reading) | no | **yes** |
| Works on a machine that has the other | **yes** | yes |

### Why we cannot drop ours and simply be a module

1. **A module cannot own a hotkey, a tray, or the selection.** speechd spawns
   modules, tells them to speak, and stops them. Press-a-key-and-read-what-is-
   selected has no speechd equivalent — it is not a TTS operation.
2. **A module's lifecycle belongs to speechd.** It restarts them on config
   reload, on error, on `spd-conf`. An 830 MB session reloading on someone
   else's schedule is a 0.43 s stall at a moment we do not choose, in a process
   we do not control.
3. **Portability, directly.** On a machine with no speech-dispatcher — a minimal
   WM, a container, a live USB — the product would be dead. Today it works
   there. Speech Dispatcher must stay something the product *offers*, never
   something it *needs*.
4. **Safety.** speechd's own socket here is `srw-rw-rw-` — mode 0666, gated only
   by its parent directory being 0700. Ours is 0600 **inside** a 0700 directory,
   and [`Protocol.SocketPath`](../src/VibeSuperTonic.Core/Ipc/Protocol.cs) sets
   both explicitly rather than inheriting them, because the `$XDG_RUNTIME_DIR`
   fallback is a shared `/tmp`. speechd can also be put on a TCP port — not the
   default, `LocalhostAccessOnly` on when it is, but one config line away.
   Keeping the model behind *our* socket means speechd's configuration cannot
   widen who reaches it: the module is just another local client with no more
   access than the user already has.

### Why we cannot drop theirs either

Nothing else on the system can reach our voices. Orca does not know our socket
exists and never will. That is the entire reason this plan exists.

### What the second daemon actually costs

Close to nothing, and much less than it looks:

- **We are not adding a daemon.** speech-dispatcher already runs for anyone with
  a screen reader — it is how they hear anything at all. Measured here:
  `speech-dispatcher` 15 MB, `sd_espeak-ng` 10 MB, `sd_dummy` 7 MB.
- **A user with no screen reader pays nothing.** speechd is not running, so our
  module is never loaded.
- **One copy of the model, in our process.** The module holds no model; it spawns
  `vst-ctl` (4 MB, 2 ms) per utterance and streams. ~40 MB of speechd stack
  against our 830 MB is noise.

**So the shape is settled**: our daemon stays the only thing that loads a model,
speechd stays optional, and the module between them is a thin adapter that holds
no state. That is the same conclusion
[the port plan reached in 2026](LINUX-PORT-PLAN.md#non-goals-for-v1) when it
deferred this — *"it needs the same warm-model daemon this plan builds, so it
becomes a thin front-end later at low cost"* — now with the numbers attached.

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

> **Superseded by route B, and [S3](#s3-landed) is where it stopped being true.**
> There is no generated config and nothing to regenerate: the module answers
> `LIST VOICES` from the store at the moment it is asked, so a voice downloaded
> mid-session appears in the next list rather than after the next login. What
> survives from this trap is the vocabulary problem itself, and the rule that only
> installed voices appear.

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
  0.2.13 is: excellent for reading a document or a web page, and a user who
  leaves espeak-ng as the echo voice. speechd supports different modules per
  message type, so that is a configuration, not a failure — and saying so in
  `INSTALL.txt` is more honest than pretending otherwise.

<a name="t15"></a>
### 15. A generic module cannot tell a keystroke from a sentence

**Measured 2026-08-27, and it decides how echo is routed.** speech-dispatcher's
protocol has distinct message types — `SPEAK`, `CHAR`, `KEY`, `SOUND_ICON` — and
`spd-say` exposes them as `-c` and `-k`. A probe module was given all three:

| Sent | What `sd_generic` handed the command |
| --- | --- |
| `spd-say "hello world"` | `hello world` |
| `spd-say -c a` | `a` |
| `spd-say -k Control_L` | `Control_L` |

**All three arrive as bare `$DATA`, with nothing marking which was which.**
`sd_generic` has one `module_speak` and one `GenericExecuteSynth`; the type does
not survive. So the plan's "neural for reading, espeak for echo" cannot be driven
by message type on [route A](#decide).

**It can be driven by shape, and nearly exactly.** A `CHAR` is always one
character. A `KEY` is always a keysym name, and those are enumerable. Everything
else is `SPEAK`. So the wrapper routes on the text it was given:

    one character            -> espeak      (3.3 ms)
    a known keysym name      -> espeak      (3.3 ms)
    anything else            -> neural      (718 ms to first word)

The only case it gets "wrong" is someone asking to *read* a single letter, which
espeak answers instantly and correctly — a better outcome than 383 ms of waiting.

**This is also the first real argument for [route B](#decide).** A native module
receives `SPDMessageType` in `module_speak` and would not need a heuristic. S0
showed route B saves six milliseconds of latency, which was not worth a binary;
*correct echo routing* is a better reason, and if the heuristic ever proves wrong
in the field, that is the upgrade path rather than a redesign.

<a name="t16"></a>
### 16. A screen reader that goes silent is worse than one that sounds wrong

Every other trap here is about correctness. This one is about what happens when
something breaks for a user who cannot see the error.

The daemon can be missing, un-startable, or still loading its model. Under
[trap 14](#t14) the module exits non-zero and speechd logs it — correct, and the
user hears **nothing**, which for someone navigating by ear is indistinguishable
from the machine having died.

**We now ship a voice that cannot fail**: 27 KB, no model, no daemon, 3.3 ms.
So the module should fall back to it rather than to silence — degraded, obviously
different, and still speaking. That turns the worst failure in this feature from
"my screen reader stopped" into "my screen reader sounds like espeak again", which
is a thing a user can notice, describe, and work around.

**Decide this before S1**, because it shapes the wrapper: the fallback is either
the wrapper's job or nobody's.

<a name="t18"></a>
### 18. The bundled espeak-ng could not find the library shipped beside it

Found 2026-08-28 while starting S2, and it was **a release blocker for 0.2.13**:
the voice [trap 16](#t16) calls "a voice that cannot fail" could not start.

`espeak-ng` links the SONAME `libespeak-ng.so.1`, and
[P5](PIPER-PLAN.md#p5-landed) named the shipped library exactly that so the
binary would find it. Necessary, and **not sufficient — the loader does not
search a binary's own directory unless an `$ORIGIN` runpath tells it to.** There
was none: cmake had baked in the *build machine's* install prefix. So the shipped
binary resolved through the ordinary loader path, which means

- on a machine with espeak-ng installed, **the distro's library, silently** —
  the exact substitution bundling exists to prevent, and
- on a machine without it, `error while loading shared libraries` and the binary
  does not start **at all**.

Verified in a bare `ubuntu:22.04` with no `libespeak-ng` present, where it
exited 127 before the fix and renders a 42 KB WAV after it.

**Why it is a linker flag and not `-DCMAKE_INSTALL_RPATH`.** espeak-ng's own
`src/CMakeLists.txt` sets the target property `INSTALL_RPATH` to
`${CMAKE_INSTALL_PREFIX}/lib`, and a target property beats the cache variable —
the first attempt changed nothing at all. Patching upstream's CMakeLists would be
worse than it looks: the GPL source offer is `git archive HEAD`, so a
working-tree patch ships a source tarball that does not correspond to the binary
beside it.

**And the check that hid it was the one meant to catch it.** The packer's
behavioural espeak probes ran under `export LD_LIBRARY_PATH="$dir"` — with a
comment saying that is "exactly how the module config will invoke it", an
assumption about a module that did not exist yet. With the path exported every
probe passed while the shipped binary could not start. Nothing is exported now.

**Three assertions replace it, and only the third catches this on a build
machine** — which is itself a finding, from sabotaging the other two:

1. the runpath begins with `$ORIGIN`;
2. **which library the loader actually picks** must be the one in the payload,
   read with `LD_TRACE_LOADED_OBJECTS=1`;
3. it renders a WAV under `env -i`.

Sabotage showed (3) still *passing* on the build machine with a payload built
without `$ORIGIN`, because the build machine is the one place the baked-in
absolute path exists. It would have gone red only on a user's machine. **A
behavioural check run in the environment that created the artifact is not
evidence about any other environment.**

<a name="t17"></a>
### 17. The audio block is not text, and three details make it work

Measured 2026-08-28 out of speech-dispatcher's own
`module_tts_output_send_server` (`src/modules/module_process.c`) and then
verified against a running server. **Identical in 0.11.1 and 0.12.1**, so there
is no per-distro variant of any of this.

```
705-bits=16\n 705-num_channels=1\n 705-sample_rate=R\n
705-num_samples=N\n 705-big_endian=0\n
705-AUDIO<NUL>              <- a NUL byte, NOT a newline
<HDLC-escaped PCM>
\n705 AUDIO\n
```

1. **`705-AUDIO` is terminated by a NUL byte.** Everything else in this protocol
   is newline-delimited, so this is the one place the obvious guess is wrong.
2. **The samples are HDLC-escaped, and this is the one that fails silently.** The
   block ends at a newline, so a newline *inside* the audio ends it early — and
   `0x0A` appears in virtually any real audio within milliseconds. `0x0A` and the
   escape byte `0x7D` are each sent as `0x7D` followed by the original with bit 5
   inverted. A 300 ms sine needed 132 escapes in 13230 bytes. **Omitting this
   does not produce an error**: the server reads a truncated block, plays it, and
   waits forever for an end-of-utterance that has been desynchronised into the
   sample data. That is precisely what the gate saw and misdiagnosed as the
   terminator.
3. **Chunk at 10000 bytes and read stdin between chunks.**
   `module_tts_output_server` does exactly this, and it is how `STOP` interrupts
   an utterance already in flight — a module that writes one large block cannot
   be stopped until it has finished writing it. Orca stops constantly, so this is
   not an optimisation.

**And the trap underneath the trap: a container cannot check any of it.**
`spike/speechd-705-gate/run.sh` proves the block is *accepted* on 22.04, but a
bare container has no audio device, so `spd-say -w` times out there for the
distro's own `sd_espeak-ng` too. Reading that timeout as a verdict on the module
under test is what produced the wrong diagnosis in the first place. The rule:
**always measure the system module beside ours, in the same run** — `run-local.sh`
does, and "ours hung" only means something when theirs did not.

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
0.2.13 is: `render` strips SSML tags (and says it does), and all four punctuation
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

Deliberately small, and in this order because each one can fail the next.
**S0 was a gate and it ran** — [its numbers are below](#s0-result). **S1 opened
with a second gate**, [the server-side audio question](#b-gate), which decided
the module's shape — [it was answered on 2026-08-27](#b-gate-result) and route B
stands. **[S1](#s1-landed), [S2](#s2-landed) and [S3](#s3-landed) all landed on
2026-08-28**; S4 is next.

Route B costs about **two days more than route A** — five to seven rather than
three — and buys correct echo routing, an explicit `STOP`, no audio backend, and
a voice list that cannot go stale. Decided 2026-08-27.

### What is a human's, and cannot be handed to anyone else

Three items in this plan are **not** implementation, and a run that treats them
as implementation produces a confident wrong answer:

1. **S0's verdict.** The gate measures three numbers and then someone decides
   whether they are good enough to keep building. That is a product judgement
   about who this feature is for, not a threshold in a script. S0 *reports*; a
   person *decides*.
2. **The Voices tab's first human eye.** Owed since P4, and [S3](#s3) answers
   `LIST VOICES` from the same installed-voice state. Nothing automated can tell
   you a layout is wrong.
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
as well as their reading. **0.2.13 should ship both voices: neural for reading,
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

**Recommendation: not in 0.2.13.** Ship our espeak for echo, and keep this
section so that whoever wants one voice everywhere has the measurements rather
than the argument.

**What all this changes below**: [S3](#s3) sets the module up for the message
types it is good at and declares an espeak-backed one for the rest, [S4](#s4)
ships the 31 KB binary and its `INSTALL.txt` sentence says what each voice is
for.

<a name="s1"></a>
### S1 · Settle the audio path, then the `render` verb · one to two days

**Starts with [the gate above](#b-gate)**: does the oldest supported distro's
speech-dispatcher accept `705 AUDIO`? Everything else in this phase is written
assuming yes, and the answer changes the module's shape if it is no.

Then `render` properly, which route B calls unchanged from
[S0's prototype](#s0-result):

- **Streaming, not buffered**, over [the transport decided above](#transport):
  base64 PCM chunks as multi-reply lines, bounded per chunk. The WAV header takes
  the voice's real rate from the first reply — [P3](PIPER-PLAN.md#p3) made the
  sink follow the voice, and this must too.
- **`--voice` takes the qualified id**, so `piper:de_DE-thorsten-medium` works,
  and `#12` speaker selection comes along for free.
- **Cancellation on SIGTERM and on a closed stdout.** Under route B this is what
  a `STOP` command becomes, and Orca sends them constantly.
- **Concurrency policy, decided and tested** — [trap 12](#t12).
- **SSML stripped, punctuation levels mapped to nothing, both documented** —
  [trap 11](#t11).
- **Never exit 0 with no audio** — [trap 14](#t14).
- Tests, per [TESTING-PLAN.md](TESTING-PLAN.md). S0's prototype has none.

<a name="s1-landed"></a>

#### S1 landed, 2026-08-28

The gate is [answered above](#b-gate-result). Everything else in the list is in
the tree, and the two decisions the phase existed to force are made rather than
inherited.

| | Where |
| --- | --- |
| Streaming base64 PCM, rate from the first reply | [DaemonServer.Render.cs](../src/VibeSuperTonic.Daemon/DaemonServer.Render.cs), from S0 |
| `--voice` takes the qualified id, `#12` included | Free from [P3](PIPER-PLAN.md#p3) — `VoiceId` already parses `piper:en_GB-vctk-medium#12` and is tested |
| SSML stripped, punctuation decided | [Ssml.cs](../src/VibeSuperTonic.Core/Text/Ssml.cs) |
| The concurrency policy, trap 12 | [RenderAdmission.cs](../src/VibeSuperTonic.Core/Ipc/RenderAdmission.cs) |
| Cancellation on SIGTERM and on a closed stdout | [RenderWav.cs](../src/VibeSuperTonic.Core/Ipc/RenderWav.cs) and `RunRender` |
| 60 tests, and each one sabotaged | `SsmlTests`, `RenderWavTests`, `RenderAdmissionTests` |

**The SSML rule is narrower than trap 11 asked for, on purpose.** "Strip SSML
tags" taken literally would delete angle brackets from source code, shell
pipelines and mathematics — text people highlight and ask to have read aloud. So
the strip runs only on text that opens with `<speak`, which is what makes it a
document rather than a document containing a bracket; everything else passes
through untouched. Inside a document a bare `<` is still a bare `<`, because
`<speak>a < b</speak>` is what an unescaping client produces and the naive rule
speaks the letter "a" and drops the rest of the sentence.

**Punctuation verbosity is decided by omission, and that is the decision.** All
four of speechd's levels map to nothing. [S4](#s4)'s `INSTALL.txt` says so.

**Trap 12's policy: the press wins.** A render arriving while the session is
anything but idle is refused with a reason. It is affordable *only* because of
[trap 16](#t16) — the module falls back to our own espeak rather than to silence
— so the user hears a flat voice rather than hearing the passage they are
listening to stutter. `RenderAdmission` says this in a comment because **if that
fallback is ever removed the policy has to change with it.** Renders do not
refuse each other: they serialise in the synthesizer already, and Orca's
commonest sequence is a stop followed at once by the next utterance, so refusing
the second would put an audible voice change on every keystroke.

**Two defects, both found by writing the tests rather than by reasoning.**

- **The WAV header write was unguarded.** Every sample write already treated a
  dead pipe as the stop; the header did not, so a `STOP` arriving between the
  module being spawned and the daemon's first reply — the fastest stop there is
  — killed `vst-ctl` with a stack trace instead of exiting quietly. The first
  write is the *likeliest* one to meet a closed pipe, not the least.
- **A vanished client was logged as a render failure.** The daemon's catch-all
  caught the `IOException` and wrote `render: failed after N chunk(s)`. Under
  route B that is what every `STOP` looks like, and Orca sends one on very nearly
  every keystroke — so the daemon log would have been unreadable on exactly the
  machine where reading it matters most.

**Why three of these files are in Core.** `RenderWav` and `RenderAdmission` are
Linux-feature code sitting in the platform-neutral project, which needs a reason.
It is [TESTING-PLAN's](TESTING-PLAN.md#where-a-check-belongs) — they are logic and
need no model, network or device — plus the thing that made S0's prototype
untestable: inside `RenderAsync` and `Program.cs` they were reachable only by
building a daemon with an engine, a session and a sink, which is to say not
reachable from a test at all. They also then run on the Windows runner, which is
the only guard shared code has against a Windows-motivated change.

**And the handshake the gate left open is closed** — 2026-08-28, before S2 rather
than during it, because it decided whether route B works at all. It was never the
terminator: `705-AUDIO` is followed by a NUL, and the samples are HDLC-escaped.
[Trap 17](#t17) has the details and the reason the first diagnosis was wrong.
`spd-say -w` through the probe now returns in 415 ms against the machine's real
speech-dispatcher, with the distro's own module measured beside it at 1413 ms.

<a name="s2"></a>
### S2 · The module · two to three days

`vst-speechd`, a process speechd spawns, speaking [the line protocol](#route-b)
on stdin and stdout. **It links nothing** — not `libspeechd_module`, not an audio
library, not speechd's headers.

What it does, in the order the failures matter:

1. **Routes on message type**, which is the whole reason for route B:
   `CHAR` and `KEY` → our espeak (3.3 ms); `SPEAK` → `vst-ctl render` (718 ms to
   first word). No heuristic, no guessing from the text's shape.
2. **Falls back to espeak rather than to silence**, decided 2026-08-27 —
   [trap 16](#t16). A daemon that is missing, still loading its model, or
   erroring must produce the espeak voice, not nothing. **A screen reader that
   goes silent is worse than one that sounds wrong**, and we now ship a voice
   that cannot fail.
3. **Returns audio with `705 AUDIO`** and never opens a device.
4. **Answers `STOP` immediately** — stop rendering, stop returning blocks, emit
   `703 STOP`. This is the operation Orca performs most.
5. **`LIST VOICES`** from what is installed, which is where [S3](#s3) plugs in.

<a name="s2-landed"></a>

#### S2 landed, 2026-08-28

`vst-speechd` — 2.7 MB NativeAOT, linking nothing but libc, spawned by
speech-dispatcher and talking the module protocol on stdin and stdout. Driven by
the machine's real speech-dispatcher with real audio,
[`run-module.sh`](../spike/speechd-705-gate/run-module.sh):

| | |
| --- | --- |
| `spd-say -O` | **both** `espeak-ng` and `vibesupertonic` — [trap 1](#t1) |
| `SPEAK` → neural | returned in 3520 ms, the daemon's model cold |
| `CHAR` / `KEY` → espeak | 615 / 1114 ms end to end, including playback |
| audio blocks the server parsed | 90 |
| `STOP` mid-utterance | `703 STOP`, and the module answers again afterwards |
| the control, `espeak-ng` SPEAK | 1914 ms |

**The single-threaded shape is upstream's, and it is load-bearing.** The audio
loop writes a block and then reads whatever has arrived before writing the next,
which is the only thing that lets a `STOP` interrupt an utterance already in
flight. A worker thread producing audio while the main loop answered commands
would need a lock around stdout and every reply would race the block beside it —
for no gain, because the thing that must be responsive is the stop, and the stop
is already checked between blocks.

**One audio path for both voices.** `vst-ctl render --out -` and the bundled
`espeak-ng --stdout` both write a WAV to a pipe, so the choice between them
changes nothing downstream of [`WavHeader`](../src/VibeSuperTonic.Core/SpeechD/WavHeader.cs)
— and [trap 16](#t16)'s fallback becomes "start the other one" rather than a
second code path. S1's *never exit 0 with no audio* is what makes the fallback
decidable at all: a non-zero exit or a stdout that is not a WAV **is** the
signal.

**What the tests are, and what sabotage changed about them.** 39 new checks — the
escaping, the block framing, the routing, the WAV reader in `Core.Tests` (both
runners), the protocol state machine in a new `SpeechD.Tests` (Linux only, since
it chmods a shell script). All sixteen rules behind them were sabotaged; **four
were not caught the first time**, and each was a test that did not reach the
thing it claimed to:

- Every utterance in the suite ended through the *both voices failed* branch, so
  deleting `702 END` from the success branch broke nothing. Fixed by a fake voice
  that actually renders — which also made it possible to assert **what text
  reached the renderer**, without which a module that dropped SSIP's `..`
  unescaping passed every test in the file.
- The non-WAV cases were all too short: they are refused by any reader simply for
  running out of bytes, so deleting the `RIFF`/`WAVE` check broke none of them.
- Every WAV in the suite declared `0xFFFFFFFF`, so a reader that special-cased a
  declared size of zero — and returned no audio from a good utterance — passed.

**Not done here, and deliberately.** The voice list is the two voices that always
exist, which is honest but is not [S3](#s3); the module is not in the archive yet,
which is [S4](#s4) along with the installer and the packer assertions.

<a name="s3"></a>
### S3 · The voice list · half a day

`LIST VOICES` answered from installed voices, mapped onto speechd's
`language + MALE1..3 / FEMALE1..3` vocabulary — [trap 8](#t8). Only what is
installed appears. Under route B this is a protocol reply computed at runtime
rather than a config file generated at install time, **which removes the
regeneration problem route A had**: nothing is stale because nothing is written
down. That is the second thing B turned out to buy.

<a name="s3-landed"></a>

#### S3 landed, 2026-08-28

[`VoiceList`](../src/VibeSuperTonic.SpeechD/VoiceList.cs) is the mapping and it is
pure; [`InstalledVoices`](../src/VibeSuperTonic.SpeechD/InstalledVoices.cs) is the
filesystem read that feeds it. Driven by the machine's real speech-dispatcher
against a composed install of six styles and one Piper voice:

| | |
| --- | --- |
| rows a client sees | **188** — 6 styles × 31 languages, + the Piper voice, + echo |
| `spd-say -t female1 -l de` | resolved to `supertonic:F1`, language `de`, **named back by the daemon's own refusal** |
| `spd-say -O` | still **both** modules — [trap 1](#t1) |
| the utterance | `rc=0` throughout, because [trap 16](#t16) caught every neural failure |

**The design was decided by a probe, and the probe corrected it.** Selecting a
voice by name sends `synthesis_voice=<name>` and *no* `voice`; everything else
sends `voice=male1` — one of eight lowercase symbolic names — with
`synthesis_voice=NULL`. So **the symbolic form is the ordinary path, not the
exception**, and a module reading only `synthesis_voice` would have ignored the
voice nearly every client asks for. Two more facts came out of the same run and
both are load-bearing: speechd **does not validate `synthesis_voice`** against the
list it was given (`-y no-such-voice` arrives verbatim), and **`language=` is sent
on every `SET` whether or not anyone chose it.**

That last one reversed a rule. Resolution originally treated a language as a
request and fell back to "the first voice for it", which meant every client that
had never picked a voice would silently replace the user's own configured default
with whichever row sorted first. **A language now narrows a request; it never is
one.** Two tests failed the moment it was written down, which is how it was found.

**Supertonic is listed once per style per language — 310 rows on a full install —
and that is deliberate.** The instinct is that no voice list should be that long;
the distro's own `espeak-ng` publishes **14,805** on this machine, so it is
unremarkable to the thing consuming it. Listing the styles once under English
would instead hide the product entirely from a user whose screen reader is set to
German, which is the audience this feature exists for.

**The gender labels are honest or absent.** Supertonic's styles are named `M1`…`M5`
and `F1`…`F5` upstream, which is the only statement of gender anywhere in this
product and is what makes `MALE1`/`FEMALE1` answerable at all. A Piper voice
states none, so none is claimed — it is still offered for a gendered request,
because refusing would answer "no voice" on a machine whose only installed voice
is a perfectly good one, and the caller's alternative to a voice is the espeak
buzz. A known gender is preferred over an unknown one. Rank counts **installed**
styles rather than reading the digit in the name: with only `M3` present, `male1`
has to find it.

**`vst-ctl` gained `--language`, which S3 found rather than planned.** Supertonic
is one model set over 31 languages and takes the language per utterance; the
daemon has read `Request.Language` since Phase 4 and `vst-ctl` was simply never
able to set it, so speechd's `language=de` had nowhere to go. A Piper voice sends
none — the model *is* the language.

**The models directory is the daemon's own rule, compiled in rather than
re-spelled.** It cannot move to Core ([R-12] keeps platform path policy out, and
Core's csproj says Core may not express a platform at all) and it cannot be
guessed here, because an AppImage's models sit beside the `.AppImage` file rather
than beside the executable and a two-line guess would report that an AppImage user
has no voices. So `LinuxDataPaths.cs` is compiled into the module the way
`Onnx.Ort` compiles the Engine's `SupertonicSdk.cs`.

**42 new checks, and all 22 sabotages of them were caught — after the first pass
found one that was not.** The rule that the voice list is re-read per request
rather than cached was tested with two *separate* module instances, so a cache
held in a field would have been re-read anyway and every mutation passed. It now
installs a voice **while one module is running**, through a stdin whose second
command only exists after the first has been answered. That is the same defect S2
found four of, and it is worth expecting one per phase. The `vst-ctl` argument
guard has no test project to live in and was sabotaged by hand instead: without
it, `--language de` makes `de` the verb — the exact bug the `--voice` comment
beside it already records.

**Not done here.** The module is still not in the archive and there is no
installer; that is [S4](#s4), along with the packer assertions.

<a name="s4"></a>
### S4 · The installer and packaging · one to one and a half days

`speechd-install.sh` in the archive beside `install.sh`. **The enumerate-then
re-declare dance is most of it** ([trap 1](#t1)): read what `spd-say -O` offers
now, copy the system `speechd.conf` if the user has none, re-declare every
existing module explicitly, add ours, restart speechd ([trap 7](#t7)).

`AddModule "vibesupertonic" "<abs path>/vst-speechd" ""` — and **the absolute
path is the AppImage trap** ([trap 13](#t13)) in a new place: an AppImage's
contents are a temporary mount, so the config must name the AppImage file itself,
which does not move. If the user renames or moves it, echo goes silent with no
error a blind user can see — so `--check` should exist and the installer should
say what it wrote.

Also: `--remove` that restores what was there; refusal when no voice is installed
([trap 14](#t14)); **no window, ever**; the module and the 27 KB espeak binary
into the tarball; and packer assertions in the shape of the nine that exist —
the module is executable, it answers `INIT` with `299`, and the config names a
path that exists.

The test that matters is not that ours works. **It is that espeak-ng still
answers afterwards, and again after `--remove`.**

<a name="s4-landed"></a>

#### S4 landed, 2026-08-28

[`speechd-install.sh`](../build/speechd-install.sh) is the installer and it ships
in the archive beside `install.sh`;
[`check-speechd-payload.sh`](../build/check-speechd-payload.sh) is the packer's
new assertion; [`appimage-speechd.sh`](../build/appimage-speechd.sh) and
[`appimage-home.sh`](../build/appimage-home.sh) are what make the same installer
correct for an image whose contents move every run.

**Driven against the machine's real speech-dispatcher 0.12.1, in a scratch
`XDG_CONFIG_HOME`** — [`restore-test.sh`](../spike/speechd-s4-install/restore-test.sh),
six scenarios, 40 checks:

| | |
| --- | --- |
| baseline, no user config | espeak-ng offered, **14,805** voices |
| after our install | **both** modules offered; espeak-ng still 14,805; ours 188 |
| after `--remove` | espeak-ng back to 14,805, ours gone, our config file deleted |
| a user who already had a config | settings survive, backed up, restored **byte-identical** |
| no voices / no phonemiser / no enumeration | refuses, and writes nothing |
| the install moved afterwards | `--check` names the path, not the module |

**One redirected variable, not a set of arguments.** The harness moves
`XDG_CONFIG_HOME` and nothing else: speech-dispatcher reads its user config from
there (measured — and it still resolves *module* configs from `/etc`, so a user
config need not carry them), and the installer writes there by default. So the
installer under test resolves the module, the store and the config exactly as it
will on a user's machine. `-C` was tried first and is wrong: it replaces the
system directory wholesale, so trap 2's copy has nowhere to copy *from*, and it
refuses to start when the directory has no `speechd.conf` — which makes the
auto-detection baseline untestable.

**The trap-1 catastrophe was in the code, and a test found it rather than a
reading.** `printf '%s\n' "${names[@]}"` on an **empty** array prints one blank
line, not nothing — so the enumeration counted one module, the refusal never
fired, and the config written declared ours **and nothing else**. That is the
exact failure this script exists to prevent, it survived being written and
reviewed, and what caught it was asking speechd through a dead socket.

**Three of the first thirteen sabotages found defects in the tests, not the
code**, which is the ratio to expect and the reason for doing it:

- `grep 'AddModule "espeak-ng"'` matched the **commented** line the copied system
  config brings with it, so "espeak-ng was re-declared" passed while the module
  list was in fact destroyed. Active declarations only.
- the moved-install scenario proved nothing: with speechd not restarted, `--check`
  failed because ours was *configured but not offered*, whatever the path said.
- the packer's own trap-13 probe had the same shape — it asserted a non-zero
  exit, which a build machine running speech-dispatcher produces anyway. It now
  requires the words.

`payload-sabotage.sh` does the same for the packer assertion, **9 of 9 caught**,
and it is fast because the assertion is a script rather than a step inside a
four-minute pack. Hardlink copies (`cp -al`) were the first attempt and are
wrong twice over: `/tmp` is a different filesystem, and a mutation that *appends*
to a hardlinked file writes through to the archive it is protecting.

**`pgrep -x speech-dispatcher` can never match.** The name is 17 characters,
pgrep matches the 15-character `comm` and refuses a longer pattern outright — so
the installer's wait-for-it-to-die loop exited on its first iteration and the
config was rewritten under a server that had not finished dying. It reads as
"Speech Dispatcher already running" three scenarios later.

**The AppImage cannot be named in a config, and the reason is measured.** A
binary field containing a space is exec'd verbatim: `Exec of module ... failed
with error 2` — *while speechd still logs the module as loaded*. Naming the image
with no argument is worse, because AppRun would fall through to its default case
and open the window once per utterance. So the config names
`~/.local/bin/vst-speechd`, a symlink to the image, and AppRun's `$ARGV0` case
turns it into the module. A **symlink, not a copy** — the opposite of the hotkey
client, which is copied: a copied module would resolve `espeak/` and `vst-ctl`
"beside me" in `~/.local/bin`, find neither, and answer `INIT` with 399. It has
to run inside the mount, and it is started once per session, so the mount it
costs is paid once.

**A portable home would have made the config unreadable**, the same way it made
the hotkeys unbindable in 0.2.10: `$HOME` points inside `<image>.AppImage.home`
and speech-dispatcher reads the *real* `~/.config`. The rule for which files
belong in the real home now lives in `appimage-home.sh` and has one
implementation with two callers, with its own [test](../spike/speechd-s4-install/home-rule-test.sh)
covering a passwd entry that points back at the portable home and a `getent` that
fails outright.

**The real machine found one more, and it is an upgrade bug rather than an
install bug.** Registering the module makes `vst-speechd` a *second* long-lived
process: speech-dispatcher spawns it once per login and holds it for the session.
So an upgrade that stops only the daemon — which is what `install.sh` and every
instruction in this project said to do — leaves a module answering the screen
reader from the binary being replaced. On an AppImage it announces itself, because
the module holds the image file open and `cp` refuses with **"Text file busy"**;
forced past that, the module dies on its next spawn, speechd never notices, and
**every utterance hangs forever with no error anywhere** — a `[vst-speechd]
<defunct>` in `ps` is the only evidence. `install.sh` now stops speech-dispatcher
as well, but only when the user's config actually declares our module, and
INSTALL.txt and the AppRun help say so for a hand-untar.

Two false leads on the way there, both worth recording because both looked
conclusive. `env -i` makes the AppImage runtime fail with *"No suitable
fusermount binary found on the $PATH"*, which is a real fragility and was not
this — speechd passes its children a full environment, read from a live
`sd_espeak-ng`'s `/proc/<pid>/environ`, and the module runs perfectly under an
exact copy of it. And a `pgrep -f "bin/speech-dispatcher"` in the diagnosis
matched **its own command line** and hung for four minutes, which is the same
trap `install.sh` carries a comment about.

**And once more on the way back in: stopping it before is not enough.** Replacing
the image a second time, anything that spoke during the copy started a fresh
speech-dispatcher which spawned the module from the image being overwritten. The
module died, and that speech-dispatcher then answered **nothing** — `spd-say -O`
empty, espeak-ng zero rows — because it was still waiting on a module that no
longer existed. One `kill` on the server and the next request rebuilt everything
correctly. So the instruction is stop it **before and after**, and INSTALL.txt and
the AppRun help now say so.

**Measured on the real install** (KDE/Wayland, portable home, CPU inference):
`spd-say -o vibesupertonic -w` returns in **2.1 s** warm for a short sentence and
**~620 ms** for a single character on the espeak echo path — whole round trips
including playback, not first-word latency, which is [S5](#s5)'s number to take
properly.

**Not done here.** Everything in [S5](#s5): the latency budget as a checked
number, the archive-size budget, and the no-audio-with-exit-0 test. The restore
test is written and passes, but it is a spike script — CI does not run it,
because it needs a speech-dispatcher and CI's smoke container deliberately has
none. What CI does get is the packer assertion and the module's `INIT` in the
bare container.

<a name="s5"></a>
### S5 · The safety net · one day

New in this revision of the plan, and it is the half that keeps 0.2.13 from
being the release that broke somebody's screen reader. Everything in
[TESTING-PLAN.md](TESTING-PLAN.md) that this feature is the reason for:

- the **restore test** (S4's, automated — espeak-ng still answers after an
  install and again after `--remove`),
- the **first-word latency budget** as a checked number rather than a memory,
- the **archive size budget**,
- the **no-audio-with-exit-0 test**,
- ~~and CI actually running the Linux packer, which today it does not~~ —
  **done 2026-08-27**, along with the clean-container smoke test, which found a
  release blocker on its first run. Items 1 and 2 of
  [TESTING-PLAN.md](TESTING-PLAN.md#the-order-to-do-it-in).

---

## Non-goals for 0.2.13

- **Index marks and word highlighting.** The protocol reserves `700 INDEX MARK`
  and route B could carry it — but the model does not emit word boundaries, so
  there is nothing to mark. Doing route B does not make this free; it makes it
  *possible later*, which is a different sentence.
- **A `.deb`.** "The Debian family" here means Speech Dispatcher, which is what
  Debian-family desktops actually speak through. The product stays portable;
  packaging it as a `.deb` is a separate argument with a separate answer.
- **Replacing espeak-ng as the system default.** Ours becomes *available*. Making
  it default is the user's choice and `spd-conf`'s job — and [trap 4](#t4) may
  well say it should not be.
- **Windows.** SAPI is already done there and this changes nothing about it, and
  **Windows advances later** — 0.2.13 is a Linux-family release. The constraint
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

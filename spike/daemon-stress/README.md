# Daemon stress harness

Two scripts, no dependencies beyond python3. They exist because the daemon's
failure modes are not reachable from `Core.Tests`: the protocol lives behind a
socket, the selection path behind a display, and both are in
`VibeSuperTonic.Daemon`, which `Core.Tests` deliberately does not reference.

Everything they do is something a real client does — a hotkey held down, a
window closing mid-transfer, a UI reconnecting, a script sending nonsense.

```bash
# start a daemon first
src/VibeSuperTonic.Daemon/bin/Release/net10.0/vibesupertonicd \
    --models spike/phase0-linux/out/linux-x64/models &

python3 spike/daemon-stress/protocol-stress.py

python3 spike/daemon-stress/selection-stress.py \
    spike/x11-select/bin/Release/net10.0/x11-own \
    src/VibeSuperTonic.Ctl/bin/Release/net10.0/vst-ctl \
    <daemon-pid>
```

`selection-stress.py` needs `spike/x11-select` built — stock Mint has no xclip
or xsel, so that spike is the only thing on the machine that can put text on
PRIMARY on demand.

## What they found, 2026-08-15

Four defects, all in code that had passed every unit test:

- **A UTF-16 byte-order mark hung the connection.** `StreamReader` defaults to
  `detectEncodingFromByteOrderMarks: true`, so a request starting `0xFF 0xFE`
  switched that connection's decoder to UTF-16 and the framing newline never
  appeared again. The one malformed input that produced no reply at all.
- **The accept backlog was 16**, and a unix socket with a full queue fails
  `connect()` with EAGAIN immediately rather than waiting. Twenty threads
  issuing status calls lost **158 of 400**.
- **`vst-ctl` read any connect failure as "no daemon is listening"**, which is
  the trigger for auto-starting one (R-5). A busy daemon therefore produced a
  second daemon.
- **`ClearStaleSocket` deleted the socket file on any `SocketException`**, so
  that second daemon would unlink the first one's socket and bind its own —
  two processes, one holding the audio device and unreachable by any client.
  The method's own comment said that outcome was far worse than refusing to
  start, and the code did it anyway.

After the fixes: 400/400 concurrent calls, 0 failures; every malformed input
answered; 40 owner-death races survived.

---

## rss-budget.py — how much memory it holds, and whether that grows

```bash
python3 spike/daemon-stress/rss-budget.py --models <models-dir> \
    [--voice piper:en_GB-cori-high] [--utterances 100] [--warmup N]
```

Warm-up is **measured, not passed in**: it renders in batches of ten until the
peak stops moving, then measures the next N. `--warmup N` forces a fixed one.

**The one check in [TESTING-PLAN.md](../../docs/TESTING-PLAN.md) that cannot be a
CI job.** Every other budget there is measured with no model on disk; this needs
1.6 GB of them and a machine that is not doing anything else. It starts its own
daemon under a redirected `XDG_RUNTIME_DIR`, so the session's daemon is never
touched, and it **renders** rather than speaks — no audio device is opened and
the machine stays quiet.

Why it matters more than the archive's size: a tarball that gained 20 MB costs a
download once. A daemon that gains 20 MB per utterance takes the machine down by
lunchtime, and the user's report is "my computer got slow", which nobody
attributes to a text-to-speech engine.

### Three wrong versions came first, and each was caught by running it

**A fixed growth budget** measured from the first utterance. Growth is
sub-linear — 30 utterances grew 52 MB, 100 grew 100 MB — so the same behaviour
passes at N=30 and fails at N=100. That budget is really a budget on how long you
ran the script.

**Comparing the two halves of the run**, on the theory that a warming heap
decelerates and a leak does not. It reads well, and a daemon deliberately leaking
2 MB per utterance **passed it**: +108 MB then +61 MB, which the ratio scored as
"settling". Warm-up here is big enough to hide a real leak inside its own
deceleration.

**A fixed warm-up of 100**, which is right for Supertonic — its peak stops moving
by utterance 60 — and wrong for Piper, which is still climbing at 100 and settles
near 200. So it reported a Piper voice as leaking **1353 kB per utterance**, and
the same run with a 200-utterance warm-up grows by **zero**. A false leak report
is the worst outcome available here: it sends somebody hunting a defect that does
not exist, in a component that is fine. Warm-up is now measured — batches of ten
until the peak has not moved for thirty utterances — which is the thing that
differs between engines, so it is not something to assume.

What works is to stop measuring during warm-up: render until it settles, throw
that away, then measure. There is then nothing for a leak to hide behind.

### What it measured, 2026-08-30, CPU, Supertonic M1

| | |
| --- | --- |
| bound, no model loaded | **665 MB** |
| after the first utterance | **697 MB** |
| peak, reached by ~utterance 60 | **842 MB** |
| at utterance 200 | 768 MB, peak still **842 MB** |

And `piper:en_GB-cori-high`, which is the more expensive of the two and takes
three times as long to settle:

| | |
| --- | --- |
| bound, no model loaded | **658 MB** |
| after the first utterance | **950 MB** |
| peak, reached by ~utterance 200 | **1087 MB** |
| 100 utterances after that | 1084 MB, peak still **1087 MB**, growth **+0 MB** |

**It plateaus.** The peak stopped moving around utterance 60 and had not moved
200 utterances later — so the early "+100 MB per 100 utterances" is warm-up, not
a leak, and Phase 0's ~830 MB figure is confirmed from a second direction.

### The sabotage, and it is the reason to believe the number

A daemon patched to retain 2 MB per render, built and run through the same
harness:

```
                       warm-up          measured
  the real daemon      +75 MB / 100     +1 MB / 100     leak ok
  leaking 2 MB/utt     +320 MB / 180    +117 MB / 60    IT LEAKS — about 2002 kB per utterance
```

It reports the per-utterance size, and 2002 kB against an injected 2048 is the
check reading the right thing rather than merely going red.

**One honest wrinkle in the adaptive warm-up**: against the leaking daemon it
declared "settled" after 180 utterances, because a GC dip held the peak still for
three batches while memory was very much still being retained. That is fine, and
it is why the measurement window rather than the warm-up is what decides —
sixty utterances later the verdict was not close. A warm-up that ends early costs
nothing; a measurement window inside warm-up is what produced the false Piper
report above.

**The budget that travels is the tail, not the ceiling.** A ceiling belongs to
this machine's models and provider and is worth recording in release notes the
way P2's table is; growth after warm-up belongs to the code and reads the same on
any hardware.

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
    [--voice piper:en_GB-cori-high] [--warmup 100] [--utterances 100]
```

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

### Two wrong versions came first, and the second one is the interesting one

**A fixed growth budget** measured from the first utterance. Growth is
sub-linear — 30 utterances grew 52 MB, 100 grew 100 MB — so the same behaviour
passes at N=30 and fails at N=100. That budget is really a budget on how long you
ran the script.

**Comparing the two halves of the run**, on the theory that a warming heap
decelerates and a leak does not. It reads well, and a daemon deliberately leaking
2 MB per utterance **passed it**: +108 MB then +61 MB, which the ratio scored as
"settling". Warm-up here is big enough to hide a real leak inside its own
deceleration.

What works is to stop measuring during warm-up: render N utterances and throw
them away, then measure the next N. There is then nothing for a leak to hide
behind.

### What it measured, 2026-08-30, CPU, Supertonic M1

| | |
| --- | --- |
| bound, no model loaded | **665 MB** |
| after the first utterance | **697 MB** |
| peak, reached by ~utterance 60 | **842 MB** |
| at utterance 200 | 768 MB, peak still **842 MB** |

**It plateaus.** The peak stopped moving around utterance 60 and had not moved
200 utterances later — so the early "+100 MB per 100 utterances" is warm-up, not
a leak, and Phase 0's ~830 MB figure is confirmed from a second direction.

### The sabotage, and it is the reason to believe the number

A daemon patched to retain 2 MB per render, built and run through the same
harness:

```
                       warm-up      measured 60
  the real daemon      +67 MB       +7 MB       leak ok
  leaking 2 MB/utt     +139 MB      +143 MB     IT LEAKS — about 2436 kB per utterance
```

It reports the per-utterance size, and 2436 kB against an injected 2 MB is the
check reading the right thing. Twenty times the separation between pass and fail.

**The budget that travels is the tail, not the ceiling.** A ceiling belongs to
this machine's models and provider and is worth recording in release notes the
way P2's table is; growth after warm-up belongs to the code and reads the same on
any hardware.

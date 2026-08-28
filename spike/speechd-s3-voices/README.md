# S3: what a client's voice selection actually looks like to a module

Measured 2026-08-28 on this machine's **speech-dispatcher 0.12.1**, and the
measurements are what [S3](../../docs/SPEECHD-PLAN.md#s3-landed) was built from —
including the one that reversed a rule after the code was already written.

Same house rule as [the 705 gate](../speechd-705-gate/README.md): **probe it,
don't read about it**, and always keep the distro's own `espeak-ng` in the config
beside ours so "ours did X" means something.

## Run

```bash
bash spike/speechd-s3-voices/run-probe.sh          # what the server sends a module
bash spike/speechd-s3-voices/run-module.sh         # the real vst-speechd, end to end
python3 spike/speechd-s3-voices/sabotage.py        # break each S3 rule, check a test notices
```

`run-module.sh` drives a scratch install at `/tmp/vst-s3-install`; make one with
`bash spike/speechd-s3-voices/compose-install.sh` first. It is the S3 counterpart
of [`run-module.sh`](../speechd-705-gate/run-module.sh) in the gate directory.

**The models it composes are fake**, because what is measured is the voice list
and the arguments reaching the renderer, not synthesis. The neural render fails at
the daemon with `ONNX model directory not found` and that is the correct end of
the test: it proves the resolved voice **reached the daemon** — the daemon names
it back — and that trap 16 caught the failure instead of going silent.

## What was found

| Question | Answer |
| --- | --- |
| Selecting a voice **by name** | `synthesis_voice=<name>`, and **no `voice` at all** |
| Selecting **any other way** | `voice=male1` — one of eight lowercase symbolic names — with `synthesis_voice=NULL` |
| Is `synthesis_voice` validated against `LIST VOICES`? | **No.** `-y no-such-voice` arrives verbatim |
| Is `language=` a request? | **No.** It is sent on *every* `SET` whether or not anyone chose it |
| The eight symbolic names | `male1..3`, `female1..3`, `child_male`, `child_female` |
| How is `LIST VOICES` asked? | Twice per `spd-say -L`, at runtime, by the client |
| Does the server filter the list by language? | No — all rows come back, and the module resolves |
| Are `:` and `+` safe in a voice name? | Yes, both survive a round trip. S3 uses neither |
| How long is a normal voice list? | The distro's `espeak-ng` publishes **14,805** rows |

**The `language=` row is the one that changed the code.** Resolution originally
treated a language as a request and fell back to "the first voice for it", which
would have had every client that never picked a voice silently replace the user's
own configured default. A language now *narrows* a request; it never is one.

## Two traps in the harness itself

- **The run directory must be short.** `AF_UNIX` truncates around 108 bytes, and
  a socket under a long scratch path makes speech-dispatcher die with
  `Fatal error [speechd.c:1001]:Can't bind local socket` — which reads like a
  permissions problem and is not. Both scripts use `/tmp/vst-s3-*`.
- **`PULSE_SERVER` is pinned by hand**, because PulseAudio finds its socket
  through `XDG_RUNTIME_DIR` too and moving that would take the audio with it,
  leaving a test that hangs for want of a sound card.

## sabotage.py

The house rule is that **a check that has never been observed failing is not
evidence**, so every rule S3 added was broken on the day it was written. This is
that pass, as a script: it applies one mutation, runs `SpeechD.Tests`, restores,
and reports anything that stayed green.

It caught 21 of 22 on the first run. The miss is worth knowing about: *"the voice
list is re-read per request rather than cached"* was tested with two **separate
module instances**, so a cache held in a field would have been re-read anyway and
every mutation passed. The test now installs a voice while **one** module is
running. That is the same defect S2 found four of.

**Its mutation list is S3-specific and will rot** as the source moves. It is a
worked example of the rule, not a maintained tool — copy it and rewrite the
`MUTATIONS` table for the next phase.

# The B gate: does 0.11.1 accept `705 AUDIO`?

Run: `docker run --rm -v "$PWD/spike/speechd-705-gate":/gate ubuntu:22.04 bash /gate/run.sh`

Answered 2026-08-27, on Ubuntu 22.04's **speech-dispatcher 0.11.1** in a bare
container and again on this machine's 0.12.1. **Outcome 1 of the three
[SPEECHD-PLAN](../../docs/SPEECHD-PLAN.md#b-gate) named: 0.11 has it.**

- The server sends `AUDIO`, then `audio_output_method=server`, and logs
  **`Initialized for server audio for probe`**. Route B's module opens no audio
  device on the oldest supported distro either.
- **The distro's own `sd_espeak-ng` takes the same path** on 0.11.1 — the
  strongest available evidence that this is the supported route, not a corner.

Three protocol facts the module must be built on, all measured here:

1. **A command is answered before its parameters arrive.** `AUDIO` → reply →
   *then* the server sends `audio_output_method=server` and a lone `.` → reply
   again. A module that reads the block first **deadlocks**, and one that answers
   out of order desynchronises: the first probe did, and the server then
   **refused to start at all** rather than running without that module.
2. `sd_espeak-ng` answers the command with `207 OK RECEIVING AUDIO SETTINGS` and
   the block with `203 OK AUDIO INITIALIZED`. `203` for both was also accepted.
3. `spd-say "hello"` reaches the module as **`<speak>hello</speak>`** — trap 11's
   SSML arrives whether or not anyone asked for it.

**Closed, 2026-08-28 — and it was not the terminator.** `spd-say -w` never
returning was blamed on the reply framing, and both `\n705 AUDIO\n` and
`705 AUDIO\n` were tried against it. Neither is the bug. Read out of
`module_tts_output_send_server` in speech-dispatcher's own
`src/modules/module_process.c` — **identical in 0.11.1 and 0.12.1**, so there is
no version skew here — the block is:

```
705-bits=16\n 705-num_channels=1\n 705-sample_rate=R\n
705-num_samples=N\n 705-big_endian=0\n
705-AUDIO<NUL>              <- a NUL byte, NOT a newline
<HDLC-escaped PCM>
\n705 AUDIO\n
```

Two things, and the second is the one that hung it:

1. **`705-AUDIO` is followed by a NUL**, which is what separates the header from
   the samples.
2. **The samples are HDLC-escaped.** The block ends at a newline, so a newline
   *inside* the audio would end it early — and `0x0A` turns up in virtually any
   real audio within milliseconds. Both `0x0A` and the escape byte `0x7D` are
   sent as `0x7D` followed by the original with bit 5 inverted. Our 300 ms sine
   needed 132 escapes in 13230 bytes.

**Verified with audio, because a container cannot answer this.** `run.sh` proves
the block is *accepted* on 22.04; it cannot prove the utterance *completes*,
since a bare container has no audio device and `spd-say -w` times out there for
the distro's own `sd_espeak-ng` too — which is why the first reading of this was
wrong. [`run-local.sh`](run-local.sh) runs against the machine's real
speech-dispatcher and real audio, isolated from the user's own configuration, and
always measures the system module beside ours:

| `spd-say -w` on 0.12.1 | |
| --- | --- |
| `-o espeak-ng` (the distro's) | returned in 1413 ms |
| `-o probe` (ours) | **returned in 415 ms** |

**And one more thing S2 must copy.** `module_tts_output_server` chunks at
`MAX_CHUNK` = 10000 bytes and calls `module_process(STDIN_FILENO, 0)` **between
chunks** — that is how `STOP` interrupts an utterance already in flight. A module
that writes one big block cannot be stopped until it finishes. The distro's
espeak-ng module is visible doing exactly this in the log above: `num_samples=5000`,
which is 10000 bytes of 16-bit mono.

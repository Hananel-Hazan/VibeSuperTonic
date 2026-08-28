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

**Open, and it is S2's first bug to fix:** with the block returned, `spd-say -w`
never returns — the end-of-utterance handshake after the PCM is not right yet.
Both `\n705 AUDIO\n` and `705 AUDIO\n` as terminator were tried. The server
*plays* (0.12.1 logs `Using pulse audio output method` then `speak_queue
Playback`), so this is the reply framing, not the audio.

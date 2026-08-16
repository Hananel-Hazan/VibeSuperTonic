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

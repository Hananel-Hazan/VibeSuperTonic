# AppImage stress harness

One script, no dependencies beyond python3. It exists because Phase 9 added an
entire layer that no unit test can reach: a squashfs mount that appears and
disappears around every process, a re-exec, a copy of `vst-ctl` living outside
the image in `~/.local/bin`, and a sidecar file that has to survive upgrades.

```bash
python3 spike/appimage-stress/appimage-stress.py dist/VibeSuperTonic-0.2.9-x86_64.AppImage
```

It copies the image into a temporary directory and runs with a temporary `HOME`
and `XDG_RUNTIME_DIR`, so the daily install is never touched. Two things it does
deliberately, both learned by getting them wrong first:

- **It passes `PULSE_SERVER` through.** Overriding `XDG_RUNTIME_DIR` also hides
  the PulseAudio socket, and a daemon that cannot open audio takes different
  paths through the speak code entirely. The first run of this file passed every
  audio scenario against a daemon answering *"no audio device available"*.
- **It waits for the runtime's teardown before counting mounts.** The image is
  unmounted after its child exits, and `shutdown` returns as soon as the socket
  stops answering — so counting immediately sees one mount on its way out and
  calls it a leak. It reported exactly that defect against itself.

It also needs a model set to render, which the archive deliberately does not
ship; it looks in `~/Apps/VibeSuperTonic/models` and honours `VST_MODELS`. The
audio scenarios skip, loudly, when there is none.

## What it covers

| Scenario | The failure it is looking for |
| --- | --- |
| 10 start/shutdown cycles | A leaked mount or a stale socket per login — the cost of a login is paid every day |
| 8 concurrent client installs | Two things that wake the product racing to repair `~/.local/bin/vst-ctl`, and leaving it torn. A torn client is a hotkey that does nothing |
| Client repaired: deleted, truncated, non-executable, sidecar pointing elsewhere | Every way an upgrade or a half-finished copy can leave it, since the daemon and the window both re-check it |
| A press with no daemon running | [R-5](../../docs/LINUX-PORT-ARCHIVE.md#r-5) through the whole chain — copy, sidecar, image, daemon — with no `vibesupertonicd` anywhere near the client |
| A second daemon while one is running | Two daemons must never both hold the socket; one owns the speakers. It must refuse, say why, and leave the first answering |
| 8 interrupted utterances, then one more | The shape of the stale-flush defect: the press after an interruption has to be heard |

## What it found, 2026-08-24

Nothing in the product on its first clean run — but the two defects above, in
itself, and both are the same mistake: **a test that agrees with the thing it is
testing.** A suite that passes against a daemon with no audio, and a leak check
that fires on its own impatience, are the vacuous-guard failure this repository
has now recorded three times in three different places.

The bind-order defect it prompted is real and was found by reading the log this
harness printed: a second daemon registered a tray icon before discovering the
socket was taken, so for a moment the user had two. Binding moved ahead of both
the tray and `--preload` — which matters more, because a preloading daemon spent
seconds loading models before it listened while `vst-ctl` waits five seconds for
an autostarted daemon to answer.

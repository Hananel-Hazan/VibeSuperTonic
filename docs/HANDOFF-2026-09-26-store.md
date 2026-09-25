# Handoff — snap and Flatpak (0.2.17), for 2026-09-26

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`. No PR. Read `CLAUDE.md`
("The store packages") and `docs/STORE-SUBMISSION.md` first; the previous
handoff, [HANDOFF-2026-09-25-store.md](HANDOFF-2026-09-25-store.md), is the
history of how each fault below was found.

## Where it stands

**The snap works on the user's Kubuntu.** Revision 5 is on the Snap Store's
`edge` channel and installed on their machine (KDE Plasma 6, Wayland). CI run 59
(`dc5e14b`) is green on every job, Windows included.

| On revision 5, on the user's machine | |
| --- | --- |
| Daemon starts under confinement | works |
| Hotkey reads the selection aloud | works (after a logout/login, see below) |
| Tray icon | works |
| Tray click opens the window | works |
| Tray menu "Read selected text" speaks | works |
| Speech Dispatcher (`spd-say -o vibesupertonic`) | works |
| Tray click after a logout/login | **fixed in revision 5, not yet confirmed by the user** |
| Orca end to end | not tried |

**CI publishes to `edge` itself**: the snap job runs `snapcraft upload
--release=edge` on every push to `main`, and on a manual run with `publish_edge`
ticked, after `--test-install` passes and after the `linux` and `speechd` jobs
pass. The secret `SNAPCRAFT_STORE_CREDENTIALS` is set, edge-only. Pushes to this
branch do not trigger `build.yml`; dispatch it (`workflow_dispatch`, branch
above). **Never have two publishing runs in flight**: the later-finishing one
wins `edge`, even if it is the older code.

## What was wrong, and what fixed it (2026-09-25)

Every one of these passed every packer check and failed only under snapd, which
is why `pack-snap.sh --test-install` now checks each of them under real
confinement in CI.

| Symptom | Cause | Fix | Commit |
| --- | --- | --- | --- |
| Daemon aborts 2 s after every hotkey press | seccomp refuses `listen()` without the `network-bind` plug (not AppArmor) | `network-bind` on every app that can start the daemon | `a929ba5` |
| No tray icon: "ConnectException: Permission denied" | no plug for the session bus | `unity7` | `f665f4a` |
| Tray click aborts the window: `libfontconfig.so.1` missing | daemon ran the bare window binary, without the GNOME extension's chain, in ctl's confinement | `SnapWindow` reads the window app's chain from `meta/snap.yaml`; the extension's plugs join the shared list | `57e72a6`, `05e450a`, `b4d07c7` |
| Hotkey silent: "could not open a PulseAudio playback stream: Connection refused" | in a snap `XDG_RUNTIME_DIR` is private; only the window's `desktop-launch` sets `PULSE_SERVER` | top-level `PULSE_SERVER=unix:/run/user/$SNAP_UID/pulse/native` | `b2c8ac7` |
| Tray click after re-login: `XOpenDisplay failed` | daemon started by speech-dispatcher outlived the logout with the old `XAUTHORITY` | `Request.XAuthority` beside `Request.Display`; `ClientDisplay` (Core) applies the latest client report to the window | `dc5e14b` |
| `speechd-install` left speech-dispatcher offering nothing | unproven; suspected: the installer waited on the wrong pid while stopping a stale speech-dispatcher | installer waits on every pid it signalled, SIGKILLs after 5 s, and **rolls itself back** if any module offered before is missing | `19b87d5` |

Also: `pack-snap.sh` asserts the plugs, `PULSE_SERVER`, and that every plug of
the window app is on `daemon`, `ctl` and `speechd`; `build/check-snap-speechd.sh`
probes a private speech-dispatcher both terminal-started and socket-activated
through systemd (a user can run it; it touches nothing of theirs).

## Open, in rough order

1. **Ask the user** whether the tray window opens after their next logout/login
   (press the hotkey once first). That is the one fix in revision 5 not yet seen
   working on the desktop.
2. **Release notes for 0.2.17** say nothing about any of the above. At least two
   items reach every Linux user, not only snap users: `speechd-install.sh` now
   undoes itself instead of warning, and it force-stops a stuck
   speech-dispatcher. The snap-only fixes can be one line ("the first store
   builds on edge could not ..."), since nothing was published to stable.
3. **Manual `snap refresh` is refused while anything of ours runs** ("has running
   apps"): the Speech Dispatcher module (speech-dispatcher keeps it alive), the
   daemon, or a window the tray opened (counted as `ctl`). Workaround:
   `vibesupertonic.ctl shutdown; systemctl --user stop speech-dispatcher.service`,
   close the window. Decide: document it in the listing and INSTALL notes, or
   make the module and daemon step aside (e.g. the module exiting when idle, if
   speech-dispatcher restarts modules on demand; verify first). In
   STORE-SUBMISSION's checklist.
4. **X11 selection capture by a stale daemon**: the `ClientDisplay` fix covers
   only the window. On an X11 session a daemon that outlived a logout would read
   the selection with a dead `XAUTHORITY` too. Wayland capture was unaffected on
   the user's machine. .NET cannot change the native environment, so a fix means
   passing the reported file to libX11 some other way (P/Invoke `setenv` before
   `XOpenDisplay`, or `XauFileName`-level handling). Not started.
5. **Two VibeSuperTonic entries in KDE's menu** (the snap's own launcher and the
   one `keybindings.sh` writes to carry the shortcuts). Confirmed. The
   STORE-SUBMISSION item says what to decide.
6. **`StartupNotify=false`**: the user added it by hand beside
   `X-KDE-StartupNotify=false` while chasing a "jumping icon" that turned out to
   be the old AppImage's window. Whether Plasma needs the standard key is
   unverified; do not change `keybindings.sh` for it without a real observation.
7. **Desktop checklist** in STORE-SUBMISSION: hotkey latency through
   `/snap/bin`, Orca, GNOME/Wayland, the Flatpak's tray and selection capture.
8. **Store listing** on snapcraft.io (Utilities, Productivity, screenshots, the
   OpenRAIL-M note), then a `--grade stable` build before promoting to stable.
   Flathub needs screenshots in the metainfo and the 0.2.17 tarball as a GitHub
   release first.
9. **Windows CI**: two timing tests fail intermittently on the Windows runner,
   `PipelineLatencyTests.An_utterance_does_not_cost_anything_to_start` and
   `Time_to_the_first_sample_stays_flat_as_the_document_grows`. The first's
   message now splits the time: two of twenty utterances at 310 and 132 ms with
   the rest under 4 ms and a bare pool round trip at 0.3 ms, so it is the
   runner's scheduling. Do not loosen them. They do not block the snap job.
10. **The speechd install hang's cause is unproven.** The rollback makes it safe
    whatever the cause.

## The user's machine

- Snap revision 5 from `edge`. Hotkeys bound through `sandbox-setup.sh bind`
  (`Ctrl+`` and `Ctrl+;` read, `Ctrl+~` stops) and working after a re-login.
  Speech Dispatcher module registered and speaking; the config backup it took is
  `~/.config/speech-dispatcher/speechd.conf.vst-backup.20260925-164429`.
- 10 voices in `~/snap/vibesupertonic/common/models`.
- The old AppImage is still in `~/Apps/VibeSuperTonic/`, unhooked, nothing of it
  running.
- To refresh: `vibesupertonic.ctl shutdown`, `systemctl --user stop
  speech-dispatcher.service`, close any VibeSuperTonic window, then `sudo snap
  refresh vibesupertonic --edge`.

## Tooling notes for a cloud session

- `apt-get update` first, then `apt-get install dotnet-sdk-10.0` (without the
  update, a stale package index 404s on `dotnet-sdk-aot`). The Linux solution
  builds and the Core (1342) and Daemon (135) suites run in seconds.
- `apt-get install squashfs-tools` makes `pack-snap.sh --check` usable on a
  synthetic snap (a `meta/snap.yaml` in a `mksquashfs` image) up to its library
  assertion. That is how every static assertion added today was seen failing.
- `apt-get install speech-dispatcher speech-dispatcher-espeak-ng` gives a real
  0.12 to drive `check-snap-speechd.sh` and `speechd-install.sh` against, with a
  fake module (`sd_espeak-ng` under our name works; a script that answers INIT on
  a pipe but hangs under speechd is the negative control). Use a **short**
  `/tmp` path for any socket: the scratchpad path is past the ~107-byte limit.
  There is no systemd here, so the socket-activated half runs only in CI and on
  the desktop.
- CI artifacts cannot be downloaded from the container (their storage host is
  refused), and raw.githubusercontent.com caches a branch URL for minutes: give
  the user **commit** URLs for scripts.
- snapcraft.io, the Snap Store and Flathub are unreachable from here; the snap is
  built, tested and published only by CI.

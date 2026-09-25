# Handoff — snap and Flatpak (0.2.17), 2026-09-25

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`, code at `a929ba5`. No PR.
Read `CLAUDE.md` (the "store packages" section) and `docs/STORE-SUBMISSION.md`
first; this file is the state of play, not the design.

## The blocker, now fixed

**The snap's daemon could not start under confinement.** It aborted (exit 134)
about 2 s after launch, so the window said "not connected" and hotkeys did
nothing. Revision 1 on the Snap Store's `edge` channel has this bug.

- **Cause, named by CI run 49** (`e7eefb4`, which prints the sandbox's denials):
  the kernel logged `type=1326 ... syscall=50`, a **seccomp** denial of
  `listen`, and no AppArmor line for the socket. snapd's default seccomp filter
  has `bind()` but not `listen()`/`accept()`; only the `network-bind` plug grants
  them. That is why the file socket (revision 1) and the abstract socket
  (`da89bc9`) failed identically: both were aimed at AppArmor.
- **Fix, commit `a929ba5`:** `network-bind` joins the shared plug list in
  `snapcraft.yaml.in` (window, daemon, ctl, speechd). It auto-connects and needs
  no manual review. `pack-snap.sh` now asserts all four apps plug it (checked
  against a synthetic snap in both directions). The abstract socket and its
  `SO_PEERCRED` check stay.
- **CI run 50** (id 36145223156) on `a929ba5`: **all green, snap job included.**
  Under snapd the hotkey client started the daemon and it answered 0.2.17, the
  store is `~/snap/vibesupertonic/common`, the module answers INIT, and the setup
  app refuses inside the sandbox. That run's `linux-snap` artifact is the one to
  upload as revision 2.
- The other two denials in run 49 are harmless: `file_lock` on
  `/etc/machine-id` and `/proc/cpuinfo`.
- When it passes: the user downloads `linux-snap` from that run's page,
  `snapcraft upload --release=edge VibeSuperTonic-0.2.17-amd64.snap` from
  `~/Downloads` (the file must be in the current directory), then
  `sudo snap refresh vibesupertonic --edge`. Hotkeys point at `/snap/bin/`, so no
  re-bind is needed.

## After revision 2 reached the user's machine

- **The daemon works under confinement** (`vibesupertonic.ctl status` answers
  0.2.17). Two things did not:
- **Tray**: `no tray icon: ConnectException: Permission denied`. The daemon had
  no plug for the session bus. Fixed in `f665f4a` with `unity7`; `--test-install`
  now checks the daemon reaches a session bus (run 53: "nothing on the session bus
  implements org.kde.StatusNotifierWatcher", the right answer with no tray host).
  **Revision 3** (run 55, `1169235`) carries it and the installer below, and is
  on `edge`.
- **Speech Dispatcher**: `sandbox-setup.sh speechd-install` left speech-dispatcher
  offering nothing and hung (`spd-say -O` never returned). The user ran `--remove`
  and espeak-ng came back. Investigated with `build/check-snap-speechd.sh`, a
  private speech-dispatcher started from a shell and socket-activated through
  systemd: **the snap's module loads in both, on the user's machine and in CI**
  (runs 52 to 54). So it is not the module, confinement, the user's voices or
  systemd activation. What differed: the speech-dispatcher the installer stopped
  had been running since before the AppImage module was removed and still
  offered `vibesupertonic` from it, and the installer waited 5 s on the wrong pid
  (the last one its loop saw) and never escalated. The suspected hang is systemd
  starting no new speech-dispatcher while the old one was still dying.
  **Unproven.** The installer now SIGKILLs a speech-dispatcher still alive 5 s
  after SIGTERM, waits on every pid it signalled, and **rolls back** (restores
  the config, restarts, verifies, exits 1) if any previously offered module is
  missing. Checked locally against speech-dispatcher 0.12: a module that hangs
  speechd is rolled back with espeak-ng restored; a working one installs; a
  speech-dispatcher that ignores SIGTERM is killed. Also fixed: `--remove` on a
  config holding only our block died silently (`grep -v` exit 1 under `set -e`).
- **The user then re-ran `speechd-install` on revision 2 and it worked**
  (`espeak-ng vibesupertonic`), with the stale speech-dispatcher gone. That fits
  the suspected cause; it does not prove it.
- **Open**: a hotkey press brought back the **old AppImage window** ("connected —
  daemon 0.2.16"), and the selection was read. So something besides the snap's
  `vibesupertonic.desktop` actions (which are bound correctly in
  kglobalshortcutsrc) still reaches the AppImage: an autostart entry, another
  shortcut, or a running AppImage process. Asked the user for `ps`,
  kglobalshortcutsrc/khotkeysrc, `~/.local/share/applications`,
  `~/.config/autostart` and `~/.local/bin`. The user also added
  `StartupNotify=false` beside `X-KDE-StartupNotify=false` by hand; if that
  stops KDE's bouncing launch icon, keybindings.sh should write both.
- **KDE lists two VibeSuperTonic entries**: the snap's own
  `vibesupertonic_vibesupertonic.desktop` and ours. Confirmed on the machine.
- **Windows**: a second timing test,
  `PipelineLatencyTests.Time_to_the_first_sample_stays_flat_as_the_document_grows`,
  failed once on the Windows runner (run 54: 62 ms against 25 ms) with no C#
  changed since it last passed. Same family as the intermittent one below. Not
  loosened.

## State of everything else

**CI** (all jobs run with `workflow_dispatch` on this branch; pushes to it do not
trigger `build.yml`, which runs on `main` and `Dev` only):
- Green: linux, pack, smoke, parity, speechd, flatpak (builds, installs, checks a
  real deployment), Windows build.
- Snap job: green from run 50, `--test-install` included. It failed through run
  49 (above), so the check has been seen both ways.
- Windows `PipelineLatencyTests.An_utterance_does_not_cost_anything_to_start`:
  intermittent (5 passes, 2 failures at 313 and 448 ms against 250). Linux
  measures 1-2 ms. Commit `59c4037` makes its failure message split the time and
  time a bare thread-pool baseline. Not caught failing since. Do not loosen the
  ceiling.

**Store accounts** (the user did these):
- Ubuntu One / snapcraft login: `hananel@hazan.org.il`. The snap name
  `vibesupertonic` is registered to it.
- Flathub: nothing yet. Needs screenshots in the metainfo, and the 0.2.17 tarball
  published as a GitHub release, then
  `bash build/pack-flatpak.sh -v 0.2.17 --manifest-only <asset URL>`.

**The user's machine** (Kubuntu, KDE Plasma, Wayland, 20 cores):
- Snap revision 1 installed from `edge`. `sandbox-setup.sh bind` done:
  `~/.local/share/applications/vibesupertonic.desktop` Exec lines are
  `/snap/bin/vibesupertonic`, `/snap/bin/vibesupertonic.ctl read|stop`. KDE picks
  them up at next login.
- The old AppImage (`~/Apps/VibeSuperTonic/VibeSuperTonic.AppImage`, portable
  home `.home` beside it) is unhooked: daemon stopped, Speech Dispatcher module
  removed with its own `speechd-install --remove` (config restored from its
  backup), `~/.local/bin/vst-ctl` and `vst-ctl.appimage` deleted. Running the
  AppImage again re-creates the `vst-ctl` copy. Its models are still beside it
  and could be copied to `~/snap/vibesupertonic/common/models`.
- Speech Dispatcher for the snap is **not** registered yet
  (`sandbox-setup.sh speechd-install`, after models are downloaded).

## Open requests from the user

1. Get the snap working. **Fixed in `a929ba5`, proven by run 50, revision 2 on
   `edge`; waiting on the user to `snap refresh` and try it on their machine.**
2. ~~CI uploads to `edge`.~~ **Done.** The secret `SNAPCRAFT_STORE_CREDENTIALS`
   (edge-only) is set, and run 51 (id 36162925538, `ec00d3d`, `publish_edge`
   ticked) published **revision 2 to `edge`**. From now on every push to `main`
   publishes too. The user's own upload attempt had failed because the file in
   `~/Downloads` was revision 1's snap (`binary_sha3_384: Error checking upload
   uniqueness` is the store refusing a duplicate).
3. Desktop checklist in `docs/STORE-SUBMISSION.md` (hotkey latency through
   `/snap/bin`, tray icon, Orca, duplicate menu entry on KDE, Status tab note).
4. Store listing on snapcraft.io: Utilities + Productivity, screenshots, licence
   note that models download separately under OpenRAIL-M.
5. Before `stable`: pack with `--grade stable` (CI builds are `devel`).

## Commits on this branch, oldest first

| Commit | What |
| --- | --- |
| `2c03962` | Sandbox-aware store, install check and Flatpak socket/launch (Core, Daemon, Ctl, Ui) |
| `748b00a` | pack-snap.sh, pack-flatpak.sh, sandbox-setup.sh, metainfo, CI jobs, docs |
| `482138a` | snapcraft output path; Windows path separators in tests |
| `d4be816` | Snap native libs in `lib/native` (GNOME gpu cleanup deleted libX11) |
| `a50a9c7` | Environment-changing Daemon tests run alone (TMPDIR race) |
| `59c4037` | Latency test says where the time went |
| `6fc3b63` | Snap name registered (docs) |
| `40d2203` | `pack-snap.sh --test-install` and CI step (seen failing on unfixed code) |
| `da89bc9` | Abstract socket + SO_PEERCRED for snaps (**did not fix it**) |
| `e7eefb4` | `--test-install` prints daemon output and sandbox denials on failure |
| `a929ba5` | `network-bind` plug (the real fix: seccomp, not AppArmor); packer asserts it |

## Tooling notes for a cloud session

- `dotnet-sdk-10.0` installs from Ubuntu's apt; `build-espeak.sh` works (git to
  GitHub is allowed). `pack-tar.sh` runs locally in about 4 minutes.
- snapcraft.io, the Snap Store and Flathub are blocked from the container, so
  snap and Flatpak builds happen only in CI. Docker runs but cannot pull from
  ghcr.io.
- `build/check-sandbox-setup.sh --negative-control` and
  `pack-snap.sh --check <file.snap>` are the fast local checks.
- Creating or removing files at the filesystem root (`/app`, `/.flatpak-info`)
  is blocked by the session's safety checks; `/snap/vibesupertonic/x7` and `x8`
  exist in the old container from a manual layout test.
- CI artifacts cannot be downloaded from the container (their storage host is
  refused by the proxy), so a built snap cannot be inspected here. `apt-get
  install squashfs-tools` works, and a synthetic snap (a `meta/snap.yaml` in a
  `mksquashfs` image) is enough to exercise `pack-snap.sh --check` up to its
  library assertion.

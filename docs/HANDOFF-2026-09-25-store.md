# Handoff — snap and Flatpak (0.2.17), 2026-09-25

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`, head `e7eefb4`. No PR.
Read `CLAUDE.md` (the "store packages" section) and `docs/STORE-SUBMISSION.md`
first; this file is the state of play, not the design.

## The one blocker

**The snap's daemon cannot start under confinement.** It aborts (exit 134) about
2 s after launch, so the window says "not connected" and hotkeys do nothing.

- Revision 1 is on the Snap Store's `edge` channel and has this bug. Nothing
  newer has been uploaded. **Do not tell the user to upload until CI's snap job
  passes `--test-install`.**
- First cause, from the user's machine (`snap run vibesupertonic.daemon`):
  `SocketException (13): Permission denied at Socket.Listen` in
  `DaemonServer.Bind`. AppArmor let it create the socket file in
  `$XDG_RUNTIME_DIR` and refused `listen()`. No `apparmor="DENIED"` line for it
  appeared in the user's journal, only unrelated file denials (machine-id lock,
  power_supply reads; both harmless).
- Fix attempt 1, commit `da89bc9`: in a snap, use the abstract socket
  `@snap.<instance>.ctl-<uid>` (`SnapPeer` in Core; `Protocol.IsAbstract` and
  `Protocol.EndPoint`), with an `SO_PEERCRED` same-user check (`PeerCredentials`
  in the daemon). **It did not work**: CI run 48 failed identically, exit 134
  after 2.1 s, "without listening on @snap.vibesupertonic.ctl-1001". The
  assumption that snapd's template allows listen on `@snap.<name>.**` was wrong,
  or something else fails. Unverified either way.
- Commit `e7eefb4` makes `pack-snap.sh --test-install` print, on failure, the
  daemon's foreground output and the kernel's AppArmor and seccomp denials.
  **CI run 49 (id 36144434415) is running it.** Read its `snap` job log first:
  it should name exactly what confinement refuses.
- Candidates, not yet tested: the `network-bind` plug (may grant unix
  bind/listen); a socket file under `$SNAP_USER_DATA` or `$SNAP_USER_COMMON`;
  checking whether the failure is really `listen` and not something earlier.
- The check is proven: run 47 (`40d2203`, test without fix) failed exactly like
  the store build. Keep `--test-install` in CI's snap job; it is the only check
  that runs the snap under snapd.
- When it passes: the user downloads `linux-snap` from that run's page,
  `snapcraft upload --release=edge VibeSuperTonic-0.2.17-amd64.snap` from
  `~/Downloads` (the file must be in the current directory), then
  `sudo snap refresh vibesupertonic --edge`. Hotkeys point at `/snap/bin/`, so no
  re-bind is needed.

## State of everything else

**CI** (all jobs run with `workflow_dispatch` on this branch; pushes to it do not
trigger `build.yml`, which runs on `main` and `Dev` only):
- Green: linux, pack, smoke, parity, speechd, flatpak (builds, installs, checks a
  real deployment), Windows build.
- Snap job: builds and passes its static checks; fails `--test-install` (above).
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

1. Get the snap working (the blocker).
2. Set up CI to upload to `edge` automatically. Needs a credential the user
   creates: `snapcraft export-login --snaps=vibesupertonic --channels=edge
   --acls=package_access,package_push,package_update,package_release <file>`,
   stored as a GitHub secret. Do this after revision 2 works.
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

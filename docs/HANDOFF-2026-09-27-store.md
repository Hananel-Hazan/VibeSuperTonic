# Handoff — snap and Flatpak (0.2.17), for 2026-09-27

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`, **identical to `Dev`**
(`2121889`). No PR. Read `CLAUDE.md` ("The store packages"),
[STORE-SUBMISSION.md](STORE-SUBMISSION.md) (its checklist is the live state) and
[RELEASE-0.2.17.md](RELEASE-0.2.17.md) (the release runbook) first. The two
earlier handoffs, [09-25](HANDOFF-2026-09-25-store.md) and
[09-26](HANDOFF-2026-09-26-store.md), are the history.

## Where it stands

**CI run 83 on `Dev` (`2121889`) is green.** Runs 79–82 were red on purpose: they
were the reverted `d5215c9` experiment, refused by the new `--test-install`
check (see below). The user's Kubuntu runs **snap revision 10** from `edge`.

Pushes to `Dev` and to this branch run CI but publish nothing. Publishing to
`edge` happens on a push to `main`, or a manual run with `publish_edge` ticked.
**Never have two publishing runs in flight**: whichever finishes last wins `edge`.

`origin/main` (`acf68c1`) is an ancestor of this branch, 89 commits behind, so
`Dev → main` fast-forwards. **Merging to main publishes to edge.**

| Confirmed on the user's Kubuntu (Plasma 6, Wayland) | Revision |
| --- | --- |
| Hotkey reads the selection, including after a reboot before any window opened (`SnapWaylandLink`) | 7 |
| Tray icon, tray click opens the window, also after logout/login | 5 |
| Orca reading through the VibeSuperTonic voice | 5 |
| The hotkeys' KDE entry hidden (`NoDisplay=true`), hotkeys survive a reboot | 7 |
| A held update: notification + tray row "Update now", snapd applies it ~20 s later | 8 → 9 |
| Same with the Speech Dispatcher module running: it steps aside | 9 → 10 |

## What happened on 2026-09-27

| Commit | What |
| --- | --- |
| `ef35877` | 0.2.17 prepared for the stores: README, release notes, metainfo, screenshots, voice samples, `snap_grade` choice on manual CI runs |
| `36e8345` | Joined `main`'s history; declined the Copilot split-ZIP PR (Hananel-Hazan/VibeSuperTonic#1) |
| `302f85c` | `refresh-mode` dropped: the Store refuses it on non-service apps. `pack-snap.sh` now refuses it too |
| `16d4204` | `SnapWaylandLink`: the daemon links `wayland-0` into the snap's runtime dir before choosing its selection source |
| `88926c1`, `36fcb25` | Held snap update: the daemon notices snapd's pre-downloaded `.snap`, asks with Update now / Later, and closes the window, module and daemon when told to. The notification now closes on either answer |
| `62a7bc0` | After Update now, speechd falls back to espeak until restarted: the notification and a window bar (`ScreenReaderBar`) name the restart command |
| `b5b3be6` | X11 selection capture uses the latest session's `XAUTHORITY` (`NativeXAuthority`, libc `setenv`) |
| `f059e0a` | `EngineOfflineBar`: every tab says when the engine is not running, with "Start it now" |
| `4ab9769`, `373ddc3` | Voices tab: Download buttons aligned; store screenshot retaken on revision 10 |
| `d5215c9` → reverted, `2121889` | Tried binding the hotkey to the mount's `vst-ctl` to skip `snap run`'s 108 ms. The confined daemon's AppArmor refuses unconfined peers, so it does not work. Kept on `/snap/bin`; written down in STORE-SUBMISSION |

## Open, in rough order

1. **The release itself.** Follow [RELEASE-0.2.17.md](RELEASE-0.2.17.md) from
   step 2's "main" check onward. Steps 1 and 2's commit are done, and 2b is
   withdrawn. Mind the Shabbat window in its header. Still in the file:
   `RELEASE_NOTES.md` line 5, "Ready to pack; not packed yet", is deleted in the
   release commit. Before packing, check that the notes cover today's
   commits (update prompt, `ScreenReaderBar`, `EngineOfflineBar`, X11
   `XAUTHORITY`, Voices tab layout, the 108 ms snap hotkey cost).
   Pack, verify and tag in one sitting. **Pushing the tag and publishing are the
   user's.**
2. **Snap listing on snapcraft.io**, then a `--grade stable` CI build, candidate,
   stable (runbook step 5). Promotion is a manual `snapcraft release`: the CI
   credential reaches only `edge`.
3. **Flathub cannot take the current manifest**: it must build from source
   (dotnet10 SDK extension, pinned NuGet sources, espeak-ng as a module). Not
   started. Runbook step 6.
4. **Checklist items still open** in STORE-SUBMISSION: GNOME on Wayland (no
   `ext-data-control`, so no hotkey reading there; the listing says so), and
   everything in the **Flatpak**: tray, Orca, selection capture on
   Plasma/Wayland.
5. **Hotkey latency in the snap is 108 ms per press**, all `snap run`. Accepted.
   The only route left is a D-Bus call to the daemon. That needs its own design.
6. **Windows CI timing tests** flake on the runner
   (`PipelineLatencyTests`). Do not loosen them.
7. **The speechd install hang's cause is unproven.** The installer's rollback
   makes it safe anyway.

## The user's machine

- Snap revision 10 from `edge`. Hotkeys bound through `sandbox-setup.sh bind`.
  The Speech Dispatcher module is registered. Its config backup is
  `~/.config/speech-dispatcher/speechd.conf.vst-backup.20260925-164429`.
- To get a new revision: wait for the daemon's "Update now" prompt, or do it by
  hand: `vibesupertonic.ctl shutdown`,
  `systemctl --user stop speech-dispatcher.service`, close any window, then
  `sudo snap refresh vibesupertonic --edge`. After Update now, restart
  speech-dispatcher so Orca uses our voice again.
- The old AppImage is still in `~/Apps/VibeSuperTonic/`, unhooked.
- The repository is at `/home/hananel/#GitRepos/VibeSuperTonic`. The runbook says
  to delete the root `screenshots/` folder there rather than commit it.

## Tooling notes for a cloud session

Unchanged from [the 09-26 handoff](HANDOFF-2026-09-26-store.md#tooling-notes-for-a-cloud-session):
run `apt-get update` before `dotnet-sdk-10.0`. `squashfs-tools` covers
`pack-snap.sh --check` on a synthetic snap. `speech-dispatcher` covers the speechd
harnesses. Use short `/tmp` socket paths. CI artifacts cannot be downloaded from
here. Give commit URLs, not branch URLs. The stores are unreachable, so the snap
is built, tested and published only by CI.

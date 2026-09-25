# Store submission: Snap Store and Flathub

Started 2026-09-25, for 0.2.17. The goal is to put VibeSuperTonic in front of
Kubuntu and Ubuntu users where they look for software, **under Accessibility**.

## Where the packages show up

| Package | Ubuntu App Center | Kubuntu Discover | Built by |
| --- | --- | --- | --- |
| Snap | yes: the App Center *is* the Snap Store | yes | [build/pack-snap.sh](../build/pack-snap.sh) |
| Flatpak | no | yes, once Flatpak support is added (`plasma-discover-backend-flatpak`) | [build/pack-flatpak.sh](../build/pack-flatpak.sh) |
| AppImage | no | no | [build/pack-appimage.sh](../build/pack-appimage.sh) |
| Tarball | no | no | [build/pack-tar.sh](../build/pack-tar.sh), the canonical artifact |

That is also the order the README offers them in. The AppImage is the third
choice and the tarball the last one, but the tarball is still what every other
package is built from.

**Neither store has an accessibility submission programme.** "Accessibility" is
a category, set by the listing:

- **Discover and Flathub** read `<categories>` from the AppStream metainfo, and
  `Categories=` from the desktop entry. Both say `Accessibility;Utility`.
- **The Snap Store** has a fixed category list that the publisher picks from in
  the dashboard. At the time of writing it had no Accessibility category; pick
  *Utilities* (primary) and *Productivity*. Search is what finds it there, so the
  summary and keywords say "screen reader" and "text to speech".

## How the packages are built

Both are **repackagings of the tarball**, the same design as the AppImage. Neither
compiles anything, so the tarball's eleven assertions apply to both, and each
re-asks the cheap ones against the tree it actually packages
([check-composed-tree.sh](../build/check-composed-tree.sh)). One listing,
[build/desktop/](../build/desktop/), serves both stores and travels inside the
tarball.

```bash
bash build/build-espeak.sh                    # only when the payload is stale
bash build/pack-tar.sh -v 0.2.17
bash build/pack-snap.sh -v 0.2.17 --grade stable
bash build/pack-flatpak.sh -v 0.2.17 --test-install
bash build/pack-appimage.sh -v 0.2.17
```

CI runs the snap and Flatpak packers on every push (`snap` and `flatpak` jobs in
[build.yml](../.github/workflows/build.yml)), from the tarball the `pack` job
built.

## What the sandbox changed in the code, and why

A snap and a Flatpak both make the program's own directory **read-only** and
give it a private view of the home directory. Three things broke, each silently:

1. **The store.** The portable rule puts `models/` and `data/` beside the
   executable. Inside a sandbox it goes to the one directory each sandbox keeps
   for the app across updates: `$SNAP_USER_COMMON` (`~/snap/vibesupertonic/common`)
   or the Flatpak's `$XDG_DATA_HOME` (`~/.var/app/<id>/data`).
   `LinuxDataPaths.DetectSandbox` decides this. It checks that the executable
   really is inside the sandbox before believing the environment variables.
2. **The install check.** snapd renames the program's directory *and* `$HOME` on
   every refresh, so the "program moved" check would have shown every snap user
   a false re-bind warning after every update, the same failure the AppImage's
   mount directory once caused. The check now records `/snap/<name>/current` and
   `$SNAP_REAL_HOME`. Verified by running revision x7 and then x8 against the
   same store: no change reported.
3. **The socket and the daemon's lifetime, Flatpak only.** Each `flatpak run` gets
   a private `/run/user/<uid>` and its own PID namespace. The socket therefore
   moves to `$XDG_RUNTIME_DIR/app/<id>/`, which every instance and the host
   share. A daemon started from inside the sandbox goes through `flatpak-spawn`
   so it survives its parent. See `FlatpakPeer` and `DaemonLaunch` in Core.

**The hotkeys and Speech Dispatcher are set up from the host**, by
[sandbox-setup.sh](../build/sandbox-setup.sh), which ships in both packages.
Both are the desktop's configuration (`kglobalshortcutsrc`, `gsettings`,
`~/.config/speech-dispatcher`), and a sandbox cannot write it. The stores *can*
grant that access (snap `personal-files`, Flatpak `--filesystem`), but only after
a manual review, and the grant would sit on the package forever to be used once.
With the script, the snap needs **no privileged interface and no classic
confinement**, and the Flatpak needs **no home or host filesystem access**.

The hotkey commands:

| | Hotkey runs | Why |
| --- | --- | --- |
| snap | `/snap/bin/vibesupertonic.ctl` | inside confinement, so it shares the daemon's runtime directory |
| Flatpak | `<deployment>/active/files/lib/vibesupertonic/vst-ctl`, directly on the host | NativeAOT, needs only libc; `flatpak run` would add a sandbox setup to every press |

## Verified, and not yet

Verified in this repository:

- Unit tests for the sandbox rules, the socket path and the launch plan, each
  sabotaged and seen failing.
- The real published binaries, laid out as a snap: the store lands in `common/`,
  `vst-ctl` auto-starts the daemon and talks to it, and a refresh from x7 to x8
  reports no move.
- The real host-side `vst-ctl` in a Flatpak deployment layout looks for the
  daemon in the shared runtime directory, and tries `flatpak run` to start it.
- `sandbox-setup.sh` against a fake snap mount and a fake Flatpak installation
  ([check-sandbox-setup.sh](../build/check-sandbox-setup.sh)), with three
  negative controls.
- The snap packer's post-build checks against a hand-built snap, and seven
  sabotages of it (wrong version, classic confinement, a command-chain on `ctl`,
  a missing app, a missing library, a model file, no espeak).
- Only in CI, because this container cannot reach the Snap Store or Flathub:
  the real `snapcraft` and `flatpak-builder` builds, and the installed-Flatpak
  checks. The Flatpak passed all of them on its first run. The snap's library
  check caught a real fault on its first run: the GNOME extension's GPU cleanup
  had deleted `libX11` from `usr/lib`, where the daemon (which has no extension)
  needed it. The daemon's libraries now live in `lib/native`.

**What the first real install found.** Revision 1, on `edge`, could not start its
daemon: seccomp refused `listen()` (the kernel logged `type=1326 ... syscall=50`),
so the window said "not connected" and every hotkey press aborted a daemon.
snapd's default filter has no `listen()`; the `network-bind` plug grants it, and
now every app that can start the daemon carries it, which `pack-snap.sh` asserts.
Nothing above had run the snap under snapd, so `pack-snap.sh --test-install`,
which CI now runs on a runner with snapd, installs the snap and makes its daemon
answer. (The socket is also abstract, `@snap.vibesupertonic.ctl-<uid>`, with a
same-user check in place of the file mode: a first fix, aimed at AppArmor, that
did not help on its own and was kept.)

**Needs a real Kubuntu and a real Ubuntu desktop before submitting**:

- [ ] **A manual `snap refresh` is refused while any VibeSuperTonic process runs**
      ("has running apps"): the Speech Dispatcher module (speech-dispatcher keeps
      it alive), the daemon, or a window the tray opened (which snapd counts as
      `ctl`). snapd's normal rule; automatic refreshes wait instead. Decide
      between telling users (listing, INSTALL notes) and making the module and
      daemon step aside. Today's workaround: `vibesupertonic.ctl shutdown`,
      `systemctl --user stop speech-dispatcher.service`, close the window.
- [ ] **After installing or re-binding, log out and back in** before the hotkey
      works on KDE: kglobalaccel reads shortcuts only at login (by design, see
      keybindings.sh). Seen on Kubuntu 2026-09-25. The listing should say so.

- [ ] **Hotkey latency through `/snap/bin/vibesupertonic.ctl`.** `snap run` adds
      its own startup; the AppImage's comparable cost was +14.7 ms. Measure it
      against R-3's 100 ms. If it is too slow, the fallback is the Flatpak's
      approach (run the file in the mount directly), which needs AppArmor to let
      an unconfined client connect to the confined daemon's socket.
- [ ] **Selection capture in the Flatpak on Plasma/Wayland.** KWin can withhold
      privileged Wayland protocols from sandboxed clients (security-context-v1),
      and `ext-data-control` is exactly that kind of protocol. If KWin withholds
      it, the hotkey reads nothing.
- [ ] **The tray icon** in both sandboxes. **Snap: done on Kubuntu 2026-09-25**
      (revision 5): the icon registers (`unity7`), a click opens the window, and
      "Read selected text" speaks. Flatpak (`--talk-name=org.kde.StatusNotifierWatcher`)
      not yet tried.
- [ ] **Orca end to end**: `sandbox-setup.sh speechd-install`, then Orca speaking
      through the module, in both. Snap: the install and `spd-say -o
      vibesupertonic` work on Kubuntu (2026-09-25); Orca itself not yet tried.
- [ ] **KDE's menu gains a second VibeSuperTonic entry** after `bind`: the store's
      own, plus the one keybindings.sh writes to carry the shortcuts. **Confirmed**
      on Kubuntu with the snap (`vibesupertonic_vibesupertonic.desktop` beside
      `vibesupertonic.desktop`). Decide
      whether that one should be `NoDisplay=true`, and test that the shortcuts
      survive it.
- [ ] **GNOME on Wayland**, which is stock Ubuntu: the hotkey cannot read the
      selection there (no `ext-data-control`), so on the App Center's default
      desktop the product is its screen-reader voice plus the window. The listing
      says so; the page's screenshots should not suggest otherwise.

Not offered inside either sandbox yet: the optional CUDA pack (`install-gpu.sh`),
and the timer helpers (`vst-gpu-guard.sh`, `vst-autotune.sh`).

## Submitting

### Snap Store

1. ~~Create a developer account at snapcraft.io and reserve the name:
   `snapcraft register vibesupertonic`.~~ **Done 2026-09-25**; the name
   `vibesupertonic` belongs to the project's Ubuntu One account.
2. `snapcraft upload --release=edge dist/VibeSuperTonic-0.2.17-amd64.snap`. CI's
   `linux-snap` artifact is a `devel`-grade build, which `edge` and `beta`
   accept; `candidate` and `stable` need a build made with `--grade stable`.

   **CI does this itself** once the repository has an
   `SNAPCRAFT_STORE_CREDENTIALS` secret: the snap job publishes to `edge` on
   every push to `main`, and on a manual run with `publish_edge` ticked, after
   `--test-install` has passed. Make the credential with
   `snapcraft export-login --snaps=vibesupertonic --channels=edge
   --acls=package_access,package_push,package_update,package_release <file>`
   and paste the file's contents into the secret. It can reach `edge` only.
   Promotion to `stable` stays a person's decision, in the dashboard or with
   `snapcraft release`.
3. In the dashboard: categories (Utilities, Productivity), screenshots, and the
   licence note that the models download separately under OpenRAIL-M.
4. Automated review should pass, because nothing privileged is requested. Promote
   `edge` → `stable` once the checklist above is done.

### Flathub

1. **Screenshots first.** Flathub's linter requires `<screenshots>` in the
   metainfo, with image URLs that stay put (a file in this repository at a tag
   works). There are none yet.
2. Publish the tarball as a GitHub release asset, then generate the manifest
   pinned to it:
   `bash build/pack-flatpak.sh -v 0.2.17 --manifest-only <asset URL>`.
3. Fork `flathub/flathub`, add the manifest on a branch off `new-pr`, and open
   the pull request. Reviewers will ask about `--device=dri` (GPU inference)
   and `--share=network` (the model download); both are in the manifest's
   comments.
4. Once merged, the app gets its own repository,
   `flathub/io.github.hananel_hazan.VibeSuperTonic`. Each release is then a PR
   there that bumps the URL and hash.

The app id is permanent once published. `io.github.hananel_hazan` is verified
through the GitHub account, so there is no domain to prove.

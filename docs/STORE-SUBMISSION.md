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
compiles anything of ours (the one exception, an export ffmpeg, is below), so
the tarball's eleven assertions apply to both, and each
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

### The one thing they do compile: an export ffmpeg (2026-10-03)

MP3, AAC and FLAC export pipe the daemon's WAV into an `ffmpeg` found at runtime.
The tarball and the AppImage use the system's; a snap or a Flatpak cannot see
it, and the Flatpak runtime ships ffmpeg's *libraries* but not the `ffmpeg`
program. So both store packages carry a minimal one, built by one recipe,
[build-ffmpeg.sh](../build/build-ffmpeg.sh), used by the snap's `ffmpeg` part
and by both Flatpak manifests:

- **Sources, pinned in the script**: ffmpeg `n8.1.3` by commit
  (`1041abdc`, from GitHub; ffmpeg.org's tarball host was not reachable from the
  session that wrote this) and LAME 3.100 by SHA-256 (SourceForge). The Flatpak
  manifests repeat both pins, because flatpak-builder must fetch the sources
  itself (Flathub builds offline); [check-ffmpeg-pins.sh](../build/check-ffmpeg-pins.sh)
  refuses a manifest that disagrees, before any build.
- **LGPL only**: no `--enable-gpl`, no `--enable-nonfree`, so no `libfdk_aac`; AAC
  is ffmpeg's native encoder. The script refuses a configuration that comes out
  anything but "LGPL version 2.1 or later".
- **`--disable-everything`, then exactly what export runs**: WAV/`pcm_s16le` in
  on a pipe, encoders `libmp3lame`, `aac`, `flac`, muxers `mp3`, `ipod` (.m4a),
  `flac`, protocols `pipe` and `file`, filters `aresample`, `aformat`, `anull`
  (the daemon's interleaved s16 must become planar for both lossy encoders). No
  network, devices, hardware acceleration, ffprobe or ffplay; `--disable-x86asm`.
  ffmpeg's libraries and LAME are static; libc and libm come from the runtime.
  **1.9 MB** stripped, measured.
- **Where**: `$SNAP/ffmpeg/ffmpeg` and `/app/lib/vibesupertonic/ffmpeg/ffmpeg`,
  which is where `FfmpegDetector.BundledPath` looks inside each sandbox (order:
  `VST_FFMPEG`, the bundled one, `PATH`). Beside it: `LICENSE-FFMPEG.txt` (both
  versions, their sources: the source offer, as `LICENSE-PHONEMIZER.txt` is for
  espeak-ng), `COPYING.LGPLv2.1`, `COPYING.LAME` and `BUILD-INFO`.
- **Checked**: [check-ffmpeg-bundle.sh](../build/check-ffmpeg-bundle.sh) asserts
  the files, the version, no GPL/non-free configuration, the three encoders, and
  a piped 0.1 s WAV encoding to all three formats with **exactly**
  `FfmpegTools.BuildArgs`'s arguments (a unit test holds its copy of them to
  BuildArgs). `pack-snap.sh` runs it on the finished snap (`--check` too) and,
  with `--test-install`, inside `ctl`'s confinement; `pack-flatpak.sh
  --test-install` runs it inside the sandbox. Both also ask `vst-ctl
  --export-formats` under confinement, which must name the bundled ffmpeg.
  [ffmpeg-bundle-sabotage.sh](../build/ffmpeg-bundle-sabotage.sh) breaks every
  clause, the manifests' pins and the recipe's refusals, and CI's `linux` job
  runs it.

**Saving into the document portal.** A Flatpak's save dialog may return
`/run/user/<uid>/doc/<id>/name`, a FUSE view that grants only the chosen file.
`ExportRunner` first tries its usual hidden temp file beside the target; when the
folder refuses it, the temp file goes to `$XDG_CACHE_HOME/vibesupertonic/export`
and only a complete, successful encode is **copied** over the target (a rename
cannot cross filesystems). A failed copy removes a target this run created, and
reports one it was replacing as possibly incomplete. The Export tab also stops
"correcting" the extension of a portal path, since a renamed file there is one
the portal never granted.

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

**The export ffmpeg, verified and not (2026-10-03).** Built here with the
script's `--fetch` (as the snap part does) and with `--ffmpeg-src/--lame-src` (as
the Flatpak manifests do); it encodes MP3, AAC and FLAC from a piped WAV;
`ExportRunner`'s real-ffmpeg test passed against it through `VST_FFMPEG`; every
sabotage in `ffmpeg-bundle-sabotage.sh` was caught, a real rebuild without
`aresample` among them. **Not yet observed**: the `ffmpeg` part under snapcraft,
the module under flatpak-builder (both CI only), the in-confinement checks, the
document-portal fallback against a real portal, and whether Flathub's reviewers
accept a bundled ffmpeg CLI (the usual alternative, the `org.freedesktop.Platform.ffmpeg-full`
extension, carries libraries rather than the program, so it would still need a
CLI built against it; not checked here, since this session has no flatpak).

**Needs a real Kubuntu and a real Ubuntu desktop before submitting**:

- [ ] **Export MP3 from the window in each package**, saving into the home
      folder through the portal's dialog, and once over an existing file.

- [ ] **A manual `snap refresh` is refused while any VibeSuperTonic process runs**
      ("has running apps"): the Speech Dispatcher module (speech-dispatcher keeps
      it alive), the daemon, or a window the tray opened (which snapd counts as
      `ctl`). snapd's normal rule; automatic refreshes wait instead. Decide
      between telling users (listing, INSTALL notes) and making the module and
      daemon step aside.
      **Measured 2026-09-26, speech-dispatcher 0.12.1: a module that exits is not
      restarted.** speechd logs "Output module terminated abnormally, probably
      crashed", routes every later request to the fallback module, and still lists
      the dead one in `spd-say -O`. So "the module exits when idle" would silently
      move a screen-reader user to espeak. Ruled out. The candidate instead is
      `refresh-mode: ignore-running` on the `speechd` and `daemon` apps, with the
      daemon exiting when idle once it sees `current` point at a newer revision.
      Needs a CI snap build to verify.
      **Built 2026-09-26, uncommitted**: both lines in snapcraft.yaml.in, asserted
      by pack-snap.sh (a synthetic snap without either line is refused), and
      `SnapRefreshWatch` in the daemon with 17 tests, three sabotages caught.
      Still to see: CI's `--test-install` accepting the snap, then a real refresh
      on the desktop with the daemon and the module running. Today's workaround: `vibesupertonic.ctl shutdown`,
      `systemctl --user stop speech-dispatcher.service`, close the window.
      **Withdrawn 2026-09-27: the Store refuses it.** The first publishing run
      failed at upload: `refresh-mode` is allowed only on services (`daemon:`
      apps), and neither app is one. Local snapd had installed the snap without
      complaint, so `--test-install` passed. The lines are gone, and pack-snap.sh
      now refuses `refresh-mode` on any app that is not a service.
      `SnapRefreshWatch` stays but does nothing, because `current` cannot move
      while the daemon runs. **Still open, and the workaround above is the answer
      for now.** Options to weigh: the daemon exiting after a long idle (costs a
      cold start on the next press), or snapd's own refresh-app-awareness
      notification, which asks the user to close the app.
      **Built 2026-09-27: the user chooses.** The user ruled out anything
      automatic (no surprise restart, no unannounced slow press). The daemon
      notices a held revision by the file snapd pre-downloads,
      `/var/lib/snapd/snaps/<instance>_<rev>.snap`, which every snap's AppArmor
      profile lets it stat (`snapctl refresh --pending` is refused to a non-root
      app, and the notices API needs a reviewed interface). It then shows a
      notification with Update now / Later and a tray row. Update now closes the
      window (`UpdateStarting` event), the module (the `update-now` marker in
      `$SNAP_USER_COMMON`, honoured only if written after the module started) and
      the daemon; snapd applies a held refresh when the apps stop.
      `SnapUpdateWatch`, `SnapUpdateMarker`, `UpdatePrompt`; 19 tests, two
      sabotages caught.
      **Confirmed on Kubuntu 2026-09-27, revision 8 to 9**, with snapd's timer
      moved to force the check: 04:06:55 snapd pre-downloaded
      `vibesupertonic_9.snap` and showed its own "Update available" notice;
      04:10:03 the daemon logged revision 9 waiting; 04:15:36 Update now from the
      tray; 04:15:56 snapd's auto-refresh completed, 20 s later, nothing typed.
      Found by it: the notification stayed on screen after Later ("resident"), so
      Later was clicked 13 times; fixed in the next revision by dropping the hint
      and closing it on either answer. **Second round, 9 to 10, 04:47:** the
      Speech Dispatcher module was running and stepped aside (left `<defunct>`:
      speechd never reaps it, and a zombie does not hold a refresh), and the
      refresh followed at once. speechd then spoke through espeak-ng until it was
      restarted, which the notification and the window now explain (62a7bc0).
- [ ] **After installing or re-binding, log out and back in** before the hotkey
      works on KDE: kglobalaccel reads shortcuts only at login (by design, see
      keybindings.sh). Seen on Kubuntu 2026-09-25. The listing should say so.

- [ ] **The hotkey after a reboot, before any window has opened** (snap). Revision
      5 read nothing in Wayland applications then: the daemon's probe looked for
      `wayland-0` in the snap's private `$XDG_RUNTIME_DIR`, where only the window's
      `desktop-launch` links it, and fell back to X11 PRIMARY for its whole life.
      Log line: `selection source: x11 (PRIMARY)` on a Wayland session. Found on
      Kubuntu 2026-09-27 and reproduced by removing the link and restarting the
      daemon. **Fixed 2026-09-27, uncommitted**: `SnapWaylandLink` makes the same
      link before the probe (7 tests, the sabotage caught by 4). To see on the
      desktop with the next revision: reboot, press the hotkey before opening the
      window, and the log says `linked ... wayland-0` then `selection source:
      wayland`.
      **Confirmed on the desktop 2026-09-27, revision 7:** after a reboot the first
      hotkey press started the daemon, which logged the link and chose Wayland,
      and the selection was read.

- [x] **Hotkey latency through `/snap/bin/vibesupertonic.ctl`.** **Measured
      2026-09-27 on Kubuntu: 108 ms median per press**, all of it `snap run`
      setting up confinement (`snap run --shell vibesupertonic.ctl -c true` costs
      the same), against a 100 ms budget for the whole press. **Accepted: it is
      the snap's cost, and the fallback does not work.** Run from the mount on
      the host, `vst-ctl` is refused by the confined daemon's AppArmor profile,
      which does not accept a connection from an unconfined peer (`DENIED
      operation="connect" ... requested="send receive accept"`), seen in CI's
      `--test-install` and then on the desktop. It had first looked like 6 ms and
      no denial, because the daemon it reached had itself been started
      unconfined by the same experiment. Tried as d5215c9, reverted. A future
      route would be a D-Bus call to the daemon (the tray's SNI object already
      receives calls from the desktop), which needs its own design and an
      autostart story. The tarball, AppImage and Flatpak do not pay this.
- [ ] **Selection capture in the Flatpak on Plasma/Wayland.** KWin can withhold
      privileged Wayland protocols from sandboxed clients (security-context-v1),
      and `ext-data-control` is exactly that kind of protocol. If KWin withholds
      it, the hotkey reads nothing.
- [ ] **The tray icon** in both sandboxes. **Snap: done on Kubuntu 2026-09-25**
      (revision 5): the icon registers (`unity7`), a click opens the window, and
      "Read selected text" speaks. **A click after a logout and login opens the
      window too** (the `ClientDisplay` fix), confirmed 2026-09-26. Flatpak
      (`--talk-name=org.kde.StatusNotifierWatcher`) not yet tried.
- [ ] **Orca end to end**: `sandbox-setup.sh speechd-install`, then Orca speaking
      through the module, in both. **Snap: done on Kubuntu 2026-09-26**, Orca
      reading through the VibeSuperTonic voice (revision 5). Flatpak not yet
      tried.
- [ ] **KDE's menu gains a second VibeSuperTonic entry** after `bind`: the store's
      own, plus the one keybindings.sh writes to carry the shortcuts. **Confirmed**
      on Kubuntu with the snap (`vibesupertonic_vibesupertonic.desktop` beside
      `vibesupertonic.desktop`). Decide
      whether that one should be `NoDisplay=true`, and test that the shortcuts
      survive it.
      **Decided 2026-09-26: yes, in the snap and the Flatpak only** (sandbox-setup.sh
      sets `VST_KB_KDE_NO_DISPLAY=1`; the tarball and AppImage keep theirs visible,
      since it is their only entry). Plasma's own "Add Command" shortcuts are
      NoDisplay entries, which is the reason to expect it to work. **Not yet seen
      after a real logout.** The user's re-login on 2026-09-27 did not test it:
      revision 5's `sandbox-setup.sh` predates the change, `bind` was not re-run,
      and the entry on disk has no `NoDisplay` line. **Confirmed later the same
      day on revision 7:** `bind` re-run, the entry written `NoDisplay=true`, and
      after a reboot the hotkeys still work. The user had hidden the entry with KDE's menu editor
      on 2026-09-26, thinking it a mistake; `keybindings.sh status` now warns when
      the shortcuts point at an entry that no longer exists.
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

**A source-build manifest exists since 2026-09-27; not yet seen building on
Flathub's image.** Flathub requires source-available apps to be "built entirely
from source code", so the tarball manifest cannot be submitted. The second
manifest, `build/flatpak/io.github.hananel_hazan.VibeSuperTonic.source.yml.in`,
builds with the `dotnet10` SDK extension, restores offline from the NuGet
packages pinned in `build/flatpak/nuget-sources.json`, builds espeak-ng at
`build-espeak.sh`'s pin, and runs `pack-tar.sh` inside the build, so every packer
assertion runs there too. The tree it installs is the one the tarball holds.

- Rehearsed here: `pack-tar.sh` against a feed holding only the 34 packages
  then pinned, with a fresh package cache and NativeAOT forced onto gcc (the SDK has
  no clang), passes all its assertions.
- CI's `flathub` workflow ([flathub.yml](../.github/workflows/flathub.yml)) is
  the real check: it regenerates the package list inside the SDK (runtime packs
  are versioned by the SDK that restores them, so a list made on a workstation
  can be wrong), then runs `pack-flatpak.sh --from-source --test-install`.
  **It found exactly that.** The workstation list named runtime packs 10.0.12,
  while Flathub's `dotnet10//25.08` restores 10.0.8 and brings its own apphost:
  33 packages. Run 3 (2026-09-27) was the first full pass of the build,
  `--test-install` included, and `467913a` re-pinned the list from that run.
  The move to `dotnet10//26.08` (`9c80722`) brought the extension to 10.0.12,
  and the list was re-pinned from CI run 12 (2026-10-04): same 33 packages,
  eight runtime packs from 10.0.8 to 10.0.12. The Flathub build and the other
  artifacts now ship the same .NET runtime, 10.0.12.

Submission, once CI is green and v0.2.17 is tagged and pushed:

1. Screenshots are in the metainfo (done), at tag URLs.
2. `bash build/pack-flatpak.sh -v 0.2.17 --flathub v0.2.17` writes
   `dist/flathub/`: the manifest pinned to the tag's commit, with
   `nuget-sources.json` and `flathub.json` beside it. `flathub.json` limits Flathub
   to x86_64. The packer refuses to write the submission without it, because an
   aarch64 build of this package cannot succeed.
3. Fork `flathub/flathub`, add all three files on a branch off `new-pr`, and open
   the pull request. Reviewers will ask about `--device=dri` (GPU inference)
   and `--share=network` (the model download); both are in the manifest's
   comments. Answers to the likely questions are in RELEASE-0.2.17.md step 6.
4. Once merged, the app gets its own repository,
   `flathub/io.github.hananel_hazan.VibeSuperTonic`. Each release is then a PR
   there that bumps the tag, the commit, and `nuget-sources.json` if it moved.

The tarball manifest stays for CI's `flatpak` job and a local bundle.

The app id is permanent once published. `io.github.hananel_hazan` is verified
through the GitHub account, so there is no domain to prove.

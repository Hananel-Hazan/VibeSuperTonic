# Store submission and distribution

The goal is to put VibeSuperTonic in front of Kubuntu and Ubuntu users where
they look for software, **under Accessibility**, and to keep every package a
repackaging of one tarball so that one set of checks defends all of them. The
steps of a release are in [RELEASING.md](RELEASING.md).

## Where the packages show up

| Package | Ubuntu App Center | Kubuntu Discover | Built by |
| --- | --- | --- | --- |
| Snap | yes: the App Center *is* the Snap Store | yes | [build/pack-snap.sh](../build/pack-snap.sh) |
| Flatpak | no | yes, once Flatpak support is added (`plasma-discover-backend-flatpak`) | [build/pack-flatpak.sh](../build/pack-flatpak.sh) |
| AppImage | no | no | [build/pack-appimage.sh](../build/pack-appimage.sh) |
| Tarball | no | no | [build/pack-tar.sh](../build/pack-tar.sh), the canonical artifact |

That is the order the README offers them in. The AppImage is the third choice
and the tarball the last, but the tarball is what every other package is built
from.

**Neither store has an accessibility submission programme.** "Accessibility" is
a category, set by the listing:

- **Discover and Flathub** read `<categories>` from the AppStream metainfo and
  `Categories=` from the desktop entry. Both say `Accessibility;Utility`.
- **The Snap Store** has a fixed category list that the publisher picks from in
  the dashboard, with no Accessibility category. The dashboard's categories are
  *Education* and *Productivity* (read 2026-10-16); *Utilities* is the other
  defensible choice. Search is what finds the snap there, and **the Snap Store
  does not read AppStream `<keywords>`**: on 2026-10-22, `snap find tts`,
  `snap find "text to speech"` and `snap find accessibility` did not list a snap
  whose summary contained none of those words, while every result for `tts` had
  it in its summary. So the summary says "Text to speech (TTS)", the
  description's first sentence says "accessibility", and the summary stays at
  78 characters or fewer (the snap limit). The App Center and Discover's snap
  backend search the same index.

## How the packages are built

The snap and the Flatpak are **repackagings of the tarball**, the same design as
the AppImage. Neither compiles anything of ours (the one exception, an export
ffmpeg, is below), so the tarball's eleven assertions apply to both, and each
re-asks the cheap ones against the tree it packages
([check-composed-tree.sh](../build/check-composed-tree.sh)). The Flatpak checks
the tree inside the tarball, because on Flathub the tarball is its only source.
One listing, [build/desktop/](../build/desktop/), serves both stores and travels
inside the tarball.

```bash
bash build/build-espeak.sh                    # only when the payload is stale
bash build/pack-tar.sh -v X.Y.Z
bash build/pack-snap.sh -v X.Y.Z --grade stable
bash build/pack-flatpak.sh -v X.Y.Z --test-install
bash build/pack-appimage.sh -v X.Y.Z
```

`pack-snap.sh` needs `snapcraft` (LXD by default, `--destructive-mode` on an
Ubuntu 24.04 machine) and `pack-flatpak.sh` needs `flatpak-builder`. Neither
store is reachable from a cloud session, so there both are built only by CI's
`snap` and `flatpak` jobs ([build.yml](../.github/workflows/build.yml)), which
run on every push from the tarball the `pack` job built.

### The one thing they compile: an export ffmpeg

MP3, AAC and FLAC export pipe the daemon's WAV into an `ffmpeg` found at runtime.
The tarball and the AppImage use the system's. A snap or a Flatpak cannot see it,
and the Flatpak runtime ships ffmpeg's *libraries* but not the `ffmpeg` program.
So both store packages carry a minimal one, built by one recipe,
[build-ffmpeg.sh](../build/build-ffmpeg.sh), used by the snap's `ffmpeg` part and
by both Flatpak manifests:

- **Sources, pinned in the script**: ffmpeg `n8.1.3` by commit (`1041abdc`, from
  GitHub) and LAME 3.100 by SHA-256 (SourceForge). The Flatpak manifests repeat
  both pins, because flatpak-builder must fetch the sources itself (Flathub builds
  offline); [check-ffmpeg-pins.sh](../build/check-ffmpeg-pins.sh) refuses a
  manifest that disagrees, before any build.
- **LGPL only**: no `--enable-gpl`, no `--enable-nonfree`, so no `libfdk_aac`; AAC
  is ffmpeg's native encoder. The script refuses a configuration that comes out
  anything but "LGPL version 2.1 or later".
- **`--disable-everything`, then exactly what export runs**: WAV/`pcm_s16le` in on
  a pipe, encoders `libmp3lame`, `aac`, `flac`, muxers `mp3`, `ipod` (.m4a),
  `flac`, protocols `pipe` and `file`, filters `aresample`, `aformat`, `anull`
  (the daemon's interleaved s16 must become planar for both lossy encoders). No
  network, devices, hardware acceleration, ffprobe or ffplay; `--disable-x86asm`.
  ffmpeg's libraries and LAME are static; libc and libm come from the runtime.
  **1.9 MB** stripped.
- **Where**: `$SNAP/ffmpeg/ffmpeg` and `/app/lib/vibesupertonic/ffmpeg/ffmpeg`,
  which is where `FfmpegDetector.BundledPath` looks inside each sandbox (order:
  `VST_FFMPEG`, the bundled one, `PATH`). Beside it: `LICENSE-FFMPEG.txt` (both
  versions and their sources: the source offer, as `LICENSE-PHONEMIZER.txt` is
  for espeak-ng), `COPYING.LGPLv2.1`, `COPYING.LAME` and `BUILD-INFO`. The
  tarball and the AppImage never carry one; `check-composed-tree.sh` refuses an
  ffmpeg in the tarball's tree.
- **Checked**: [check-ffmpeg-bundle.sh](../build/check-ffmpeg-bundle.sh) asserts
  the files, the version, no GPL or non-free configuration, the three encoders,
  and a piped 0.1 s WAV encoding to all three formats with **exactly**
  `FfmpegTools.BuildArgs`'s arguments (a unit test holds the check's copy of them
  to BuildArgs, so a new ffmpeg option fails that test until the check, and maybe
  the recipe, follow). `pack-snap.sh` runs it on the finished snap (`--check`
  too) and, with `--test-install`, inside `ctl`'s confinement; `pack-flatpak.sh
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
reports one it was replacing as possibly incomplete. The Export tab does not
"correct" the extension of a portal path, since a renamed file there is one the
portal never granted. Export refuses while anything reads aloud, and the window
keeps that message up until the reading ends (`ExportBusyGate`, asked before the
save dialog opens).

## What the sandbox changes in the code, and why

A snap and a Flatpak both make the program's own directory **read-only** and give
it a private view of the home directory. Each of the following breaks silently
without the rule next to it.

1. **The store.** The portable rule puts `models/` and `data/` beside the
   executable. Inside a sandbox it goes to the one directory each sandbox keeps
   for the app across updates: `$SNAP_USER_COMMON` (`~/snap/vibesupertonic/common`)
   or the Flatpak's `$XDG_DATA_HOME` (`~/.var/app/<id>/data`).
   `LinuxDataPaths.DetectSandbox` decides this, and checks that the executable
   really is inside the sandbox before believing the environment variables.
2. **The install check.** snapd renames the program's directory *and* `$HOME` on
   every refresh, so the "program moved" check would show every snap user a false
   re-bind warning after every update. The check records `/snap/<name>/current`
   and `$SNAP_REAL_HOME` instead. Running revision x7 and then x8 against the
   same store reported no move.
3. **The control socket.**
   - **Snap:** the socket is abstract, `@snap.<instance>.ctl-<uid>` (`SnapPeer`),
     and **every app that can start the daemon plugs `network-bind`**. snapd's
     default seccomp filter has `bind()` but not `listen()`, which only
     `network-bind` grants, whatever the address; without it the daemon aborted
     1.7 s after every hotkey press while every packer check passed, because they
     all ran the snap's files outside confinement. The kernel's `type=1326 ...
     syscall=50` line named seccomp; AppArmor had been blamed first. An abstract
     socket has no file mode, so the daemon checks each peer's uid
     (`SO_PEERCRED`, `PeerCredentials`) and refuses other users.
   - **Flatpak:** each `flatpak run` gets a private `/run/user/<uid>` and its own
     PID namespace, so the socket lives in `$XDG_RUNTIME_DIR/app/<id>/`, which
     every instance and the host share. A daemon started from inside the sandbox
     goes through `flatpak-spawn` so it outlives its parent (`FlatpakPeer`,
     `DaemonLaunch` in Core).
4. **The tray and the window.**
   - **Snap:** the tray opens the window under `ctl`'s confinement, and a process
     runs under its starter's confinement, so the window app's every plug is also
     on `daemon`, `ctl` and `speechd`, and the daemon opens the window by the
     window app's own command chain read from `meta/snap.yaml` (`SnapWindow`).
     Running the bare `$SNAP/vibesupertonic-ui` there aborts for want of
     `libfontconfig.so.1`. `daemon`, `ctl` and `speechd` carry hidden desktop
     entries in `meta/gui/`: without one, `snap routine portal-info` says
     `DesktopFile=.` and the document portal grants a saved file to an app named
     "." that the snap cannot see, so exporting a new file from a tray-opened
     window failed. The portal can still grant to `snap.vibesupertonic.ctl` while
     the snap sees only `snap.vibesupertonic`'s folder, so the window passes the
     grant on with `Documents.GrantPermissions`.
   - **Flatpak:** the window draws only through X11, and `fallback-x11` withholds
     X11 whenever Wayland exists, so the manifest asks for `--socket=x11` and
     `--socket=wayland` (X11 for the window, Wayland so the hotkey can read the
     selection). Adding `--socket=x11` beside `fallback-x11` still gave no
     display. `check-flatpak-sockets.sh` asserts it, and Flathub's linter reports
     `finish-args-contains-both-x11-and-wayland` for it, a warning this app needs.
     The proxy refuses `RequestName` for `org.kde.StatusNotifierItem-<pid>-1`, so
     the tray icon registers under the unique bus name instead.
5. **Everything in a snap needs sound and a Wayland socket it cannot see.**
   `PULSE_SERVER` points at `/run/user/$SNAP_UID/pulse/native` for every app,
   because `XDG_RUNTIME_DIR` is private and a daemon started by the bare `ctl`
   would otherwise find no sound server. `SnapWaylandLink` links the Wayland
   socket into the snap's runtime directory before the daemon chooses its
   selection source; without it, the hotkey pressed after a reboot and before any
   window had opened read nothing, because the probe looked for `wayland-0` in the
   snap's private runtime directory and fell back to X11 PRIMARY for the daemon's
   whole life. The daemon's own libraries (PulseAudio, X11, Wayland) live in
   `lib/native`, because the GNOME extension's GPU cleanup deletes `libX11` from
   `usr/lib`.

**The hotkeys and Speech Dispatcher are set up from the host**, by
[sandbox-setup.sh](../build/sandbox-setup.sh), which ships in both packages. Both
are the desktop's configuration (`kglobalshortcutsrc`, `gsettings`,
`~/.config/speech-dispatcher`), and a sandbox cannot write it. The stores *can*
grant that access (snap `personal-files`, Flatpak `--filesystem`), but only after
a manual review, and the grant would sit on the package forever to be used once.
With the script, the snap needs **no privileged interface and no classic
confinement**, and the Flatpak needs **no home or host filesystem access**.
**Do not add those grants to make binding work from inside.** The script reuses
`keybindings.sh` and `speechd-install.sh` unchanged, and sets
`VST_KB_KDE_NO_DISPLAY=1` so that the menu entry carrying the shortcuts is hidden
in the snap and the Flatpak (it is the only entry in the tarball and the
AppImage, so it stays visible there). Plasma's own "Add Command" shortcuts are
NoDisplay entries too. `keybindings.sh status` warns when the shortcuts point at
an entry that no longer exists, because deleting it in KDE's menu editor breaks
them. Its harness is [check-sandbox-setup.sh](../build/check-sandbox-setup.sh),
with negative controls, in CI's `linux` job.

| | Hotkey runs | Why |
| --- | --- | --- |
| snap | `/snap/bin/vibesupertonic.ctl` | inside confinement, so it shares the daemon's runtime directory |
| Flatpak | `<deployment>/active/files/lib/vibesupertonic/vst-ctl`, directly on the host | NativeAOT, needs only libc; `flatpak run` would add a sandbox setup to every press |

**`speechd-install.sh` undoes itself** when a module that was offered before the
install is missing after it: it restores the previous config, restarts
speech-dispatcher, confirms the old modules answer, and exits non-zero. It also
SIGKILLs a speech-dispatcher that has not exited 5 s after SIGTERM, because a
socket-activated one that is still dying keeps its service active, so systemd
starts no new one and every client queues forever. The first `speechd-install` on
the snap (2026-09-25) left speech-dispatcher offering no voices and hung; the
cause was never proven (a stale speech-dispatcher waited on by the wrong process
id is the suspect), and the installer is safe whatever it was.

### Refreshes

snapd refuses a manual `snap refresh` while any VibeSuperTonic process runs
("has running apps"): the Speech Dispatcher module (speech-dispatcher keeps it
alive), the daemon, or a window the tray opened (which snapd counts as `ctl`);
automatic refreshes wait instead. Three designs were ruled out:

- **A module that exits when idle.** Measured with speech-dispatcher 0.12.1: a
  module that exits is not restarted. speechd logs "Output module terminated
  abnormally, probably crashed", routes every later request to the fallback
  module, and still lists the dead one in `spd-say -O`, which would silently move
  a screen-reader user to espeak.
- **`refresh-mode: ignore-running`.** The Snap Store accepts it only on services
  (`daemon:` apps), and none of ours is one; a local snapd installs it without
  complaint, so `--test-install` passes. `pack-snap.sh` refuses `refresh-mode` on
  any app that is not a service.
- **Anything automatic.** The user ruled out a surprise restart or an unannounced
  slow press.

What is built instead lets the user choose. The daemon notices a held revision by
the file snapd pre-downloads, `/var/lib/snapd/snaps/<instance>_<rev>.snap`, which
every snap's AppArmor profile lets it stat (`snapctl refresh --pending` is
refused to a non-root app, and the notices API needs a reviewed interface). It
shows a notification with Update now / Later and a tray row (`SnapUpdateWatch`,
`SnapUpdateMarker`, `UpdatePrompt`). Update now closes the window (the
`UpdateStarting` event), the module (an `update-now` marker in
`$SNAP_USER_COMMON`, honoured only if written after the module started) and the
daemon; snapd applies a held refresh when the apps stop. The notification closes
on either answer. Speech Dispatcher's module is left `<defunct>` (speechd never
reaps it, and a zombie does not hold a refresh), after which speechd speaks
through espeak-ng until it is restarted; the notification and the window say so.
`SnapRefreshWatch` remains in the daemon but does nothing, since `current` cannot
move while the daemon runs. The workaround for an unattended refresh is
`vibesupertonic.ctl shutdown`, `systemctl --user stop speech-dispatcher.service`,
and closing the window.

## What a user needs to know

- **Log out and back in after installing or re-binding** before the hotkey works
  on KDE: kglobalaccel reads shortcuts only at login (by design; see
  `keybindings.sh`). The listing says so.
- **A snap hotkey press costs about 108 ms**, measured 2026-09-27 on Kubuntu, all
  of it `snap run` setting up confinement (`snap run --shell
  vibesupertonic.ctl -c true` costs the same), against a 100 ms budget for the
  whole press. Accepted as the snap's cost. Running `vst-ctl` from the mount on the
  host does not work around it: the confined daemon's AppArmor profile refuses a
  connection from an unconfined peer (`DENIED operation="connect" ...
  requested="send receive accept"`), seen in CI's `--test-install` and on the
  desktop. A route would be a D-Bus call to the daemon, which needs its own design
  and an autostart story. The tarball, AppImage and Flatpak do not pay this.
- **GNOME on Wayland**, which is stock Ubuntu, has no `ext-data-control`, so the
  hotkey cannot read the selection there. On the App Center's default desktop the
  product is its screen-reader voice plus the window. The listing says so, and
  its screenshots do not suggest otherwise.
- **AutoCopy** (the Tune tab's "Press Ctrl+C for me") presses a key through the
  desktop's RemoteDesktop portal. Whether that portal is reachable from inside
  the snap or the Flatpak has not been checked, so those two packages may not
  offer it.
- **KDE's menu gains a second VibeSuperTonic entry** after `bind` in the tarball
  and the AppImage (the one `keybindings.sh` writes); in the snap and the Flatpak
  it is hidden.
- **Not offered inside either sandbox:** the optional CUDA pack (`install-gpu.sh`)
  and the timer helpers (`vst-gpu-guard.sh`, `vst-autotune.sh`).

## What has been verified, and what has not

**Checked by this repository's own tests and packers:**

- Unit tests for the sandbox rules, the socket path and the launch plan, each
  sabotaged and seen failing; the same for `SnapWaylandLink`, `SnapUpdateWatch`
  and `ExportBusyGate`.
- The real published binaries laid out as a snap: the store lands in `common/`,
  `vst-ctl` auto-starts the daemon and talks to it, and a refresh from x7 to x8
  reports no move.
- The real host-side `vst-ctl` in a Flatpak deployment layout looks for the
  daemon in the shared runtime directory.
- `sandbox-setup.sh` against a fake snap mount and a fake Flatpak installation,
  with three negative controls.
- `pack-snap.sh` asserts, on the finished snap: the version; **strict**
  confinement; all five apps (`vibesupertonic`, `daemon`, `ctl`, `speechd`,
  `setup`); `network-bind` and `unity7` (the tray's session bus) on the four that
  can start the daemon, and every plug of the window app on `daemon`, `ctl` and
  `speechd`; the hidden desktop entries; `PULSE_SERVER` for every app; **no
  `command-chain` on `ctl`**, because an extension there wraps every hotkey press
  in a launcher script; no `refresh-mode` on a non-service; no models and no CUDA
  provider; the daemon's three staged libraries; and the module answering `INIT`
  with 299. `--check <file.snap>` runs only these; each was seen failing against
  a sabotaged snap.
- `pack-snap.sh --test-install` installs the snap under snapd and makes its
  daemon answer; CI's snap job runs it. It also runs
  [check-snap-speechd.sh](../build/check-snap-speechd.sh): a **private**
  speech-dispatcher, started once from a shell and once socket-activated through
  `systemd-run --user` as a desktop starts it, must load the snap's module and
  keep everything it offered before (a desktop with the snap installed can run the
  same script, which never touches the user's own speech-dispatcher). And it opens
  the window the way the tray does: `vibesupertonicd --window-command` run inside
  `ctl`'s confinement on Xvfb must stay up, and the bare
  `$SNAP/vibesupertonic-ui` in the same place must not, or the check cannot tell
  them apart.
- `pack-flatpak.sh --test-install` installs the bundle and asserts the store lands
  under `~/.var/app`, the module answers `INIT` inside the sandbox, and the
  host-side `vst-ctl`, run from the deployment's `active` path as a hotkey runs
  it, looks for the daemon in the shared runtime directory. If that were wrong,
  every press would start a second daemon nobody talks to.
- The export ffmpeg was built with the script's `--fetch` (as the snap part does)
  and with `--ffmpeg-src/--lame-src` (as the Flatpak manifests do); it encodes
  MP3, AAC and FLAC from a piped WAV, `ExportRunner`'s real-ffmpeg test passed
  against it through `VST_FFMPEG`, and every sabotage in
  `ffmpeg-bundle-sabotage.sh` was caught, a real rebuild without `aresample`
  among them.
- CI runs the real `snapcraft` and `flatpak-builder` builds and the installed
  checks, which this development container cannot (it cannot reach the Snap Store
  or Flathub).

**Seen on a real Kubuntu desktop:**

- *Snap.* The tray icon registers (`unity7`), a click opens the window, "Read
  selected text" speaks, and a click after a logout and login opens the window.
  Orca reads through the VibeSuperTonic voice. After a reboot the first hotkey
  press, before any window has opened, starts the daemon, which chooses Wayland,
  and the selection is read. The hidden menu entry keeps its hotkeys after a
  reboot. Export works from a menu-opened window and from a tray-opened one (WAV
  twice, the second over the first; MP3 of 395 KB); an export while the hotkey
  speaks says "busy (Speaking) …", with no ffmpeg left behind. A refresh from
  revision 8 to 9 with snapd's timer moved to force the check: snapd
  pre-downloaded the revision, the daemon logged it waiting, Update now from the
  tray was pressed, and snapd's refresh completed 20 s later with nothing typed.
  From 9 to 10 the module was running, stepped aside, and the refresh followed at
  once.
- *Flatpak (Kubuntu 2026-10-16).* The window opens on Wayland, the tray icon
  registers from inside the sandbox, export of a new file and of an overwrite
  works through the file dialog, and the sandbox gets the Wayland protocol the
  hotkey reads the selection with (the daemon logged `selection source:
  wayland`).

**Not verified:**

- The Flatpak's hotkey itself pressed on Plasma/Wayland (KWin can withhold
  privileged protocols from sandboxed clients), the Flatpak's tray after a logout
  and login, and Orca through the Flatpak's module.
- AutoCopy inside the snap and the Flatpak.
- A real X11 desktop after a real logout, for the daemon that outlived it
  (`NativeXAuthority` was seen failing and then passing against this machine's
  XWayland with a daemon started on a dead `XAUTHORITY`).
- Whether Flathub's reviewers accept a bundled ffmpeg CLI. The usual alternative,
  the `org.freedesktop.Platform.ffmpeg-full` extension, carries libraries rather
  than the program, so it would still need a CLI built against it.
- The two entries in [lint-exceptions.json](../build/flatpak/lint-exceptions.json)
  about screenshot mirroring (`appstream-external-screenshot-url` and
  `appstream-screenshots-not-mirrored-in-ostree`) have never been seen firing,
  because only Flathub's own builders mirror screenshots. If Flathub's build shows
  either one not raised, remove it.
- AUR and winget have not been run through their own submission checks beyond
  what each section below lists.

## Where each store stands (2026-11-08)

| Channel | State |
| --- | --- |
| Snap Store | `stable` and `candidate`: 0.2.18, revision 18; `beta`: 0.2.18, revision 19; `edge`: 0.2.19, revision 21 (read from the store's API). Revision 21 is the stable-grade 0.2.19 build, byte-identical to the GitHub release asset (SHA3-384 compared); it is not promoted yet. The main-branch push published a `devel` build before it, which `stable` refuses |
| GitHub Releases | Latest 0.2.19 (tag `v0.2.19`), with the tarball, AppImage, Windows ZIP, snap and Flatpak |
| Flathub | flathub/flathub#10671, open, labels `leave-open` and `too-new`. Opened 2026-10-17 from `Hananel-Hazan/flathub` branch `add-vibesupertonic`, with a manifest the maintainer wrote by hand, building `v0.2.18`. Flathub's test build passed 2026-10-24 with one linter warning, `finish-args-contains-both-x11-and-wayland`. A reviewer objected to "vibe" in the name on 2026-10-21. On 2026-11-05 the submission bot answered that Flathub requires a meaningful history of development, evidence of real-world use and a commitment to ongoing maintenance, and that the submission will be revisited in 1 to 2 months |
| AppImageHub | Listed (AppImage/appimage.github.io#10295, merged 2026-10-16) |
| AUR, winget | Drafted in `build/aur/` and `build/winget/`, not submitted |

## Submitting

### Snap Store

1. The name `vibesupertonic` is registered to the project's Ubuntu One account.
2. `snapcraft upload --release=edge dist/VibeSuperTonic-X.Y.Z-amd64.snap`. CI's
   `linux-snap` artifact is a `devel`-grade build, which `edge` and `beta` accept;
   `candidate` and `stable` need a build made with `--grade stable`.

   **CI does this itself** once the repository has an `SNAPCRAFT_STORE_CREDENTIALS`
   secret: the snap job publishes to `edge` on every push to `main`, and on a
   manual run with `publish_edge` ticked, after `--test-install` has passed. Make
   the credential with `snapcraft export-login --snaps=vibesupertonic
   --channels=edge --acls=package_access,package_push,package_update,package_release
   <file>` and paste the file's contents into the secret. It can reach `edge`
   only. Promotion to `candidate` and `stable` stays a person's decision, in the
   dashboard or with `snapcraft release`. **Never have two publishing runs in
   flight**: whichever finishes last takes `edge`, even when it carries the older
   code.
3. In the dashboard: categories, screenshots, and the licence note that the models
   download separately under OpenRAIL-M.
4. Automated review passes because nothing privileged is requested.

### Flathub

Flathub requires source-available apps to be "built entirely from source code", so
the tarball manifest cannot be submitted. A second manifest,
[build/flatpak/io.github.hananel_hazan.VibeSuperTonic.source.yml.in](../build/flatpak/io.github.hananel_hazan.VibeSuperTonic.source.yml.in),
builds with the `dotnet10` SDK extension, restores offline from the NuGet packages
pinned in `build/flatpak/nuget-sources.json`, builds espeak-ng at
`build-espeak.sh`'s pin (and libsonic, which espeak-ng's CMake otherwise clones at
configure time even though nothing links it; `build-espeak.sh --sonic-src`
refuses any commit but the one espeak-ng names), and runs `pack-tar.sh` inside the
build, so every packer assertion runs there too. The tree it installs is the one
the tarball holds. `pack-flatpak.sh --from-source` builds it and `--flathub TAG`
writes a submission.

**The submission cannot be generated.** Flathub's
[Generative AI policy](https://docs.flathub.org/docs/for-app-authors/requirements#generative-ai-policy)
says manifests "must not contain AI-generated or AI-assisted content", and
disclosure does not exempt them. Every commit to the source manifest and to
`gen-nuget-sources.py` is co-authored by Claude. The policy also forbids AI tools
from opening the pull request or writing its description, commits or review
replies, and requires the app's own AI-generated material to be disclosed with its
extent (243 of 268 commits were co-authored on 2026-10-16). So the Flathub
manifest is written by the maintainer by hand, with `flatpak-dotnet-generator.py`
from flatpak-builder-tools for the NuGet sources, and `dist/flathub/` is not
submittable. The in-repo manifests are right for CI and for the local bundle.
Flathub's template also asks the submitter to confirm that no AI tool generated or
automated the pull request, and to attach a video of the Flatpak running.

**CI's `flathub` workflow** ([flathub.yml](../.github/workflows/flathub.yml)) is
the real check of the source manifest. It regenerates the package list inside the
SDK, because runtime packs are versioned by the SDK that restores them and a list
made on a workstation can be wrong (a workstation list named runtime packs
10.0.12 while `dotnet10//25.08` restored 10.0.8), then runs `pack-flatpak.sh
--from-source --test-install`, and refuses a stale list. The list is regenerated
with `python3 build/flatpak/gen-nuget-sources.py --in-sdk 26.08`, and whenever a
`PackageReference` changes it changes with it. It currently holds 33 packages, and
the Flathub build and the other artifacts ship the same .NET runtime, 10.0.12.
Rehearsed locally: `pack-tar.sh` against a feed holding only those packages, with a
fresh package cache and NativeAOT forced onto gcc (the SDK has no clang), passes
all its assertions. The same job runs Flathub's linter and refuses a screenshot
URL pinned to any tag but the one being built.

**What a submission contains:**

1. The manifest pinned to the tag's commit, `nuget-sources.json`, and
   `flathub.json`. `flathub.json` limits Flathub to x86_64; without it Flathub also
   builds aarch64, which this package cannot do, and the pull request is blocked.
2. A fork of `flathub/flathub`, a branch off `new-pr`, and a pull request against
   `new-pr`. Reviewers ask about `--device=dri` (optional GPU inference, with a CPU
   fallback) and `--share=network` (the model download after the user accepts the
   licence); both are in the manifest's comments.
3. Suggested description:

> **VibeSuperTonic**: text to speech for the Linux desktop. Select text, press a
> key, and a neural voice reads it aloud. It runs entirely on the user's machine
> and is also a Speech Dispatcher voice, so Orca can use it. Accessibility,
> Utility.
>
> I am the upstream developer. Source: MIT; the package is GPL-3.0-or-later as a
> whole because it bundles espeak-ng (licence and source offer in
> `LICENSE-PHONEMIZER.txt`).

| Question | Answer |
| --- | --- |
| `--share=network`? | The voice models download on first run from Hugging Face, after the user accepts their licence. Shipping them would skip that acceptance. Nothing the user reads is sent anywhere |
| `--device=dri`? | Optional GPU inference for the neural model. It falls back to the CPU |
| `--talk-name=org.kde.StatusNotifierWatcher`? | The tray icon |
| Prebuilt binaries in the NuGet packages? | ONNX Runtime (Microsoft) and SkiaSharp/HarfBuzzSharp (the .NET Foundation's Avalonia stack) ship their native libraries only inside their NuGet packages, pinned by SHA-512 in `nuget-sources.json`. Everything of ours, and espeak-ng, is compiled in the build |
| Why no filesystem or host access for hotkeys and speech-dispatcher? | Both are the desktop's configuration. A script the package ships (`sandbox-setup.sh`) is run on the host once, by the user, instead of asking the sandbox for write access to `~/.config` |

Once merged, the app gets its own repository,
`flathub/io.github.hananel_hazan.VibeSuperTonic`, and each release is a pull request
there that bumps the tag, the commit, and `nuget-sources.json` if it moved. Then the
README's Flathub badge points at
`https://flathub.org/apps/io.github.hananel_hazan.VibeSuperTonic` and the "coming
soon" badge goes. The app id is permanent once published; `io.github.hananel_hazan`
is verified through the GitHub account, so there is no domain to prove.

### AUR: [build/aur/](../build/aur/PKGBUILD)

`vibesupertonic-appimage` installs **the release AppImage whole** at
`/opt/vibesupertonic/VibeSuperTonic.AppImage` with `/usr/bin/vibesupertonic` linked
to it, and the desktop entry, metainfo, icon and licence from the tag. It is not
built from the tarball because a tarball tree in root-owned `/opt` cannot run:
outside an AppImage or a sandbox, `LinuxDataPaths.ResolveStore` puts the store
beside the executable, and the AppImage branch already falls back to
`$XDG_DATA_HOME/vibesupertonic`. A real distro package (from source, or the tarball
in `/usr/lib`) would need a third branch there first.

Verified in an `archlinux:latest` container: `makepkg` builds it, `pacman -U`
installs it, `vibesupertonic --version` through the symlink prints the version
(with `APPIMAGE_EXTRACT_AND_RUN=1`, because the container could not FUSE-mount), and
`appstreamcli validate` passes. namcap's two remaining complaints, ELF under `/opt`
and `fuse3` "maybe not needed", are expected for an AppImage package: the runtime
calls `fusermount3` by name, so namcap cannot see the dependency.

To publish: an AUR account with an SSH key, then `git clone
ssh://aur@aur.archlinux.org/vibesupertonic-appimage.git`, copy in `PKGBUILD`,
`vibesupertonic-appimage.install` and `.SRCINFO`, commit, push. Each release bumps
`pkgver` and the five hashes, and regenerates `.SRCINFO` with `makepkg
--printsrcinfo`.

### winget: [build/winget/](../build/winget/manifests/h/HananelHazan/VibeSuperTonic)

`HananelHazan.VibeSuperTonic` is the release ZIP installed as a **portable** zip,
with the x64 .NET 10 runtime and VC++ redistributable as dependencies. The folder
stays one portable unit; no installer was added, by decision. A manifest folder is
named for the version it describes, so each release adds one.

- **`ArchiveBinariesDependOnPath: true`**: winget puts the folder itself on PATH
  instead of a symlink in `WinGet\Links`. The launcher takes its own folder as the
  root for every COM path it registers (`AppContext.BaseDirectory`), and a launch
  through a symlink could otherwise register the Links folder.
- **Settings and models survive upgrades**: winget's portable uninstall does not
  purge unless `--purge` or `purgePortablePackage` asks it to, so `data\` and
  `models\` beside the exe are kept.
- **What winget cannot do, the install notes say**, because a portable package
  runs no code at install or uninstall: register once with *Repair all* or
  `--register` (a plain launch of `VibeSuperTonic.exe` never registers); run
  `--unregister` before `winget uninstall`, or dead voices stay in every SAPI list;
  close SAPI programs before `winget upgrade`, because the engine DLL is loaded
  in-process and cannot be replaced while one holds it. The Status tab's lock row
  names the holders. A real fix for the last one is the half-upgraded-engine item
  in [WINDOWS-PLAN.md](WINDOWS-PLAN.md).
- **The x86 pair** cannot be a dependency of an x64 package; the launcher's first
  window detects it missing and offers the winget command.

Validated against winget's published 1.10.0 JSON schemas, with a sabotaged copy
seen failing (a string boolean and a short hash). Not yet run: `winget validate
--manifest <dir>` and `winget install --manifest <dir>` on Windows, which come
before the pull request to `microsoft/winget-pkgs`.

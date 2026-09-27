# Releasing 0.2.17: snap to stable, GitHub release, Flathub

Prepared 2026-09-26, on Shabbat, so **nothing here has touched git**. Every
file change listed in step 1 is uncommitted on
`claude/kubuntu-ubuntu-store-submission-gzfqcb`.

**When:** not between Friday sunset in Israel and Saturday nightfall in the
mainland US. On 26 September that means after about 7:35 pm PDT (10:35 pm EDT),
and the next window closes on Friday 2 October around 6:10 pm IDT (11:10 am EDT).
Check your own calendar; these are approximate.

**Rule kept throughout:** pack, verify and tag in one sitting (CLAUDE.md). Steps
3 to 5 are that sitting. Pushing a tag and publishing anything is yours to do.

State on 2026-09-26, confirmed on Kubuntu with snap revision 5: the hotkey reads
the selection, the tray opens the window after a logout and login, and Orca
reads through the VibeSuperTonic voice.

## 1. Screenshots (you, about 10 minutes, before anything is committed)

**Done 2026-09-26**: `reader.png`, `voices.png` and `tune.png` are in
`docs/screenshots/`, in the metainfo and in the README, with `reading.gif` (made
from the user's screencast, cropped to the text, 525 KB). The originals are in
`screenshots/` at the repository root: delete that folder rather than committing
it. `status.png` and `pronunciations.png` below are optional extras.

Both stores want them, and Flathub will not accept a listing without them. Take
them from the snap, in the default theme, window only, about 1200×800. **Nothing
personal should be on screen**, and the Reader shows whatever was read last, so
start by reading the demo text:

```bash
cd "/home/hananel/#GitRepos/VibeSuperTonic"
mkdir -p docs/screenshots
vibesupertonic.ctl speak "$(cat samples/twenty-thousand-leagues.txt)"
vibesupertonic.ctl pause           # freezes the highlight mid-sentence
```

Then, for each tab: open it, and run the command. You have 3 seconds to click
the VibeSuperTonic window so it's the active one.

| File | Tab | What it should show |
| --- | --- | --- |
| `reader.png` | Reader | the Verne text with a word highlighted (the default screenshot) |
| `voices.png` | Voices | the catalog, with a licence visible |
| `tune.png` | Tune | the speed and quality controls |
| `status.png` | Status | a healthy install (green rows) |
| `pronunciations.png` | Pronunciations | one or two example rules, e.g. `Tcl` → `tickle` |

```bash
spectacle -b -n -a -d 3000 -o docs/screenshots/reader.png     # and so on per file
vibesupertonic.ctl stop                                       # when done
```

Then add this to `build/desktop/io.github.hananel_hazan.VibeSuperTonic.metainfo.xml`,
after `</description>` and before `<launchable>`. The URLs name the tag, so they
start working the moment `v0.2.17` is pushed, and they never move afterwards,
which is what Flathub asks for. The build validates with `--no-net`, so nothing
fetches them before then.

```xml
  <screenshots>
    <screenshot type="default">
      <caption>Reading the selection aloud, highlighting each word</caption>
      <image>https://raw.githubusercontent.com/Hananel-Hazan/VibeSuperTonic/v0.2.17/docs/screenshots/reader.png</image>
    </screenshot>
    <screenshot>
      <caption>Downloading voices, with each voice's licence shown first</caption>
      <image>https://raw.githubusercontent.com/Hananel-Hazan/VibeSuperTonic/v0.2.17/docs/screenshots/voices.png</image>
    </screenshot>
    <screenshot>
      <caption>Speed, quality and volume, for all voices or just one</caption>
      <image>https://raw.githubusercontent.com/Hananel-Hazan/VibeSuperTonic/v0.2.17/docs/screenshots/tune.png</image>
    </screenshot>
    <screenshot>
      <caption>Teaching it how to say a word</caption>
      <image>https://raw.githubusercontent.com/Hananel-Hazan/VibeSuperTonic/v0.2.17/docs/screenshots/pronunciations.png</image>
    </screenshot>
    <screenshot>
      <caption>Checking the install, and the one command that connects the hotkeys</caption>
      <image>https://raw.githubusercontent.com/Hananel-Hazan/VibeSuperTonic/v0.2.17/docs/screenshots/status.png</image>
    </screenshot>
  </screenshots>
```

Check it: `appstreamcli validate --no-net build/desktop/io.github.hananel_hazan.VibeSuperTonic.metainfo.xml`.

## 2. Commit the preparation, and bring Dev along

What is uncommitted, all prepared on 2026-09-26:

| File | Change |
| --- | --- |
| `README.md` | rewritten for store visitors, with badges, samples, limits, and a call for AI riders and agents |
| `docs/voices/*.mp3` | six voice samples, rendered from snap revision 5 |
| `RELEASE_NOTES.md` | 0.2.17: the installer's rollback, the snap fixes, the two snap caveats |
| `build/desktop/…metainfo.xml` | 0.2.17 is `type="stable"` with a real "what's new" (+ screenshots, from step 1) |
| `.github/workflows/build.yml` | a `snap_grade` choice on manual runs, so CI can build the stable-grade snap |
| `docs/STORE-SUBMISSION.md`, `docs/HANDOFF-2026-09-26-store.md` | tray-after-relogin and Orca confirmed |
| `docs/RELEASE-0.2.17.md` | this file |
| `docs/screenshots/*` | three screenshots and the GIF |
| `README.md` (again) | Flathub and AppImage badges beside the Snap Store's, the portable AppImage folder, a Sponsor badge and section |
| `.github/FUNDING.yml` | the repository's Sponsor button (appears once the GitHub Sponsors profile exists) |
| `build/snap/snapcraft.yaml.in`, `build/pack-snap.sh` | `refresh-mode: ignore-running` on `daemon` and `speechd`, asserted |
| `src/VibeSuperTonic.Daemon/SnapRefreshWatch.cs`, `DaemonServer.cs`, `…Tests/SnapRefreshWatchTests.cs` | the daemon steps aside, when idle, for a newer current revision |
| `build/pack-appimage.sh` | the store listing inside the AppImage, for AppImageHub; help text's speech-dispatcher stop command |
| `build/pack-tar.sh` | INSTALL.txt's speech-dispatcher stop command (`-x speech-dispatch`: the full name matches nothing) |
| `src/VibeSuperTonic.Daemon/SnapWaylandLink.cs`, `Program.cs`, `…Tests/SnapWaylandLinkTests.cs` | the hotkey after a reboot: the daemon links the Wayland socket into the snap's runtime dir before choosing its selection source (2026-09-27) |
| `build/keybindings.sh`, `build/sandbox-setup.sh`, `build/check-sandbox-setup.sh` | snap/Flatpak: the entry carrying the hotkeys is hidden from the menu; `status` warns if it was deleted; a new sabotage, caught |

**Dev is not orphaned.** This branch was started from Dev: Dev's tip, `0122667`
("0.2.16 gets its notes"), is in this branch's history, and Dev has nothing this
branch lacks (measured 2026-09-26 before Shabbat: 31 commits ahead, 0 behind). So
Dev can simply move forward to it, with no merge commit and nothing lost:

```bash
git status                                      # expect only the files above
git add README.md docs/voices docs/screenshots RELEASE_NOTES.md \
        build/desktop .github/workflows/build.yml docs/STORE-SUBMISSION.md \
        docs/HANDOFF-2026-09-26-store.md build/keybindings.sh \
        build/sandbox-setup.sh build/check-sandbox-setup.sh \
        .github/FUNDING.yml build/snap/snapcraft.yaml.in build/pack-snap.sh \
        build/pack-appimage.sh build/pack-tar.sh \
        src/VibeSuperTonic.Daemon/SnapRefreshWatch.cs src/VibeSuperTonic.Daemon/DaemonServer.cs \
        src/VibeSuperTonic.Daemon.Tests/SnapRefreshWatchTests.cs \
        docs/RELEASE-0.2.17.md
git commit -m "Prepare 0.2.17 for the stores: README, notes, listing, stable-grade CI"

git merge-base --is-ancestor Dev HEAD && echo "Dev is behind this branch, safe to fast-forward"
git checkout Dev
git merge --ff-only claude/kubuntu-ubuntu-store-submission-gzfqcb
git push origin Dev                             # runs CI on Dev; publishes nothing
```

**main is the open question.** Local `main` (`acf68c1`) and `origin/main`
(`906fbc4`) were not the same commit on 2026-09-26, and whether either is in
Dev's history was not checked (it would have meant touching git). Check first:

```bash
git fetch origin
git merge-base --is-ancestor origin/main Dev && echo "main can fast-forward to Dev"
```

If it prints the line, a pull request Dev → main merges cleanly. If it doesn't,
main has commits Dev lacks, and it needs a look before anything merges. Either
way, **merging to main publishes to `edge`** (the snap job uploads on every push
to main), so do it when edge may change, and not while a manual publishing run
is in flight: the later one to finish wins `edge`.

## 2b. Prove the refresh fix on the desktop (before packing)

It needs two revisions that both carry it, so it takes two CI publishes to
`edge` (a push to main, or a manual run with `publish_edge`).

1. The first one (say revision 6) still arrives the old way, since revision 5
   lacks the fix: `vibesupertonic.ctl shutdown`,
   `systemctl --user stop speech-dispatcher.service`, close the window, then
   `sudo snap refresh vibesupertonic --edge`. Press the hotkey, and have Orca or
   `spd-say -o vibesupertonic hi` speak once, so both are running.
2. When the next revision (7) is on edge, with the daemon and the module
   running and **no window open**: `sudo snap refresh vibesupertonic --edge`.
   **Expected: it succeeds.** Before, it said "has running apps".
3. `pgrep -a vibesupertonicd` shows `/snap/vibesupertonic/6/...`. Don't press
   anything and don't run `vibesupertonic.ctl status` (a connection counts as
   use). After 6 or 7 minutes, `pgrep -a vibesupertonicd` shows nothing. Press
   the hotkey: it reads, and `pgrep -a vibesupertonicd` shows `/7/`.
4. Orca still speaks, through the revision 6 module, until the next login.

If step 2 still says "has running apps", the store's snapd did not honour
`ignore-running`: say so in STORE-SUBMISSION and do not pack until it's
understood.

## 3. Pack and tag (one sitting with steps 4 and 5)

1. In `build/desktop/…metainfo.xml`, set 0.2.17's `date` to today.
2. In `RELEASE_NOTES.md`, delete the "Ready to pack; not packed yet" paragraph.
3. Commit both (`0.2.17 is released`), on Dev.
4. Pack. The espeak payload is rebuilt only if the pin moved; the packer says so.

```bash
bash build/build-espeak.sh                      # only if pack-tar.sh refuses the payload
bash build/pack-tar.sh -v 0.2.17 && bash build/pack-appimage.sh -v 0.2.17
ls -la dist/VibeSuperTonic-0.2.17-*
```

5. Verify: `mkdir -p /tmp/vst && tar -xzf dist/VibeSuperTonic-0.2.17-linux-x64.tar.gz -C /tmp/vst && bash build/smoke-test.sh /tmp/vst/VibeSuperTonic`,
   and open the AppImage once.
6. Tag the commit the artifacts came from:

```bash
git tag -a v0.2.17 -m "$(sed -n '/^# VibeSuperTonic v0.2.17$/,/^---$/p' RELEASE_NOTES.md)"
```

Windows: 0.2.17 has no Windows ZIP unless `pack-zip.ps1` runs on a Windows
machine from the same commit. Say "Linux only" in the release if it doesn't.

## 4. Publish (yours)

```bash
git push origin Dev v0.2.17
git push origin --tags        # optional: the older tags (v0.2.7.4 … v0.2.16) exist only on this machine
gh release create v0.2.17 \
  dist/VibeSuperTonic-0.2.17-linux-x64.tar.gz \
  dist/VibeSuperTonic-0.2.17-x86_64.AppImage \
  --title "VibeSuperTonic 0.2.17" \
  --notes "$(sed -n '/^# VibeSuperTonic v0.2.17$/,/^---$/p' RELEASE_NOTES.md | sed '1d;$d')"
```

This is the **first Linux release on GitHub**: Releases held only v0.1.0 and
v0.2.0 on 2026-09-26. The README's AppImage and tarball instructions point
there. **Never replace the tarball under the same name**: Flathub pins its
SHA-256.

## 5. The snap to stable

The stable-grade snap is built by CI from the tag, installed and checked under
real snapd (`--test-install`), and uploaded to `edge`. The CI credential can only
reach `edge`, so promotion is a person's command:

```bash
gh workflow run build.yml --ref v0.2.17 -f snap_grade=stable -f publish_edge=true
gh run watch                                    # the snap job's log names the new revision
sudo snap refresh vibesupertonic --edge         # try that revision on this machine first
snapcraft release vibesupertonic <revision> candidate
# live with it for a day or two, then:
snapcraft release vibesupertonic <revision> stable
```

Then drop `--edge` from the README's install line and its badge.

### The store listing, in the snapcraft.io dashboard

The title, summary, description, icon, licence (`MIT AND GPL-3.0-or-later`),
website, source and issue links already come from the snap. What the dashboard
needs by hand:

- **Categories:** Utilities (primary), Productivity. There is no Accessibility
  category on the Snap Store.
- **Screenshots:** the five from step 1, `reader.png` first.
- **Contact:** the GitHub issues page.
- **A note in the description, if the dashboard lets you add one:** "The voice
  models (about 380 MB) download on first run from Hugging Face, after you accept
  their licence (OpenRAIL-M). Nothing you read leaves your computer. Connecting
  the hotkeys and the screen reader is one command in a terminal, which the
  Status tab shows you."

## 5b. AppImageHub (the AppImage catalog)

Needs the AppImage on GitHub Releases (step 4). It then takes one file:

1. Open https://github.com/AppImage/appimage.github.io/new/master/data, name the
   file `VibeSuperTonic`, and put one line in it:
   `https://github.com/Hananel-Hazan/VibeSuperTonic`
2. Open the pull request. Its CI downloads the newest AppImage from Releases,
   runs it on the oldest supported Ubuntu LTS under X11 and takes a screenshot.
   The AppImage now carries the store listing (`pack-appimage.sh` says "store
   listing validates"), which is where the catalog takes the description and
   screenshots from. **This is the first pack with that step**, since CI does
   not build the AppImage; if it fails, the pack stops before any artifact.
3. Once it's listed, point the README's AppImage badge at
   `https://appimage.github.io/VibeSuperTonic/` instead of `#appimage`, and
   replace the "Once 0.2.17 is out…" sentence with the link.

## 5c. GitHub Sponsors

`.github/FUNDING.yml` is ready, but the button appears only after you set up a
profile at https://github.com/sponsors (payout account and a tax form; for a
non-US resident that's the W-8BEN). Until then the README's Sponsor badge leads
to your profile page. Ask an accountant about registering the income in Israel.

## 6. Flathub

**Checked 2026-09-26: we are not on Flathub, and the current manifest cannot
be accepted.** Flathub's requirements say "All source available submissions must
be built entirely from source code", with exceptions only "to well-known vendors
on a case-by-case basis". This manifest installs the prebuilt tarball, so it
would be rejected. What a submission needs is a second manifest that builds from
source, offline, on Flathub's servers:

- the .NET 10 SDK extension, `org.freedesktop.Sdk.Extension.dotnet10` for the
  25.08 runtime (it exists on Flathub);
- every NuGet package as a pinned source, generated by
  `flatpak-dotnet-generator.py` (flatpak-builder-tools), including the NativeAOT
  compiler package for `vst-ctl`;
- espeak-ng as its own module, at the commit `build-espeak.sh` pins, with the
  same pruned dictionaries;
- the composition `pack-tar.sh` does, as build commands.

The tarball manifest stays: CI's Flatpak job, the AppImage and the snap all use
it. Until the source build exists, skip steps 1-5 below.

1. Screenshots in the metainfo (step 1) and the tarball published (step 4).
2. Generate the manifest pinned to the published tarball:

```bash
bash build/pack-flatpak.sh -v 0.2.17 --manifest-only \
  https://github.com/Hananel-Hazan/VibeSuperTonic/releases/download/v0.2.17/VibeSuperTonic-0.2.17-linux-x64.tar.gz
```

3. Fork `flathub/flathub`, branch off `new-pr`, add the manifest, and open the pull
   request against `new-pr`. Suggested description:

> **VibeSuperTonic**: text to speech for the Linux desktop. Select text, press a
> key, and a neural voice reads it aloud. It runs entirely on the user's machine
> and is also a Speech Dispatcher voice, so Orca can use it. Accessibility,
> Utility.
>
> I am the upstream developer. Source: MIT; the package is GPL-3.0-or-later as a
> whole because it bundles espeak-ng (licence and source offer in
> `LICENSE-PHONEMIZER.txt`).

4. Answers to the questions reviewers are likely to ask:

| Question | Answer |
| --- | --- |
| `--share=network`? | The voice models download on first run from Hugging Face, after the user accepts their licence. Shipping them would skip that acceptance. Nothing the user reads is sent anywhere. |
| `--device=dri`? | Optional GPU inference for the neural model. It falls back to the CPU. |
| `--talk-name=org.kde.StatusNotifierWatcher`? | The tray icon. |
| Why no filesystem or host access for hotkeys and speech-dispatcher? | Both are the desktop's configuration. A script the package ships (`sandbox-setup.sh`) is run on the host once, by the user, instead of asking the sandbox for write access to `~/.config`. |

5. Once it's live, point the README's Flathub badge at
   `https://flathub.org/apps/io.github.hananel_hazan.VibeSuperTonic` instead of
   `#flatpak`, and drop the small "coming soon" badge.
6. Once it's merged, the app gets its own repository,
   `flathub/io.github.hananel_hazan.VibeSuperTonic`, and each release is a PR
   there that bumps the URL and hash.

## Still open, not blocking this release

- Fixed in 0.2.17, and each still needs its desktop proof on a revision that
  carries it: the hidden menu entry after a logout (re-run `sandbox-setup.sh
  bind`, log out and in, press Ctrl+`); the refresh (step 2b); and the hotkey
  after a reboot, pressed before any window opens (STORE-SUBMISSION's checklist).
  The 2026-09-27 re-login on revision 5 tested none of them: it is what found
  the third.
- The Flatpak on a real desktop: tray, selection capture on Plasma/Wayland, Orca.
- X11 selection capture by a daemon that outlived a logout (handoff item 4).

# Releasing

How a version goes from Dev to every place users get it. `X.Y.Z` stands for
the version being released. How the store packages are built, what each store
requires, and where each submission stands are in
[STORE-SUBMISSION.md](STORE-SUBMISSION.md).

**Rules that hold for every step:**

- **Pack, verify and tag in one sitting** (CLAUDE.md). A release that is not
  tagged cannot be found again, and a tag must name the commit the artifacts
  were packed from.
- **No git work between Friday sunset in Israel and Saturday nightfall in the
  mainland US.** Prepare instead; commit with `sc`, never `git commit`.
- **Never replace a published tarball under the same name.** Flathub pins its
  SHA-256, and so does the AUR package.
- **Merging to `main` publishes the snap to `edge`**: the snap job uploads on
  every push to `main`. Do not have two publishing runs in flight, because the
  one that finishes last takes `edge`, even when it carries older code.
- **Promoting a snap to `candidate` or `stable` is a person's decision.** CI's
  credential reaches `edge` only.

## 1. Settle the version and prepare the files

Ask which version (CLAUDE.md, "Before you run it: settle the version"). Then,
all on Dev:

| File | What it must say |
| --- | --- |
| `Directory.Build.props` | `<VstVersion>` is the version being built (it is bumped when the version's work begins) |
| `RELEASE_NOTES.md` | an `# VibeSuperTonic vX.Y.Z` entry at the top, ending with `---`: what a user will notice, what it costs, and what was not tested. The tag message and the GitHub release body are cut from it |
| `build/desktop/…metainfo.xml` | a `<release version="X.Y.Z" date="…">` entry whose date is the day it is packed, and every screenshot URL pinned to `vX.Y.Z`. The Flathub linter refuses a screenshot URL that names another tag |
| `README.md`, `CLAUDE.md` | the version table and any claim the release changes |

Every claim in the notes is checked against the code or a recorded
measurement. A limit that was measured goes in the notes next to the feature
it limits.

Validate the listing: `appstreamcli validate --no-net build/desktop/io.github.hananel_hazan.VibeSuperTonic.metainfo.xml`.

Run the tests once, in a single process (parallel `dotnet test` runs crash and
look like product bugs):

```bash
dotnet test VibeSuperTonic.linux.slnx -m:1
```

Commit with `sc -a "…"`. The pack is built from this commit, so nothing may
change after it that ends up inside an artifact.

### Screenshots

Needed when a tab's look changes. Take them from the snap in the default theme,
window only, about 1200×800, with nothing personal on screen. The Reader shows
whatever was read last, so start by reading the demo text:

```bash
vibesupertonic.ctl speak "$(cat samples/twenty-thousand-leagues.txt)"
vibesupertonic.ctl pause           # freezes the highlight mid-sentence
spectacle -b -n -a -d 3000 -o docs/screenshots/reader.png     # then click the window; repeat per file
vibesupertonic.ctl stop
```

| File | Tab | What it shows |
| --- | --- | --- |
| `reader.png` | Reader | the Verne text with a word highlighted (the default screenshot) |
| `voices.png` | Voices | the catalog, with a licence visible |
| `tune.png` | Tune | the speed and quality controls |
| `status.png` | Status | a healthy install (green rows) |
| `pronunciations.png` | Pronunciations | one or two example rules, e.g. `Tcl` → `tickle` |

The URLs in the metainfo name the tag, so they start working when the tag is
pushed and never move afterwards, which is what Flathub asks for.

## 2. Pack

```bash
bash build/build-espeak.sh        # only if pack-tar.sh refuses the payload (the pin moved)
bash build/pack-tar.sh -v X.Y.Z
bash build/pack-appimage.sh -v X.Y.Z
bash build/pack-flatpak.sh -v X.Y.Z      # add --test-install on a machine you can afford to disturb
ls -la dist/VibeSuperTonic-X.Y.Z-*
```

Each packer runs its own assertions and stops before writing an artifact that
fails one. The snap is built by CI only (see step 5). The Windows ZIP is built
by CI's `build` job, from `build/pack-zip.ps1`.

`pack-tar.sh` empties `dist/build-linux/`, which is where `pack-appimage.sh`
keeps the AppImage runtime it checks against a SHA-256 pin. If that pin cannot
be fetched or no longer matches because upstream rebuilt the runtime, the pinned
bytes are the first 944,632 bytes of any earlier AppImage (SHA-256 starting
`156f4bdb`). Putting them back is safe; changing the pin is the maintainer's
decision.

`--test-install` on the Flatpak packer installs the bundle on the machine it
runs on. On a machine whose user runs the AppImage day to day, restore that
arrangement afterwards: hotkeys bound to the AppImage's `vst-ctl`, the Speech
Dispatcher module pointing at the AppImage's `vst-speechd` symlink, and the
AppImage daemon running.

Verify before tagging:

```bash
mkdir -p /tmp/vst-verify && tar -xzf dist/VibeSuperTonic-X.Y.Z-linux-x64.tar.gz -C /tmp/vst-verify
bash build/smoke-test.sh /tmp/vst-verify/VibeSuperTonic
```

and open the AppImage once.

## 3. Tag

```bash
git tag -a vX.Y.Z -m "$(sed -n '/^# VibeSuperTonic vX.Y.Z$/,/^---$/p' RELEASE_NOTES.md)"
```

Check that the tag points at the commit the artifacts were packed from. If any
file inside an artifact changes afterwards, repack and move the tag before
anything is pushed. A tag that has been pushed is never moved.

## 4. Push, and let CI check it

```bash
git push origin Dev vX.Y.Z
```

Push the tag together with Dev, because the screenshot URLs in the metainfo
return 404 until the tag exists on GitHub and the Flathub job fails on a
missing one. Wait for every job of the `build` workflow on the push to be green
(`build`, `linux`, `pack`, `smoke`, `speechd`, `parity`, `snap`, `flatpak`) and for
the `release` workflow. The `flathub` workflow runs on a push only when packaging
files changed, which a metainfo edit is not, so run it by hand and wait for it,
because its linter is what checks the screenshot URLs against the tag:
`gh workflow run flathub.yml --ref Dev`.

## 5. Merge to `main`

Open a pull request Dev → `main` and merge it. This publishes the snap to
`edge` as a `devel`-grade build, which `stable` refuses. Wait for that run to
finish, then build the stable-grade snap from the tag:

```bash
gh workflow run build.yml --ref vX.Y.Z -f snap_grade=stable -f publish_edge=true
gh run watch                       # the snap job's log names the new revision
```

## 6. The GitHub release

Pushing the tag starts the `release` workflow
([release.yml](../.github/workflows/release.yml)), which builds the Windows ZIP
and creates the release with that one asset and generated notes. Finish it by
hand: replace the notes with the entry from `RELEASE_NOTES.md`, and add the
tarball and the AppImage from step 2 and, from the tag's CI run
(`gh run download <run-id>`), the Flatpak (`linux-flatpak`) and the stable-grade
snap (`linux-snap`, from the manual run in step 5).

```bash
gh release edit vX.Y.Z --notes "$(sed -n '/^# VibeSuperTonic vX.Y.Z$/,/^---$/p' RELEASE_NOTES.md | sed '1d;$d')"
gh release upload vX.Y.Z \
  dist/VibeSuperTonic-X.Y.Z-linux-x64.tar.gz \
  dist/VibeSuperTonic-X.Y.Z-x86_64.AppImage \
  <the Flatpak and the snap from CI>
```

The tarball is what every other package is built from, so it is the one that
must never change once published.

## 7. The snap to stable

Try the new revision on a machine first (`sudo snap refresh vibesupertonic --edge`),
then promote it. Promote the stable-grade revision from step 5, which is the one
byte-identical to the release's snap asset, and never the `devel` build the merge
produced:

```bash
snapcraft release vibesupertonic <revision> candidate
# live with it for a day or two, then:
snapcraft release vibesupertonic <revision> stable
```

### The store listing in the snapcraft.io dashboard

The title, summary, description, icon, licence (`MIT AND GPL-3.0-or-later`),
website, source and issue links come from the snap. The dashboard needs by hand:

- **Categories.** There is no Accessibility category on the Snap Store; see
  [STORE-SUBMISSION.md](STORE-SUBMISSION.md) for what is set.
- **Screenshots:** the five from step 1, `reader.png` first.
- **Contact:** the GitHub issues page.
- **A note in the description, if the dashboard allows one:** "The voice models
  (about 380 MB) download on first run from Hugging Face, after you accept their
  licence (OpenRAIL-M). Nothing you read leaves your computer. Connecting the
  hotkeys and the screen reader is one command in a terminal, which the Status
  tab shows you."

## 8. Other channels

- **AppImageHub** lists the project from `https://github.com/Hananel-Hazan/VibeSuperTonic`
  (AppImage/appimage.github.io#10295, merged). Its CI takes the newest AppImage
  from Releases, so a release needs nothing more. The AppImage carries the store
  listing (`pack-appimage.sh` validates it), which is where the catalog takes the
  description and screenshots from.
- **Flathub** is a pull request the maintainer writes by hand, and a repository
  of its own once merged; each release is a pull request there. See
  [STORE-SUBMISSION.md](STORE-SUBMISSION.md), "Flathub".
- **AUR and winget** are drafted in `build/aur/` and `build/winget/` and not yet
  submitted. When they are, each release bumps the version, the hashes, and for
  the AUR `.SRCINFO` (`makepkg --printsrcinfo`).
- **GitHub Sponsors.** `.github/FUNDING.yml` is ready; the button appears once
  the profile at https://github.com/sponsors exists (payout account and a tax
  form, the W-8BEN for a non-US resident). Registering the income in Israel is a
  question for an accountant.

## 9. After the release

Record in `CLAUDE.md`'s version table what shipped, its tag and commit, and the
snap revision. Update the state paragraph in
[STORE-SUBMISSION.md](STORE-SUBMISSION.md), "Where each store stands". Delete
old `.bak` AppImages and any `dist/` artifact of a version that was never
released.

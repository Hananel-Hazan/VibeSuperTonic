# Handoff — snap and Flatpak (0.2.17), for 2026-09-28

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`. It is **ahead of `Dev`**
(`78b9b56`) by `31d0d94` (the Flathub source build), `c8deba3`, `bf71b44` and
`467913a` (its CI fixes), and this handoff. No PR. Read these first:

- `CLAUDE.md`: "The store packages", and its new Flathub paragraph.
- [STORE-SUBMISSION.md](STORE-SUBMISSION.md): its checklist is the live state.
- [RELEASE-0.2.17.md](RELEASE-0.2.17.md): the runbook.

[The 09-27 handoff](HANDOFF-2026-09-27-store.md) is still accurate on the snap,
on the user's machine and on the tooling. This one adds only what changed.

## Where it stands

- **`build.yml` run 85 on `31d0d94` is green.** That covers every job: pack, smoke,
  speechd, parity, snap with `--test-install`, flatpak, and Windows.
- **The `flathub` workflow is new.** It runs on pushes to `main`, `Dev` and
  `claude/**` that touch what it builds from: `build/flatpak/**`, the packers,
  `build-espeak.sh`, the `check-*.sh` scripts, any `.csproj`, and
  `Directory.Build.*`. It also runs on manual dispatch.
  - Run 1 (`31d0d94`) went red in its first real step. See the table below.
  - Run 2 (`c8deba3`, id 36319430193) was red, and got much further. All four
    publishes succeeded **offline** in the SDK, NativeAOT included. It then
    failed in espeak-ng's configure step (see `bf71b44` below). An earlier
    version of this handoff said run 2 showed the package list matching. It
    did not: the refusing step was skipped only because the build had failed.
  - Run 3 (`25f8231`, id 36320821518): **the whole build passed**, including
    `--test-install`. The only failing step was the refusal of a stale package
    list, and it was right to fail (see `467913a` below).
  - Run 4 (`467913a`) was started by the push, and should be the first fully
    green run. **Read its result first.**
- The user's Kubuntu still runs **snap revision 10** from `edge`. Nothing new
  was published. Merging to `main` still publishes to `edge`.

## What happened on 2026-09-27, after the 09-27 handoff

| Commit | What |
| --- | --- |
| `31d0d94` | **Flathub from source.** New manifest `build/flatpak/…source.yml.in`: the `dotnet10` SDK extension, 34 NuGet packages pinned by SHA-512 in `nuget-sources.json`, espeak-ng at `build-espeak.sh`'s pin, and `pack-tar.sh` run inside the build, so every packer assertion still runs. `pack-flatpak.sh --from-source` builds it; `--flathub TAG` writes the submission to `dist/flathub/`. The new `flathub.yml` workflow regenerates the package list **inside the SDK** and refuses a committed file that differs, then builds, installs and runs `--test-install`. Rehearsed locally against a feed of only the pinned packages. |
| `c8deba3` | Run 1 died at `gen-nuget-sources.py --in-sdk`, before any build: `UnauthorizedAccessException: Access to the path '/github/home/.dotnet' is denied`. dotnet's first run inside `flatpak run` tries to create `~/.dotnet`. In the CI container `HOME` is `/github/home`, owned by the runner's user, while the job runs as root, and bwrap drops the capability root would need to write there anyway. The restore now gets `HOME` and `DOTNET_CLI_HOME` under its own temporary directory. The manifest's build already set `DOTNET_CLI_HOME` to the build directory, so it was not affected. |
| `bf71b44` | Run 2's failure, twice over. espeak-ng's `cmake/deps.cmake` clones libsonic at configure time whenever none is installed, even with `USE_LIBSONIC=OFF`, where nothing links it. Offline, the clone fails. The manifest now carries sonic as a git source, and `build-espeak.sh --sonic-src` passes it to CMake through `FETCHCONTENT_SOURCE_DIR_SONIC-GIT`, refusing any commit but the one `deps.cmake` names. The manifest's build command also gained `set -e`: without it, `pack-tar.sh` ran on after the failure, and the log blamed the missing payload instead of the cause. Seen locally with the network cut off: without the flag, CI's clone failure; with it, a full pass with all 35 voices; with sonic one commit off, refused. |
| `467913a` | Run 3's one red step. The committed package list came from a workstation SDK (10.0.112, runtime packs 10.0.12). Flathub's `dotnet10//25.08` restores **10.0.8**, and brings its own apphost, so `Microsoft.NETCore.App.Host` drops out: 33 packages, not 34. The new file is exactly what run 3 printed, and the build it fed verified every hash. **So the Flathub build ships .NET runtime 10.0.8, while the tarball, snap and AppImage ship 10.0.12.** Its runtime patches arrive when Flathub updates the extension, not when we bump. |

## What the flathub workflow proved

Run 3 (`25f8231`) is the first full pass of Flathub's build, as Flathub will run
it. Each of these was seen in its log:

1. **Offline build.** Every NuGet package came from `nuget-sources/`, espeak-ng
   and sonic came from the manifest's git sources, and NativeAOT linked with the
   SDK's gcc.
2. **`pack-tar.sh` inside the SDK, with every assertion.** All four versions
   are 0.2.17. `vst-ctl` and `vst-speechd` are native. No models. The catalog
   is pinned and hashed. No ICU. espeak-ng: 34 voices phonemise, 31
   dictionaries. `INIT` gets 299. The speechd installer refuses a voiceless
   install. No GPU provider, and ORT pinned by SHA-512. glibc floor 2.34.
   Budgets: 61 MiB of 75, espeak 14 MiB of 18, `vst-ctl` 5 ms.
3. **`--test-install`.** The installed bundle reports 0.2.17, and its store
   lands under `~/.var/app/…/data/vibesupertonic`. The module answers `INIT`
   inside the sandbox. The deployment's `vst-ctl` finds the shared socket.
   `sandbox-setup.sh` refuses to run inside.

The package-list check stays a **standing check**. When Flathub moves the
`dotnet10` extension to a new runtime, the job prints the regenerated file,
builds with it anyway, and fails at the end. Commit the printed file as
`build/flatpak/nuget-sources.json`, as `467913a` did. Any other failure is a
real finding about the source build: fix the manifest or the packer, and never
add a network grant.

## Open, in rough order

1. **Confirm run 4 of `flathub.yml` is green** (above). Then merge this branch
   into `Dev`, and check that `build.yml` is still green there. `bf71b44`
   touched `build-espeak.sh`, which the `pack` job runs without `--sonic-src`,
   so it should be unaffected. A manual `build.yml` run is the check.
2. **The release itself**, per [RELEASE-0.2.17.md](RELEASE-0.2.17.md). The
   09-27 handoff's item 1 still applies word for word, including the notes check
   and "pack, verify, tag in one sitting". **The user pushes the tag and
   publishes.**
3. **Snap listing on snapcraft.io**, then `--grade stable`, then candidate, then
   stable (runbook step 5). Promotion is a manual `snapcraft release`.
4. **Flathub submission** (runbook step 6). It needs the tag, because
   `--flathub v0.2.17` pins the manifest to the tag's commit. Then fork
   `flathub/flathub`, branch from `new-pr`, and open the PR with the manifest and
   `nuget-sources.json`. It also needs the user's GitHub account. No other
   account is needed.
5. **The Flatpak desktop checklist** in STORE-SUBMISSION: tray, Orca, and
   selection capture on Plasma/Wayland. It needs a real desktop, so a cloud
   session cannot do it.
6. Unchanged from 09-27:
   - the snap's 108 ms hotkey cost (accepted);
   - Windows `PipelineLatencyTests` flakes (do not loosen them);
   - the speechd install hang's cause (unproven; the rollback makes it safe).

## Tooling notes

The 09-27 notes still apply, plus these:

- **flatpak is not installed in the cloud container**, and `gen-nuget-sources.py
  --in-sdk` needs it. Without `--in-sdk` the script runs the machine's own
  `dotnet`, which pins that SDK's runtime packs. That is right only when it is the
  same SDK version as the extension's. **The CI regeneration is the one to trust.**
- The flathub workflow's container is
  `ghcr.io/flathub-infra/flatpak-github-actions:freedesktop-25.08`, run
  `--privileged`. `HOME` there belongs to another uid, so anything that writes
  under `$HOME` from inside `flatpak run` needs its own directory, as
  `c8deba3` did.

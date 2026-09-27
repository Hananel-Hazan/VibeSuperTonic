# Handoff — snap and Flatpak (0.2.17), for 2026-09-28

Branch `claude/kubuntu-ubuntu-store-submission-gzfqcb`. It is **four commits ahead
of `Dev`** (`78b9b56`): `31d0d94` (the Flathub source build), then `c8deba3` and
`bf71b44` (its CI fixes), then this handoff. No PR. Read these first:

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
  - Run 2 (`c8deba3`, id 36319430193) was red, and got much further. The
    package list regenerated inside the SDK **matches** the committed one. All
    four publishes succeeded **offline** in the SDK, NativeAOT included. It then
    failed in espeak-ng's configure step (see `bf71b44` below).
  - Run 3 (`bf71b44`) was started by the push. **Read its result first.**
- The user's Kubuntu still runs **snap revision 10** from `edge`. Nothing new
  was published. Merging to `main` still publishes to `edge`.

## What happened on 2026-09-27, after the 09-27 handoff

| Commit | What |
| --- | --- |
| `31d0d94` | **Flathub from source.** New manifest `build/flatpak/…source.yml.in`: the `dotnet10` SDK extension, 34 NuGet packages pinned by SHA-512 in `nuget-sources.json`, espeak-ng at `build-espeak.sh`'s pin, and `pack-tar.sh` run inside the build, so every packer assertion still runs. `pack-flatpak.sh --from-source` builds it; `--flathub TAG` writes the submission to `dist/flathub/`. The new `flathub.yml` workflow regenerates the package list **inside the SDK** and refuses a committed file that differs, then builds, installs and runs `--test-install`. Rehearsed locally against a feed of only the pinned packages. |
| `c8deba3` | Run 1 died at `gen-nuget-sources.py --in-sdk`, before any build: `UnauthorizedAccessException: Access to the path '/github/home/.dotnet' is denied`. dotnet's first run inside `flatpak run` tries to create `~/.dotnet`. In the CI container `HOME` is `/github/home`, owned by the runner's user, while the job runs as root, and bwrap drops the capability root would need to write there anyway. The restore now gets `HOME` and `DOTNET_CLI_HOME` under its own temporary directory. The manifest's build already set `DOTNET_CLI_HOME` to the build directory, so it was not affected. |
| `bf71b44` | Run 2's failure, twice over. espeak-ng's `cmake/deps.cmake` clones libsonic at configure time whenever none is installed, even with `USE_LIBSONIC=OFF`, where nothing links it. Offline, the clone fails. The manifest now carries sonic as a git source, and `build-espeak.sh --sonic-src` passes it to CMake through `FETCHCONTENT_SOURCE_DIR_SONIC-GIT`, refusing any commit but the one `deps.cmake` names. The manifest's build command also gained `set -e`: without it, `pack-tar.sh` ran on after the failure, and the log blamed the missing payload instead of the cause. Seen locally with the network cut off: without the flag, CI's clone failure; with it, a full pass with all 35 voices; with sonic one commit off, refused. |

## What the flathub workflow still has to prove

Run 2 settled the first item and half of the second. The rest has not yet been
seen in CI:

1. **Settled by run 2.** The committed `nuget-sources.json` matches what the
   SDK's own dotnet restores. It stays a standing check: if the extension carries another SDK, the runtime packs are a
   different version. The job then prints the regenerated file, still builds
   with it so that the questions below get answered, and fails at the end.
   That failure is correct. Commit the printed file as
   `build/flatpak/nuget-sources.json`.
2. **The offline build**: `flatpak-builder` with no network. The .NET half is
   **settled by run 2**: the restore used only `nuget-sources/`, and NativeAOT
   (`vst-ctl`, `vst-speechd`) linked with the SDK's gcc. Still open is
   espeak-ng, which `bf71b44` should fix. If something else in its CMake reaches
   for the network, it gets the same treatment: a pinned manifest source, never
   a network grant.
3. **`pack-tar.sh` inside the build**: all its assertions, including espeak-ng
   phonemising the catalog's voices and the budgets, now in the SDK sandbox.
4. **`--test-install` on the result**: the store under `~/.var/app`, `INIT`
   answered inside the sandbox, and the host-side `vst-ctl` looking in the shared
   runtime directory.

A failure in 2–4 is a real finding about the source build. Fix the manifest or
the packer. Do not loosen the check.

## Open, in rough order

1. **Get `flathub.yml` green** (above). Then merge this branch into `Dev`, and
   check that `build.yml` is still green there.
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

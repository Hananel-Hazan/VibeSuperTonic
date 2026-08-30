# VibeSuperTonic — Claude rules

## Shipping / building / releasing

When the user asks for a build, a release, an executable to test, an archive to
hand off — or you have changes you want to ship to the user — use the canonical
packaging script **for the platform they mean**. Ask which one if it is not
obvious; the project ships two products from one repository.

| Target | Script | Output |
| --- | --- | --- |
| Windows | [build/pack-zip.ps1](build/pack-zip.ps1) | `dist/VibeSuperTonic-<version>-win.zip` |
| Linux | [build/pack-tar.sh](build/pack-tar.sh) | `dist/VibeSuperTonic-<version>-linux-x64.tar.gz` |
| Linux, AppImage | [build/pack-appimage.sh](build/pack-appimage.sh) | `dist/VibeSuperTonic-<version>-x86_64.AppImage` |

**Linux ships two artifacts, and the tarball is the canonical one.** Decided
2026-08-24. The AppImage is built *from the tree pack-tar.sh composed* — it
publishes nothing itself — so all six of the tarball's assertions are inherited
rather than copied. A run that produces both is:

```bash
bash build/pack-tar.sh -v <X.Y.Z> && bash build/pack-appimage.sh -v <X.Y.Z>
```

In that order, and a failed tarball means no AppImage. `pack-appimage.sh` refuses
to run against a composed tree whose binaries report a different version, so the
two artifacts of one release cannot come from two builds.

**Since S4 (2026-08-28) the archive also carries a Speech Dispatcher module** —
`vst-speechd` and `speechd-install.sh` beside `install.sh` — which is what makes
these voices reachable from Orca and anything else that speaks on Linux. The
installer is a shipped file in [build/](build/speechd-install.sh) rather than a
heredoc inside the packer, deliberately: it can be driven against a private
speech-dispatcher that way, and
[spike/speechd-s4-install/](spike/speechd-s4-install/README.md) is what does it.
**Never test it against your own `~/.config/speech-dispatcher`** — a mistake
there is somebody's screen reader going quiet, and the harness redirects
`XDG_CONFIG_HOME` precisely so it does not have to.

**Since P5 (2026-08-27) a Linux release needs one step before either**, because
the archive now contains a phonemiser:

```bash
bash build/build-espeak.sh          # ~2 min, and only when the payload is stale
```

It clones espeak-ng at the commit piper pins, builds it with audio and
time-stretch turned off, prunes 118 dictionaries to the ~31 the catalog and P1's
corpus need, and composes `build/espeak-out/espeak` — the payload `pack-tar.sh`
copies in. **The packer refuses to run without it** and refuses a payload whose
recorded pin disagrees with the script's, so a stale build cannot ship. It is a
separate script rather than a step inside the packer because it clones a
repository and compiles dictionaries: two minutes and a network fetch that have
nothing to do with a release run, and that only matter when the pin moves.

**From that release onward the archive as a whole is GPL-3.0-or-later**, because
it distributes espeak-ng. The repository's own source stays MIT — MIT is
GPL-compatible and no `.cs` file changes — and `LICENSE-PHONEMIZER.txt`, written
by the packer, is where the terms and the source offer live. Releases up to and
including 0.2.10 — the newest one that was ever published — contain no espeak-ng
and are unaffected; that is worth saying in 0.2.11's notes, because "the project
became GPL" is what a reader will otherwise conclude retroactively.

### What the tests defend, and what they do not

[docs/TESTING-PLAN.md](docs/TESTING-PLAN.md), written 2026-08-27, audits the
checks against four properties — **safe, slim, fast, valid** — and is worth
reading before adding a test, because it says where a check belongs and why.

**Since 2026-08-27 CI packs and smoke-tests**, which changes how a green run
should be read. Two jobs were added to [build.yml](.github/workflows/build.yml):

- **`pack`** builds espeak-ng (cached on the pin) and runs `pack-tar.sh`, so all
  nine assertions run on every push rather than only when a human packs.
- **`smoke`** extracts that tarball into a bare `ubuntu:22.04` container — the
  oldest distro the glibc floor claims — and starts the daemon there with no
  .NET, no display, no audio device, no models and no espeak-ng installed.
  [build/smoke-test.sh](build/smoke-test.sh) is the same script you can run
  locally against any extracted release.

**Nothing is installed into that container on purpose.** If the smoke job ever
needs `libicu`, `openssl` or `libespeak-ng` added to it, that is a finding about
the archive and belongs in `INSTALL.txt` — it is not a fix to the workflow.

Its first run found that **the daemon could not start on a fresh install**: a
routing decision read `SampleRate`, which loads the model, from the daemon's
startup path with no models present. 0.2.10 was fine; the tree packed as the
withdrawn `0.2.11` was not.

The rule the existing checks are built on, and the one to keep: **a check that
has never been observed failing is not evidence.** Every packer assertion was
sabotaged on the day it was written. Do that for the next one too.

Do **not** use ad-hoc `dotnet build` or `dotnet publish` to produce shippable
artifacts, on either platform. The Windows script is canonical because it:

- Publishes Engine x64 + x86 (framework-dependent, COM in-proc) AND Launcher
  x64 (self-contained single-file). Skipping x86 silently breaks 32-bit SAPI
  hosts (Balabolka, some Narrator paths). Skipping self-contained means the
  user needs .NET preinstalled.
- Composes the portable folder layout (`engine/x64`, `engine/x86`,
  `models/onnx`, `models/voice_styles`, `samples/`, manifest, license, install
  notes). Anything that hand-rolls this layout will drift.
- Strips `*.pdb` from `engine/` to keep the ZIP small.
- Generates `LICENSE-MODELS.txt` (the OpenRAIL-M acceptance note the user
  agrees to when models download from Hugging Face) and `INSTALL.txt`.

### The Linux packer — what it asserts, and why

Written 2026-08-22. `bash build/pack-tar.sh [-v X.Y.Z]`. It publishes all three
binaries into freshly emptied directories, composes the portable layout
(binaries at the root, `models/` and `data/` beside them — no `engine/` split,
because Linux has no COM bitness problem), writes `install.sh`, `uninstall.sh`,
`INSTALL.txt` and `LICENSE-MODELS.txt`, copies `models-manifest.json` and
`install-gpu.sh` to the root, and archives it.

Ten assertions run against the **composed tree**, not the build outputs, and
each exists because the failure it catches is silent:

- **All three binaries report the same version**, asked of the shipped files via
  `--version`. Guards a stale binary surviving in an output folder and a UI
  shipping that disagrees with its daemon.
- **The shipped `vst-ctl` is native, not a managed apphost.** `file` reports
  *byte-identical* output for both — verified — so the check is the absence of a
  companion `vst-ctl.dll` plus a size floor. A managed apphost is ~78 KB and
  works perfectly while costing ~100 ms on every hotkey press; the AOT binary is
  ~4 MB. A `grep ELF` check cannot tell them apart. CI checks the `.dll` too, but
  not the size floor — and it checks its own publish output, never the composed
  tree, which is the difference this assertion exists for.
- **No models in the archive.** They download on first run behind the
  OpenRAIL-M acceptance; shipping them would make that screen a lie.
- **No `libonnxruntime_providers_cuda.so` in the archive** (Phase 8b). It is
  330 MB against a 52 MB tarball and arrives by default with the GPU package the
  backend links; `Directory.Build.targets` removes it at publish. The first
  version of that removal lived in the backend csproj, looked correct and built
  clean — a target in a project governs *that project's* output, not the publish
  of the daemon referencing it — and this assertion is what caught the 330 MB.
- **`install-gpu.sh` fetches the same ONNX Runtime version the daemon links.**
  The provider library and `libonnxruntime.so` are one build split across two
  files; a drift between them fails on the user's machine and nowhere else, and
  this is the only place both numbers are visible at once.
- **The glibc floor has not risen above 2.34** (Phase 9). Measured across every
  ELF in the tree: `vst-ctl` needs `GLIBC_2.34` and nothing else needs above
  2.27, because `vst-ctl` is the only binary compiled on the build machine — the
  runtime, Skia and ONNX Runtime all arrive prebuilt from NuGet. So the floor
  belongs to the toolchain rather than to this repository, it can rise under a
  distro upgrade with every test still green, and the symptom is a user on a
  supported distro told `GLIBC_2.39 not found` by a binary that ran yesterday.
  2.34 is Ubuntu 22.04+, Debian 12+, RHEL 9+.

- **The Piper voice catalog ships, and it is checked** (P4, 2026-08-25).
  `piper-voices.json` is copied to the archive root beside `models-manifest.json`
  — it is what the Voices tab reads, and without it an install can list the
  voices it has and offer none. Assertion 3b runs
  [check-piper-catalog.py](build/check-piper-catalog.py) over the shipped file
  and refuses an unhashed voice (it would download unverified, because an empty
  `sha256` means "nothing to check"), a non-https URL, a duplicate id, a filename
  that does not match its voice id, an entry with no licence text (the download
  gate would be empty), or a speaker count that disagrees with its names. It also
  refuses a catalog over 1 MB, which is the shape a generator bug that inlined
  weights would take — a stray `.onnx` is caught by assertion 3, and one enormous
  JSON file is not. Regenerate with
  `python3 build/gen-piper-catalog.py`; it is deterministic for a given revision
  and policy, and **it must stay that way** — a catalog that moves when
  regenerated is one whose per-voice `MODEL_CARD` review no longer describes what
  shipped.

- **The phonemiser is the one we built, and it works** (P5, 2026-08-27). Three
  failures, all silent, all in `espeak/`. *The revision*: nothing rebuilds the
  payload when the pin in [build-espeak.sh](build/build-espeak.sh) moves, so a
  months-old `espeak-out/` composes in without complaint — the packer compares
  the pin against the one the payload recorded. *The link line*: a build that
  picked up `libsonic` or `libpcaudio` because they were installed on the build
  machine produces an archive that works there and nowhere else, so the shipped
  `.so` must declare exactly `libc` and `libm`, and must export
  `espeak_TextToPhonemesWithTerminator` — which does not exist at the 1.52.0 tag
  and whose absence is wrong prosody rather than a failure. *The data*: *a
  missing dictionary makes espeak-ng exit 0, print one line to stderr that
  nothing reads, and return no phonemes* — which reaches a user as a voice they
  downloaded, accepted a licence for, installed and selected, and which then says
  nothing. So that check is behavioural: every espeak voice the catalog names
  phonemises a probe sentence against the **shipped** data, and exit code, stdout
  and stderr all have to be right. Nothing maps between the three names involved,
  because they disagree — `no_NO` is voice `nb` and reads `no_dict`, `es_MX` is
  `es-419` and reads `es_dict`. The catalog carries each voice's `espeakVoice`,
  taken from the model's own config, and `build-espeak.sh` discovers the
  dictionaries by asking espeak which file it failed to open.

- **The Speech Dispatcher module works, and its installer refuses a voiceless
  archive** (S4, 2026-08-28). Assertion 3e runs
  [check-speechd-payload.sh](build/check-speechd-payload.sh) over the composed
  tree. `vst-speechd` is the one binary nobody ever runs by hand: speechd spawns
  it, reads one line and **drops it on anything it does not like** — and a
  dropped module reaches the user as VibeSuperTonic simply missing from their
  screen reader's list, with no error on any screen they can reach. So the check
  is behavioural: the shipped binary must answer `INIT` with `299` *in the
  composed tree*, which is also the only way to prove `espeak/` landed where the
  module looks for it. Then the installer, which is the piece that can silence a
  machine: `--check` must pass and name the module in this tree, it must notice a
  config whose path does not exist (a moved install — trap 13), and a real
  install attempt must **refuse**, because the archive ships no voices and a
  synthesizer with nothing to say is worse than one that is absent. All hermetic
  — no speech-dispatcher, no display, no models — so it runs in CI's bare
  container too. Sabotage it with
  [payload-sabotage.sh](spike/speechd-s4-install/payload-sabotage.sh), which is
  possible *because* the assertion is a script rather than a step inside a
  four-minute pack.

**The AppImage's two new assertions are about one mechanism.** speech-dispatcher
execs a single absolute path with **no arguments** — measured: a binary field
containing a space fails with `Exec of module ... error 2` while speechd still
logs the module as loaded — so an AppImage install points it at a symlink,
`~/.local/bin/vst-speechd`, and AppRun's `$ARGV0` case turns that into the
module. Without that case the symlink falls through to AppRun's default branch
and **opens the window, once per utterance**. `pack-appimage.sh` therefore builds
such a symlink and asks it for both `--version` and `INIT`.

**The optional GPU pack is not the packer's business.** `build/install-gpu.sh`
ships in the archive and fetches ~3.1 GB on request — the CUDA provider from
nuget.org, CUDA and cuDNN from PyPI — because a 52 MB download must not become a
3 GB one for a machine that may have no NVIDIA GPU. Never bundle it. If it
appears in the tree, assertion 4 is what will tell you.

`install.sh` **stops a running daemon before doing anything**, found by
`/proc/<pid>/exe` and never `pkill -f`, and `INSTALL.txt` says to do the same
before a hand-untar. `vibesupertonicd` is long-lived by design, Linux lets you
replace a running executable without complaint, and the result is a new binary
on disk with the old one still serving every hotkey press — and `status`
truthfully reporting the old version.

### Before you run it: settle the version

The script defaults to `<VstVersion>` in
[Directory.Build.props](Directory.Build.props) — the last shipped version,
shared with the Linux packer so one number produces both artifacts. Before
invoking it, **ask the user** whether to bump the version, and propose a bump
based on what changed:

- **Patch** (`0.2.0` → `0.2.1`) — bug fixes, internal refactors, no
  user-visible behavior change.
- **Minor** (`0.2.0` → `0.3.0`) — new features, new SAPI behaviors, new
  Control Panel surfaces, default-behavior changes that the user will notice.
- **Major** (`0.2.0` → `1.0.0`) — breaking changes (registry layout, voice
  id renames, data dir contract changes, settings.json schema breaks).

Check `dist/` for prior ZIPs to confirm the last shipped version. If the user
has not shipped this round of changes before, the last `dist/` filename is the
correct baseline; if they have, infer from the most recent ZIP.

**The three-release sequence settled on 2026-08-24 is spent.** `<VstVersion>` is
`0.2.12`, which is S5's number and is **under development**. `0.2.11` shipped on
2026-08-30 and is tagged `v0.2.11`; the bump followed the same day, which is the
rule below working rather than a coincidence.

| Version | What it is | State |
| --- | --- | --- |
| `0.2.8` | The Linux tarball. Inherited the number Windows had already shipped — the shared-version rule working, not an accident | packed |
| `0.2.9` | The AppImage, beside the tarball — [Phase 9](docs/LINUX-PORT-PLAN.md#phase-9) | **shipped 2026-08-24** |
| `0.2.10` | A portable home that made the hotkeys unbindable, and a remedy for it that could orphan the store | **shipped 2026-08-25**, and the newest release that actually works |
| `0.2.11` | The GPU fix, the fresh-install fix, all of P5, all of Piper P0–P5, a Speech Dispatcher module — [SPEECHD-PLAN.md](docs/SPEECHD-PLAN.md) — and the moved-install startup check | **shipped 2026-08-30**, tagged `v0.2.11` |
| `0.2.12` | Skipped | never cut |
| `0.2.13` | Briefly the number this work carried, 2026-08-28. Reclaimed to `0.2.11` the same day | never cut |
| `0.2.12` | S5, the safety net — the restore test in CI, the latency budget, the archive size budget, the no-audio-with-exit-0 test | **in development** |
| `0.3` | ~~Piper as a second engine~~ — P0–P5 **shipped inside 0.2.11**, so this number is now free for whatever the next feature release turns out to be | undecided |

**A first `0.2.11` was packed and then withdrawn the same day**, and the
reasoning on both sides is worth keeping. (The number was later reclaimed by the
current work — see below. Everything in this paragraph is about the *withdrawn*
artifact, which no longer exists anywhere.) It was cut from `7ed7a67` on purpose: the GPU fix
landed after 0.2.10's artifacts were packed, and by then P5 had put espeak-ng in
the archive and moved the whole thing to GPL-3.0-or-later — which is not
something a user should discover in a patch release. Then
[the new smoke test](build/smoke-test.sh) found that **the daemon could not start
on a fresh install** in that same tree, and the fix for it lives in a tree that
also contains P5. Re-cutting a no-espeak patch release meant a second branch and
a second pack run to ship something that had already been overtaken. Nothing was
published, so it was deleted instead — which is exactly what left the number free
to take again.

**So `0.2.11` carries all of it**: the GPU fallback, the fresh-install fix, P5's
phonemiser, and the Speech Dispatcher module. It is the release where
GPL-3.0-or-later first applies — say so in its notes. Everything up to and
including the shipped `0.2.10` contains no espeak-ng.

**The number went back to `0.2.11` on 2026-08-28, and it was free to take.**
This work carried `0.2.13` for a day. Nothing was ever published as `0.2.11`,
`0.2.12` or `0.2.13` — the withdrawn `0.2.11` was deleted rather than released —
so no artifact anywhere claims those numbers and reusing the first of them
collides with nothing. What it does mean: **`dist/` may still hold a
`0.2.13` tarball from before the renumber, and it is not a release.** Delete it
rather than reasoning about it.

`0.2.12` is the number being built. **Everything after it is a decision to ask
about** — propose a bump from what changed, and use
`AskUserQuestion`.

**`<VstVersion>` names the version being built, not the last one shipped**, and
that rule changed on 2026-08-27 when CI started packing. It was "the version just
shipped", which was fine while the packer only ran by hand — but the `pack` job
runs `pack-tar.sh` with no `-v` on every push, so the default is now a name
applied to real artifacts continuously. Two things follow:

- A default naming the *last shipped* version would have CI produce a tarball
  named after a release that already exists, and a local run with no `-v` would
  overwrite the genuine artifact in `dist/`.
- Bumping it at the *start* of a version's work rather than after its pack means
  every CI artifact is honestly named a pre-release build of the thing being
  built.

So: **bump `<VstVersion>` when a version's work begins**, and a release run still
passes `-v` explicitly. `0.2.10` shipped under the older rule with the bump
committed alongside the work, which is the same thing by accident.

Use `AskUserQuestion` to confirm the version — do not silently pick one. The
version is durable: it embeds in the ZIP filename and is what the user will
reference when triaging field reports.

### Run

After the user confirms the version:

```powershell
.\build\pack-zip.ps1 -Version <X.Y.Z>
```

The script throws on any publish failure (`$ErrorActionPreference = 'Stop'` +
`$LASTEXITCODE` checks per step), so a non-zero exit means the build is
broken and the ZIP is not produced. Surface the failure to the user with the
relevant `dotnet publish` output — don't paper over it.

On success, report the final ZIP path and size to the user. The script prints
both at the end; relay them verbatim.

### Then tag it, in the same sitting

**A release that is not tagged cannot be found again.** Tag the commit the
artifacts were packed from, with that version's release notes as the message:

```bash
git tag -a v<X.Y.Z> -m "$(sed -n '/^# VibeSuperTonic v<X.Y.Z>$/,/^---$/p' RELEASE_NOTES.md)"
# push it when the user asks — tags are theirs to publish
```

This rule exists because the first eight releases were **not** tagged, and on
2026-08-28 they had to be reconstructed from artifact timestamps. Three of the
six landed within a minute of a commit and are trustworthy; three are placed
against gaps of hours to four days and say so in their own tag message. That is
recoverable only because the artifacts were still on disk — the moment one is
deleted, an untagged release becomes a tree nobody can identify. It nearly
happened: a cleanup was about to remove the last two copies of 0.2.9 with no tag
to rebuild from.

So: **pack, verify, tag, in one sitting.** Do not push the tag — the user pushes.

### Do not bypass

If a packaging script appears broken, fix the script — don't run the underlying
`dotnet publish` calls manually as a workaround. The composition step
(strip PDBs, generate license/install txt, archive) is part of what makes the
output shippable. A manual `dotnet publish` produces a `bin/` tree that the
user cannot drop on a fresh machine.

The same applies to the script that does not exist yet: "there is no Linux
packer" is a reason to write one, never a reason to hand-roll a tarball once.

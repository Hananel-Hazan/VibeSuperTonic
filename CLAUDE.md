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

Six assertions run against the **composed tree**, not the build outputs, and
each exists because the failure it catches is silent:

- **All three binaries report the same version**, asked of the shipped files via
  `--version`. Guards a stale binary surviving in an output folder and a UI
  shipping that disagrees with its daemon.
- **The shipped `vst-ctl` is native, not a managed apphost.** `file` reports
  *byte-identical* output for both — verified — so the check is the absence of a
  companion `vst-ctl.dll` plus a size floor. A managed apphost is ~78 KB and
  works perfectly while costing ~100 ms on every hotkey press; the AOT binary is
  ~4 MB. A `grep ELF` check (which is what CI does) cannot tell them apart.
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

**The three-release sequence settled on 2026-08-24 is spent**, and it grew a
fourth entry it did not plan for. `<VstVersion>` is `0.2.10`.

| Version | What it is | State |
| --- | --- | --- |
| `0.2.8` | The Linux tarball. Inherited the number Windows had already shipped — the shared-version rule working, not an accident | packed |
| `0.2.9` | The AppImage, beside the tarball — [Phase 9](docs/LINUX-PORT-PLAN.md#phase-9) | **shipped 2026-08-24** |
| `0.2.10` | What looking harder at 0.2.9 found: a portable home that made the hotkeys unbindable, and a remedy for it that could orphan the store | **decided 2026-08-25**, packs from this tree |
| `0.3` | Piper as a second engine — [PIPER-PLAN.md](docs/PIPER-PLAN.md) | next |

`0.3` is the next number and it is settled. **Everything after it is a decision
to ask about** — propose a bump from what changed, and use `AskUserQuestion`.

After a release ships, update `<VstVersion>` in
[Directory.Build.props](Directory.Build.props) to the version just shipped, so
the default stays truthful for the next run and the Linux packer agrees. `0.2.10`
is the one exception on record and it was the user's call: the bump was committed
with the work rather than after the pack, so that one commit *is* the release.
The rule still holds for everything after it — a release run passes `-v`
explicitly either way, so the default is a safety net rather than an input.

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

### Do not bypass

If a packaging script appears broken, fix the script — don't run the underlying
`dotnet publish` calls manually as a workaround. The composition step
(strip PDBs, generate license/install txt, archive) is part of what makes the
output shippable. A manual `dotnet publish` produces a `bin/` tree that the
user cannot drop on a fresh machine.

The same applies to the script that does not exist yet: "there is no Linux
packer" is a reason to write one, never a reason to hand-roll a tarball once.

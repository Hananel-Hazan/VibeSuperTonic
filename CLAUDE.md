# VibeSuperTonic — Claude rules

## Shipping / building / releasing

When the user asks for a build, a release, an executable to test, an archive to
hand off — or you have changes you want to ship to the user — use the canonical
packaging script **for the platform they mean**. Ask which one if it is not
obvious; the project ships two products from one repository.

| Target | Script | Output |
| --- | --- | --- |
| Windows | [build/pack-zip.ps1](build/pack-zip.ps1) | `dist/VibeSuperTonic-<version>-win.zip` |
| Linux | `build/pack-tar.sh` — **does not exist yet**, see below | `dist/VibeSuperTonic-<version>-linux-x64.tar.gz` |

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

### The Linux packer does not exist yet

`build/pack-tar.sh` is **Phase 7** of
[docs/LINUX-PORT-PLAN.md](docs/LINUX-PORT-PLAN.md), and Phase 6 comes first.
Until it is written there is **no supported way to produce a Linux release** —
so if the user asks for one, say that, and offer to build the packer as the
actual task. Publishing the three binaries by hand and tarring the result is
precisely the ad-hoc route this section forbids: it produces a tree the user
cannot drop on a fresh machine.

What it must do when it is written, all of it settled in the port plan:

- Publish **three** binaries in one run — `vibesupertonicd` (self-contained),
  `vibesupertonic-ui` (self-contained), `vst-ctl` (**NativeAOT**) — and assert
  they report the same version. The guard is against a stale binary surviving in
  an output folder and shipping a UI that disagrees with its daemon.
- Assert the shipped `vst-ctl` is a **native ELF**. A `dotnet build` leaves a
  managed one that works and costs ~100 ms on every hotkey press, so the slow
  one ships silently.
- Compose the portable layout — binaries at the root, `models/` and `data/`
  beside them. No `engine/` split: Linux has no COM bitness problem.
- Generate `LICENSE-MODELS.txt` and `INSTALL.txt`, and ship **no models**. They
  download on first run, because the OpenRAIL-M acceptance has to be a human
  agreeing to something — that screen exists as of Phase 6 and works.
- **Copy `models-manifest.json` to the root of the layout**, as
  [build/pack-zip.ps1](build/pack-zip.ps1) already does for Windows. It is what
  the first-run download reads; without it a release cannot fetch anything and
  says so, which is correct behaviour for a broken archive and a silly way to
  ship one.
- **Stop the daemon before replacing anything**, in `install.sh` and in the
  `INSTALL.txt` instructions for a hand-untar. Added 2026-08-17 from trap 16 in
  the port plan: `vibesupertonicd` is long-lived by design, Linux lets you
  replace a running executable without complaint, and the result is a new binary
  on disk with the old one still serving every hotkey press — and `status`
  truthfully reporting the old version.

Add it to the table above and delete this section once it exists.

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

**The next release is already settled at `0.3.0`** — decided 2026-08-16 for the
first Linux release, and the number is shared, so the next Windows ZIP carries
it too. That is a deliberate jump from `0.2.7.5`, and it belongs in the release
notes rather than looking like a numbering accident. Confirm it rather than
re-deriving a bump; the rule to ask still governs everything after it.

After a release ships, update `<VstVersion>` in
[Directory.Build.props](Directory.Build.props) to the version just shipped, so
the default stays truthful for the next run and the Linux packer agrees.

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

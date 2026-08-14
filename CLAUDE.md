# VibeSuperTonic — Claude rules

## Shipping / building / releasing

When the user asks for a build, a release, an executable to test, a zip to hand
off — or you have changes you want to ship to the user — use the canonical
packaging script:

**Script:** [build/pack-zip.ps1](build/pack-zip.ps1)
**Output:** `dist/VibeSuperTonic-<version>-win.zip`

Do **not** use ad-hoc `dotnet build` or `dotnet publish` to produce shippable
artifacts. The script is canonical because it:

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

### Before you run it: settle the version

The script defaults to `0.2.0`. Before invoking it, **ask the user** whether to
bump the version, and propose a bump based on what changed:

- **Patch** (`0.2.0` → `0.2.1`) — bug fixes, internal refactors, no
  user-visible behavior change.
- **Minor** (`0.2.0` → `0.3.0`) — new features, new SAPI behaviors, new
  Control Panel surfaces, default-behavior changes that the user will notice.
- **Major** (`0.2.0` → `1.0.0`) — breaking changes (registry layout, voice
  id renames, data dir contract changes, settings.json schema breaks).

Check `dist/` for prior ZIPs to confirm the last shipped version. If the user
has not shipped this round of changes before, the last `dist/` filename is the
correct baseline; if they have, infer from the most recent ZIP.

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

If the script appears broken, fix the script — don't run the underlying
`dotnet publish` calls manually as a workaround. The composition step
(strip PDBs, generate license/install txt, zip) is part of what makes the
output shippable. A manual `dotnet publish` produces a `bin/` tree that the
user cannot drop on a fresh machine.

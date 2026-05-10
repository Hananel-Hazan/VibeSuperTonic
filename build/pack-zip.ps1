<#
.SYNOPSIS
    Build a portable VibeSuperTonic release ZIP.

.DESCRIPTION
    Publishes the Engine (x64+x86, framework-dependent) and the Launcher (x64,
    self-contained single-file), composes a portable folder layout, and zips it.

    The output ZIP contains everything the end user needs except the ONNX models —
    those are downloaded at first launch from Hugging Face.

.PARAMETER Version
    Version string to embed in the ZIP filename. Defaults to "0.2.0".
#>
param(
    [string]$Version = "0.2.0"
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root "dist\release\VibeSuperTonic"
$zipPath = Join-Path $root "dist\VibeSuperTonic-$Version-win.zip"

function Step($msg) { Write-Host ">>> $msg" -ForegroundColor Cyan }

# ------------------------------------------------------------------ build
Step "Publishing Engine (x64) framework-dependent…"
& dotnet publish "$root\src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj" `
    -c Release -r win-x64 --no-self-contained `
    --nologo --verbosity minimal | Out-Null
if ($LASTEXITCODE) { throw "Engine x64 publish failed" }

Step "Publishing Engine (x86) framework-dependent…"
& dotnet publish "$root\src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj" `
    -c Release -r win-x86 --no-self-contained `
    --nologo --verbosity minimal | Out-Null
if ($LASTEXITCODE) { throw "Engine x86 publish failed" }

Step "Publishing Launcher (x64) single-file self-contained…"
& dotnet publish "$root\src\VibeSuperTonic.Launcher\VibeSuperTonic.Launcher.csproj" `
    -c Release -r win-x64 `
    --nologo --verbosity minimal | Out-Null
if ($LASTEXITCODE) { throw "Launcher publish failed" }

# ----------------------------------------------------------------- compose
Step "Composing portable folder at $staging…"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path $staging | Out-Null
New-Item -ItemType Directory -Path "$staging\engine\x64" | Out-Null
New-Item -ItemType Directory -Path "$staging\engine\x86" | Out-Null
New-Item -ItemType Directory -Path "$staging\models\onnx" | Out-Null
New-Item -ItemType Directory -Path "$staging\models\voice_styles" | Out-Null

Copy-Item "$root\src\VibeSuperTonic.Launcher\bin\Release\net10.0-windows\win-x64\publish\VibeSuperTonic.exe" `
    "$staging\VibeSuperTonic.exe"
Copy-Item "$root\src\VibeSuperTonic.Engine\bin\Release\net10.0-windows\win-x64\publish\*" `
    "$staging\engine\x64\" -Recurse
Copy-Item "$root\src\VibeSuperTonic.Engine\bin\Release\net10.0-windows\win-x86\publish\*" `
    "$staging\engine\x86\" -Recurse
# strip .pdb to keep ZIP small
Get-ChildItem "$staging\engine" -Filter *.pdb -Recurse | Remove-Item -Force
Copy-Item "$root\README.md" "$staging\README.md"
Copy-Item "$root\LICENSE"   "$staging\LICENSE.txt"

# Models manifest (paths + SHA-256 hashes the Control Panel verifies/repairs against)
if (Test-Path "$root\models-manifest.json") {
    Copy-Item "$root\models-manifest.json" "$staging\models-manifest.json"
}

# Benchmark sample text (Twenty Thousand Leagues excerpt — replaceable by user)
if (Test-Path "$root\samples") {
    New-Item -ItemType Directory -Path "$staging\samples" -Force | Out-Null
    Copy-Item "$root\samples\*" "$staging\samples\" -Recurse
}

# A pointer for users on what they're agreeing to when models download
@"
The VibeSuperTonic engine code is MIT licensed.

The neural voice models (downloaded by VibeSuperTonic.exe on first launch) are
distributed by Supertone, Inc. under the OpenRAIL-M license. By running the
launcher and accepting the model download, you agree to Supertone's terms:

  https://huggingface.co/Supertone/supertonic-3

VibeSuperTonic does not redistribute the models — they are downloaded directly
from Hugging Face at install time. The sha256 hashes pinned in the launcher
prevent tampering.
"@ | Out-File "$staging\LICENSE-MODELS.txt" -Encoding utf8

# Brief plain-text install instructions for users who don't read the README
@"
VibeSuperTonic — Quick install
==============================

1. Run VibeSuperTonic.exe.
2. Accept the UAC prompt (one-time — registers voice tokens to HKLM).
3. The models will download (~380 MB) on first run.
4. Open any SAPI client (Balabolka, NVDA, Narrator) — voices appear as
   VibeSuperTonic M1 .. F5.

Move folder anywhere. Re-run VibeSuperTonic.exe at the new location to
update registry — no admin needed for moves.

Uninstall:  VibeSuperTonic.exe --unregister  (UAC to clean HKLM)

See README.md for full documentation.
"@ | Out-File "$staging\INSTALL.txt" -Encoding utf8

# ----------------------------------------------------------------- zip
Step "Creating $zipPath…"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path "$staging\*" -DestinationPath $zipPath -CompressionLevel Optimal

$size = (Get-Item $zipPath).Length / 1MB
Step ("Done. ZIP size: {0:N1} MB → {1}" -f $size, $zipPath)

# ----------------------------------------------------------------- summary
Write-Host ""
Write-Host "Layout:" -ForegroundColor Yellow
Get-ChildItem $staging -Recurse | Where-Object { -not $_.PSIsContainer } |
    Select-Object @{N='Path';E={$_.FullName.Substring($staging.Length+1)}}, Length |
    Format-Table -AutoSize

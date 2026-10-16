<#
.SYNOPSIS
    Build a portable VibeSuperTonic release ZIP.

.DESCRIPTION
    Publishes the Engine (x64+x86, framework-dependent) and the Launcher (x64,
    self-contained single-file), composes a portable folder layout, and zips it.

    The output ZIP contains everything the end user needs except the ONNX models —
    those are downloaded at first launch from Hugging Face.

.PARAMETER Version
    Version string to embed in the ZIP filename. Defaults to <VstVersion> from
    Directory.Build.props — the single source of truth shared with the Linux
    packer, so one number produces both artifacts. Pass explicitly for a release
    run, then update Directory.Build.props to match.
#>
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

# Version comes from Directory.Build.props unless overridden. It used to be a
# hardcoded param default, which is how this script kept claiming 0.2.0 through
# the 0.2.1–0.2.5 releases — every caller had to remember to pass -Version, and
# the one time nobody did, the ZIP was named after a version that shipped months
# earlier. Two packers with two defaults would repeat that across platforms.
if (-not $Version) {
    $propsPath = Join-Path $root "Directory.Build.props"
    if (-not (Test-Path $propsPath)) { throw "Directory.Build.props not found at $propsPath" }
    $node = ([xml](Get-Content -Raw $propsPath)).SelectSingleNode('//VstVersion')
    if (-not $node) { throw "No <VstVersion> element in $propsPath" }
    $Version = $node.InnerText.Trim()
    if (-not $Version) { throw "<VstVersion> in $propsPath is empty" }
    Write-Host ">>> Version not supplied; using <VstVersion> $Version from Directory.Build.props" -ForegroundColor DarkGray
}

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

# RenderHost is the out-of-process SAPI render helper the Export tab spawns.
# Framework-dependent ON PURPOSE — see its csproj: a self-contained single-file
# host crashes ONNX Runtime, so this must mirror the ordinary SAPI-host shape.
Step "Publishing RenderHost (x64) framework-dependent…"
& dotnet publish "$root\src\VibeSuperTonic.RenderHost\VibeSuperTonic.RenderHost.csproj" `
    -c Release -r win-x64 --no-self-contained `
    --nologo --verbosity minimal | Out-Null
if ($LASTEXITCODE) { throw "RenderHost publish failed" }

# TestHarness — the System.Speech suite that drives the engine through a real
# SAPI client. Shipped because it is the ONLY way to verify the engine on a
# machine that is not a dev box: Phase 1's exit criterion is "the TestHarness
# passes unchanged on Windows", and until now it was not in the ZIP at all, so
# nobody without the source could run it.
#
# Framework-dependent and multi-file, for the same reason RenderHost is: SAPI
# loads the engine INTO this process, so ONNX Runtime ends up hosted here, and a
# self-contained single-file host crashes it. A verification harness that does
# not have the shape of an ordinary SAPI client is not verifying much anyway.
Step "Publishing TestHarness (x64) framework-dependent…"
& dotnet publish "$root\src\VibeSuperTonic.TestHarness\VibeSuperTonic.TestHarness.csproj" `
    -c Release -r win-x64 --no-self-contained `
    --nologo --verbosity minimal | Out-Null
if ($LASTEXITCODE) { throw "TestHarness publish failed" }

# ----------------------------------------------------------------- compose
Step "Composing portable folder at $staging…"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path $staging | Out-Null
New-Item -ItemType Directory -Path "$staging\engine\x64" | Out-Null
New-Item -ItemType Directory -Path "$staging\engine\x86" | Out-Null
New-Item -ItemType Directory -Path "$staging\models\onnx" | Out-Null
New-Item -ItemType Directory -Path "$staging\models\voice_styles" | Out-Null
New-Item -ItemType Directory -Path "$staging\render" | Out-Null
New-Item -ItemType Directory -Path "$staging\tools" | Out-Null

Copy-Item "$root\src\VibeSuperTonic.Launcher\bin\Release\net10.0-windows\win-x64\publish\VibeSuperTonic.exe" `
    "$staging\VibeSuperTonic.exe"
Copy-Item "$root\src\VibeSuperTonic.Engine\bin\Release\net10.0-windows\win-x64\publish\*" `
    "$staging\engine\x64\" -Recurse
Copy-Item "$root\src\VibeSuperTonic.Engine\bin\Release\net10.0-windows\win-x86\publish\*" `
    "$staging\engine\x86\" -Recurse
# Out-of-process render helper (Export tab spawns render\VibeSuperTonic.RenderHost.exe)
Copy-Item "$root\src\VibeSuperTonic.RenderHost\bin\Release\net10.0-windows\win-x64\publish\*" `
    "$staging\render\" -Recurse
# strip .pdb to keep ZIP small
Get-ChildItem "$staging\engine" -Filter *.pdb -Recurse | Remove-Item -Force
Get-ChildItem "$staging\render" -Filter *.pdb -Recurse | Remove-Item -Force

# Verification harness. Run it AFTER the voices are registered and the models
# have downloaded — it drives the real engine through System.Speech.
Copy-Item "$root\src\VibeSuperTonic.TestHarness\bin\Release\net10.0-windows\win-x64\publish\*" `
    "$staging\tools\" -Recurse -Force
Get-ChildItem "$staging\tools" -Filter *.pdb -Recurse | Remove-Item -Force
@"
VibeSuperTonic verification harness
===================================

Drives the engine through a real SAPI client (System.Speech) and checks what it
actually did. Run it from a normal command prompt AFTER VibeSuperTonic.exe has
registered the voices and downloaded the models:

    tools\VibeSuperTonic.TestHarness.exe

Exit code 0 means every check passed; anything else is the number of failures.
You will hear it speak — that is expected, the word-boundary checks need real
audio timing to fire.

Steps 1-7  engine smoke tests: COM activation, voice enumeration, sync and async
           speech, cancellation, SSML bookmarks, prosody rate, language tags.
Step 8     word-boundary offsets land on real word starts, across five different
           whitespace layouts between sentences.
Step 9     the same with a length-changing pronunciation rule active.
Step 10    the shared boundary planner predicts exactly what this engine reports.
Step 11    the playback clock and boundary scheduler.

Steps 8 and 9 are the ones worth watching. They verify the two offset fixes in
0.2.7 -- a highlight that drifts away from the spoken word -- which nothing else
can check automatically, and which working audio does NOT demonstrate. Step 9
temporarily installs a pronunciation rule and restores your pronunciations.json
afterwards.

Steps 10 and 11 cover shared code the Linux port added. Step 10 reads YOUR
pronunciations.json and chunk-size settings, predicts where every word boundary
should be, and holds the prediction against what the engine actually reported --
so it fails if the two ever disagree about the same sentence. Step 11 is
arithmetic and needs no audio; it runs in well under a second.

Neither of them changes the engine. If steps 1-9 pass and 10 or 11 fails, the
engine is fine and the shared code is wrong.

    tools\VibeSuperTonic.TestHarness.exe --stress

runs the longer concurrency and recovery suite instead.
"@ | Out-File "$staging\tools\README.txt" -Encoding utf8
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
VibeSuperTonic — Setup
======================

PREREQUISITES — TWO runtimes, in the SAME BITNESS as your reader
---------------------------------------------------------------
The Control Panel is self-contained and always runs. The speech engine is not:
it loads INSIDE your SAPI client, so it needs both runtimes below matching that
program's bitness. The Control Panel's own Test button will speak perfectly
even when your reader cannot, because the Control Panel is 64-bit.

Most readers are still 32-bit — Balabolka, Lingoes, many NVDA setups — so
install the x86 pair unless you are certain you do not need them. Installing
both architectures is fine and is the safe default.

  64-bit clients
    winget install Microsoft.DotNet.Runtime.10
    winget install Microsoft.VCRedist.2015+.x64

  32-bit clients
    winget install Microsoft.DotNet.Runtime.10 --architecture x86 --force
    winget install Microsoft.VCRedist.2015+.x86

The two fail differently, which is worth knowing when you are diagnosing:

  .NET runtime missing        -> the reader lists NO VibeSuperTonic voices.
  Visual C++ runtime missing  -> the voices ARE listed, and are silent.
                                 ONNX Runtime links against it; without it
                                 onnxruntime.dll cannot load.

Not sure? The Control Panel's Status tab checks all four, says which is
missing in plain words, and can install them for you (double-click the row).

Quick install
-------------
1. Install the runtimes above — or let the Control Panel do it in step 4.
2. Run VibeSuperTonic.exe.
3. Accept the UAC prompt (one-time — registers voice tokens to HKLM).
4. The models will download (~380 MB) on first run.
5. Open any SAPI client (Balabolka, NVDA, Narrator) — voices appear as
   VibeSuperTonic M1 .. F5.

If you install a runtime after step 2, re-run VibeSuperTonic.exe (or press
"Repair all" on the Status tab) so the newly usable bitness gets registered.

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

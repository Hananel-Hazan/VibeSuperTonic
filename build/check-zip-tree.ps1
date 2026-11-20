<#
.SYNOPSIS
    Assert the composed Windows tree before pack-zip.ps1 archives it.

.DESCRIPTION
    The Windows half of what pack-tar.sh asserts about the Linux tree. Until
    0.3.0 the ZIP packer had none of it and tolerated a missing manifest (review
    item 53), so each of these failures shipped silently:

      1. models-manifest.json is missing or holds no files. The Status tab
         verifies and repairs the models against it; without it "Repair all"
         has nothing to fetch, and a fresh install can never get its voices.
      2. A binary reports a version other than the one the ZIP is named after.
         Every binary takes its version from <VstVersion> in
         Directory.Build.props; -Version only names the archive. A mismatch is
         a stale binary that survived a publish, or a release named after a
         number the props file never had — the ZIP would lie about itself.
      3. An engine bitness is incomplete: no engine DLL, no COM host, no
         onnxruntime.dll, or an engine\x86 that is not 32-bit. Skipping x86
         silently breaks every 32-bit SAPI host; a missing onnxruntime.dll is
         a voice that is listed and silent.
      4. models\ holds files. They download on first run behind the OpenRAIL-M
         acceptance; shipping them makes that acceptance a lie.
      5. A .pdb survived in engine\, render\ or tools\.

    A separate script, like build/check-composed-tree.sh, so each assertion can
    be sabotaged against a doctored copy of a tree in a second rather than in a
    two-minute pack. Every failure is reported, then it throws.

.PARAMETER Tree
    The composed folder (pack-zip.ps1's dist\release\VibeSuperTonic).

.PARAMETER Version
    The version the ZIP will be named after.
#>
param(
    [Parameter(Mandatory)][string]$Tree,
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string]$why) { $failures.Add($why) }
function Info([string]$msg) { Write-Host "    $msg" }

if (-not (Test-Path -LiteralPath $Tree -PathType Container)) { throw "no composed tree at $Tree" }

# --- 1. the manifest ----------------------------------------------------------
$manifest = Join-Path $Tree 'models-manifest.json'
if (-not (Test-Path -LiteralPath $manifest)) {
    Fail "models-manifest.json is missing from the tree: the Status tab cannot fetch or verify the models"
} else {
    try {
        $json = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
        $entries = @($json.files)
        if ($entries.Count -eq 0) { Fail "models-manifest.json lists no files" }
        else { Info "manifest: $($entries.Count) files" }
    } catch {
        Fail "models-manifest.json does not parse: $($_.Exception.Message)"
    }
}

# --- 2. one version, read off the files in the box ----------------------------
function ProductVersion([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "MISSING" }
    $v = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
    if (-not $v) { return "NONE" }
    $plus = $v.IndexOf('+')     # the SDK appends +<commit>; provenance, not version
    if ($plus -ge 0) { $v = $v.Substring(0, $plus) }
    return $v.Trim()
}
$launcher = Join-Path $Tree 'VibeSuperTonic.exe'
$asked = "MISSING"
if (Test-Path -LiteralPath $launcher) {
    # Asked of the program, as pack-tar.sh asks its binaries: the file's resource
    # and the program's own answer are different claims, and the user sees the
    # second one in --version and the About tab.
    #
    # Through Process with stdout redirected, not '&': VibeSuperTonic.exe is a
    # GUI-subsystem program, and '& exe --version 2>$null' measured as returning
    # nothing at all — the same exe that answers 0.3.0 this way (review item 52).
    $psi = [System.Diagnostics.ProcessStartInfo]::new($launcher, '--version')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $asked = $proc.StandardOutput.ReadToEnd().Trim()
    if (-not $proc.WaitForExit(30000)) { $proc.Kill(); $asked = "DID NOT EXIT" }
    elseif (-not $asked) { $asked = "NO OUTPUT (exit $($proc.ExitCode))" }
    $proc.Dispose()
}
$versions = [ordered]@{
    'VibeSuperTonic.exe --version'         = $asked
    'engine\x64\VibeSuperTonic.Engine.dll' = ProductVersion (Join-Path $Tree 'engine\x64\VibeSuperTonic.Engine.dll')
    'engine\x86\VibeSuperTonic.Engine.dll' = ProductVersion (Join-Path $Tree 'engine\x86\VibeSuperTonic.Engine.dll')
    'render\VibeSuperTonic.RenderHost.dll' = ProductVersion (Join-Path $Tree 'render\VibeSuperTonic.RenderHost.dll')
    'tools\VibeSuperTonic.TestHarness.dll' = ProductVersion (Join-Path $Tree 'tools\VibeSuperTonic.TestHarness.dll')
}
foreach ($k in $versions.Keys) {
    Info ("{0,-38} {1}" -f $k, $versions[$k])
    if ($versions[$k] -ne $Version) {
        Fail "$k reports '$($versions[$k])' but the ZIP would be named $Version. The binaries take their version from <VstVersion> in Directory.Build.props: change it there and rebuild, do not rename the ZIP"
    }
}

# --- 3. both engine bitnesses, complete ---------------------------------------
function PeMachine([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        $fs.Position = 0x3C
        $fs.Position = $br.ReadInt32() + 4
        return $br.ReadUInt16()
    } finally { $fs.Dispose() }
}
foreach ($arch in @(@{ Dir = 'x64'; Machine = 0x8664 }, @{ Dir = 'x86'; Machine = 0x014C })) {
    $dir = Join-Path $Tree "engine\$($arch.Dir)"
    foreach ($f in 'VibeSuperTonic.Engine.dll', 'VibeSuperTonic.Engine.comhost.dll', 'onnxruntime.dll') {
        $p = Join-Path $dir $f
        if (-not (Test-Path -LiteralPath $p)) { Fail "engine\$($arch.Dir)\$f is missing"; continue }
    }
    $ort = Join-Path $dir 'onnxruntime.dll'
    if (Test-Path -LiteralPath $ort) {
        $m = PeMachine $ort
        if ($m -ne $arch.Machine) {
            Fail ("engine\{0}\onnxruntime.dll is machine 0x{1:X4}, expected 0x{2:X4}" -f $arch.Dir, $m, $arch.Machine)
        } else {
            Info ("engine\{0}\onnxruntime.dll machine 0x{1:X4}" -f $arch.Dir, $m)
        }
    }
}

# --- 4. no models ---------------------------------------------------------------
$shippedModels = @(Get-ChildItem -LiteralPath (Join-Path $Tree 'models') -Recurse -File -ErrorAction SilentlyContinue)
if ($shippedModels.Count -gt 0) {
    Fail "models\ holds $($shippedModels.Count) file(s), first $($shippedModels[0].FullName.Substring($Tree.Length + 1)): models download behind the OpenRAIL-M acceptance and must not ship"
} else {
    Info "models\: empty"
}

# --- 5. no debug symbols where the packer strips them --------------------------
foreach ($sub in 'engine', 'render', 'tools') {
    $pdbs = @(Get-ChildItem -LiteralPath (Join-Path $Tree $sub) -Recurse -Filter *.pdb -File -ErrorAction SilentlyContinue)
    if ($pdbs.Count -gt 0) { Fail "$sub\ still holds $($pdbs.Count) .pdb file(s)" }
}

if ($failures.Count -gt 0) {
    $list = ($failures | ForEach-Object { " - $_" }) -join [Environment]::NewLine
    throw "The composed tree failed $($failures.Count) check(s):$([Environment]::NewLine)$list"
}
Write-Host "    composed tree: all checks passed"

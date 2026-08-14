<#
.SYNOPSIS
    Download the Supertonic model files named in models-manifest.json and verify
    every one against its SHA-256 / byte size.

.DESCRIPTION
    Two jobs, one script:

      -Layout install   Writes models\onnx\... and models\voice_styles\... — the
                        layout VibeSuperTonic expects under its BaseDir. Copy the
                        resulting `models` folder into an install to fix a machine
                        that can't reach Hugging Face, or to preseed an offline one.

      -Layout hf        Writes onnx\... and voice_styles\... — the layout of the
                        upstream Hugging Face repo. Upload this to your own model
                        repo to stand up the mirror the manifest falls back to.

    Why this exists rather than `git clone`: the four .onnx files are git-LFS
    objects. A clone without git-lfs active leaves you with ~130-byte pointer
    files that name the real object instead of containing it — which looks like
    a successful clone right up until the engine tries to load a model. This
    script fetches actual bytes over HTTPS and checks them, so "it downloaded"
    and "it is correct" are the same statement.

    Integrity does not depend on trusting the source: the same hash is checked
    whether bytes came from upstream or a mirror. The small .json files have no
    LFS hash (they are plain git blobs), so they are verified by exact size,
    which is what the manifest records for them.

.EXAMPLE
    .\build\fetch-models.ps1 -Layout hf
    Fetch into $HOME\Downloads\supertonic-3, ready to upload as a mirror.

.EXAMPLE
    .\build\fetch-models.ps1 -Layout install -OutDir D:\staging
    Produce D:\staging\models\... for a manual install.
#>
[CmdletBinding()]
param(
    # Where to write. Defaults outside the repo on purpose — the repo lives in a
    # synced folder, and 380 MB of models has no business being uploaded to it.
    [string]$OutDir = (Join-Path $env:USERPROFILE "Downloads\supertonic-3"),

    [ValidateSet('install', 'hf')]
    [string]$Layout = 'install',

    # 'primary' uses each entry's url; 'mirror' uses the first entry in mirrors.
    # Use -Source mirror to prove a freshly uploaded mirror actually serves the
    # right bytes before you rely on it.
    [ValidateSet('primary', 'mirror')]
    [string]$Source = 'primary',

    [string]$ManifestPath = (Join-Path $PSScriptRoot "..\models-manifest.json"),

    # Re-download and re-verify even files that already pass.
    [switch]$Force,

    # Check every mirror in the manifest without downloading anything, then exit.
    # Hugging Face publishes each LFS object's sha256 through its tree API, and
    # that hash IS what the manifest pins — so a mirror can be proven byte-equal
    # to upstream in a couple of seconds instead of a 380 MB download per mirror.
    # Run it after publishing a mirror, and any time you want to know the
    # fallbacks are still alive.
    [switch]$VerifyMirrors
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar makes large downloads crawl

if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if (-not $manifest.files) { throw "Manifest has no 'files' array: $ManifestPath" }

if ($VerifyMirrors) {
    # Group the manifest's mirror URLs by repo so each repo costs two API calls
    # (onnx/ and voice_styles/) instead of one per file.
    $byRepo = @{}
    foreach ($f in $manifest.files) {
        foreach ($m in $f.mirrors) {
            if ($m -notmatch '^https://huggingface\.co/([^/]+/[^/]+)/resolve/[^/]+/(.+)$') {
                Write-Host "  SKIP  non-Hugging-Face mirror, cannot check without downloading: $m" -ForegroundColor Yellow
                continue
            }
            $repo = $Matches[1]; $rel = $Matches[2]
            if (-not $byRepo.ContainsKey($repo)) { $byRepo[$repo] = @{} }
            $byRepo[$repo][$rel] = $f
        }
    }

    $bad = 0
    foreach ($repo in $byRepo.Keys | Sort-Object) {
        Write-Host "=== $repo ==="
        $remote = @{}
        $reachable = $true
        foreach ($dir in @('onnx', 'voice_styles')) {
            try {
                $tree = (Invoke-WebRequest -Uri "https://huggingface.co/api/models/$repo/tree/main/$dir" -TimeoutSec 60).Content | ConvertFrom-Json
                foreach ($e in $tree) { $remote[$e.path] = $e }
            }
            catch {
                $code = $_.Exception.Response.StatusCode.value__
                # 401 from an unauthenticated request means private OR absent —
                # Hugging Face deliberately does not distinguish the two.
                $why = if ($code -eq 401) { "private or does not exist" } else { "HTTP $code" }
                Write-Host "  UNREACHABLE ($why)" -ForegroundColor Red
                $reachable = $false; $bad++
                break
            }
        }
        if (-not $reachable) { continue }

        $okCount = 0; $issues = @(); $hashChecked = 0; $sizeOnly = 0
        foreach ($rel in $byRepo[$repo].Keys | Sort-Object) {
            $entry = $byRepo[$repo][$rel]
            $r = $remote[$rel]
            if ($null -eq $r) { $issues += "$rel — missing"; continue }
            if ($entry.bytes -gt 0 -and $r.size -ne $entry.bytes) {
                $issues += "$rel — size $($r.size), manifest says $($entry.bytes)"; continue
            }
            # Hugging Face's tree API exposes a content hash only for git-LFS
            # objects. The small .json files are plain git blobs, so the API can
            # confirm their size but not their bytes — say so rather than
            # claiming a hash check that didn't happen. The full download path
            # (-Source mirror) does verify them properly, and so does the
            # installer before it ever loads one.
            if (-not [string]::IsNullOrWhiteSpace($entry.sha256)) {
                if ($null -ne $r.lfs) {
                    if ($r.lfs.oid -ine $entry.sha256) {
                        $issues += "$rel — LFS hash differs from manifest"; continue
                    }
                    $hashChecked++
                }
                else { $sizeOnly++ }
            }
            else { $sizeOnly++ }
            $okCount++
        }
        Write-Host "  $okCount/$($byRepo[$repo].Count) files match the manifest ($hashChecked hash-verified, $sizeOnly size-only — run -Source mirror to hash those)"
        if ($issues.Count -gt 0) {
            $bad++
            $issues | ForEach-Object { Write-Host "  MISMATCH $_" -ForegroundColor Red }
        }
    }

    Write-Host ""
    if ($bad -gt 0) { Write-Host "$bad mirror(s) unusable." -ForegroundColor Yellow; exit 1 }
    Write-Host "All mirrors serve files identical to the manifest." -ForegroundColor Green
    exit 0
}

function Get-TargetPath {
    param($EntryPath)
    # Manifest paths are install-relative ("models/onnx/x.onnx"). The Hugging
    # Face repo has no "models" prefix, so drop it for the hf layout.
    $rel = if ($Layout -eq 'hf') { $EntryPath -replace '^models/', '' } else { $EntryPath }
    Join-Path $OutDir ($rel -replace '/', '\')
}

function Test-File {
    param($Path, $Entry)
    if (-not (Test-Path $Path)) { return $false }
    $info = Get-Item $Path
    if ($Entry.bytes -gt 0 -and $info.Length -ne $Entry.bytes) { return $false }
    if ([string]::IsNullOrWhiteSpace($Entry.sha256)) { return $true }   # size-only entry
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash
    return $actual -ieq $Entry.sha256
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "Manifest : $((Resolve-Path $ManifestPath).Path)"
Write-Host "Output   : $OutDir  (layout: $Layout)"
Write-Host "Source   : $Source"
Write-Host ""

$ok = 0; $failed = @(); $skipped = 0; $totalBytes = 0

foreach ($f in $manifest.files) {
    $target = Get-TargetPath $f.path
    $name = Split-Path $target -Leaf

    if (-not $Force -and (Test-File $target $f)) {
        Write-Host ("  OK      {0,-28} (already verified)" -f $name)
        $ok++; $skipped++
        continue
    }

    # Try every source of the requested kind in order, exactly as the installer
    # does — a dead or private first mirror must fall through to the next, not
    # fail the file. Testing only mirrors[0] would report the whole mirror set
    # broken because one entry in it is.
    $urls = if ($Source -eq 'mirror') { @($f.mirrors) } else { @($f.url) }
    $urls = @($urls | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

    if ($urls.Count -eq 0) {
        Write-Host ("  NO URL  {0,-28} ({1} source missing in manifest)" -f $name, $Source)
        $failed += $f.path
        continue
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    $tmp = "$target.part"
    $mb = [math]::Round($f.bytes / 1MB, 1)
    Write-Host ("  GET     {0,-28} {1} MB…" -f $name, $mb) -NoNewline

    $got = $false
    for ($i = 0; $i -lt $urls.Count -and -not $got; $i++) {
        try {
            Invoke-WebRequest -Uri $urls[$i] -OutFile $tmp -MaximumRedirection 10 -TimeoutSec 1800
            Move-Item $tmp $target -Force
        }
        catch {
            $code = $_.Exception.Response.StatusCode.value__
            Write-Host (" [src$($i+1): {0}]" -f $(if ($code) { "HTTP $code" } else { "unreachable" })) -NoNewline
            if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
            continue
        }

        if (Test-File $target $f) {
            $host_ = ([uri]$urls[$i]).Host
            $suffix = if ($i -gt 0) { " (source $($i+1): $host_)" } else { "" }
            Write-Host "  verified$suffix"
            $got = $true
            $ok++; $totalBytes += (Get-Item $target).Length
        }
        else {
            $len = (Get-Item $target).Length
            Write-Host " [src$($i+1): got $len bytes, manifest says $($f.bytes)]" -NoNewline
            # A pointer file is ~130 bytes and is the classic failure here — name
            # it, because "hash mismatch" sends people hunting for corruption.
            if ($len -lt 1024 -and $f.bytes -gt 1MB) {
                Write-Host " (that is an LFS pointer — use resolve/ not raw/)" -NoNewline
            }
        }
    }

    if (-not $got) {
        Write-Host "  FAILED (all $($urls.Count) source(s))" -ForegroundColor Red
        $failed += $f.path
    }
}

# The mirror layout is for REDISTRIBUTION, and OpenRAIL-M permits that only if
# the licence and its use restrictions travel with the weights. Upstream's
# LICENSE and model card are not in the manifest (VibeSuperTonic never
# redistributes, so it never needed them), but a mirror does — fetch them so the
# uploaded repo is compliant by construction rather than by the operator
# remembering. Best-effort: a missing licence file is worth a warning, not a
# failed run, since the models themselves are already verified.
if ($Layout -eq 'hf') {
    foreach ($doc in @('LICENSE', 'README.md')) {
        $docPath = Join-Path $OutDir $doc
        if ((Test-Path $docPath) -and -not $Force) { continue }
        try {
            Invoke-WebRequest -Uri "https://huggingface.co/Supertone/supertonic-3/resolve/main/$doc" `
                -OutFile $docPath -MaximumRedirection 10 -TimeoutSec 120
            Write-Host ("  GET     {0,-28} (redistribution requires it)" -f $doc)
        }
        catch {
            Write-Host "  WARN    could not fetch $doc — add it to the mirror by hand before publishing" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "verified : $ok/$($manifest.files.Count)  ($skipped already present, $([math]::Round($totalBytes/1MB,1)) MB downloaded)"
if ($failed.Count -gt 0) {
    Write-Host "FAILED   : $($failed.Count)" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "           $_" -ForegroundColor Red }
    exit 1
}
Write-Host "All files match the manifest." -ForegroundColor Green
if ($Layout -eq 'hf') {
    Write-Host ""
    Write-Host "To publish this as the mirror the installer falls back to:"
    Write-Host "  1. Create a PUBLIC model repo named supertonic-3 on huggingface.co"
    Write-Host "  2. Upload the contents of $OutDir (keep the onnx\ and voice_styles\ folders)"
    Write-Host "  3. Verify with:  .\build\fetch-models.ps1 -Source mirror -OutDir `$env:TEMP\mirror-check"
}

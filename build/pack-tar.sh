#!/usr/bin/env bash
#
# Build a portable VibeSuperTonic release tarball for Linux.
#
#     bash build/pack-tar.sh [-v X.Y.Z]
#
# Publishes all three binaries, composes the portable layout, and archives it to
# dist/VibeSuperTonic-<version>-linux-x64.tar.gz.
#
# This is the canonical Linux packer and the counterpart to build/pack-zip.ps1.
# Do NOT hand-roll a tarball out of `dotnet publish` output: the composition
# step here is most of what makes the result droppable on a fresh machine, and
# the assertions below are the only thing standing between a green build and an
# archive that is quietly wrong.
#
# ---------------------------------------------------------------------------
# The six assertions, and why each exists. Every one of them exists because the
# failure it catches is SILENT — a green build, a plausible archive, and a defect
# that surfaces on someone else's machine.
#
#  1. ALL THREE BINARIES REPORT THE SAME VERSION.
#     Publishing writes into per-project bin/ trees that nothing clears between
#     runs, so a binary that failed to rebuild survives from the previous run and
#     ships beside two that did. The failure is silent and the symptom is a UI
#     disagreeing with its daemon about a protocol. We publish into freshly
#     emptied directories AND then ask each SHIPPED file its version, because the
#     first guard is about this run and the second is about what is in the box.
#
#  2. THE SHIPPED vst-ctl IS A NATIVE ELF, NOT A MANAGED APPHOST.
#     `dotnet build` (and a publish that quietly skips AOT) leaves a managed
#     apphost that works perfectly and costs ~100 ms of runtime startup on every
#     hotkey press — against a 150 ms budget from key to feedback. It is the same
#     shape of file, so `file` alone cannot tell them apart; see _assert_native
#     for what actually discriminates.
#
#  3. NO MODELS ARE IN THE ARCHIVE.
#     They are ~380 MB and, more importantly, the OpenRAIL-M acceptance has to be
#     a human agreeing to something. The first-run screen downloads them behind
#     that acceptance. An archive that shipped them would make the licence screen
#     a lie.
#
#  4. NO CUDA PROVIDER LIBRARY IN THE ARCHIVE (Phase 8b).
#     330 MB against a 52 MB tarball, arriving by default with the GPU package
#     the backend links. The removal lives in Directory.Build.targets; its first
#     version sat in the backend csproj, looked right and built clean, and this
#     assertion is what found the 330 MB sitting in the composed tree.
#
#  5. install-gpu.sh FETCHES THE ORT VERSION THE DAEMON LINKS.
#     The provider library and libonnxruntime.so are one build split across two
#     files. A drift between them fails on the user's machine and nowhere else,
#     and this is the only place both numbers are visible at once.
#
#  6. THE GLIBC FLOOR HAS NOT RISEN (Phase 9).
#     Measured, not assumed: vst-ctl needs GLIBC_2.34 and everything else in the
#     tree needs 2.27 or lower, because vst-ctl is the only binary compiled here
#     — the rest arrive prebuilt from NuGet. The floor is therefore a property of
#     THIS machine's toolchain, it can rise under a distro upgrade with every
#     test still green, and the symptom is a user on a supported distro being
#     told GLIBC_2.39 is missing by a binary that worked yesterday.
#
# What is deliberately NOT here: the engine/x64 + engine/x86 split the Windows
# packer needs. That exists because the Windows engine is an in-process COM
# server and inherits its host's bitness. Nothing here is loaded into anyone
# else's process, so the binaries sit at the root and BaseDir resolves to the
# folder with no walk-up.
# ---------------------------------------------------------------------------

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version) version="${2:-}"; shift 2 ;;
        -h|--help)
            sed -n '3,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

# ------------------------------------------------------------------ version
#
# Defaults to <VstVersion> in Directory.Build.props — the single source of truth
# shared with pack-zip.ps1, so one number produces both artifacts. The Windows
# packer learned this the hard way: a hardcoded default kept naming ZIPs 0.2.0
# through four releases. Two packers with two defaults would repeat it across
# platforms, so this reads the same element.
if [[ -z "$version" ]]; then
    props="$root/Directory.Build.props"
    [[ -f "$props" ]] || die "Directory.Build.props not found at $props"
    version="$(sed -n 's:.*<VstVersion>\(.*\)</VstVersion>.*:\1:p' "$props" | head -1 | tr -d '[:space:]')"
    [[ -n "$version" ]] || die "no non-empty <VstVersion> in $props"
    info "version not supplied; using <VstVersion> $version from Directory.Build.props"
fi

staging="$root/dist/release-linux/VibeSuperTonic"
tarball="$root/dist/VibeSuperTonic-$version-linux-x64.tar.gz"

# ------------------------------------------------------------------ publish
#
# Into emptied directories. See assertion 1: without this, a project that fails
# to build leaves the previous run's binary in place and it ships.
pub_daemon="$root/dist/build-linux/daemon"
pub_ui="$root/dist/build-linux/ui"
pub_ctl="$root/dist/build-linux/ctl"
rm -rf "$root/dist/build-linux" "$staging"
mkdir -p "$pub_daemon" "$pub_ui" "$pub_ctl"

step "Publishing vibesupertonicd (linux-x64, self-contained)…"
dotnet publish "$root/src/VibeSuperTonic.Daemon/VibeSuperTonic.Daemon.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -o "$pub_daemon" --nologo --verbosity minimal \
    || die "daemon publish failed"

step "Publishing vibesupertonic-ui (linux-x64, self-contained)…"
dotnet publish "$root/src/VibeSuperTonic.Ui/VibeSuperTonic.Ui.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -o "$pub_ui" --nologo --verbosity minimal \
    || die "ui publish failed"

# NativeAOT. This is what a hotkey binding actually runs, so its startup is added
# to every press: 107 ms on the runtime, 6 ms compiled. PublishAot only takes
# effect on publish, which is exactly why a managed one can ship by accident.
step "Publishing vst-ctl (linux-x64, NativeAOT)…"
dotnet publish "$root/src/VibeSuperTonic.Ctl/VibeSuperTonic.Ctl.csproj" \
    -c Release -r linux-x64 \
    -o "$pub_ctl" --nologo --verbosity minimal \
    || die "vst-ctl publish failed"

# ----------------------------------------------------------------- compose
step "Composing portable layout at $staging…"
mkdir -p "$staging"

# The daemon's runtime tree first, then the UI's on top. Both are self-contained
# and both are built from this same solution in this same run, so the shared
# framework assemblies they each carry are byte-identical and the overlay is a
# no-op for everything except each app's own files. Publishing them into one
# folder is what lets the product ship ONE copy of the .NET runtime instead of
# two, and it is safe only because of that "same run" clause — which assertion 1
# is what enforces.
cp -a "$pub_daemon/." "$staging/"
cp -a "$pub_ui/."     "$staging/"
cp -a "$pub_ctl/vst-ctl" "$staging/vst-ctl"

# Debug symbols are large and nobody diagnoses a field report from them.
find "$staging" -name '*.pdb' -delete
find "$staging" -name '*.dbg' -delete

chmod 755 "$staging/vibesupertonicd" "$staging/vibesupertonic-ui" "$staging/vst-ctl"

# models/ and data/ beside the binaries, both empty. models/ empty is what the
# first-run screen keys off ("no models present"), and there is deliberately no
# marker file anywhere: the folder may be read-only.
mkdir -p "$staging/models" "$staging/data"

# The manifest the first-run download reads — paths and the pinned SHA-256s it
# verifies against. Without it a release cannot fetch anything and says so, which
# is correct behaviour for a broken archive and a silly way to ship one.
[[ -f "$root/models-manifest.json" ]] || die "models-manifest.json missing from $root"
cp "$root/models-manifest.json" "$staging/models-manifest.json"

# The curated Piper voice catalog (P4). Beside models-manifest.json and for the
# same reason: it is what the Voices tab reads to offer anything at all, and an
# archive without it can list installed voices and download none. Generated by
# build/gen-piper-catalog.py against a pinned upstream revision; it carries URLs,
# sizes, SHA-256s and each voice's own licence, and NO voice bytes.
[[ -f "$root/piper-voices.json" ]] || die "piper-voices.json missing from $root.
       Generate it with: python3 build/gen-piper-catalog.py"
cp "$root/piper-voices.json" "$staging/piper-voices.json"

# The installer, and the keybinding logic it sources. Phase 5 wrote how to bind;
# the installer knows where things landed — the absolute path is only knowable at
# install time, which is why the split exists.
cp "$root/build/keybindings.sh" "$staging/keybindings.sh"
chmod 755 "$staging/keybindings.sh"

# The optional GPU pack's installer. The pack itself is ~3.1 GB against a 51 MB
# archive, so it is fetched on request from nuget.org and PyPI rather than
# shipped — see assertion 4, which is what stops it arriving by accident.
cp "$root/build/install-gpu.sh" "$staging/install-gpu.sh"
chmod 755 "$staging/install-gpu.sh"

[[ -f "$root/README.md" ]] && cp "$root/README.md" "$staging/README.md"
[[ -f "$root/LICENSE" ]]   && cp "$root/LICENSE"   "$staging/LICENSE.txt"

# Benchmark sample text. `vst-ctl benchmark` reads it; it is replaceable.
if [[ -d "$root/samples" ]]; then
    mkdir -p "$staging/samples"
    cp -a "$root/samples/." "$staging/samples/"
fi

# ---------------------------------------------------------------- assertions
step "Checking the composed tree…"

# --- 1. one version, from the files that are actually in the box -------------
v_daemon="$("$staging/vibesupertonicd"   --version 2>/dev/null || echo FAILED)"
v_ui="$(    "$staging/vibesupertonic-ui" --version 2>/dev/null || echo FAILED)"
v_ctl="$(   "$staging/vst-ctl"           --version 2>/dev/null || echo FAILED)"
info "vibesupertonicd   $v_daemon"
info "vibesupertonic-ui $v_ui"
info "vst-ctl           $v_ctl"
if [[ "$v_daemon" != "$v_ui" || "$v_daemon" != "$v_ctl" ]]; then
    die "the three binaries do not agree on a version — a stale one survived a publish"
fi
# And they must agree with the archive's NAME, or the tarball lies about itself.
if [[ "$v_daemon" != "$version" ]]; then
    die "binaries report $v_daemon but the archive would be named $version.
       <VstVersion> in Directory.Build.props is $(sed -n 's:.*<VstVersion>\(.*\)</VstVersion>.*:\1:p' "$root/Directory.Build.props" | head -1).
       Pass -v to name the archive, but the binaries take their version from the
       props file — change it there and rebuild, do not rename the box."
fi

# --- 2. vst-ctl is native, not a managed apphost -----------------------------
#
# `file` says "ELF 64-bit LSB pie executable" for BOTH, so it cannot discriminate
# and the CI check that greps for ELF only catches a catastrophically wrong file.
# What actually separates them:
#
#   - a managed apphost is a ~70 KB shim that loads a companion vst-ctl.dll;
#     the .dll's presence beside it is the giveaway, and its absence is required.
#   - the AOT binary carries the whole runtime, so it is megabytes, not kilobytes.
#
# Both conditions, because either alone has a plausible false pass: a publish
# could omit the .dll for another reason, and a future managed single-file host
# could be large.
_assert_native() {
    local exe="$1"
    file "$exe" | grep -q 'ELF 64-bit' \
        || die "vst-ctl is not an ELF binary: $(file -b "$exe")"
    [[ -e "$staging/vst-ctl.dll" ]] \
        && die "vst-ctl.dll is present beside vst-ctl — that is a MANAGED apphost.
       PublishAot did not take effect. It costs ~100 ms on every hotkey press and
       the binary works perfectly, so nothing else will tell you."
    local bytes; bytes=$(stat -c%s "$exe")
    (( bytes >= 1048576 )) \
        || die "vst-ctl is only $bytes bytes; a NativeAOT build is megabytes.
       This is a managed apphost or a truncated copy."
    info "vst-ctl is native ($(numfmt --to=iec "$bytes"))"
}
_assert_native "$staging/vst-ctl"

# --- 3. no models in the archive ---------------------------------------------
if find "$staging/models" -type f -print -quit | grep -q .; then
    die "models/ is not empty. The archive must ship NO models — they download on
       first run behind the OpenRAIL-M acceptance, and shipping them makes that
       screen a lie."
fi
# A belt-and-braces sweep: an .onnx anywhere in the tree means something copied
# a model in by a route models/ does not cover.
if find "$staging" -name '*.onnx' -print -quit | grep -q .; then
    die "an .onnx file is in the staging tree; the archive must ship no models"
fi
info "models/ is empty, as it must be"

# --- 3b. the voice catalog ships, and ships no voices -------------------------
# The catalog is a list of things to download, so "no models in the archive" has
# to hold for it too — and it would fail differently from a stray .onnx. A
# generator bug that inlined weights would produce one enormous JSON file that
# the sweep above cannot see: it is not called *.onnx and is not under models/.
#
# The other three checks are the ones that make the file worth shipping at all.
# A voice with no sha256 downloads unverified, a voice with no https url cannot
# download, and a voice with no licence text puts an empty gate in front of a
# person — and all three produce a catalog that loads perfectly and is wrong,
# which is this packer's whole reason for existing.
_assert_catalog() {
    local file="$staging/piper-voices.json"
    local bytes
    bytes=$(stat -c%s "$file")

    # 43 voices of metadata is ~40 KB. A megabyte means something other than
    # metadata got in.
    (( bytes < 1048576 )) || die "piper-voices.json is $(numfmt --to=iec "$bytes").
       A catalog is metadata; this is large enough to be carrying voice data."

    command -v python3 >/dev/null || die "python3 is needed to check piper-voices.json.
       It is the same interpreter build/gen-piper-catalog.py runs on. Skipping the
       check is not an option: an unverified catalog ships voices that download
       without a hash."

    python3 "$root/build/check-piper-catalog.py" "$file" \
        || die "piper-voices.json did not pass its checks"
}
_assert_catalog

# --- 4. the GPU provider is NOT in the archive, and its installer agrees ------
#
# libonnxruntime_providers_cuda.so is 330 MB against a 51 MB archive, and it
# arrives by default with the Gpu.Linux package — the backend csproj removes it
# with an MSBuild target, which is exactly the kind of thing that stops working
# quietly under an SDK bump. The archive doubling in size is not something the
# publish step will complain about.
for unwanted in libonnxruntime_providers_cuda.so libonnxruntime_providers_tensorrt.so; do
    if [[ -e "$staging/$unwanted" ]]; then
        die "$unwanted is in the archive ($(numfmt --to=iec "$(stat -c%s "$staging/$unwanted")")).
       RemoveOptionalGpuProviders in VibeSuperTonic.Onnx.Ort.csproj is not taking
       effect. The pack is fetched by install-gpu.sh; it must not ship."
    fi
done

# The provider library and libonnxruntime.so are one ORT build split across two
# files. install-gpu.sh downloads the first and the archive ships the second, so
# a version drift between them is a load failure on the user's machine and
# nowhere else — this is the only place both numbers are visible at once.
# awk rather than `sed | head -1`: head exits early, sed takes SIGPIPE, and with
# `set -o pipefail` the assignment inherits 141 — which `set -e` turns into a
# packer that dies with no message on a large enough input file. The same
# construct in install-gpu.sh made its driver check report "no NVIDIA driver" on
# a machine with one.
ort_in_csproj=$(awk -F'"' '/Microsoft.ML.OnnxRuntime.Gpu.Linux" Version=/ { print $4; exit }' \
    "$root/src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj")
ort_in_script=$(awk -F'"' '/^ORT_VERSION=/ { print $2; exit }' "$staging/install-gpu.sh")
[[ -n "$ort_in_csproj" ]] || die "could not read the ONNX Runtime version from the backend csproj"
if [[ "$ort_in_csproj" != "$ort_in_script" ]]; then
    die "install-gpu.sh fetches ONNX Runtime $ort_in_script but this build links $ort_in_csproj.
       The CUDA provider and libonnxruntime.so are one build in two files."
fi
info "no GPU provider in the archive; install-gpu.sh fetches ORT $ort_in_script to match"

# --- 5. the glibc floor has not risen ----------------------------------------
#
# WHAT THIS IS ABOUT. A shipped binary runs on any glibc at least as new as the
# highest GLIBC_x.y symbol version it imports. Measured 2026-08-24 across every
# ELF in the composed tree:
#
#     vst-ctl            GLIBC_2.34      <- the floor, and the only local build
#     libonnxruntime.so  GLIBC_2.27
#     libcoreclr.so      GLIBC_2.27
#     everything else    GLIBC_2.17 or lower
#
# So the product's floor is set by ONE 4 MB file. Everything else arrives
# prebuilt from NuGet, built by Microsoft against an old glibc on purpose;
# vst-ctl is NativeAOT, which links here, against whatever this machine has.
# This box has 2.43, and the binary still only asks for 2.34 — the merge of
# libpthread into libc, which is where a modern link lands regardless.
#
# 2.34 means Ubuntu 22.04+, Debian 12+, RHEL 9+, Fedora 35+. Accepted 2026-08-24
# rather than chased into a container: what a lower floor buys is Ubuntu 20.04,
# Debian 11 and RHEL 8, all EOL, at the price of a second build environment for
# one binary.
#
# WHY A GUARD RATHER THAN A NOTE. The floor is a property of the toolchain, not
# of this repository, so it can rise under a distro upgrade with every test still
# green — and the symptom is a user on a supported distro getting "GLIBC_2.39 not
# found" from a binary that ran yesterday. Nothing else here can see that.
command -v objdump >/dev/null 2>&1 || die "objdump is required (binutils) — it is what measures the glibc floor.
       Skipping the check silently would be worse than not having it: the floor
       rises under a toolchain upgrade with every other test still green."

glibc_floor="2.34"
glibc_max=""
while IFS= read -r elf; do
    [[ "$(head -c4 "$elf" | od -An -tx1 | tr -d ' \n')" == "7f454c46" ]] || continue
    needed=$(objdump -T "$elf" 2>/dev/null | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -uV | tail -1)
    [[ -n "$needed" ]] || continue
    if [[ -z "$glibc_max" ]] || [[ "$(printf '%s\n%s\n' "$glibc_max" "$needed" | sort -V | tail -1)" == "$needed" ]]; then
        glibc_max="$needed"
        glibc_worst="$elf"
    fi
done < <(find "$staging" -type f)

if [[ "$(printf '%s\n%s\n' "$glibc_floor" "$glibc_max" | sort -V | tail -1)" != "$glibc_floor" ]]; then
    die "the glibc floor has risen to $glibc_max, needed by ${glibc_worst#$staging/}.
       It was $glibc_floor, which is Ubuntu 22.04+ / Debian 12+ / RHEL 9+. Something
       in the toolchain moved. Either build vst-ctl somewhere older, or raise the
       floor here deliberately and say so in INSTALL.txt — but do not let it move
       on its own."
fi
info "glibc floor $glibc_max (${glibc_worst#$staging/}) — Ubuntu 22.04+, Debian 12+, RHEL 9+"

# ------------------------------------------------------------------- text
step "Writing INSTALL.txt and LICENSE-MODELS.txt…"

cat > "$staging/LICENSE-MODELS.txt" <<'EOF'
The VibeSuperTonic code is MIT licensed.

The neural voice models are NOT included in this archive. They are downloaded on
first run, directly from Hugging Face, and are distributed by Supertone, Inc.
under the OpenRAIL-M license. By accepting the download on the first-run screen
you agree to Supertone's terms:

  https://huggingface.co/Supertone/supertonic-3

VibeSuperTonic does not redistribute the models. The SHA-256 hashes pinned in
models-manifest.json are verified after download, which is what prevents
tampering in transit.
EOF

cat > "$staging/INSTALL.txt" <<'EOF'
VibeSuperTonic for Linux — Setup
================================

No runtime to install. All three binaries carry what they need.

  vibesupertonicd     the daemon: holds the warm model, owns the audio device
                      and the tray icon. Long-lived, started on the first
                      hotkey press. Nothing autostarts it.
  vibesupertonic-ui   the window: Reader, Tune, Pronunciations, Status.
  vst-ctl             the command-line client. Anything the window can do.


UPGRADING? STOP THE DAEMON FIRST
--------------------------------
    ./vst-ctl shutdown

This matters more than it looks. vibesupertonicd is long-lived by design and
Linux will happily let you replace a running executable without complaint. The
result is the new binary on disk, the OLD one still serving every hotkey press,
and `vst-ctl status` truthfully reporting the old version — for as long as that
process lives, which may be a week.

Stop it BEFORE you extract over an existing folder, not after. `shutdown` with
nothing running is a success, so it is always safe to type. The next hotkey
press starts the new daemon by itself.


Install
-------
1. Extract anywhere you like. Everything lives in this folder — models, settings,
   logs — so "installing" is putting the folder where you want it.

2. Run the installer. It stops any running daemon, then registers the two global
   shortcuts with your desktop:

       ./install.sh

   Supported automatically: Cinnamon/GNOME (gsettings) and KDE Plasma. On other
   desktops it prints exactly what to bind by hand.

       Ctrl+`      speak the selected text, interrupting anything playing
       Ctrl+~      stop

   On KDE the shortcuts load at your NEXT LOGIN. Plasma keeps its shortcut
   service inside the compositor and there is no way to make it re-read its
   configuration that does not risk restarting your desktop.

3. Open the window once to download the models (~380 MB):

       ./vibesupertonic-ui

   The first-run screen carries the model licence and the download. Nothing
   speaks until this is done. See LICENSE-MODELS.txt.

4. Highlight text in any application and press Ctrl+` .


Checking it works
-----------------
    ./vst-ctl status            one line of JSON
    ./vst-ctl config            which folder this instance is actually using
    ./vst-ctl speak "hello"     bypasses the selection entirely
    ./vst-ctl benchmark         measure this machine (~1 min) and use the result

If a hotkey does nothing, the daemon's log is the place to look:

    tail -f data/logs/daemon.log


Optional: use an NVIDIA GPU
---------------------------
    ./install-gpu.sh

Downloads about 3.1 GB — the ONNX Runtime CUDA provider, CUDA and cuDNN — which
is why it is not in this archive. It needs a working NVIDIA driver, which it
checks for before downloading anything.

Measured on an RTX A2000 8GB laptop GPU: the wait before the first word drops
from 802 ms to 77 ms, and synthesis runs about ten times faster than real time
instead of five. Run `./vst-ctl benchmark` afterwards — the sweep measures the
GPU as another row and only picks it if it actually wins.

ON BATTERY THE DAEMON USES THE CPU, deliberately: a discrete GPU is the
difference between a laptop that lasts an afternoon and one that does not, and
the CPU path is fast enough that only the first word arrives later. Set
GpuOnBattery in data/settings.json (or the Tune tab) if you are permanently on a
dock. `./vst-ctl status` says which is in force and why.

    ./install-gpu.sh --remove   puts the machine back on the CPU


Known limits
------------
- Selection capture uses ext-data-control-v1 on a Wayland session, which reads
  both native Wayland clients and anything running through Xwayland, and the X11
  PRIMARY selection on an X11 session. The daemon logs which one it chose.
- GNOME declines to implement ext-data-control on security grounds, so on
  GNOME/Wayland there is no way for a background process to read the selection.
  The daemon says so in one line rather than failing silently.
- A few applications never publish a selection at all — some in-frame document
  viewers. Copy with Ctrl+C first, then press the key.
- Screen lockers and some fullscreen games hold a keyboard grab, and no global
  shortcut fires under one. That is a property of the display server.


Uninstall
---------
    ./uninstall.sh          removes the shortcuts and the launcher entry
    rm -rf <this folder>    removes everything else, including the models

Nothing is written outside this folder except the desktop shortcut entries,
which uninstall.sh removes.
EOF

# ------------------------------------------------------------------ install.sh
#
# Written by the packer rather than kept in build/, because it is a file that
# only makes sense inside a composed release — it assumes the layout around it.
cat > "$staging/install.sh" <<'INSTALLER'
#!/usr/bin/env bash
#
# VibeSuperTonic — install into this folder and bind the global shortcuts.
#
#     ./install.sh [--no-bind]
#
# The product is portable: this folder IS the installation. Nothing is copied
# anywhere. What this script does is stop a daemon that may still be running from
# a previous version of these files, then register the two hotkeys.

set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bind=1
[[ "${1:-}" == "--no-bind" ]] && bind=0

echo "VibeSuperTonic — installing in $here"

# --- stop any daemon running from this folder -------------------------------
#
# BEFORE anything else. Linux lets you replace a running executable, so an
# upgrade leaves the old daemon serving every hotkey press from a binary that no
# longer exists on disk, with `status` truthfully reporting the old version.
#
# Found by /proc/<pid>/exe, never by `pkill -f`. That pattern matches the shell
# running this script as readily as the daemon, and during the port it killed a
# test harness outright and nearly certified a build that did not contain the fix
# it was testing.
stopped=0
for exe in /proc/[0-9]*/exe; do
    target="$(readlink "$exe" 2>/dev/null || true)"
    case "$target" in
        "$here/vibesupertonicd"|"$here/vibesupertonicd "*)
            pid="${exe#/proc/}"; pid="${pid%/exe}"
            echo "  stopping daemon (pid $pid) running from this folder"
            "$here/vst-ctl" shutdown >/dev/null 2>&1 || kill "$pid" 2>/dev/null || true
            stopped=1
            ;;
    esac
done
if (( stopped )); then
    for _ in $(seq 1 50); do
        pgrep -x vibesupertonicd >/dev/null 2>&1 || break
        sleep 0.1
    done
    echo "  daemon stopped"
else
    echo "  no daemon was running from this folder"
fi

chmod 755 "$here/vibesupertonicd" "$here/vibesupertonic-ui" "$here/vst-ctl" 2>/dev/null || true

# --- the hotkeys, and the application-menu entry ----------------------------
#
# On KDE these are the SAME artefact: Plasma binds a key to a command through a
# .desktop file, so vst_bind writes a normal visible launcher carrying the two
# shortcuts as Desktop Actions. Writing a second entry here would put two
# identical "VibeSuperTonic" items in the menu.
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
if (( bind )); then
    echo
    # shellcheck source=/dev/null
    source "$here/keybindings.sh"
    vst_bind "$here" || echo "  (shortcuts were not bound automatically — see above)"
else
    echo "  --no-bind given; shortcuts not registered"
fi

# The Cinnamon/GNOME path binds through gsettings and writes no launcher, and
# --no-bind writes nothing at all, so the menu entry is added here when it is
# not already there. Idempotent: re-running install.sh never stacks entries.
if [[ ! -e "$apps/vibesupertonic.desktop" ]]; then
    mkdir -p "$apps"
    cat > "$apps/vibesupertonic-ui.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=VibeSuperTonic
Comment=Read selected text aloud
Exec=$here/vibesupertonic-ui
Icon=audio-speakers
Terminal=false
Categories=Utility;Accessibility;
EOF
    echo "  added VibeSuperTonic to the application menu"
fi

cat <<EOF

Done.

Next: open the window once to accept the model licence and download the voices.

    $here/vibesupertonic-ui

Then highlight text anywhere and press Ctrl+\` .
EOF
INSTALLER
chmod 755 "$staging/install.sh"

cat > "$staging/uninstall.sh" <<'UNINSTALLER'
#!/usr/bin/env bash
#
# VibeSuperTonic — remove the desktop integration.
#
# Deletes ONLY what install.sh created outside this folder: the global shortcuts
# and the application-menu entry. Your models, settings and logs live in this
# folder and are removed by deleting it.

set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

"$here/vst-ctl" shutdown >/dev/null 2>&1 || true

# shellcheck source=/dev/null
source "$here/keybindings.sh"
vst_unbind || true

rm -f "${XDG_DATA_HOME:-$HOME/.local/share}/applications/vibesupertonic-ui.desktop"
echo "removed the application-menu entry"

cat <<EOF

The shortcuts and menu entry are gone. This folder — with your models, settings
and logs — is still here:

    $here

Delete it to remove everything.
EOF
UNINSTALLER
chmod 755 "$staging/uninstall.sh"

# ------------------------------------------------------------------ archive
step "Creating $tarball…"
mkdir -p "$root/dist"
rm -f "$tarball"
tar -czf "$tarball" -C "$root/dist/release-linux" VibeSuperTonic

size="$(du -h "$tarball" | cut -f1)"
step "Done. $size → $tarball"

# ------------------------------------------------------------------ summary
printf '\n\033[33mLayout:\033[0m\n'
( cd "$staging" && find . -maxdepth 1 -mindepth 1 -printf '%y %10s  %p\n' | sort -k3 )
printf '\n%s files, %s uncompressed\n' \
    "$(find "$staging" -type f | wc -l)" \
    "$(du -sh "$staging" | cut -f1)"

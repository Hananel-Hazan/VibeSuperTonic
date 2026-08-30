#!/usr/bin/env bash
#
# Build the Linux AppImage.
#
#     bash build/pack-tar.sh -v X.Y.Z && bash build/pack-appimage.sh -v X.Y.Z
#
# Produces dist/VibeSuperTonic-<version>-x86_64.AppImage.
#
# ---------------------------------------------------------------------------
# THIS SCRIPT PUBLISHES NOTHING. It packages the tree build/pack-tar.sh already
# composed, at dist/release-linux/VibeSuperTonic, and refuses to run if that
# tree is absent or reports a different version.
#
# That is the whole design. The tarball's six assertions — one version across
# three binaries, a native vst-ctl rather than a managed apphost, no models, no
# 330 MB CUDA provider, install-gpu.sh fetching the ORT version the daemon links,
# and a glibc floor that has not risen — are properties of a composed tree, so
# consuming that tree inherits them instead of copying them somewhere they can
# drift. A second packer with
# its own publish steps is how two artifacts of "the same" release end up
# differing, which is the failure the shared <VstVersion> already exists to
# prevent one level up.
#
# The tarball is the canonical artifact. A failed tarball means no AppImage.
# ---------------------------------------------------------------------------
#
# WHAT AN APPIMAGE COSTS, measured 2026-08-24 and worth knowing before editing
# anything here: a hotkey press through `App.AppImage ctl toggle` takes 20.0 ms
# median against the native binary's 5.3 — +14.7 ms for the squashfs mount,
# comfortably inside R-3's 100 ms acknowledge budget. The fallback path,
# --appimage-extract-and-run, costs 82 ms warm and 247 ms cold and leaves 125 MB
# in /tmp; it is what runs when FUSE is missing, and it must never be reached
# silently. See docs/LINUX-PORT-PLAN.md#phase-9.
#
# THE RUNTIME IS FETCHED AND HASH-PINNED. AppImage's type-2 runtime is a 944 KB
# static ELF that is concatenated in front of a squashfs image; that is all an
# AppImage is, and it is why appimagetool is not required. It is downloaded once
# into dist/build-linux/ and checked against the SHA-256 below on every run,
# fresh or cached — the same discipline models-manifest.json applies to the
# weights, for the same reason: something fetched from the internet ends up
# inside what we hand a user.

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

# Same source of truth as both other packers. See pack-tar.sh.
if [[ -z "$version" ]]; then
    props="$root/Directory.Build.props"
    [[ -f "$props" ]] || die "Directory.Build.props not found at $props"
    # awk, not `sed | head -1`: awk exits by itself, so nothing takes SIGPIPE.
    version="$(awk '/<VstVersion>/ { sub(/.*<VstVersion>/, ""); sub(/<\/VstVersion>.*/, ""); gsub(/[[:space:]]/, ""); print; exit }' "$props")"
    [[ -n "$version" ]] || die "no non-empty <VstVersion> in $props"
    info "version not supplied; using <VstVersion> $version from Directory.Build.props"
fi

staging="$root/dist/release-linux/VibeSuperTonic"
appdir="$root/dist/build-linux/AppDir"
runtime="$root/dist/build-linux/runtime-x86_64"
out="$root/dist/VibeSuperTonic-$version-x86_64.AppImage"

RUNTIME_URL="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64"
RUNTIME_SHA256="1cc49bcf1e2ccd593c379adb17c9f85a36d619088296504de95b1d06215aebbf"

# --------------------------------------------------------------- preconditions
step "Checking the composed tree…"

[[ -d "$staging" ]] || die "no composed tree at $staging.
       Run build/pack-tar.sh first — this script packages what that one composed
       and deliberately publishes nothing itself."

for tool in mksquashfs curl sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is required and was not found.
       squashfs-tools provides mksquashfs."
done

# ASSERTION 1, inherited and re-asked of THIS tree. pack-tar.sh asserts the three
# binaries agree before it archives; asking again here is what catches the
# sequence that actually happens — a tarball built yesterday, a rebuild since,
# and an AppImage packaged from whatever is on disk now.
for binary in vibesupertonicd vibesupertonic-ui vst-ctl vst-speechd; do
    [[ -x "$staging/$binary" ]] || die "$binary is missing from $staging"
    reported="$("$staging/$binary" --version 2>/dev/null | tr -d '[:space:]')"
    [[ "$reported" == "$version" ]] || die "$binary in the composed tree reports $reported, not $version.
       The tree is from another build. Re-run build/pack-tar.sh -v $version."
done
info "all four binaries report $version"

# ASSERTIONS 2-4, re-asked for pennies. Each caught something real once, and the
# cost of asking again is three tests against a directory that is already there.
[[ ! -e "$staging/vst-ctl.dll" ]] || die "vst-ctl.dll is beside vst-ctl in the composed tree —
       PublishAot did not take effect and this would ship a managed apphost."
ctl_bytes=$(stat -c%s "$staging/vst-ctl")
(( ctl_bytes > 1000000 )) || die "vst-ctl is $ctl_bytes bytes; a NativeAOT build is megabytes."

if [[ -d "$staging/models" ]] && find "$staging/models" -type f -print -quit | grep -q .; then
    die "the composed tree contains model files. They download on first run behind
       the OpenRAIL-M acceptance; shipping them would make that screen a lie."
fi

if find "$staging" -name 'libonnxruntime_providers_cuda.so' -print -quit | grep -q .; then
    die "libonnxruntime_providers_cuda.so is in the composed tree — 330 MB that must
       not ship. See Directory.Build.targets."
fi
info "native vst-ctl, no models, no CUDA provider"

# ------------------------------------------------------------------- runtime
step "Fetching the AppImage runtime…"

mkdir -p "$(dirname "$runtime")"
if [[ ! -f "$runtime" ]]; then
    info "downloading $RUNTIME_URL"
    curl -sSL -o "$runtime" "$RUNTIME_URL" || die "could not download the AppImage runtime"
else
    info "using the cached copy at $runtime"
fi

# Checked whether it was just downloaded or has sat in dist/ for months. A cache
# that is trusted because it is a cache is a cache that can be poisoned once and
# believed forever.
actual="$(sha256sum "$runtime" | cut -d' ' -f1)"
if [[ "$actual" != "$RUNTIME_SHA256" ]]; then
    rm -f "$runtime"
    die "the AppImage runtime does not match its pinned SHA-256.
       expected $RUNTIME_SHA256
       got      $actual
       The cached copy has been deleted. If upstream published a new runtime,
       verify it by hand and update RUNTIME_SHA256 in this script deliberately."
fi
info "runtime verified ($(stat -c%s "$runtime") bytes)"

# ------------------------------------------------------------------- compose
step "Composing the AppDir…"

rm -rf "$appdir"
mkdir -p "$appdir/usr/lib/vibesupertonic" \
         "$appdir/usr/share/applications" \
         "$appdir/usr/share/icons/hicolor/256x256/apps"

cp -a "$staging/." "$appdir/usr/lib/vibesupertonic/"

# AppRun. One entry point, several programs — an AppImage has exactly one, and
# this product is three binaries plus two shell scripts.
#
# Dispatch on the first argument rather than on argv[0] alone, because the file
# a user downloads is named for the release and nobody symlinks it. argv[0] is
# still honoured through $ARGV0 — the runtime execs AppRun with argv[0] set to
# AppRun's own path inside the mount and exports the name the user typed
# separately, so `basename "$0"` is always "AppRun" and a symlink shim would have
# fallen through to the window.
cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
# VibeSuperTonic AppImage entry point.
#
#   ./VibeSuperTonic.AppImage                 open the window
#   ./VibeSuperTonic.AppImage ctl <verb>      everything vst-ctl does
#   ./VibeSuperTonic.AppImage daemon [args]   run the daemon in the foreground
#   ./VibeSuperTonic.AppImage bind            bind the hotkeys (and install vst-ctl)
#   ./VibeSuperTonic.AppImage speechd-install register with Speech Dispatcher
#   ./VibeSuperTonic.AppImage gpu-install     fetch the optional CUDA pack (~3.1 GB)
#   ./VibeSuperTonic.AppImage store           print where models and data live
#
#   Keep this file and its store together. Models and settings live beside the
#   image in VibeSuperTonic/ — or inside <name>.AppImage.home if you keep a
#   portable home, and under ~/.local/share/vibesupertonic if neither is
#   writable. `store` prints which one won. Move the image on its own and the
#   next run starts an empty store while several hundred MB of models sit in the
#   directory you left behind.
#
#   Upgrading? Stop the daemon FIRST:  ./VibeSuperTonic.AppImage ctl shutdown
#   And if you ran `speechd-install`, stop speech-dispatcher too — it holds this
#   image open for your whole session, so overwriting it fails with "Text file
#   busy" (and forcing past that leaves a module that dies on its next spawn,
#   which a screen reader sees as every utterance hanging):
#       pkill -u "$USER" -x speech-dispatcher
#   Run that AFTER replacing the image as well: anything that spoke during the
#   copy will have started a new speech-dispatcher holding the old image, and it
#   then answers nothing at all — not us, not espeak-ng — until it is restarted.
#   The daemon is long-lived and Linux lets you replace a running file without
#   complaint. Overwrite this image while it runs and the OLD daemon keeps
#   serving every hotkey press — with `ctl status` truthfully reporting the old
#   version — and the old runtime process stays behind holding the deleted
#   image. `shutdown` with nothing running is a success, so it is always safe to
#   type; the next press starts the new daemon by itself.
#
# Kept to POSIX sh and to as few processes as possible: `ctl` is on the hotkey
# path, where this script's own startup is added to every press.
set -eu

HERE="${APPDIR:-$(dirname "$(readlink -f "$0")")}"
APP="$HERE/usr/lib/vibesupertonic"

# The store is the daemon's decision, never this script's — the rule has four
# branches and one owner. See LinuxDataPaths.ResolveStore.
vst_store() { "$APP/vibesupertonicd" --print-store; }

case "$(basename "${ARGV0:-$0}")" in
    vst-ctl) exec "$APP/vst-ctl" "$@" ;;
    # speech-dispatcher execs ONE absolute path with no arguments (measured:
    # a binary field containing a space fails with "Exec of module ... error 2"
    # while speechd still logs the module as loaded). So the config names a
    # symlink, appimage-speechd.sh installs it, and this is what it lands on.
    # Without this case the module falls through to the default below and opens
    # a window per utterance.
    vst-speechd) exec "$APP/vst-speechd" "$@" ;;
esac

case "${1-}" in
    ctl)    shift; exec "$APP/vst-ctl" "$@" ;;
    daemon) shift; exec "$APP/vibesupertonicd" "$@" ;;
    ui)     shift; exec "$APP/vibesupertonic-ui" "$@" ;;
    store)  vst_store; exit 0 ;;
    # The verb form, for `--version` and for anyone driving it by hand. The
    # symlink above is what speechd uses.
    speechd) shift; exec "$APP/vst-speechd" "$@" ;;
    # bash, not sh: appimage-bind.sh sources keybindings.sh, which uses arrays.
    # Every desktop this targets has bash; a system without it cannot bind keys
    # with the tarball either, and says so rather than failing strangely.
    bind)
        shift
        command -v bash >/dev/null 2>&1 || {
            echo "bash is required to bind hotkeys and was not found." >&2
            exit 1
        }
        exec bash "$APP/appimage-bind.sh" "$@" ;;
    speechd-install)
        shift
        command -v bash >/dev/null 2>&1 || {
            echo "bash is required to register the Speech Dispatcher module." >&2
            exit 1
        }
        exec bash "$APP/appimage-speechd.sh" "$@" ;;
    gpu-install)
        shift
        store="$(vst_store)"
        # data/ as well as the directory itself: install-gpu.sh checks that what
        # it is pointed at is recognisably ours before writing 3.1 GB into it,
        # and an empty directory is not. This is the same directory the daemon
        # creates on its first start, so fetching the pack before ever running
        # the product does not have to fail with a sentence telling you to run
        # the command you just ran.
        mkdir -p "$store/data"
        exec bash "$APP/install-gpu.sh" --dir "$store" "$@" ;;
    --version) exec "$APP/vibesupertonicd" --version ;;
    -h|--help)
        sed -n '3,25p' "$0" | sed 's/^# \{0,1\}//'
        exit 0 ;;
    *) exec "$APP/vibesupertonic-ui" "$@" ;;
esac
APPRUN
chmod +x "$appdir/AppRun"

# The desktop entry, at the root where the AppImage spec looks for it and under
# usr/share where a desktop that integrates the file expects to find it.
cat > "$appdir/VibeSuperTonic.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=VibeSuperTonic
GenericName=Text to speech
Comment=Reads what you select, out loud, in a neural voice that runs on this machine
Exec=AppRun
Icon=vibesupertonic
Categories=Utility;Accessibility;
Keywords=speech;tts;reader;voice;accessibility;
Terminal=false
StartupWMClass=vibesupertonic-ui
DESKTOP
cp "$appdir/VibeSuperTonic.desktop" "$appdir/usr/share/applications/"

# The icon, at the root, as .DirIcon (what file managers read off the image
# without mounting it), and in the theme path.
[[ -f "$root/build/vibesupertonic.png" ]] || die "build/vibesupertonic.png is missing.
       Regenerate it with: python3 build/make-icon.py"
cp "$root/build/vibesupertonic.png" "$appdir/vibesupertonic.png"
cp "$root/build/vibesupertonic.png" "$appdir/.DirIcon"
cp "$root/build/vibesupertonic.png" "$appdir/usr/share/icons/hicolor/256x256/apps/vibesupertonic.png"

# The bind helper travels inside the image beside the binaries it copies out.
cp "$root/build/appimage-bind.sh" "$appdir/usr/lib/vibesupertonic/appimage-bind.sh"
chmod +x "$appdir/usr/lib/vibesupertonic/appimage-bind.sh"

# The Speech Dispatcher adapter, and the portable-home rule it shares with the
# hotkeys. speechd-install.sh itself arrives with the composed tree — it is a
# tarball file — and these two are what make it correct for an image whose
# contents move on every run.
cp "$root/build/appimage-speechd.sh" "$appdir/usr/lib/vibesupertonic/appimage-speechd.sh"
cp "$root/build/appimage-home.sh"    "$appdir/usr/lib/vibesupertonic/appimage-home.sh"
chmod +x "$appdir/usr/lib/vibesupertonic/appimage-speechd.sh"

if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$appdir/VibeSuperTonic.desktop" \
        || die "the .desktop entry does not validate"
    info "desktop entry validates"
fi

# --------------------------------------------------------------------- build
step "Building the image…"

squash="$root/dist/build-linux/app.squashfs"
rm -f "$squash" "$out"

# zstd because the mount happens on every hotkey press. Measured: it also
# produced a SMALLER image than the tarball's gzip, 48 MB against 52.
mksquashfs "$appdir" "$squash" -root-owned -noappend -comp zstd -Xcompression-level 19 -b 128K -quiet \
    || die "mksquashfs failed"

mkdir -p "$root/dist"
cat "$runtime" "$squash" > "$out"
chmod +x "$out"
rm -f "$squash"

# ---------------------------------------------------------------- assertions
step "Checking what came out…"

# The type-2 magic, three bytes at offset 8: 'A' 'I' 0x02. Without it the file is
# an ELF that happens to have a filesystem stuck to the end, and every tool that
# integrates AppImages will ignore it.
magic="$(dd if="$out" bs=1 skip=8 count=3 2>/dev/null | od -An -tx1 | tr -d ' \n')"
[[ "$magic" == "414902" ]] || die "not a type-2 AppImage: magic bytes are $magic, expected 414902"
info "type-2 magic present"

# Run the thing. Both paths, because they are different code: the default lands
# on the UI, and `ctl` is the one on the hotkey path.
reported="$("$out" --version 2>/dev/null | tr -d '[:space:]')" \
    || die "the AppImage would not run. If this box has no FUSE, that is the cause —
       and it is what --appimage-extract-and-run exists for."
[[ "$reported" == "$version" ]] || die "the AppImage reports $reported, not $version"

ctl_reported="$("$out" ctl --version 2>/dev/null | tr -d '[:space:]')"
[[ "$ctl_reported" == "$version" ]] || die "AppRun's ctl dispatch reports '$ctl_reported', not $version.
       The hotkey path goes through that branch."

# The verb, and then the ARGV0 form — which is the one speech-dispatcher will
# actually use, and the one nothing else in this script exercises.
#
# THE SYMLINK IS THE ASSERTION. A module config names one absolute path with no
# arguments, so an AppImage install points speechd at ~/.local/bin/vst-speechd
# -> the image. If AppRun's $ARGV0 case were missing or misspelled, that symlink
# would fall through to the default branch and OPEN THE WINDOW once per
# utterance — and every check that ran the image by its own name would still
# pass. Tested here with a symlink in a scratch directory, which is exactly what
# appimage-speechd.sh installs.
sd_reported="$("$out" speechd --version 2>/dev/null | tr -d '[:space:]')"
[[ "$sd_reported" == "$version" ]] || die "AppRun's speechd dispatch reports '$sd_reported', not $version."

link_dir="$(mktemp -d)"
ln -s "$(readlink -f "$out")" "$link_dir/vst-speechd"
# `|| true` on both, and no display for either. Without the first, a symlink
# that falls through to the WINDOW takes this script out at the assignment under
# `set -e` — a packer that fails with no sentence, which is the failure mode
# every assertion here exists to replace. Without the second, that same
# fall-through opens a window on the packer's desktop. Both found by sabotage.
link_reported="$(env -u DISPLAY -u WAYLAND_DISPLAY "$link_dir/vst-speechd" --version 2>/dev/null | tr -d '[:space:]' || true)"
# INIT through the same symlink: a module that reports its version and then
# cannot answer the server is a module speechd drops, and the espeak payload
# living inside the mount is the part that could be wrong here.
link_init="$(printf 'INIT\nQUIT\n' | timeout 30 env -u DISPLAY -u WAYLAND_DISPLAY "$link_dir/vst-speechd" 2>/dev/null | grep -E '^(299|399) ' | tail -1 || true)"
rm -rf "$link_dir"
[[ "$link_reported" == "$version" ]] || die "a symlink named vst-speechd pointing at the image reported
       '$link_reported', not $version. AppRun's \$ARGV0 dispatch is what makes the
       Speech Dispatcher config work, and without it speechd opens the window."
[[ "$link_init" == 299\ * ]] || die "through the vst-speechd symlink, INIT answered: ${link_init:-nothing}.
       speech-dispatcher drops a module that does not answer 299. 399 means the
       espeak payload is not reachable from inside the mount."
info "runs; ctl, speechd and the vst-speechd symlink all report $version"
info "the module answers INIT through the symlink — ${link_init#299 }"

size="$(du -h "$out" | cut -f1)"
step "Done."
info "$out"
info "$size"
printf '\n'
printf 'Install:  chmod +x %s && ./%s bind\n' "$(basename "$out")" "$(basename "$out")"
printf 'Screen reader:  ./%s speechd-install   (after the first-run download)\n' "$(basename "$out")"
printf 'Models and data go beside the file, in a VibeSuperTonic/ folder.\n'
printf 'Keep them together: moving the AppImage alone leaves the store behind.\n'

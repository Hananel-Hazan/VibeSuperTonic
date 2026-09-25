#!/usr/bin/env bash
#
# Build the snap.
#
#     bash build/pack-tar.sh -v X.Y.Z && bash build/pack-snap.sh -v X.Y.Z [--grade stable]
#
# Produces dist/VibeSuperTonic-<version>-amd64.snap.
#
#   --grade devel|stable   devel (the default) cannot be released to the
#                          stable or candidate channels; a release run says stable
#   --destructive-mode     build on this machine rather than in an LXD container.
#                          Only on Ubuntu 24.04 (the core24 base); CI uses it
#   --compose-only         write the snapcraft project and stop, for inspection
#   --check FILE.snap      run only the checks on a snap already built, which is
#                          also how each of them was seen failing
#
# ---------------------------------------------------------------------------
# THIS SCRIPT PUBLISHES NOTHING, like pack-appimage.sh. It packages the tree
# build/pack-tar.sh composed at dist/release-linux/VibeSuperTonic, so the
# tarball's assertions are the snap's too, and it re-asks the ones a stale tree
# could break (check-composed-tree.sh). snapcraft only adds the snap metadata and
# the three native libraries the daemon loads. It compiles nothing.
#
# Uploading is not this script's job either. Once the snap name is registered:
#     snapcraft upload --release=edge dist/VibeSuperTonic-<version>-amd64.snap
# ---------------------------------------------------------------------------

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
grade="devel"
destructive=0
compose_only=0
check_only=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version)       version="${2:-}"; shift 2 ;;
        --grade)            grade="${2:-}"; shift 2 ;;
        --destructive-mode) destructive=1; shift ;;
        --compose-only)     compose_only=1; shift ;;
        --check)            check_only="${2:-}"; shift 2 ;;
        -h|--help)
            sed -n '3,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

[[ "$grade" == devel || "$grade" == stable ]] || die "--grade is devel or stable, not '$grade'"

# Same source of truth as every other packer. See pack-tar.sh.
if [[ -z "$version" ]]; then
    props="$root/Directory.Build.props"
    [[ -f "$props" ]] || die "Directory.Build.props not found at $props"
    version="$(awk '/<VstVersion>/ { sub(/.*<VstVersion>/, ""); sub(/<\/VstVersion>.*/, ""); gsub(/[[:space:]]/, ""); print; exit }' "$props")"
    [[ -n "$version" ]] || die "no non-empty <VstVersion> in $props"
    info "version not supplied; using <VstVersion> $version from Directory.Build.props"
fi

app_id="io.github.hananel_hazan.VibeSuperTonic"
staging="$root/dist/release-linux/VibeSuperTonic"
project="$root/dist/build-linux/snap"
out="$root/dist/VibeSuperTonic-$version-amd64.snap"

# shellcheck source=check-composed-tree.sh
source "$root/build/check-composed-tree.sh"

# Everything up to a finished .snap, skipped by --check.
compose_and_build() {
    # --------------------------------------------------------------- preconditions
    step "Checking the composed tree…"
    vst_check_composed_tree "$staging" "$version"
    vst_check_store_metadata "$staging" "$version"

    # ------------------------------------------------------------------- compose
    step "Composing the snapcraft project…"

    rm -rf "$project"
    mkdir -p "$project/snap/gui" "$project/payload"
    cp -a "$staging/." "$project/payload/"

    # Where snapd and the store look for the desktop entry, the listing and the
    # icon. The tree keeps them in desktop/, which is a tarball's layout, not a
    # desktop's.
    share="$project/payload/usr/share"
    install -Dm644 "$staging/desktop/$app_id.metainfo.xml" "$share/metainfo/$app_id.metainfo.xml"
    install -Dm644 "$staging/desktop/$app_id.png"          "$share/icons/hicolor/256x256/apps/$app_id.png"
    # Icon= as a ${SNAP} path, which snapd expands. A bare theme name would need
    # the host's icon theme to find a file that exists only inside the snap.
    install -d "$share/applications"
    sed "s|^Icon=.*|Icon=\${SNAP}/usr/share/icons/hicolor/256x256/apps/$app_id.png|" \
        "$staging/desktop/$app_id.desktop" > "$share/applications/$app_id.desktop"
    cp "$staging/desktop/$app_id.png" "$project/snap/gui/vibesupertonic.png"

    sed -e "s|@VERSION@|$version|g" -e "s|@GRADE@|$grade|g" \
        "$root/build/snap/snapcraft.yaml.in" > "$project/snap/snapcraft.yaml"
    if grep -c '@[A-Z_]*@' "$project/snap/snapcraft.yaml" >/dev/null; then
        die "a placeholder survived in $project/snap/snapcraft.yaml"
    fi
    info "$project"

    if (( compose_only )); then
        step "Composed; not building (--compose-only)."
        exit 0
    fi

    # --------------------------------------------------------------------- build
    step "Building the snap…"

    command -v snapcraft >/dev/null 2>&1 || die "snapcraft is required and was not found.
           sudo snap install snapcraft --classic
           Or run with --compose-only to inspect the project without building."
    command -v unsquashfs >/dev/null 2>&1 || die "unsquashfs is required (squashfs-tools)"

    rm -f "$out"
    mode=()
    (( destructive )) && mode=(--destructive-mode)
    # snapcraft refuses to write outside its project directory, so the snap
    # lands there first and is moved to dist/ after. Found by CI's first run.
    built="$project/$(basename "$out")"
    rm -f "$built"
    (cd "$project" && snapcraft pack "${mode[@]}" --output "$built") || die "snapcraft pack failed"
    [[ -f "$built" ]] && mv -f "$built" "$out"
    [[ -f "$out" ]] || die "snapcraft reported success and produced no $out"
}

if [[ -n "$check_only" ]]; then
    [[ -f "$check_only" ]] || die "no snap at $check_only"
    out="$(cd "$(dirname "$check_only")" && pwd)/$(basename "$check_only")"
    command -v unsquashfs >/dev/null 2>&1 || die "unsquashfs is required (squashfs-tools)"
else
    compose_and_build
fi

# ---------------------------------------------------------------- assertions
step "Checking what came out…"

extract="$root/dist/build-linux/snap-extract"
rm -rf "$extract"
unsquashfs -q -d "$extract" "$out" >/dev/null || die "unsquashfs could not read $out"

meta="$extract/meta/snap.yaml"
[[ -f "$meta" ]] || die "no meta/snap.yaml in the snap"

# The version snapd and the store will show.
meta_version="$(awk '/^version:/ && !seen { v = $2; gsub(/["'"'"']/, "", v); print v; seen = 1 }' "$meta")"
[[ "$meta_version" == "$version" ]] || die "meta/snap.yaml says version '$meta_version', not $version"

confinement="$(awk '/^confinement:/ && !seen { print $2; seen = 1 }' "$meta")"
[[ "$confinement" == strict ]] || die "confinement is '$confinement'. It must be strict: classic needs a
       manual store review this package does not need. See snapcraft.yaml.in."

# The apps block of snap.yaml, one app per line with its keys, so the next two
# checks can read it without a YAML library the build machine may not have.
apps="$(awk '
    /^apps:/ { inapps = 1; next }
    inapps && /^[^ ]/ { inapps = 0 }
    inapps && /^  [A-Za-z0-9-]+:/ { name = $1; sub(/:$/, "", name); line[name] = ""; order[++n] = name; next }
    inapps && name != "" { line[name] = line[name] " " $0 }
    END { for (i = 1; i <= n; i++) print order[i] ":" line[order[i]] }' "$meta")"

for app in vibesupertonic daemon ctl speechd setup; do
    [[ "$apps" == *"$app:"* ]] || die "the snap has no '$app' app. sandbox-setup.sh binds keys and the
       Speech Dispatcher module to /snap/bin/vibesupertonic.<app>; without it they name nothing."
done

# THE HOTKEY STAYS BARE. An extension or a command-chain on ctl wraps every key
# press in a launcher script. It would still work, so nothing else would ever
# complain, and every press would be slower for the life of the package.
# An awk that reads to the end, not one that exits on the match: an early exit
# in a pipeline is the SIGPIPE shape CLAUDE.md forbids.
ctl_line="$(awk -F: '$1 == "ctl" && !seen { print; seen = 1 }' <<<"$apps")"
[[ -n "$ctl_line" ]] || die "could not read the ctl app out of meta/snap.yaml"
[[ "$ctl_line" != *command-chain* ]] || die "the ctl app has a command-chain:
       $ctl_line
       It runs on every hotkey press. Keep extensions off it (snapcraft.yaml.in)."
info "version $version, strict, five apps, and ctl is bare"

# Assertions 3 and 4 again, on the finished snap: snapcraft's stage-packages are
# the one thing here that could drag something in.
found="$(find "$extract/models" -type f -print -quit 2>/dev/null || true)"
[[ -z "$found" ]] || die "the snap contains model files ($found)"
found="$(find "$extract" -name 'libonnxruntime_providers_cuda.so' -print -quit)"
[[ -z "$found" ]] || die "the snap contains libonnxruntime_providers_cuda.so"

# The daemon's native libraries, where the top-level LD_LIBRARY_PATH says.
# Without them the daemon starts, cannot open the audio device, and speaks to
# nobody.
for lib in libpulse.so.0 libX11.so.6 libwayland-client.so.0; do
    [[ -e "$extract/usr/lib/x86_64-linux-gnu/$lib" ]] || die "$lib is not in the snap's usr/lib/x86_64-linux-gnu.
       The daemon loads it by name; see the native-libs part."
done
info "no models, no CUDA provider, and the daemon's three libraries are staged"

# The binaries as packed, and the module's INIT. It answers 399 when espeak/ did
# not land beside it, which is the one thing this repackaging could break.
for binary in vibesupertonicd vst-ctl vst-speechd; do
    reported="$("$extract/$binary" --version 2>/dev/null | tr -d '[:space:]' || true)"
    [[ "$reported" == "$version" ]] || die "$binary inside the snap reports '$reported', not $version"
done
init="$(printf 'INIT\nQUIT\n' | timeout 30 "$extract/vst-speechd" 2>/dev/null | awk '/^(299|399) / { last = $0 } END { print last }' || true)"
[[ "$init" == 299\ * ]] || die "the module inside the snap answered INIT with: ${init:-nothing}"
info "binaries report $version; the module answers INIT"

rm -rf "$extract"

size="$(du -h "$out" | cut -f1)"
step "Done."
info "$out"
info "$size"
printf '\n'
printf 'Try it:    sudo snap install --dangerous %s\n' "$(basename "$out")"
printf 'Hotkeys:   bash /snap/vibesupertonic/current/sandbox-setup.sh bind\n'
printf 'Publish:   snapcraft upload --release=edge %s\n' "$(basename "$out")"

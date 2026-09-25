#!/usr/bin/env bash
#
# Build the Flatpak.
#
#     bash build/pack-tar.sh -v X.Y.Z && bash build/pack-flatpak.sh -v X.Y.Z [--test-install]
#
# Produces dist/VibeSuperTonic-<version>-x86_64.flatpak, a single-file bundle:
#     flatpak install --user dist/VibeSuperTonic-<version>-x86_64.flatpak
#
#   --test-install      also install the bundle for this user, run the checks
#                       that need a real deployment, and uninstall it. CI does.
#   --manifest-only URL write the manifest Flathub builds from, pointing at the
#                       tarball as published at URL, and stop. See below.
#
# ---------------------------------------------------------------------------
# THIS SCRIPT PUBLISHES NOTHING, like pack-appimage.sh and pack-snap.sh. The
# manifest's only source is the Linux TARBALL, pinned by SHA-256. This packer
# checks the tree inside that tarball rather than dist/release-linux, because
# the tarball is what the Flatpak installs, and on Flathub it is the only input
# there is.
#
# FLATHUB. Its build servers take a manifest and fetch every source themselves.
# So a submission is the manifest from --manifest-only, with the tarball's
# GitHub release URL, committed to the flathub/io.github.hananel_hazan.VibeSuperTonic
# repository. The tarball must be published first, and never replaced under the
# same name: the hash pins the bytes.
# ---------------------------------------------------------------------------

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
test_install=0
manifest_url=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version)    version="${2:-}"; shift 2 ;;
        --test-install)  test_install=1; shift ;;
        --manifest-only) manifest_url="${2:-}"; shift 2
                         [[ "$manifest_url" == https://* ]] || { echo "--manifest-only needs an https:// URL" >&2; exit 64; } ;;
        -h|--help)
            sed -n '3,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

if [[ -z "$version" ]]; then
    props="$root/Directory.Build.props"
    [[ -f "$props" ]] || die "Directory.Build.props not found at $props"
    version="$(awk '/<VstVersion>/ { sub(/.*<VstVersion>/, ""); sub(/<\/VstVersion>.*/, ""); gsub(/[[:space:]]/, ""); print; exit }' "$props")"
    [[ -n "$version" ]] || die "no non-empty <VstVersion> in $props"
    info "version not supplied; using <VstVersion> $version from Directory.Build.props"
fi

app_id="io.github.hananel_hazan.VibeSuperTonic"
tarball="$root/dist/VibeSuperTonic-$version-linux-x64.tar.gz"
work="$root/dist/build-linux/flatpak"
manifest="$work/$app_id.yml"
out="$root/dist/VibeSuperTonic-$version-x86_64.flatpak"

# shellcheck source=check-composed-tree.sh
source "$root/build/check-composed-tree.sh"

# --------------------------------------------------------------- preconditions
step "Checking the tarball the Flatpak installs…"

[[ -f "$tarball" ]] || die "no tarball at $tarball.
       Run build/pack-tar.sh -v $version first. The Flatpak installs that archive."

rm -rf "$work"
mkdir -p "$work/tree"
tar -xzf "$tarball" -C "$work/tree" || die "could not extract $tarball"
[[ -d "$work/tree/VibeSuperTonic" ]] || die "the tarball has no VibeSuperTonic/ at its top"

vst_check_composed_tree "$work/tree/VibeSuperTonic" "$version"
vst_check_store_metadata "$work/tree/VibeSuperTonic" "$version"
rm -rf "$work/tree"

sha256="$(sha256sum "$tarball" | cut -d' ' -f1)"
info "tarball sha256 $sha256"

# ------------------------------------------------------------------ manifest
step "Writing the manifest…"

if [[ -n "$manifest_url" ]]; then
    location="url: $manifest_url"
else
    location="path: $tarball"
fi
sed -e "s|@TARBALL_LOCATION@|$location|" -e "s|@TARBALL_SHA256@|$sha256|" \
    "$root/build/flatpak/$app_id.yml.in" > "$manifest"
if grep -c '@[A-Z_]*@' "$manifest" >/dev/null; then
    die "a placeholder survived in $manifest"
fi
info "$manifest"

if [[ -n "$manifest_url" ]]; then
    cp "$manifest" "$root/dist/$app_id.yml"
    step "Done: the manifest for Flathub."
    info "$root/dist/$app_id.yml"
    info "It pins $manifest_url to sha256 $sha256."
    info "Check that the published file has that hash before submitting."
    exit 0
fi

# --------------------------------------------------------------------- build
step "Building the Flatpak…"

for tool in flatpak flatpak-builder; do
    command -v "$tool" >/dev/null 2>&1 || die "$tool is required and was not found."
done

builddir="$work/build"
repo="$work/repo"
# --disable-rofiles-fuse: containers, CI's included, usually have no FUSE, and
# the build does not need it.
flatpak-builder --force-clean --disable-rofiles-fuse --user --install-deps-from=flathub \
    --repo="$repo" "$builddir" "$manifest" || die "flatpak-builder failed"

rm -f "$out"
flatpak build-bundle --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo \
    "$repo" "$out" "$app_id" || die "flatpak build-bundle failed"
[[ -f "$out" ]] || die "build-bundle reported success and produced no $out"

# ---------------------------------------------------------------- assertions
step "Checking what came out…"

files="$builddir/files/lib/vibesupertonic"
[[ -x "$files/vibesupertonicd" ]] || die "the build has no /app/lib/vibesupertonic/vibesupertonicd.
       FlatpakPeer and DaemonLaunch both depend on that path."
for f in "share/applications/$app_id.desktop" "share/metainfo/$app_id.metainfo.xml" \
         "share/icons/hicolor/256x256/apps/$app_id.png" "bin/vibesupertonic"; do
    [[ -e "$builddir/files/$f" ]] || die "the build has no /app/$f"
done
info "the layout FlatpakPeer expects, and the desktop files the stores read"

# THE HOST-SIDE HOTKEY CLIENT. Run from the deployment as the desktop runs it,
# it must find the daemon in the app's shared runtime directory, not in its own.
# If FlatpakPeer failed to read the metadata it would look in
# $XDG_RUNTIME_DIR/vibesupertonic, find nothing, start a SECOND daemon inside the
# sandbox and talk past it. Nothing would report an error. The build directory
# has the same shape as a deployment: files/ beside metadata.
rt="$(mktemp -d)"
said="$(XDG_RUNTIME_DIR="$rt" "$files/vst-ctl" status --no-start 2>&1 || true)"
rm -rf "$rt"
[[ "$said" == *"/app/$app_id/vibesupertonic/ctl.sock"* ]] || die "the host-side vst-ctl did not look in the app's shared runtime directory.
       It said: $said"
info "the host-side vst-ctl looks for the daemon in \$XDG_RUNTIME_DIR/app/$app_id/"

if (( test_install )); then
    step "Installing it, and checking a real deployment…"

    flatpak --user install -y --noninteractive "$out" >/dev/null || die "could not install $out"
    trap 'flatpak --user uninstall -y --noninteractive "$app_id" >/dev/null 2>&1 || true' EXIT

    # Inside: the version, and where the store goes. Asked of the daemon, like
    # every other caller does, because the rule has one owner.
    reported="$(flatpak run --command=/app/lib/vibesupertonic/vibesupertonicd "$app_id" --version 2>/dev/null | tr -d '[:space:]' || true)"
    [[ "$reported" == "$version" ]] || die "the installed daemon reports '$reported', not $version"

    store="$(flatpak run --command=/app/lib/vibesupertonic/vibesupertonicd "$app_id" --print-store 2>/dev/null | tail -1 || true)"
    [[ "$store" == "$HOME/.var/app/$app_id/data/vibesupertonic" ]] || die "the installed daemon puts its store at '$store'.
       Expected $HOME/.var/app/$app_id/data/vibesupertonic. Anywhere under
       /app is read-only, and the first model download would fail."
    info "installed; reports $version; store at ~/.var/app/$app_id/data/vibesupertonic"

    # The module, exactly as the wrapper sandbox-setup.sh writes runs it.
    init="$(printf 'INIT\nQUIT\n' | timeout 60 flatpak run --command=/app/lib/vibesupertonic/vst-speechd "$app_id" 2>/dev/null \
            | awk '/^(299|399) / { last = $0 } END { print last }' || true)"
    [[ "$init" == 299\ * ]] || die "the installed module answered INIT with: ${init:-nothing}"
    info "the module answers INIT inside the sandbox"

    # The real deployment path, which is what sandbox-setup.sh and the hotkey
    # use. It goes through `active`, which the build directory does not have.
    location="$(flatpak --user info --show-location "$app_id")"
    active="$(dirname "$location")/active/files/lib/vibesupertonic"
    [[ -x "$active/vst-ctl" ]] || die "no vst-ctl under the deployment's active link ($active)"
    rt="$(mktemp -d)"
    said="$(XDG_RUNTIME_DIR="$rt" "$active/vst-ctl" status --no-start 2>&1 || true)"
    rm -rf "$rt"
    [[ "$said" == *"/app/$app_id/vibesupertonic/ctl.sock"* ]] || die "from the installed deployment, vst-ctl said: $said"

    # And sandbox-setup.sh, run inside, must refuse to act and print the host
    # command: inside, its writes would land in the sandbox and look like success.
    inside="$(flatpak run --command=sandbox-setup.sh "$app_id" bind 2>&1 || true)"
    [[ "$inside" == *"flatpak info --show-location"* ]] || die "sandbox-setup.sh inside the Flatpak did not print the host command.
       It said: $inside"
    info "the deployment's vst-ctl finds the shared socket; sandbox-setup.sh refuses inside"
fi

size="$(du -h "$out" | cut -f1)"
step "Done."
info "$out"
info "$size"
printf '\n'
printf 'Try it:    flatpak install --user %s\n' "$(basename "$out")"
# shellcheck disable=SC2016  # printed for the user to type, not expanded here
printf 'Hotkeys:   bash "$(flatpak info --show-location %s)/files/lib/vibesupertonic/sandbox-setup.sh" bind\n' "$app_id"
printf 'Flathub:   bash build/pack-flatpak.sh -v %s --manifest-only <published tarball URL>\n' "$version"

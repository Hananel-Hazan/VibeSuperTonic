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
#   --manifest-only URL write the tarball manifest, pointing at the tarball as
#                       published at URL, and stop. NOT what Flathub takes: below.
#   --from-source       build Flathub's manifest instead: everything from source,
#                       offline, no tarball needed. Writes ...-source.flatpak.
#   --flathub TAG       write Flathub's submission (manifest, nuget-sources.json,
#                       flathub.json) pinned to TAG's commit, into dist/flathub/,
#                       and stop.
#   --lint              with --from-source: after the build, run Flathub's own
#                       linter (org.flatpak.Builder) on the submission's manifest
#                       and on the built repository. CI's flathub job does.
#
# ---------------------------------------------------------------------------
# THIS SCRIPT PUBLISHES NOTHING, like pack-appimage.sh and pack-snap.sh. The
# manifest's only source is the Linux TARBALL, pinned by SHA-256. This packer
# checks the tree inside that tarball rather than dist/release-linux, because
# the tarball is what the Flatpak installs, and on Flathub it is the only input
# there is.
#
# FLATHUB builds every submission from source, offline, and does not accept one
# that installs a prebuilt archive (checked 2026-09-26). So Flathub gets the
# second manifest, build/flatpak/<id>.source.yml.in: the .NET SDK extension, the
# NuGet packages pinned in build/flatpak/nuget-sources.json, espeak-ng at
# build-espeak.sh's pin, and pack-tar.sh run inside the build. The tree it
# installs is composed and asserted by the same packer as the tarball's, so
# this script's checks after the build apply to both. --from-source builds it
# here (CI's flathub job does), and --flathub TAG writes what to submit.
# ---------------------------------------------------------------------------

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
test_install=0
manifest_url=""
from_source=0
flathub_tag=""
lint=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version)    version="${2:-}"; shift 2 ;;
        --test-install)  test_install=1; shift ;;
        --manifest-only) manifest_url="${2:-}"; shift 2
                         [[ "$manifest_url" == https://* ]] || { echo "--manifest-only needs an https:// URL" >&2; exit 64; } ;;
        --from-source)   from_source=1; shift ;;
        --flathub)       flathub_tag="${2:-}"; from_source=1; shift 2
                         [[ -n "$flathub_tag" ]] || { echo "--flathub needs a tag, e.g. v0.2.17" >&2; exit 64; } ;;
        --lint)          lint=1; shift ;;
        -h|--help)
            sed -n '3,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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

(( lint )) && [[ -n "$flathub_tag" || $from_source -eq 0 ]] \
    && { echo "--lint goes with --from-source: it lints what that builds" >&2; exit 64; }

if (( from_source )); then
# --------------------------------------------------- the from-source manifest
#
# No tarball: pack-tar.sh runs inside the build, and its assertions with it.
# What can drift here, and is checked before a twenty-minute build:
step "Writing the from-source manifest…"

work="$root/dist/build-flatpak-source"
manifest="$work/$app_id.yml"
out="$root/dist/VibeSuperTonic-$version-x86_64-source.flatpak"
template="$root/build/flatpak/$app_id.source.yml.in"
nuget="$root/build/flatpak/nuget-sources.json"

# THE ESPEAK PIN. build-espeak.sh checks out its PIN inside the build, and the
# manifest supplies the commit. Two copies of one number: when the script's
# moves and the manifest's does not, the offline checkout fails at best.
pin="$(awk -F= '/^PIN=/ { print $2; exit }' "$root/build/build-espeak.sh")"
[[ -n "$pin" ]] || die "no PIN= line in build/build-espeak.sh"
commit="$(awk '/espeak-ng.git/ { want = 1; next } want && /commit:/ { print $2; exit }' "$template")"
[[ "$commit" =~ ^[0-9a-f]{40}$ ]] || die "the manifest's espeak-ng commit is not a full SHA-1: '$commit'"
[[ "$commit" == "$pin"* ]] || die "the manifest builds espeak-ng $commit, but build-espeak.sh pins $pin.
       Put the new pin's full commit in $template."
info "espeak-ng $commit, build-espeak.sh's pin"

# THE PACKAGE LIST. Only a restore inside the SDK can say it is complete (CI's
# flathub job regenerates it and compares); what is checked here is that it
# exists, and pins every package by hash.
[[ -f "$nuget" ]] || die "no $nuget. Generate it: python3 build/flatpak/gen-nuget-sources.py --in-sdk 25.08"
python3 - "$nuget" <<'PY' || die "nuget-sources.json is malformed"
import json, re, sys
src = json.load(open(sys.argv[1]))
assert src, "empty"
for s in src:
    assert s["type"] == "file" and s["url"].startswith("https://api.nuget.org/"), s
    assert re.fullmatch(r"[0-9a-f]{128}", s["sha512"]), s
    assert s["dest"] == "nuget-sources", s
print(f"    {len(src)} NuGet packages, each pinned by SHA-512")
PY

# THE ARCHITECTURES. Flathub builds x86_64 AND aarch64 unless flathub.json says
# otherwise, and this build is x86_64 only: pack-tar.sh publishes linux-x64, and
# the NuGet list pins linux-x64 runtime packs. Without the file, the submission's
# aarch64 build fails and blocks the pull request. If an aarch64 build is ever
# added, this check and the file change together.
flathub_json="$root/build/flatpak/flathub.json"
[[ -f "$flathub_json" ]] || die "no $flathub_json. Flathub would build aarch64 too, and that build cannot succeed."
python3 - "$flathub_json" <<'PY' || die "flathub.json must say {\"only-arches\": [\"x86_64\"]}: this build is linux-x64 only"
import json, sys
d = json.load(open(sys.argv[1]))
assert d.get("only-arches") == ["x86_64"], d
PY
info "flathub.json limits Flathub to x86_64, the one architecture this builds"

if [[ -n "$flathub_tag" ]]; then
    tag_commit="$(git -C "$root" rev-parse --verify -q "$flathub_tag^{commit}")" \
        || die "no tag $flathub_tag here. Tag the release first (CLAUDE.md), then write the submission."
    app_source="      - type: git
        url: https://github.com/Hananel-Hazan/VibeSuperTonic.git
        tag: $flathub_tag
        commit: $tag_commit"
    work="$root/dist/flathub"
    manifest="$work/$app_id.yml"
else
    # This checkout, as it is. What flatpak-builder must not copy: dist/ (its
    # own state is there too), a local espeak build (the git source goes to
    # that path), and the repository's history.
    app_source="      - type: dir
        path: $root
        skip:
          - dist
          - .git
          - .flatpak-builder
          - build/espeak-out"
fi

rm -rf "$work"
mkdir -p "$work"
awk -v src="$app_source" '$0 == "@APP_SOURCE@" { print src; next } { print }' "$template" > "$manifest"
cp "$nuget" "$work/nuget-sources.json"
cp "$flathub_json" "$work/flathub.json"
if grep -c '@[A-Z_]*@' "$manifest" >/dev/null; then
    die "a placeholder survived in $manifest"
fi
info "$manifest"

if [[ -n "$flathub_tag" ]]; then
    # THE SCREENSHOTS, which Flathub's builders download and mirror, and which
    # --lint cannot check (see there): the metainfo points at tag URLs, so they
    # resolve only once the tag is pushed. An unreachable one drops out of the
    # store listing, and Flathub requires at least one.
    metainfo="$root/build/desktop/$app_id.metainfo.xml"
    mapfile -t shots < <(python3 - "$metainfo" <<'PY'
import sys, xml.etree.ElementTree as ET
for img in ET.parse(sys.argv[1]).getroot().iter("image"):
    print(img.text.strip())
PY
)
    (( ${#shots[@]} > 0 )) || die "the metainfo lists no screenshots; Flathub requires at least one"
    for url in "${shots[@]}"; do
        [[ "$url" == *"/$flathub_tag/"* ]] || die "screenshot $url is not pinned to $flathub_tag.
       Flathub mirrors it at build time; a branch URL can change under the listing."
        curl -fsSL -o /dev/null --retry 2 "$url" || die "screenshot $url does not resolve.
       Push the tag first: git push origin $flathub_tag"
    done
    info "${#shots[@]} screenshots, all at $flathub_tag and all reachable"

    step "Done: Flathub's submission, pinned to $flathub_tag ($tag_commit)."
    info "$manifest"
    info "$work/nuget-sources.json"
    info "$work/flathub.json"
    info "All three go into the pull request against flathub/flathub's new-pr branch."
    info "The tag must be pushed before Flathub can fetch it."
    exit 0
fi
else
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
# --state-dir under dist/: the from-source manifest copies this checkout, and a
# state directory inside it would be copied into itself.
flatpak-builder --force-clean --disable-rofiles-fuse --user --install-deps-from=flathub \
    --state-dir="$work/state" --repo="$repo" "$builddir" "$manifest" || die "flatpak-builder failed"

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

# ------------------------------------------------------------ Flathub's linter
#
# What Flathub's CI runs on a submission, and a finding there blocks the pull
# request. Two checks, as Flathub runs them:
#
#   manifest  on the manifest as submitted: git source pinned to a commit, with
#             nuget-sources.json and flathub.json beside it. Not the one built
#             above: that one names this checkout as a `dir`, and its directory
#             holds the build state, which the linter measures ("more than
#             25 MB").
#   repo      on the OSTree repository the build exported: desktop file,
#             metainfo, icons, the ELF architectures, finish-args, size.
#
# The manifest check has NO exceptions. The repo check has two, in
# build/flatpak/lint-exceptions.json, and both are about screenshot MIRRORING,
# which only Flathub's builders do: they copy each screenshot to
# dl.flathub.org/media and commit it to a screenshots/<arch> ref. Outside that
# pipeline the catalogue keeps our own URLs, and the linter says so. Doing the
# mirroring here would mean downloading the screenshots from their tag URLs,
# which do not exist until the release is tagged and pushed. Their reachability
# is checked by --flathub instead, which runs after the push. Nothing else goes
# in that file: a new finding is fixed, or taken to Flathub as an exception
# request, never silenced here.
if (( lint )); then
    step "Running Flathub's linter…"

    lintdir="$work/lint"
    rm -rf "$lintdir"
    mkdir -p "$lintdir"
    head_commit="$(git -C "$root" rev-parse HEAD)"
    lint_source="      - type: git
        url: https://github.com/Hananel-Hazan/VibeSuperTonic.git
        commit: $head_commit"
    awk -v src="$lint_source" '$0 == "@APP_SOURCE@" { print src; next } { print }' "$template" > "$lintdir/$app_id.yml"
    cp "$nuget" "$lintdir/nuget-sources.json"
    cp "$flathub_json" "$lintdir/flathub.json"

    flatpak --user install -y --noninteractive flathub org.flatpak.Builder >/dev/null \
        || die "could not install org.flatpak.Builder, which carries Flathub's linter"
    fbl() { flatpak run --filesystem="$work" --filesystem="$root/build/flatpak:ro" \
                --command=flatpak-builder-lint org.flatpak.Builder "$@"; }

    fbl manifest "$lintdir/$app_id.yml" \
        || die "Flathub's linter refuses the manifest (above). Flathub's CI would block the submission on it."
    info "manifest: clean, no exceptions"
    fbl --exceptions --user-exceptions "$root/build/flatpak/lint-exceptions.json" repo "$repo" \
        || die "Flathub's linter refuses the built repository (above). Flathub's CI would block the submission on it."
    info "repo: clean, apart from the two screenshot-mirroring findings only Flathub's builders can clear"
fi

size="$(du -h "$out" | cut -f1)"
step "Done."
info "$out"
info "$size"
printf '\n'
printf 'Try it:    flatpak install --user %s\n' "$(basename "$out")"
# shellcheck disable=SC2016  # printed for the user to type, not expanded here
printf 'Hotkeys:   bash "$(flatpak info --show-location %s)/files/lib/vibesupertonic/sandbox-setup.sh" bind\n' "$app_id"
printf 'Flathub:   bash build/pack-flatpak.sh -v %s --flathub v%s   (after tagging)\n' "$version" "$version"

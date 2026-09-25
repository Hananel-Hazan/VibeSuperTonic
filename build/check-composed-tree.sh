#!/usr/bin/env bash
#
# The checks every package built FROM the composed tree re-asks of it.
#
# Sourced, never run:
#
#     source build/check-composed-tree.sh
#     vst_check_composed_tree  "$staging" "$version"   # dies on the first failure
#     vst_check_store_metadata "$staging" "$version"
#
# pack-tar.sh composes dist/release-linux/VibeSuperTonic and asserts eleven
# things about it. The snap and the Flatpak are built from that tree, as the
# AppImage is, so they inherit those assertions rather than copying them. What
# this file re-asks is the cheap subset whose failure the SEQUENCE can cause: a
# tarball packed yesterday, a rebuild since, and a store package built from
# whatever is on disk now. pack-appimage.sh asks the same questions inline; it
# predates this file.
#
# Needs the caller to define die() and info().

vst_check_composed_tree() {
    local staging="$1" version="$2" binary reported ctl_bytes found

    [[ -d "$staging" ]] || die "no composed tree at $staging.
       Run build/pack-tar.sh first. This package is built from the tree that
       one composes and publishes nothing itself."

    # Assertion 1, re-asked of THIS tree.
    for binary in vibesupertonicd vibesupertonic-ui vst-ctl vst-speechd; do
        [[ -x "$staging/$binary" ]] || die "$binary is missing from $staging"
        reported="$("$staging/$binary" --version 2>/dev/null | tr -d '[:space:]' || true)"
        [[ "$reported" == "$version" ]] || die "$binary in the composed tree reports '$reported', not $version.
       The tree is from another build. Re-run build/pack-tar.sh -v $version."
    done
    info "all four binaries report $version"

    # Assertion 2. The hotkey runs vst-ctl on every press, in a snap and in a
    # Flatpak just as in the tarball.
    [[ ! -e "$staging/vst-ctl.dll" ]] || die "vst-ctl.dll is beside vst-ctl in the composed tree —
       PublishAot did not take effect and this would ship a managed apphost."
    ctl_bytes=$(stat -c%s "$staging/vst-ctl")
    (( ctl_bytes > 1000000 )) || die "vst-ctl is $ctl_bytes bytes; a NativeAOT build is megabytes."

    # Assertions 3 and 4. Written as `find -print -quit` into a variable rather
    # than piped into `grep -q`: an early-exiting consumer in a pipeline is the
    # shape CLAUDE.md forbids.
    found=""
    [[ -d "$staging/models" ]] && found="$(find "$staging/models" -type f -print -quit)"
    [[ -z "$found" ]] || die "the composed tree contains model files ($found). They download on
       first run behind the OpenRAIL-M acceptance; shipping them would make that screen a lie."

    found="$(find "$staging" -name 'libonnxruntime_providers_cuda.so' -print -quit)"
    [[ -z "$found" ]] || die "libonnxruntime_providers_cuda.so is in the composed tree — 330 MB that
       must not ship. See Directory.Build.targets."
    info "native vst-ctl, no models, no CUDA provider"

    [[ -x "$staging/sandbox-setup.sh" ]] || die "sandbox-setup.sh is missing from the composed tree.
       Without it a snap or Flatpak user has no way to bind the hotkeys."
}

# The store listing. Separate from the tree checks because only the store
# packages publish it.
vst_check_store_metadata() {
    local staging="$1" version="$2"
    local id="io.github.hananel_hazan.VibeSuperTonic"
    local dir="$staging/desktop" newest

    local f
    for f in "$id.desktop" "$id.metainfo.xml" "$id.png"; do
        [[ -s "$dir/$f" ]] || die "desktop/$f is missing from the composed tree"
    done

    # The first <release> is the newest one (AppStream orders them that way, and
    # so does the file). An awk that reads to the end rather than exiting: see
    # CLAUDE.md on early-exiting consumers.
    newest="$(awk 'match($0, /<release [^>]*version="[^"]+"/) && !seen {
                       s = substr($0, RSTART, RLENGTH); sub(/.*version="/, "", s); sub(/".*/, "", s)
                       print s; seen = 1 }' "$dir/$id.metainfo.xml")"
    [[ "$newest" == "$version" ]] || die "the metainfo's newest release is '$newest', not $version.
       Add a <release version=\"$version\" ...> at the top of
       build/desktop/$id.metainfo.xml. The store page shows that entry as the
       current release."
    info "metainfo lists $version as the newest release"

    if command -v appstreamcli >/dev/null 2>&1; then
        appstreamcli validate --no-net "$dir/$id.metainfo.xml" >/dev/null \
            || { appstreamcli validate --no-net "$dir/$id.metainfo.xml" >&2 || true
                 die "the metainfo does not validate"; }
        info "metainfo validates"
    else
        info "appstreamcli not found; metainfo not validated here (the stores will)"
    fi
    if command -v desktop-file-validate >/dev/null 2>&1; then
        desktop-file-validate "$dir/$id.desktop" || die "the desktop entry does not validate"
        info "desktop entry validates"
    fi
}

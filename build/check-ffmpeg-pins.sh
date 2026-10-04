#!/usr/bin/env bash
#
# The store manifests build the export ffmpeg from the sources build-ffmpeg.sh
# pins, and install it where FfmpegDetector looks.
#
#     bash build/check-ffmpeg-pins.sh [SNAPCRAFT_TEMPLATE FLATPAK_TEMPLATE...]
#
# With no arguments, checks the repository's three templates. Exit 0 and a last
# line starting "ok:" when every one agrees.
#
# WHY. The pins live in build-ffmpeg.sh, and the Flatpak manifests must repeat
# them, because flatpak-builder fetches the sources itself (Flathub builds with
# no network, so the script cannot). Two copies of one number drift: bump the
# script and not a manifest, and the Flatpak builds a different ffmpeg from the
# one the snap ships, or fails offline at best. And the install path is a
# contract with FfmpegDetector.BundledPath: move it in a manifest and export
# silently offers WAV only, in that one package.
#
# Run by pack-snap.sh and pack-flatpak.sh before they build, and sabotaged by
# build/ffmpeg-bundle-sabotage.sh.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=build-ffmpeg.sh
source "$here/build-ffmpeg.sh"            # the pins; it returns when sourced

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

if [[ $# -eq 0 ]]; then
    set -- "$here/snap/snapcraft.yaml.in" "$here"/flatpak/*.yml.in
fi

# Every `- type:` item of a manifest as one line: type|url|tag|commit|sha256|dest.
# An awk that reads to the end (no early exit in a pipeline; CLAUDE.md).
sources_of() {
    awk '
        function flush() { if (type != "") print type "|" url "|" tag "|" commit "|" sha "|" dest; type = url = tag = commit = sha = dest = "" }
        /^ *- type: / { flush(); type = $3; next }
        /^ *url: /    { url = $2 }
        /^ *tag: /    { tag = $2 }
        /^ *commit: / { commit = $2 }
        /^ *sha256: / { sha = $2 }
        /^ *dest: /   { dest = $2 }
        /^ *- name: / { flush() }
        END { flush() }' "$1"
}

checked=0
for manifest in "$@"; do
    [[ -f "$manifest" ]] || fail "no manifest at $manifest"
    name="$(basename "$manifest")"
    text="$(cat "$manifest")"

    if [[ "$name" == snapcraft* ]]; then
        # The snap's part fetches with the script's own pins (--fetch verifies
        # them), so all that can drift is the call and where it installs.
        [[ "$text" == *"build-ffmpeg.sh --fetch"* ]] || fail "$name does not build ffmpeg with build-ffmpeg.sh --fetch"
        [[ "$text" == *'--out "$CRAFT_PART_INSTALL/ffmpeg"'* ]] || fail "$name does not install ffmpeg to \$SNAP/ffmpeg,
       which is where FfmpegDetector.BundledPath looks in a snap"
        checked=$((checked + 1))
        continue
    fi

    items="$(sources_of "$manifest")"
    ff="$(awk -F'|' -v u="$FFMPEG_GIT" '$1 == "git" && $2 == u { print }' <<<"$items")"
    [[ -n "$ff" ]] || fail "$name has no git source for $FFMPEG_GIT"
    [[ "$(wc -l <<<"$ff")" -eq 1 ]] || fail "$name has more than one ffmpeg source"
    IFS='|' read -r _ _ tag commit _ _ <<<"$ff"
    [[ "$tag" == "$FFMPEG_TAG" ]] || fail "$name builds ffmpeg tag '$tag', but build-ffmpeg.sh pins $FFMPEG_TAG"
    [[ "$commit" == "$FFMPEG_COMMIT" ]] || fail "$name pins ffmpeg to commit '$commit', but build-ffmpeg.sh pins $FFMPEG_COMMIT"

    lame="$(awk -F'|' -v u="$FFMPEG_LAME_URL" '$1 == "archive" && $2 == u { print }' <<<"$items")"
    [[ -n "$lame" ]] || fail "$name has no archive source for $FFMPEG_LAME_URL"
    IFS='|' read -r _ _ _ _ sha _ <<<"$lame"
    [[ "$sha" == "$FFMPEG_LAME_SHA256" ]] || fail "$name pins LAME to sha256 '$sha', but build-ffmpeg.sh pins $FFMPEG_LAME_SHA256"

    [[ "$text" == *"build-ffmpeg.sh "* ]] || fail "$name does not run build-ffmpeg.sh"
    [[ "$text" == *"--out /app/lib/vibesupertonic/ffmpeg"* ]] || fail "$name does not install ffmpeg to /app/lib/vibesupertonic/ffmpeg,
       which is where FfmpegDetector.BundledPath looks in a Flatpak"
    checked=$((checked + 1))
done

printf 'ok: %d manifests build ffmpeg %s (%s) and LAME %s as build-ffmpeg.sh pins them\n' \
    "$checked" "$FFMPEG_TAG" "$FFMPEG_COMMIT" "$FFMPEG_LAME_VERSION"

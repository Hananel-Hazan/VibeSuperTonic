#!/usr/bin/env bash
#
# Observe build/check-ffmpeg-bundle.sh FAILING, once per clause, and passing on
# the unbroken bundle. A check that has never been seen failing is not evidence.
#
#     bash build/ffmpeg-bundle-sabotage.sh DIR [--rebuild WORK]
#
# DIR is a bundle build-ffmpeg.sh produced (ffmpeg plus its licence files). Each
# case copies it, breaks one thing, and requires the check to fail with the
# words that name that thing. Most breakages wrap the real binary in a shell
# script that misbehaves in one way, so the run takes seconds.
#
#   --rebuild WORK   also rebuild ffmpeg for real WITHOUT the aresample filter,
#                    from the sources build-ffmpeg.sh --work WORK left there
#                    (about a minute). That is the sabotage only a real binary
#                    can stand for: encoders that are present and still cannot
#                    take the daemon's 16-bit samples. And the recipe's own
#                    refusals: a checkout at another commit or release, a LAME
#                    of another version or hash (needs the network), a GPL
#                    configuration.
#
# Always, and hermetic: check-ffmpeg-pins.sh against copies of the three store
# manifests, each broken in one way (a pin, a source, an install path).
#
# The same check runs inside the snap and the Flatpak by being fed on stdin; one
# pass and one failure here go that way too, so that mode is observed as well.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
check="$here/check-ffmpeg-bundle.sh"
good="${1:-}"
rebuild=""
[[ "${2:-}" == --rebuild ]] && rebuild="${3:-}"
[[ -x "$good/ffmpeg" ]] || { echo "usage: ffmpeg-bundle-sabotage.sh DIR [--rebuild WORK]  (DIR from build-ffmpeg.sh --out)" >&2; exit 64; }
good="$(cd "$good" && pwd)"
# shellcheck source=build-ffmpeg.sh
source "$here/build-ffmpeg.sh"            # the pins only; it returns when sourced
set +e                                    # and its set -e must not end this script
version="$FFMPEG_VERSION"

work=$(mktemp -d "${TMPDIR:-/tmp}/vst-ff-sabotage-XXXXXX")
trap 'rm -rf "$work"' EXIT
failures=0

fresh() {
    rm -rf "$work/b"
    mkdir -p "$work/b"
    cp -a "$good/." "$work/b/"
    cp -a "$good/ffmpeg" "$work/real-ffmpeg"
}

# A wrapper in place of the binary: $1 is the shell body, with the real one at $REAL.
wrap() {
    printf '#!/bin/bash\nREAL=%q\n%s\n' "$work/real-ffmpeg" "$1" > "$work/b/ffmpeg"
    chmod 755 "$work/b/ffmpeg"
}

run_check() {   # how=file|stdin, then the check's own arguments
    local how="$1"; shift
    if [[ "$how" == stdin ]]; then bash -s -- "$@" < "$check"; else bash "$check" "$@"; fi
}

expect_pass() {
    local how="$1" out
    fresh
    if out=$(run_check "$how" "$work/b" "$version" 2>&1) && [[ "$out" == ok:* ]]; then
        echo "ok    unbroken bundle passes ($how)"
    else
        echo "FAIL  unbroken bundle was rejected ($how):"
        sed 's/^/        /' <<<"$out"
        failures=$((failures + 1))
    fi
}

# expect_fail <label> <expected-text> <how> <want-version> <mutation...>
expect_fail() {
    local label="$1" needle="$2" how="$3" want="$4"; shift 4
    fresh
    ( cd "$work/b" && "$@" )
    local out rc
    out=$(run_check "$how" "$work/b" "$want" 2>&1); rc=$?
    if [[ $rc -ne 0 && "$out" == *"$needle"* ]]; then
        echo "ok    caught: $label"
    else
        echo "FAIL  NOT caught: $label (rc=$rc)"
        sed 's/^/        /' <<<"$out"
        failures=$((failures + 1))
    fi
}

expect_pass file
expect_pass stdin

# The files.
expect_fail "no ffmpeg at all"                "no executable ffmpeg" file "$version" rm ffmpeg
expect_fail "ffmpeg not executable"           "no executable ffmpeg" file "$version" chmod 644 ffmpeg
expect_fail "the licence file missing"        "LICENSE-FFMPEG.txt is missing" file "$version" rm LICENSE-FFMPEG.txt
expect_fail "LAME's licence text missing"     "COPYING.LAME is missing" stdin "$version" rm COPYING.LAME
expect_fail "the LGPL text empty"             "COPYING.LGPLv2.1 is missing or empty" file "$version" truncate -s0 COPYING.LGPLv2.1

# It runs, and is what the pin says.
expect_fail "a binary that does not run"      "-version failed" file "$version" wrap 'exit 127'
expect_fail "a different ffmpeg version"      "not 0.0.1" file "0.0.1" true
expect_fail "a GPL build" "configured with --enable-gpl" file "$version" wrap \
    'if [[ "$*" == *-version* ]]; then "$REAL" "$@" | sed "s/^configuration:/configuration: --enable-gpl/"; else exec "$REAL" "$@"; fi'
expect_fail "a non-free build" "configured with --enable-nonfree" file "$version" wrap \
    'if [[ "$*" == *-version* ]]; then "$REAL" "$@" | sed "s/^configuration:/configuration: --enable-nonfree/"; else exec "$REAL" "$@"; fi'

# The encoders, one missing at a time.
for e in libmp3lame aac flac; do
    expect_fail "no $e encoder listed" "no '$e' encoder" file "$version" wrap \
        "if [[ \"\$*\" == *-encoders* ]]; then \"\$REAL\" \"\$@\" | sed '/ $e  /d'; else exec \"\$REAL\" \"\$@\"; fi"
done

# The encode.
expect_fail "an encode that fails" "failed" file "$version" wrap \
    'case "$*" in *pipe:0*) cat >/dev/null; echo "Unknown encoder" >&2; exit 1;; *) exec "$REAL" "$@";; esac'
expect_fail "an encode that writes nothing and exits 0" "produced an empty file" file "$version" wrap \
    'case "$*" in *pipe:0*) cat >/dev/null; for a; do o="$a"; done; : > "$o";; *) exec "$REAL" "$@";; esac'
expect_fail "an encode that writes the WAV back out as MP3" "does not start like an MP3" stdin "$version" wrap \
    'case "$*" in *pipe:0*) for a; do o="$a"; done; cat > "$o";; *) exec "$REAL" "$@";; esac'
expect_fail "an AAC that is not in an MP4" "is not an MP4" file "$version" wrap \
    'case "$*" in *"-f ipod"*) for a; do o="$a"; done; cat >/dev/null; printf "ADTSxxxxxxxxxxxx" > "$o";; *) exec "$REAL" "$@";; esac'
expect_fail "a FLAC that is not FLAC" "is not a FLAC file" file "$version" wrap \
    'case "$*" in *"-f flac"*) for a; do o="$a"; done; cat >/dev/null; printf "RIFFxxxxxxxxxxxx" > "$o";; *) exec "$REAL" "$@";; esac'
expect_fail "an ffmpeg that cannot read a pipe (only a seekable file)" "failed" file "$version" wrap \
    'case "$*" in *pipe:0*) if [[ -p /dev/stdin ]]; then cat >/dev/null; echo "pipe:0: Invalid argument" >&2; exit 1; fi; exec "$REAL" "$@";; *) exec "$REAL" "$@";; esac'

# THE MANIFESTS' PINS AND INSTALL PATHS (check-ffmpeg-pins.sh), hermetic: the
# three templates are copied and one is broken at a time.
pins="$here/check-ffmpeg-pins.sh"
snap_t="$here/snap/snapcraft.yaml.in"
pre_t="$here/flatpak/io.github.hananel_hazan.VibeSuperTonic.yml.in"
src_t="$here/flatpak/io.github.hananel_hazan.VibeSuperTonic.source.yml.in"
# expect_pins <label> <expected-text> <which: snap|pre|src> <sed-expression>
expect_pins() {
    local label="$1" needle="$2" which="$3" expr="$4" out rc
    rm -rf "$work/m"; mkdir -p "$work/m"
    cp "$snap_t" "$work/m/snapcraft.yaml.in"; cp "$pre_t" "$work/m/pre.yml.in"; cp "$src_t" "$work/m/src.yml.in"
    local target="$work/m/$which.yml.in" original="$src_t"
    [[ "$which" == pre ]] && original="$pre_t"
    [[ "$which" == snap ]] && { target="$work/m/snapcraft.yaml.in"; original="$snap_t"; }
    sed -i "$expr" "$target"
    if cmp -s "$target" "$original"; then
        echo "FAIL  the sabotage changed nothing: $label"; failures=$((failures + 1)); return
    fi
    out=$(bash "$pins" "$work/m/snapcraft.yaml.in" "$work/m/pre.yml.in" "$work/m/src.yml.in" 2>&1); rc=$?
    if [[ $rc -ne 0 && "$out" == *"$needle"* ]]; then
        echo "ok    caught: $label"
    else
        echo "FAIL  NOT caught: $label (rc=$rc)"
        sed 's/^/        /' <<<"$out"; failures=$((failures + 1))
    fi
}
if out=$(bash "$pins" 2>&1); then echo "ok    the repository's manifests pass"; else echo "FAIL  the repository's manifests:"; sed 's/^/        /' <<<"$out"; failures=$((failures + 1)); fi
expect_pins "the prebuilt manifest at another ffmpeg commit" "pins ffmpeg to commit" pre \
    "s/commit: $FFMPEG_COMMIT/commit: 0000000000000000000000000000000000000000/"
expect_pins "the from-source manifest at another ffmpeg tag" "builds ffmpeg tag 'n8.0'" src "s/tag: $FFMPEG_TAG/tag: n8.0/"
expect_pins "a LAME hash that is not the script's" "pins LAME to sha256" src "s/sha256: $FFMPEG_LAME_SHA256/sha256: ${FFMPEG_LAME_SHA256%?}0/"
expect_pins "the LAME source gone" "has no archive source" pre "s|url: $FFMPEG_LAME_URL|url: https://example.org/lame.tar.gz|"
expect_pins "the ffmpeg source gone" "has no git source" src "s|url: $FFMPEG_GIT|url: https://example.org/ffmpeg.git|"
expect_pins "a Flatpak installing ffmpeg somewhere else" "does not install ffmpeg to /app/lib/vibesupertonic/ffmpeg" pre \
    "s|--out /app/lib/vibesupertonic/ffmpeg|--out /app/bin|"
expect_pins "the snap installing ffmpeg somewhere else" "does not install ffmpeg to \$SNAP/ffmpeg" snap \
    's|--out "$CRAFT_PART_INSTALL/ffmpeg"|--out "$CRAFT_PART_INSTALL/bin"|'
expect_pins "the snap not using the recipe" "does not build ffmpeg with build-ffmpeg.sh --fetch" snap \
    's|build-ffmpeg.sh --fetch|build-ffmpeg.sh|'

# A REAL build missing one filter: every encoder listed, and no export possible,
# because the daemon's interleaved s16 has to become planar for libmp3lame
# (s16p) and aac (fltp). Observed: "'aresample' filter not present".
if [[ -n "$rebuild" ]]; then
    if [[ -d "$rebuild/ffmpeg-src" && -d "$rebuild/lame-src" ]]; then
        sed 's/--enable-filter=aresample,aformat,anull/--enable-filter=anull/' "$here/build-ffmpeg.sh" > "$work/build-nofilter.sh"
        if bash "$work/build-nofilter.sh" --ffmpeg-src "$rebuild/ffmpeg-src" --lame-src "$rebuild/lame-src" \
                --work "$work/rebuild" --out "$work/nofilter" >/dev/null 2>&1; then
            out=$(bash "$check" "$work/nofilter" "$version" 2>&1); rc=$?
            if [[ $rc -ne 0 && "$out" == *"aresample' filter not present"* ]]; then
                echo "ok    caught: a real build without the aresample filter"
            else
                echo "FAIL  NOT caught: a real build without the aresample filter (rc=$rc)"
                sed 's/^/        /' <<<"$out"; failures=$((failures + 1))
            fi
        else
            echo "FAIL  could not rebuild without aresample"; failures=$((failures + 1))
        fi
    else
        echo "FAIL  --rebuild $rebuild has no ffmpeg-src/ and lame-src/ (run build-ffmpeg.sh --fetch --work $rebuild first)"
        failures=$((failures + 1))
    fi
else
    echo "skip  a real build without the aresample filter (pass --rebuild WORK)"
fi

# THE RECIPE'S OWN REFUSALS, which run before or during a build: the sources are
# what the pins say, and the result is LGPL. Also only with --rebuild WORK.
# expect_recipe_fail <label> <expected-text> <sed-expression> <ffmpeg-src> <lame-src>
expect_recipe_fail() {
    local label="$1" needle="$2" expr="$3" fsrc="$4" lsrc="$5" out rc
    sed "$expr" "$here/build-ffmpeg.sh" > "$work/build-sabotaged.sh"
    out=$(bash "$work/build-sabotaged.sh" --ffmpeg-src "$fsrc" --lame-src "$lsrc" \
              --work "$work/recipe" --out "$work/recipe-out" 2>&1); rc=$?
    if [[ $rc -ne 0 && "$out" == *"$needle"* ]]; then
        echo "ok    caught: $label"
    else
        echo "FAIL  NOT caught: $label (rc=$rc)"
        tail -5 <<<"$out" | sed 's/^/        /'; failures=$((failures + 1))
    fi
}
if [[ -n "$rebuild" && -d "$rebuild/ffmpeg-src" && -d "$rebuild/lame-src" ]]; then
    fs="$rebuild/ffmpeg-src"; ls_="$rebuild/lame-src"
    mkdir -p "$work/old-ffmpeg" "$work/old-lame"
    echo 8.1.2 > "$work/old-ffmpeg/RELEASE"
    printf "PACKAGE_VERSION='3.99.5'\n" > "$work/old-lame/configure"
    expect_recipe_fail "an ffmpeg checkout at another commit" "the ffmpeg checkout is at" \
        's/^FFMPEG_COMMIT=.*/FFMPEG_COMMIT=0000000000000000000000000000000000000000/' "$fs" "$ls_"
    expect_recipe_fail "an ffmpeg tree of another release" "not $FFMPEG_VERSION" 's/^$//' "$work/old-ffmpeg" "$ls_"
    expect_recipe_fail "a LAME tree of another version" "LAME source is version '3.99.5'" 's/^$//' "$fs" "$work/old-lame"
    expect_recipe_fail "a GPL configuration" "not LGPL 2.1 or later" \
        's/^    --disable-everything$/    --disable-everything --enable-gpl/' "$fs" "$ls_"
    # --fetch's hash check, which needs the network (a clone and a download).
    sed 's/^FFMPEG_LAME_SHA256=./FFMPEG_LAME_SHA256=0/' "$here/build-ffmpeg.sh" > "$work/build-badsha.sh"
    out=$(bash "$work/build-badsha.sh" --fetch --work "$work/fetch" --out "$work/fetch-out" 2>&1); rc=$?
    if [[ $rc -ne 0 && "$out" == *"LAME's tarball hashes to"* ]]; then
        echo "ok    caught: a LAME tarball that does not match its pin (--fetch)"
    else
        echo "FAIL  NOT caught: a LAME tarball that does not match its pin (rc=$rc)"
        tail -5 <<<"$out" | sed 's/^/        /'; failures=$((failures + 1))
    fi
fi

if (( failures )); then
    echo "$failures sabotage case(s) not caught"
    exit 1
fi
echo "all sabotage cases caught"

#!/usr/bin/env bash
#
# The AppImage's two new assertions, watched failing.
#
#     bash spike/speechd-s4-install/appimage-sabotage.sh
#
# Both defend one mechanism: speech-dispatcher execs ~/.local/bin/vst-speechd,
# a symlink to the image, and AppRun's $ARGV0 case is what turns that into the
# module. Without it the symlink falls through to AppRun's default branch and
# OPENS THE WINDOW — once per utterance, on the machine of someone using a
# screen reader.
#
# Runs against a mutated COPY of build/pack-appimage.sh, never the tree, and
# reuses the composed tree that is already on disk (~1 min per case).
set -uo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

VERSION="$(sed -n 's:.*<VstVersion>\(.*\)</VstVersion>.*:\1:p' Directory.Build.props | head -1 | tr -d '[:space:]')"
[[ -d dist/release-linux/VibeSuperTonic ]] || { echo "no composed tree — run build/pack-tar.sh first" >&2; exit 2; }

# The copy must live in build/: pack-appimage.sh finds the repository root from
# its own location, so a copy in /tmp looks for a composed tree at //dist.
COPY=build/.s4-sabotage-pack-appimage.sh
pass=0; fail=0

run_case() {
    local label="$1" find="$2" repl="$3" expect="$4" out rc
    python3 - "$find" "$repl" <<'PY'
import sys, pathlib
src = pathlib.Path("build/pack-appimage.sh").read_text()
find, repl = sys.argv[1], sys.argv[2]
assert find in src, "patch missed: " + find[:60]
pathlib.Path("build/.s4-sabotage-pack-appimage.sh").write_text(src.replace(find, repl, 1))
PY
    out="$(bash "$COPY" -v "$VERSION" 2>&1)"; rc=$?
    if (( rc == 0 )); then
        printf '  \033[31mNOT CAUGHT\033[0m   %s\n' "$label"; fail=$((fail + 1))
    elif grep -qi -- "$expect" <<< "$out"; then
        printf '  \033[32mcaught\033[0m       %s\n' "$label"; pass=$((pass + 1))
    else
        printf '  \033[33mMISATTRIBUTED\033[0m %s\n' "$label"
        printf '               %s\n' "$(grep -i 'error\|die\|not ' <<< "$out" | head -2 | tr '\n' ' ')"
        fail=$((fail + 1))
    fi
}

echo "sabotaging build/pack-appimage.sh (version $VERSION)"
echo

# The case itself. This is the whole mechanism.
run_case "AppRun has no \$ARGV0 case for vst-speechd" \
    '    vst-speechd) exec "$APP/vst-speechd" "$@" ;;' \
    '' \
    "vst-speechd"

# The module is in the tree but the espeak beside it is not reachable from
# inside the mount — a squashfs that dropped a directory, which no listing of
# the AppDir would show and no --version would notice.
run_case "the phonemiser is not in the image" \
    'cp -a "$staging/." "$appdir/usr/lib/vibesupertonic/"' \
    'cp -a "$staging/." "$appdir/usr/lib/vibesupertonic/"; rm -rf "$appdir/usr/lib/vibesupertonic/espeak"' \
    "INIT"

rm -f "$COPY"
printf '\n%s caught, %s not\n' "$pass" "$fail"
exit $(( fail > 0 ))

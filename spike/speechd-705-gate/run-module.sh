#!/bin/bash
# S2's end-to-end check: the real vst-speechd, driven by the machine's real
# speech-dispatcher, with real audio.
#
# Same isolation as run-local.sh (-C -S -P -L, nothing touching the user's own
# configuration) and the same rule: ALWAYS measure the system module beside ours,
# because "ours hung" only means something when theirs did not.
#
#   usage: run-module.sh <dir containing vst-speechd, vst-ctl and espeak/>
set -u
INSTALL="${1:?usage: run-module.sh <install dir>}"
MODULE="$INSTALL/vst-speechd"
[ -x "$MODULE" ] || { echo "no vst-speechd at $MODULE"; exit 1; }

RUN="${TMPDIR:-/tmp}/speechd-module-test"
rm -rf "$RUN"; mkdir -p "$RUN/config" "$RUN/out"

cat > "$RUN/config/speechd.conf" <<CONF
LogLevel 5
AudioOutputMethod "pulse"
AddModule "espeak-ng"      "sd_espeak-ng" "espeak-ng.conf"
AddModule "vibesupertonic" "$MODULE"      ""
DefaultModule "espeak-ng"
CONF

# AN ISOLATED DAEMON, AND THIS IS NOT OPTIONAL. vst-ctl finds the daemon through
# $XDG_RUNTIME_DIR, so without this the module talks to whatever VibeSuperTonic
# the person running the test already has open — which on the machine this was
# written on was a 0.2.10 AppImage, mid-utterance, that has no `render` verb at
# all. The test then measures someone else's daemon and reports a fallback that
# is really a version mismatch.
#
# PULSE_SERVER is pinned back to the real one by hand, because PulseAudio finds
# its socket the same way and moving XDG_RUNTIME_DIR would otherwise take the
# audio with it — leaving a test that hangs for want of a sound card, which is
# exactly the false negative run.sh already taught us to distrust.
export PULSE_SERVER="${PULSE_SERVER:-unix:${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/pulse/native}"
export XDG_RUNTIME_DIR="$RUN/runtime"
mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"

export SPEECHD_ADDRESS="unix_socket:$RUN/speechd.sock"
speech-dispatcher -d -t 0 -l 5 \
  -C "$RUN/config" -S "$RUN/speechd.sock" -P "$RUN/speechd.pid" -L "$RUN/out"

for _ in $(seq 20); do [ -S "$RUN/speechd.sock" ] && break; sleep 0.5; done
[ -S "$RUN/speechd.sock" ] || { echo "FAIL: speech-dispatcher did not start"; exit 1; }

# TRAP 1, AND IT IS THE FIRST THING TO CHECK. Adding a module must not remove
# every other one. If espeak-ng is missing from this list, the install broke the
# user's working screen reader and nothing else in this script matters.
mods="$(spd-say -O 2>/dev/null | tr '\n' ' ')"
echo "modules offered: $mods"
case "$mods" in
  *espeak-ng*vibesupertonic*|*vibesupertonic*espeak-ng*) echo "  both modules present" ;;
  *) echo "  FAIL: expected both espeak-ng and vibesupertonic"; ;;
esac
echo

say() {  # say <label> <module> <spd-say args...>
    local label="$1" mod="$2"; shift 2
    local start ms rc
    start=$(date +%s%N)
    timeout 20 spd-say -o "$mod" -w "$@"
    rc=$?
    ms=$(( ($(date +%s%N) - start) / 1000000 ))
    case $rc in
      0)   printf '  %-34s returned in %5s ms\n' "$label" "$ms" ;;
      124) printf '  %-34s HUNG (timed out at 20 s)\n' "$label" ;;
      *)   printf '  %-34s rc=%s after %s ms\n' "$label" "$rc" "$ms" ;;
    esac
}

say "espeak-ng   SPEAK (the control)" espeak-ng      "hello from the system module"
say "ours        SPEAK -> neural"     vibesupertonic "hello from the module under test"
say "ours        CHAR  -> espeak"     vibesupertonic -c "a"
say "ours        KEY   -> espeak"     vibesupertonic -k "Control_L"
say "ours        empty message"       vibesupertonic ""

echo
echo "what the module reported on stderr:"
sed 's/^/  /' "$RUN/out/vibesupertonic.log" 2>/dev/null | tail -15 || echo "  (none)"

echo
echo "did the server accept our audio blocks:"
grep -cE '705-num_samples' "$RUN/out/speech-dispatcher.log" 2>/dev/null \
    | sed 's/^/  blocks parsed by the server: /'

kill "$(cat "$RUN/speechd.pid" 2>/dev/null)" 2>/dev/null; sleep 1

# The daemon the module started, in the isolated runtime dir. Stopped by asking
# it rather than by pkill, for the reason install.sh gives: a pattern match would
# find the user's own daemon too.
"$INSTALL/vst-ctl" --no-start shutdown >/dev/null 2>&1 || true

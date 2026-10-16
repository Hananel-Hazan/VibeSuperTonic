#!/bin/bash
set -u
INSTALL=/tmp/vst-s3-install
MODULE="$INSTALL/vst-speechd"
RUN=/tmp/vst-s3-speechd; rm -rf "$RUN"; mkdir -p "$RUN/config" "$RUN/out"
cat > "$RUN/config/speechd.conf" <<CONF
LogLevel 5
AudioOutputMethod "pulse"
AddModule "espeak-ng"      "sd_espeak-ng" "espeak-ng.conf"
AddModule "vibesupertonic" "$MODULE"      ""
DefaultModule "espeak-ng"
CONF
export PULSE_SERVER="${PULSE_SERVER:-unix:${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/pulse/native}"
export XDG_RUNTIME_DIR="$RUN/runtime"; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
export SPEECHD_ADDRESS="unix_socket:$RUN/speechd.sock"
speech-dispatcher -d -t 0 -l 5 -C "$RUN/config" -S "$RUN/speechd.sock" -P "$RUN/speechd.pid" -L "$RUN/out" 2>/dev/null
for _ in $(seq 20); do [ -S "$RUN/speechd.sock" ] && break; sleep 0.5; done
[ -S "$RUN/speechd.sock" ] || { echo "FAIL: speechd did not start"; exit 1; }

echo "=== TRAP 1: both modules still offered ==="
spd-say -O

echo; echo "=== the voice list a client sees (row count) ==="
timeout 15 spd-say -o vibesupertonic -L | tail -n +2 | wc -l
echo "--- the German rows ---"
timeout 15 spd-say -o vibesupertonic -L | awk '$2=="de" || $2=="de-de"'
echo "--- the echo row ---"
timeout 15 spd-say -o vibesupertonic -L | grep echo

echo; echo "=== speaking ==="
for args in "-t female1 -l de" "-y vibesupertonic-echo" "-c a"; do
  start=$(date +%s%N)
  timeout 20 spd-say -o vibesupertonic -w $args "hallo welt" >/dev/null 2>&1; rc=$?
  printf '  %-28s rc=%s in %s ms\n' "$args" "$rc" "$(( ($(date +%s%N) - start) / 1000000 ))"
done

echo; echo "=== what the module reported ==="
tail -6 "$RUN/out/vibesupertonic.log" 2>/dev/null | sed 's/^/  /'
kill "$(cat "$RUN/speechd.pid" 2>/dev/null)" 2>/dev/null; sleep 1
"$INSTALL/vst-ctl" --no-start shutdown >/dev/null 2>&1 || true

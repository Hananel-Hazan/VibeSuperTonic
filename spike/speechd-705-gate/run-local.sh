#!/bin/bash
# The half of the gate a container cannot answer.
#
# run.sh proves the 705 block is ACCEPTED, on the oldest supported distro. It
# cannot prove the utterance COMPLETES, because a bare container has no audio
# device: `spd-say -w` times out there for the distro's own sd_espeak-ng too, so
# a timeout in that environment says nothing about the module under test.
#
# This runs against the machine's real speech-dispatcher and real audio, fully
# isolated from the user's own configuration (-C -S -P -L), and always measures
# the SYSTEM module beside ours — because the only meaningful reading of "ours
# hung" is "ours hung and theirs did not".
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
RUN="${TMPDIR:-/tmp}/speechd-local-gate"
rm -rf "$RUN"; mkdir -p "$RUN/config" "$RUN/out"

cat > "$RUN/config/speechd.conf" <<CONF
LogLevel 5
AudioOutputMethod "pulse"
AddModule "espeak-ng" "sd_espeak-ng" "espeak-ng.conf"
AddModule "probe"     "$HERE/probe-module.py" ""
DefaultModule "espeak-ng"
CONF

export PROBE_LOG="$RUN/out/probe-recv.log"
export SPEECHD_ADDRESS="unix_socket:$RUN/speechd.sock"

speech-dispatcher -d -t 0 -l 5 \
  -C "$RUN/config" -S "$RUN/speechd.sock" -P "$RUN/speechd.pid" -L "$RUN/out"

for _ in $(seq 20); do [ -S "$RUN/speechd.sock" ] && break; sleep 0.5; done
[ -S "$RUN/speechd.sock" ] || { echo "FAIL: no socket"; exit 1; }

echo "speech-dispatcher $(speech-dispatcher -v | head -1 | awk '{print $3}'), isolated in $RUN"
echo "modules offered: $(spd-say -O 2>/dev/null | tr '\n' ' ')"
echo

for mod in espeak-ng probe; do
    start=$(date +%s%N)
    timeout 15 spd-say -o "$mod" -w "hello from the gate"
    rc=$?
    ms=$(( ($(date +%s%N) - start) / 1000000 ))
    case $rc in
      0)   verdict="RETURNED in ${ms} ms" ;;
      124) verdict="HUNG (timed out at 15 s)" ;;
      *)   verdict="rc=$rc after ${ms} ms" ;;
    esac
    printf '  spd-say -o %-10s -w   %s\n' "$mod" "$verdict"
done

echo
kill "$(cat "$RUN/speechd.pid" 2>/dev/null)" 2>/dev/null
sleep 1
echo "module saw:"; sed 's/^/  /' "$PROBE_LOG" 2>/dev/null | tail -12

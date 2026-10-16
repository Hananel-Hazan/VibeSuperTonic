#!/bin/bash
# Runs inside a bare ubuntu:22.04 container. Answers SPEECHD-PLAN's B gate:
# does speech-dispatcher 0.11.1 accept audio returned with `705 AUDIO`?
set -u
export DEBIAN_FRONTEND=noninteractive
rm -f /gate/out/*.log

echo "===== the version under test ====="
apt-get update -qq >/dev/null
apt-get install -y -qq --no-install-recommends \
    speech-dispatcher speech-dispatcher-espeak-ng python3 >/dev/null 2>&1
dpkg -s speech-dispatcher | grep -E '^Version'

echo
echo "===== static: does the 0.11 SERVER parse a 705 audio block? ====="
strings /usr/bin/speech-dispatcher | grep -E '705-|num_samples|big_endian|Got audio parameter' \
    || echo "  NONE — this server has no 705 parser"

echo
echo "===== behavioural: a module that returns audio instead of playing it ====="
mkdir -p /root/.config/speech-dispatcher /gate/out
cat > /root/.config/speech-dispatcher/speechd.conf <<'EOF'
LogLevel 5
AudioOutputMethod "alsa"
AddModule "espeak-ng" "sd_espeak-ng" "espeak-ng.conf"
AddModule "probe"     "/gate/probe-module.py" ""
DefaultModule "probe"
EOF

speech-dispatcher -d -t 0 -l 5 -L /gate/out
for _ in $(seq 20); do [ -S /root/.cache/speech-dispatcher/speechd.sock ] && break; sleep 0.5; done

echo "-- modules the server offers --"
timeout 20 spd-say -O; echo "   spd-say -O rc=$?"
echo "-- speak through the probe --"
timeout 30 spd-say -o probe -w "hello from the gate"; echo "   spd-say rc=$?"
sleep 2
echo "-- and does the system module still answer alongside ours (trap 1) --"
timeout 30 spd-say -o espeak-ng -w "system voice"; echo "   spd-say rc=$?"
sleep 1
pkill -f 'speech-dispatcher -d'; sleep 1

echo
echo "===== the handshake, as the module saw it ====="
cat /gate/out/probe-recv.log 2>/dev/null || echo "  (module never ran)"

echo
echo "===== what the server did with the 705 block ====="
grep -E -i 'audio parameter|705|num_samples|server-side|Playing|alsa|probe.*(error|fail)' \
    /gate/out/speech-dispatcher.log 2>/dev/null | tail -40 || echo "  (no server log)"

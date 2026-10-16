#!/bin/bash
# What does speech-dispatcher send a module when a client selects a voice?
#
# Answered 2026-08-28 on this machine's 0.12.1. probe.py logs every byte the
# server sends and answers LIST VOICES with a deliberately varied list, so the
# reply to each way of selecting one is visible in out/probe-recv.log.
#
#   usage: bash spike/speechd-s3-voices/run-probe.sh
set -u
S="$(cd "$(dirname "$0")" && pwd)"

# RUN DIR MUST BE SHORT. AF_UNIX truncates at ~108 bytes and speech-dispatcher
# dies with "Fatal error [speechd.c:1001]:Can't bind local socket" — which reads
# like a permissions problem and is not. A run directory under a long scratch
# path fails here every time.
RUN="/tmp/vst-s3-probe"; rm -rf "$RUN"; mkdir -p "$RUN/config" "$RUN/out"

cat > "$RUN/probe" <<EOF
#!/bin/bash
exec /usr/bin/python3 "$S/probe.py"
EOF
chmod +x "$RUN/probe"
export PROBE_LOG="$RUN/out/probe-recv.log"

cat > "$RUN/config/speechd.conf" <<CONF
LogLevel 5
AudioOutputMethod "pulse"
AddModule "espeak-ng" "sd_espeak-ng" "espeak-ng.conf"
AddModule "probe"     "$RUN/probe"   ""
DefaultModule "espeak-ng"
CONF

# PULSE_SERVER pinned by hand: PulseAudio finds its socket through
# XDG_RUNTIME_DIR too, so moving that would take the audio with it and leave a
# test that hangs for want of a sound card.
export PULSE_SERVER="${PULSE_SERVER:-unix:${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/pulse/native}"
export XDG_RUNTIME_DIR="$RUN/runtime"; mkdir -p "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
export SPEECHD_ADDRESS="unix_socket:$RUN/speechd.sock"
speech-dispatcher -d -t 0 -l 5 -C "$RUN/config" -S "$RUN/speechd.sock" -P "$RUN/speechd.pid" -L "$RUN/out" 2>/dev/null
for _ in $(seq 20); do [ -S "$RUN/speechd.sock" ] && break; sleep 0.5; done
[ -S "$RUN/speechd.sock" ] || { echo "FAIL: speech-dispatcher did not start"; exit 1; }

echo "=== the voice list a client sees ==="
timeout 10 spd-say -o probe -L

echo; echo "=== selecting a voice, each of the ways a client can ==="
timeout 10 spd-say -o probe -w -y en_US-lessac-medium "one"   # by name
timeout 10 spd-say -o probe -w -t female1              "two"   # symbolic
timeout 10 spd-say -o probe -w -l de                   "drei"  # language only
timeout 10 spd-say -o probe -w -y no-such-voice        "four"  # a name we never listed
for t in child_female child_male male3 female3; do
    timeout 10 spd-say -o probe -w -t "$t" "x" >/dev/null 2>&1
done

kill "$(cat "$RUN/speechd.pid" 2>/dev/null)" 2>/dev/null; sleep 1

echo; echo "=== what the module was told ==="
grep -E "^>>   (voice|language|synthesis_voice)=" "$RUN/out/probe-recv.log"
echo
echo "full transcript: $RUN/out/probe-recv.log"

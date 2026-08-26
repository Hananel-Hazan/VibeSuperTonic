#!/usr/bin/env bash
# VibeSuperTonic — pin the CPU while the GPU is unusable, and let go when it is not.
#
#   bash vst-gpu-guard.sh [--data <dir>] [--dry-run] [-q]
#
# WHY THIS EXISTS. On 2026-08-26 an RTX A2000 entered "GPU requires reset". The
# driver still loaded, the device still enumerated, ONNX Runtime still built a
# CUDA session — and every render died on "CUDA failure 100: no CUDA-capable
# device is detected". The daemon now survives that on its own (it falls back to
# the CPU on the first failure and stays there for the life of the process), but
# surviving is not the same as being right:
#
#   - Each daemon start pays the failure again: a CUDA session is built, the
#     first render fails, the session is torn down and rebuilt on the CPU. That
#     is seconds, on the first hotkey press, every time.
#   - `vst-ctl benchmark` will happily sweep a GPU that cannot render and write
#     the result into benchmark.json.
#   - Nothing tells the user their GPU is broken. They just have a slower day.
#
# So this decides the question BEFORE the daemon has to, writes the answer into
# settings.json, and — the half that matters for not being annoying — takes it
# back out again once the GPU works. A laptop dGPU in this state is fixed by a
# reboot (nvidia-smi -r answers "Not Supported" when a display is attached), so
# "it recovers while nobody is looking" is the normal case, not the rare one.
#
# WHAT IT WILL NOT DO. It never overrides a preference the user set by hand. If
# settings.json says "cpu" or "gpu" and this script did not write it, it is left
# alone and the script says so. The marker file beside settings.json is how it
# tells its own edit from yours.
#
# Run it at login, or on a timer:
#
#   systemd --user timer, cron, or KDE Autostart
#   bash vst-gpu-guard.sh -q

set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

data=""
dry=0
quiet=0

while (( $# )); do
    case "$1" in
        --data) data="${2:?--data needs a directory}"; shift 2 ;;
        --dry-run) dry=1; shift ;;
        -q|--quiet) quiet=1; shift ;;
        -h|--help) sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

say() { (( quiet )) || echo "$@"; }
die() { echo "vst-gpu-guard: $*" >&2; exit 1; }

command -v python3 >/dev/null || die "python3 is required."

# ---------------------------------------------------------------- where things are
#
# The data directory is resolved by the daemon, not by this script. The rule has
# four branches (beside the AppImage, beside the binary, XDG, and "an existing
# store beats a preferred location") and reimplementing it in bash is how a
# script ends up confidently editing a settings.json that nothing reads. So: ask
# a running daemon, and fall back to looking beside this script, which is where
# the tarball puts both.
ctl=""
for candidate in "$here/vst-ctl" "$(command -v vst-ctl 2>/dev/null || true)"; do
    [[ -n "$candidate" && -x "$candidate" ]] && { ctl="$candidate"; break; }
done

if [[ -z "$data" ]]; then
    if [[ -n "$ctl" ]]; then
        # --no-start deliberately: starting a daemon to ask it where its files are
        # would, on a broken GPU, be starting the very thing we are here to spare.
        data=$("$ctl" --no-start config 2>/dev/null \
            | python3 -c 'import sys,json
try: print(json.load(sys.stdin).get("DataDir",""))
except Exception: pass' || true)
    fi
fi

[[ -z "$data" && -d "$here/data" ]] && data="$here/data"

[[ -n "$data" ]] || die "could not find the data directory. Pass --data <dir>."
[[ -d "$data" ]] || die "$data does not exist."

settings="$data/settings.json"
marker="$data/gpu-guard.json"

# ---------------------------------------------------------------- is the GPU usable
#
# Three questions, cheapest first, and each one has been seen to be the only one
# that fires:
#
#   1. Is there a driver at all? No nvidia-smi is not a fault — it is a machine
#      without an NVIDIA GPU, which is the ordinary case and not ours to fix.
#   2. Does the driver think the device needs a reset? This is the state that
#      started all this. It appears in `nvidia-smi -q` as "GPU requires reset"
#      against Channel Repair Pending / TPC Repair Pending, and it is invisible
#      to `nvidia-smi -L`, which cheerfully lists the card by name.
#   3. Can it still answer an ordinary query? A card that has fallen off the bus
#      reports ERR! or [N/A] for temperature and utilisation while still being
#      listed. Checked last because it is the noisiest signal — a headless or
#      MIG-partitioned card can legitimately answer N/A for some fields, so this
#      only fires when the query fails outright or every field is unreadable.
gpu_state() {
    if ! command -v nvidia-smi >/dev/null 2>&1; then
        echo "absent:no nvidia-smi on this machine"
        return
    fi

    local q
    if ! q=$(nvidia-smi -q 2>&1); then
        echo "broken:nvidia-smi could not query the driver"
        return
    fi

    if grep -q 'GPU requires reset' <<<"$q"; then
        echo "broken:the driver reports \"GPU requires reset\" — a reboot is what clears this"
        return
    fi

    if grep -qiE '(fallen off the bus|has fallen off)' <<<"$q"; then
        echo "broken:the device has fallen off the bus"
        return
    fi

    local probe
    if ! probe=$(nvidia-smi --query-gpu=name,temperature.gpu,utilization.gpu \
                     --format=csv,noheader 2>&1); then
        echo "broken:nvidia-smi could not read the device"
        return
    fi

    # Every readable field unreadable — as opposed to one of them, which several
    # legitimate configurations produce.
    if grep -qE '(ERR!|Unknown Error)' <<<"$probe"; then
        echo "broken:the device answers ERR! to an ordinary query"
        return
    fi

    echo "ok:"
}

state_line=$(gpu_state)
state="${state_line%%:*}"
why="${state_line#*:}"

# ---------------------------------------------------------------- settings.json
#
# Edited through python3 rather than sed: this file is the user's, it has a
# schema, and it may carry keys this script has never heard of (the settings type
# keeps an extension bag exactly so a newer Control Panel and an older daemon can
# share one file). A regex edit would eventually eat one of them.
read_provider() {
    python3 -c '
import json,sys
try:
    with open(sys.argv[1]) as f: print(json.load(f).get("Provider","auto"))
except FileNotFoundError: print("auto")
except Exception: print("")
' "$settings"
}

write_provider() {
    python3 -c '
import json,sys,os,tempfile
path, value = sys.argv[1], sys.argv[2]
try:
    with open(path) as f: doc = json.load(f)
except FileNotFoundError:
    doc = {}
doc["Provider"] = value
d = os.path.dirname(path) or "."
fd, tmp = tempfile.mkstemp(dir=d, prefix=".settings-", suffix=".json")
with os.fdopen(fd, "w") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
os.replace(tmp, path)
' "$settings" "$1"
}

current=$(read_provider)
[[ -n "$current" ]] || die "$settings is not readable as JSON. Not touching it."

pinned_by_us=0
restore_to="auto"
if [[ -f "$marker" ]]; then
    pinned_by_us=1
    restore_to=$(python3 -c '
import json,sys
try:
    with open(sys.argv[1]) as f: print(json.load(f).get("previousProvider","auto"))
except Exception: print("auto")
' "$marker")
fi

apply() {
    local value="$1"
    if (( dry )); then
        say "would set Provider=$value (currently $current)"
        return
    fi
    write_provider "$value"
    # --no-start: if no daemon is running there is nothing to reload, and the
    # next one to start will read the file anyway.
    [[ -n "$ctl" ]] && "$ctl" --no-start reload >/dev/null 2>&1 || true
}

case "$state" in
    absent)
        say "no NVIDIA GPU here; nothing to guard."
        exit 0
        ;;

    broken)
        if [[ "$current" == "cpu" ]]; then
            (( pinned_by_us )) && say "GPU still unusable ($why); already pinned to the CPU." \
                               || say "GPU unusable ($why), and Provider is already cpu by hand."
            exit 0
        fi

        if [[ "$current" == "gpu" ]]; then
            # An explicit "gpu" is a person overriding a measurement, which they
            # are entitled to do. Overriding them back would be this script
            # deciding it knows better, silently, about the one setting they went
            # out of their way to set.
            say "GPU unusable ($why), but Provider is set to gpu by hand — leaving it."
            say "The daemon will fall back to the CPU per utterance; set Provider=auto to let this script help."
            exit 0
        fi

        say "GPU unusable: $why"
        say "pinning Provider=cpu (was $current)."
        if (( ! dry )); then
            python3 -c '
import json,sys,datetime
with open(sys.argv[1], "w") as f:
    json.dump({
        "pinnedCpuUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "previousProvider": sys.argv[2],
        "reason": sys.argv[3],
    }, f, indent=2)
    f.write("\n")
' "$marker" "$current" "$why"
        fi
        apply cpu
        ;;

    ok)
        if (( ! pinned_by_us )); then
            say "GPU healthy; Provider=$current, not set by this script. Nothing to do."
            exit 0
        fi

        say "GPU healthy again; restoring Provider=$restore_to."
        apply "$restore_to"
        (( dry )) || rm -f "$marker"

        # The stored benchmark was measured on a GPU that has since failed and
        # been fixed. It is not wrong, but it has not been checked, and this is
        # the moment when checking is cheap to suggest.
        say "The stored benchmark predates the fault — 'vst-ctl benchmark' will re-measure."
        ;;
esac

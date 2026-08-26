#!/usr/bin/env bash
# VibeSuperTonic — route to whichever provider is actually faster for YOUR reading.
#
#   bash vst-autotune.sh [--data <dir>] [--min-samples N] [--status] [--reset] [--dry-run] [-q]
#
# WHAT THIS IS, AND WHY IT IS NOT THE BENCHMARK. `vst-ctl benchmark` sweeps a
# fixed passage on an idle machine and writes the winner to benchmark.json; with
# Provider=auto the daemon already routes to it. That is the right answer when
# there is no other evidence, and it is a different question from this one:
#
#   - It measures one passage. Your text is longer, shorter, or full of numbers.
#   - It measures an idle machine. Yours has your actual work on it.
#   - It measures once. A GPU degrades, gets fixed, starts throttling in summer.
#
# So the daemon now times every render and files it against the provider that
# produced it (data/usage-stats.json, as wall time over audio duration — the same
# quantity the benchmark calls Rtf, so the two are directly comparable). This
# script reads those, and when it has enough of BOTH providers to be worth
# believing, pins the faster one.
#
# HOW IT GETS SAMPLES OF BOTH. The daemon only ever runs one provider at a time,
# so nothing will volunteer a comparison — left alone it would gather 4000
# samples of the winner and none of the loser, and could never notice the loser
# had become the winner. So while it is short of samples this script ALTERNATES:
# it sets the provider that has fewer, waits for you to use the program normally,
# and comes back. That is the "normal use" part, and it is why this belongs on a
# timer rather than being run once.
#
#   Run it every 15-30 minutes. It does nothing at all most times it runs.
#
# WHAT IT WILL NOT DO. It will not touch a Provider set by hand to "cpu" or "gpu"
# unless this script set it (same marker rule as vst-gpu-guard.sh). It will not
# fight the guard: if the GPU is pinned off for being broken, this exits.

set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

data=""
min_samples=20
dry=0
quiet=0
mode="run"

while (( $# )); do
    case "$1" in
        --data) data="${2:?--data needs a directory}"; shift 2 ;;
        --min-samples) min_samples="${2:?--min-samples needs a number}"; shift 2 ;;
        --status) mode="status"; shift ;;
        --reset) mode="reset"; shift ;;
        --dry-run) dry=1; shift ;;
        -q|--quiet) quiet=1; shift ;;
        -h|--help) sed -n '2,36p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

say() { (( quiet )) || echo "$@"; }
die() { echo "vst-autotune: $*" >&2; exit 1; }

command -v python3 >/dev/null || die "python3 is required."

ctl=""
for candidate in "$here/vst-ctl" "$(command -v vst-ctl 2>/dev/null || true)"; do
    [[ -n "$candidate" && -x "$candidate" ]] && { ctl="$candidate"; break; }
done

if [[ -z "$data" && -n "$ctl" ]]; then
    data=$("$ctl" --no-start config 2>/dev/null \
        | python3 -c 'import sys,json
try: print(json.load(sys.stdin).get("DataDir",""))
except Exception: pass' || true)
fi
[[ -z "$data" && -d "$here/data" ]] && data="$here/data"
[[ -n "$data" ]] || die "could not find the data directory. Pass --data <dir>."

settings="$data/settings.json"
usage="$data/usage-stats.json"
marker="$data/autotune.json"
guard="$data/gpu-guard.json"

# ---------------------------------------------------------------- the numbers
#
# One python for the whole read, because every question here is about the same
# file and shelling out per field is how the median and the count end up
# disagreeing about which flush they came from.
read_usage() {
    python3 - "$usage" <<'PY'
import json, sys
try:
    with open(sys.argv[1]) as f:
        doc = json.load(f)
except Exception:
    doc = {}

by = {p.get("Provider",""): p for p in doc.get("Providers", [])}

def field(name, key, default="0"):
    p = by.get(name)
    if not p: return default
    v = p.get(key, default)
    return str(v)

for name in ("cpu", "cuda"):
    p = by.get(name) or {}
    print(f'{name}_n={len(p.get("Recent", []))}')
    print(f'{name}_total={p.get("Count", 0)}')
    print(f'{name}_rtf={p.get("MedianRtf", 0) or 0}')
PY
}

eval "$(read_usage)"

fmt() { python3 -c 'import sys; v=float(sys.argv[1]); print("n/a" if v<=0 else f"{v:.4f}")' "$1"; }

if [[ "$mode" == "status" ]]; then
    echo "usage samples in $usage"
    echo "  cpu   window=$cpu_n  lifetime=$cpu_total  median rtf=$(fmt "$cpu_rtf")"
    echo "  cuda  window=$cuda_n lifetime=$cuda_total median rtf=$(fmt "$cuda_rtf")"
    echo "  need $min_samples in the window of each to decide"
    [[ -f "$marker" ]] && { echo "  autotune marker:"; cat "$marker"; }
    exit 0
fi

if [[ "$mode" == "reset" ]]; then
    (( dry )) || rm -f "$usage" "$marker"
    say "cleared usage samples and the autotune marker."
    exit 0
fi

# ---------------------------------------------------------------- who is in charge
if [[ -f "$guard" ]]; then
    say "the GPU is pinned off by vst-gpu-guard.sh; nothing to tune until it is back."
    exit 0
fi

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

ours=0
[[ -f "$marker" ]] && ours=1

if [[ "$current" != "auto" ]] && (( ! ours )); then
    say "Provider=$current was set by hand; leaving it alone."
    exit 0
fi

# Is there even a GPU to compare against? Without the provider pack the daemon
# has one option, and alternating would just be turning the CPU off and on.
has_gpu=0
if [[ -n "$ctl" ]]; then
    "$ctl" --no-start config 2>/dev/null \
        | grep -q '"Provider":"cuda"' && has_gpu=1
fi
if [[ -d "$data/../runtime/cuda" ]]; then has_gpu=1; fi
if (( ! has_gpu )) && ! command -v nvidia-smi >/dev/null 2>&1; then
    say "no GPU provider installed (see install-gpu.sh); nothing to compare."
    exit 0
fi

apply() {
    local value="$1" note="$2"
    if (( dry )); then
        say "would set Provider=$value ($note)"
        return
    fi
    write_provider "$value"
    python3 -c '
import json,sys,datetime
with open(sys.argv[1], "w") as f:
    json.dump({
        "setUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "provider": sys.argv[2],
        "why": sys.argv[3],
    }, f, indent=2)
    f.write("\n")
' "$marker" "$value" "$note"
    [[ -n "$ctl" ]] && "$ctl" --no-start reload >/dev/null 2>&1 || true
    say "Provider=$value — $note"
}

# ---------------------------------------------------------------- decide
#
# Short of samples on either side: go and get some. Whichever provider has fewer
# is the one to run next, so the two windows fill at roughly the same rate rather
# than the machine spending a week on whichever it happened to start with.
if (( cpu_n < min_samples || cuda_n < min_samples )); then
    if (( cuda_n <= cpu_n )); then
        want=gpu; short="cuda has $cuda_n of $min_samples samples"
    else
        want=cpu; short="cpu has $cpu_n of $min_samples samples"
    fi

    if [[ "$current" == "$want" ]]; then
        say "gathering: $short. Use the program normally; I will look again later."
        exit 0
    fi

    apply "$want" "gathering samples — $short"
    exit 0
fi

# Both windows are full. Compare, and require the win to be worth having: a
# provider that is 3% faster is a provider that is the same speed with a
# different set of rounding errors, and flipping the setting for that would
# rebuild the ONNX session — about a second and ~380 MB — for nothing.
decision=$(python3 - "$cpu_rtf" "$cuda_rtf" <<'PY'
import sys
cpu, cuda = float(sys.argv[1]), float(sys.argv[2])
if cpu <= 0 or cuda <= 0:
    print("skip unusable-median"); raise SystemExit

margin = 0.10
if cuda < cpu * (1 - margin):
    print(f"gpu cuda {cuda:.4f} vs cpu {cpu:.4f} — {cpu/cuda:.1f}x faster")
elif cpu < cuda * (1 - margin):
    print(f"cpu cpu {cpu:.4f} vs cuda {cuda:.4f} — {cuda/cpu:.1f}x faster")
else:
    print(f"tie within {int(margin*100)}% — cpu {cpu:.4f}, cuda {cuda:.4f}")
PY
)

winner="${decision%% *}"
detail="${decision#* }"

case "$winner" in
    skip) say "not enough usable measurements yet."; exit 0 ;;
    tie)  say "no clear winner: $detail. Leaving Provider=$current."; exit 0 ;;
esac

if [[ "$current" == "$winner" ]]; then
    say "already on the faster one ($detail)."
    exit 0
fi

apply "$winner" "measured from your own use — $detail"

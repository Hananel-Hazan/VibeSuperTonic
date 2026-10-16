#!/usr/bin/env bash
#
# What does this archive cost the person downloading it, and how fast does the
# binary that runs on every keystroke start?
#
#     bash build/check-budgets.sh <composed-tree> [tarball]
#
# Called by pack-tar.sh as assertion 6, and a separate file for the same reason
# check-speechd-payload.sh is: an assertion that can only run inside a
# four-minute packer run is one nobody sabotages, and a check that has never
# been observed failing is not evidence. Every budget here can be broken against
# a copy of a composed tree in about a second — build/budget-sabotage.sh does it.
#
# THREE NUMBERS, AND EACH HAS A DIFFERENT FAILURE.
#
#   * The archive: assertions 3 and 4 in the packer catch two NAMED files, any
#     .onnx and the 330 MB CUDA provider, because those two specific things once
#     appeared. Nothing catches growth in general — a package reference that
#     drags in a native blob, a publish that stops trimming — and that is the
#     shape most size regressions have.
#   * The espeak payload: build-espeak.sh prunes 118 dictionaries to the ~31 the
#     catalog needs, and a build that skipped the pruning passes every other
#     check in the packer. It is a bigger download and nothing else, which is
#     exactly the kind of regression nobody notices for three releases.
#   * vst-ctl's startup: assertion 2 proves the binary is NATIVE, which is a
#     proxy for the number that actually matters. Phase 7 chose NativeAOT
#     because a managed apphost cost ~107 ms on every hotkey press and every
#     utterance a screen reader speaks. A binary can stay native and get slow —
#     an ICU probe, a stat storm, a static constructor that reads a file — and
#     the proxy would not see it. docs/TESTING-PLAN.md, "fast": measure it.
#
# THE BUDGETS ARE DELIBERATELY WIDE. These catch a doubling, not a drift. A
# budget that fails on an ordinary release is one somebody raises without
# reading it, which is worse than not having it.
set -uo pipefail

# 75 MiB against 60.7 measured 2026-08-30 — 54.1 before P5's phonemiser landed.
ARCHIVE_BUDGET_MIB=75

# 18 MiB against 13.2 measured 2026-08-30. An unpruned or --full-data espeak-ng
# is ~25 MB of dictionaries, so this is on the useful side of that.
ESPEAK_BUDGET_MIB=18

# Above this machine's own fork/exec floor, measured here and now, because the
# absolute number is a property of the machine as much as of the binary — a
# loaded CI runner spawns processes slower and would fail an absolute budget
# while shipping a perfectly fast binary. Measured 2026-08-30 on the packing
# box: /bin/true 5 ms, vst-ctl 7 ms, vst-speechd 5 ms, and the managed
# self-contained daemon 25 ms. 35 ms of headroom is wide enough that a slow
# machine passes and narrow enough that the ~107 ms this exists for cannot.
STARTUP_HEADROOM_MS=35

tree="${1:-}"
tarball="${2:-}"
[[ -n "$tree" && -d "$tree" ]] || { echo "usage: $0 <composed-tree> [tarball]" >&2; exit 2; }

die()  { printf '\033[31mcheck-budgets: %s\033[0m\n' "$*" >&2; exit 1; }
info() { printf '    %s\n' "$*"; }

# The ten biggest files, largest first. Printed when a size budget fails,
# because "the archive grew by 14 MB" without saying WHERE starts an
# investigation instead of ending one.
biggest() {
    find "$1" -type f -printf '%s %p\n' \
        | sort -rn | awk 'NR<=10' \
        | while read -r bytes path; do
              printf '       %8s  %s\n' "$(numfmt --to=iec "$bytes")" "${path#"$1"/}"
          done
}

# The median of 21 runs. A mean is moved by one scheduling hiccup, and a minimum
# measures the machine rather than the binary.
median_ms() {
    local i start end
    for ((i = 0; i < 21; i++)); do
        start=$(date +%s%N)
        "$@" >/dev/null 2>&1
        end=$(date +%s%N)
        echo $(( (end - start) / 1000000 ))
    done | sort -n | sed -n '11p'
}

# ---------------------------------------------------------------- the archive
if [[ -n "$tarball" ]]; then
    [[ -f "$tarball" ]] || die "no tarball at $tarball"
    bytes=$(stat -c%s "$tarball")
    if (( bytes > ARCHIVE_BUDGET_MIB * 1024 * 1024 )); then
        printf '\n'
        biggest "$tree"
        die "$(basename "$tarball") is $(numfmt --to=iec "$bytes"), over the ${ARCHIVE_BUDGET_MIB} MiB budget.
       The ten biggest files in the composed tree are above — that is where the
       growth is, since the tarball is only their compression. Either something
       arrived that should not have, or the product genuinely got bigger and this
       number needs raising with a reason and a date."
    fi
    info "archive $(numfmt --to=iec "$bytes") (budget ${ARCHIVE_BUDGET_MIB} MiB)"
fi

# ---------------------------------------------------------- the espeak payload
if [[ -d "$tree/espeak" ]]; then
    bytes=$(du -sb "$tree/espeak" | cut -f1)
    if (( bytes > ESPEAK_BUDGET_MIB * 1024 * 1024 )); then
        printf '\n'
        biggest "$tree/espeak"
        die "espeak/ is $(numfmt --to=iec "$bytes"), over the ${ESPEAK_BUDGET_MIB} MiB budget.
       An unpruned or --full-data build of espeak-ng is the usual cause and is
       ~25 MB of dictionaries; build-espeak.sh keeps the ~31 the catalog needs.
       If the payload genuinely has to grow, raise the number here deliberately
       and say why — it is a download on somebody's domestic connection."
    fi
    info "espeak payload $(numfmt --to=iec "$bytes") (budget ${ESPEAK_BUDGET_MIB} MiB)"
else
    die "no espeak/ in $tree — the phonemiser payload is not optional since P5"
fi

# ------------------------------------------------------------------- startup
command -v date >/dev/null || die "coreutils date is required for the startup measurement"

floor=$(median_ms /bin/true)
budget=$(( floor + STARTUP_HEADROOM_MS ))
report=""
for name in vst-ctl vst-speechd; do
    [[ -x "$tree/$name" ]] || die "$name is missing from $tree"
    started=$(median_ms "$tree/$name" --version)
    (( started <= budget )) || die "$name takes ${started} ms to start and print its version.
       The budget is ${budget} ms — this machine's own fork/exec floor (${floor} ms) plus
       ${STARTUP_HEADROOM_MS} ms. This binary runs once per hotkey press and once per utterance
       a screen reader speaks; ~107 ms is what a managed apphost cost and what
       NativeAOT was chosen to avoid. Being ELF is not enough."
    report+="$name ${started} ms, "
done
info "${report}median of 21 (fork/exec floor ${floor} ms, budget ${budget} ms)"

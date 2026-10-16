#!/usr/bin/env bash
#
# Break the archive in each way build/check-budgets.sh claims to catch, and
# watch it catch them.
#
#     bash build/budget-sabotage.sh [composed-tree]
#
# Defaults to dist/release-linux/VibeSuperTonic, the tree pack-tar.sh leaves
# behind. Each case works on a full copy beside it, under dist/, because that is
# the same filesystem — and `cp -a` rather than `cp -al`, because a mutation
# that appends to a hardlinked file writes through to the tree it is supposed to
# be protecting.
#
# The house rule, applied to three new budgets: a check that has never been
# observed failing is not evidence, so each one gets broken on the day it is
# written. What makes it possible in a second rather than a four-minute pack is
# that the assertion lives in a script of its own.
#
# THE ARCHIVE CASE RE-TARS 144 MB and takes the better part of a minute. It is
# the only honest way to test a budget on a finished tarball: handing the check
# a big file that is not a tarball would test `stat`, not the growth path.
set -uo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

TREE="${1:-dist/release-linux/VibeSuperTonic}"
[[ -d "$TREE" ]] || { echo "no composed tree at $TREE — run: bash build/pack-tar.sh" >&2; exit 2; }

pass=0; fail=0
work="$(dirname "$TREE")/.budget-work"
work_tar="$(dirname "$TREE")/.budget-work.tar.gz"

# Each case: a label, a mutation of the copy, whether the tarball matters, and
# the words the refusal must use.
run_case() {
    local label="$1" mutate="$2" retar="$3" expect="$4" out rc tarball=""
    rm -rf "$work"; mkdir -p "$work"; cp -a "$TREE/." "$work/"
    ( cd "$work" && eval "$mutate" )

    if [[ "$retar" == "retar" ]]; then
        rm -f "$work_tar"
        tar -czf "$work_tar" -C "$(dirname "$work")" "$(basename "$work")"
        tarball="$work_tar"
    fi

    out="$(bash build/check-budgets.sh "$work" $tarball 2>&1)"; rc=$?
    if (( rc == 0 )); then
        printf '  \033[31mNOT CAUGHT\033[0m   %s\n' "$label"; fail=$((fail + 1))
    elif grep -qi -- "$expect" <<< "$out"; then
        printf '  \033[32mcaught\033[0m       %s\n' "$label"; pass=$((pass + 1))
    else
        printf '  \033[33mMISATTRIBUTED\033[0m %s\n' "$label"
        printf '               it failed with: %s\n' "$(head -3 <<< "$out" | tr '\n' ' ')"
        fail=$((fail + 1))
    fi
}

echo "sabotaging build/check-budgets.sh against a copy of $TREE"
echo

# THE SHAPE MOST SIZE REGRESSIONS HAVE: something arrives that nobody asked for.
# Incompressible, because a blob that gzip flattens would not reach the user as
# a bigger download either. 20 MB against 14 MB of headroom.
run_case "a 20 MB blob arrives in the archive" \
    'head -c 20971520 /dev/urandom > blob.bin' retar \
    "over the .* MiB budget"

# The pruning step in build-espeak.sh stopped happening — the payload that
# passes every other check in the packer and costs 11 MB more.
run_case "espeak ships unpruned dictionaries" \
    'head -c 6291456 /dev/urandom > espeak/espeak-ng-data/xx_dict' "" \
    "espeak/ is .* over the"

# Still ELF, still native, still passes assertion 2 — and 100 ms slower on every
# hotkey press and every utterance a screen reader speaks. This is the failure
# the nativeness proxy cannot see.
run_case "vst-ctl got slow but stayed native" \
    'printf "#include <unistd.h>\nint main(void){usleep(100000);return 0;}\n" > /tmp/slow.c
     cc -O2 -o vst-ctl /tmp/slow.c' "" \
    "ms to start"

# The composition forgot the phonemiser. Assertion 3c would also catch this in
# the packer; here it must not pass silently as "no espeak, no budget".
run_case "the phonemiser payload is missing entirely" \
    'rm -rf espeak' "" \
    "not optional"

run_case "vst-speechd is not in the tree" \
    'rm -f vst-speechd' "" \
    "missing from"

rm -rf "$work" "$work_tar"
printf '\n%s caught, %s not\n' "$pass" "$fail"
exit $(( fail > 0 ))

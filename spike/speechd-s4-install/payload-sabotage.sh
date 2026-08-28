#!/usr/bin/env bash
#
# Break the archive in each way build/check-speechd-payload.sh claims to catch,
# and watch it catch them.
#
#     bash spike/speechd-s4-install/payload-sabotage.sh [composed-tree]
#
# Defaults to dist/release-linux/VibeSuperTonic, the tree pack-tar.sh leaves
# behind. Each case works on a full copy BESIDE it — under dist/, deliberately,
# because that is the same filesystem and the copy is a local one.
#
# Hardlinks (`cp -al`) were the first attempt and are wrong twice over: /tmp is
# a different filesystem here, so every link failed and the fallback then nested
# the tree inside itself; and a mutation that APPENDS to a hardlinked file
# writes through to the archive it is supposed to be protecting.
#
# This is the house rule applied to a packer assertion: every one of them was
# sabotaged on the day it was written, because a check that has never been
# observed failing is not evidence. The reason it is possible at all is that the
# assertion lives in a script of its own rather than inside a four-minute pack.
set -uo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

TREE="${1:-dist/release-linux/VibeSuperTonic}"
[[ -d "$TREE" ]] || { echo "no composed tree at $TREE — run: bash build/pack-tar.sh" >&2; exit 2; }

pass=0; fail=0
work="$(dirname "$TREE")/.s4-payload-work"

# Each case: a label, a mutation of the copy, and the words the refusal must use.
run_case() {
    local label="$1" mutate="$2" expect="$3" out rc
    rm -rf "$work"; mkdir -p "$work"; cp -a "$TREE/." "$work/"
    ( cd "$work" && eval "$mutate" )
    out="$(bash build/check-speechd-payload.sh "$work" 2>&1)"; rc=$?
    if (( rc == 0 )); then
        printf '  \033[31mNOT CAUGHT\033[0m   %s\n' "$label"; fail=$((fail + 1))
    elif grep -qi -- "$expect" <<< "$out"; then
        printf '  \033[32mcaught\033[0m       %s\n' "$label"; pass=$((pass + 1))
    else
        printf '  \033[33mMISATTRIBUTED\033[0m %s\n' "$label"
        printf '               it failed with: %s\n' "$(head -2 <<< "$out" | tr '\n' ' ')"
        fail=$((fail + 1))
    fi
}

echo "sabotaging build/check-speechd-payload.sh against a copy of $TREE"
echo

# The composition forgot the module, or forgot the installer beside it. Both are
# one missing `cp` in the packer, and neither breaks anything else in the box.
run_case "the module is not in the archive" \
    'rm -f vst-speechd' "missing or not executable"
run_case "the installer is not in the archive" \
    'rm -f speechd-install.sh' "missing or not executable"
run_case "the installer shipped without its execute bit" \
    'chmod 644 speechd-install.sh' "missing or not executable"
run_case "the installer has a syntax error" \
    'printf "\nif then fi\n" >> speechd-install.sh' "does not parse"

# THE ONE THAT MATTERS. The module answers INIT with 399 when it cannot find
# espeak beside it, speechd drops it, and the user sees a synthesizer that is
# simply not in the list. `mv` rather than `rm`, because a hardlink copy shares
# its files with the real tree.
run_case "the phonemiser did not land where the module looks" \
    'mv espeak espeak-elsewhere' "answered INIT with"

# A module that cannot start at all: the same silence, a different cause.
run_case "the module is not a working binary" \
    'rm -f vst-speechd && printf "#!/bin/sh\nexit 0\n" > vst-speechd && chmod 755 vst-speechd' \
    "answered INIT with"

# The installer stopped refusing a voiceless archive (trap 14) — the state every
# fresh extract is in.
run_case "the installer registers an archive with no voices" \
    "sed -i 's/if (( voices == 0 && force == 0 )); then/if false; then/' speechd-install.sh" \
    "REGISTERED the module"

# It still refuses, but for a reason that has nothing to do with voices — which
# would make the check above pass on an installer that is broken differently.
run_case "the installer refuses for the wrong reason" \
    "sed -i 's/^say \"VibeSuperTonic — registering/die \"something else went wrong\"; say \"VibeSuperTonic — registering/' speechd-install.sh" \
    "for the wrong reason"

# Trap 13: --check stopped noticing that the configured path is gone.
run_case "--check no longer verifies the configured path" \
    'sed -i "s@if \[\[ -e .*declared.*then@if true; then@" speechd-install.sh' \
    "naming a module path that does not exist"

rm -rf "$work"
printf '\n%s caught, %s not\n' "$pass" "$fail"
exit $(( fail > 0 ))

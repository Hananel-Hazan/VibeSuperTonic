#!/usr/bin/env bash
#
# Does the archive we just built actually run?
#
#     bash build/smoke-test.sh <extracted-release-dir>
#
# Written 2026-08-27 for TESTING-PLAN.md item 2. Meant to be run inside a clean
# ubuntu:22.04 container against an extracted tarball, and it works unchanged on
# a developer machine.
#
# ---------------------------------------------------------------------------
# WHY THIS EXISTS, WHEN THE PACKER ALREADY HAS NINE ASSERTIONS.
#
# Every one of those runs on the machine that packed the archive — a machine
# with the .NET SDK, a display, PulseAudio, ICU, espeak-ng already installed,
# and every build dependency the product does not ship. They check what is IN
# the box. Nothing checks that the box OPENS somewhere else.
#
# The glibc-floor assertion is the sharpest example: it measures symbol versions
# and concludes "Ubuntu 22.04+, Debian 12+, RHEL 9+". That is an inference from
# an ELF header. This script is the experiment.
#
# WHAT IT DELIBERATELY DOES NOT NEED, because a user on a fresh machine has none
# of them: the .NET runtime, a display, an audio device, a downloaded model, a
# distro espeak-ng, or root.
#
# ISOLATION IS NOT POLITENESS. The daemon is long-lived and singular per user:
# it binds one socket under $XDG_RUNTIME_DIR and a `shutdown` reaches whichever
# daemon answers there. Run without isolation on a developer machine and this
# script cheerfully stops the daemon the developer is using — and, worse, a
# `status` that talked to THEIR daemon would report a healthy version while
# proving nothing about the archive. Both variables below are what stop that.
# ---------------------------------------------------------------------------

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dir="${1:-}"
[[ -n "$dir" && -d "$dir" ]] || {
    echo "usage: bash build/smoke-test.sh <extracted-release-dir>" >&2
    exit 64
}
dir="$(cd "$dir" && pwd)"

pass() { printf '    \033[32mok\033[0m   %s\n' "$*"; }
step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
die()  { printf '\033[31mFAILED: %s\033[0m\n' "$*" >&2; exit 1; }

# A sandbox of our own for the socket and the store. Removed on the way out,
# including when a check fails — a leftover daemon would poison the next run.
sandbox="$(mktemp -d)"
export XDG_RUNTIME_DIR="$sandbox/run"
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
data="$sandbox/data"

daemon_pid=""
cleanup() {
    if [[ -n "$daemon_pid" ]] && kill -0 "$daemon_pid" 2>/dev/null; then
        kill "$daemon_pid" 2>/dev/null || true
        wait "$daemon_pid" 2>/dev/null || true
    fi
    rm -rf "$sandbox"
}
trap cleanup EXIT

echo "smoke test: $dir"
echo "sandbox:    $sandbox"

# ------------------------------------------------------------------ 1. shape
step "The three binaries are there and executable"
for b in vibesupertonicd vibesupertonic-ui vst-ctl; do
    [[ -x "$dir/$b" ]] || die "$b is missing or not executable"
done
pass "vibesupertonicd, vibesupertonic-ui, vst-ctl"

# ---------------------------------------------------------------- 2. they run
#
# THE CENTRAL CLAIM OF THIS SCRIPT. On a machine with no .NET installed, a
# self-contained binary either runs or reports a missing symbol from the dynamic
# loader — and the second is what a glibc floor that has quietly risen looks
# like to a user. Deliberately with no DISPLAY: the UI must answer --version
# without a compositor, because a headless check of it is otherwise impossible
# and CI has no display either.
step "Each binary starts and reports its version, headless"
versions=()
for b in vibesupertonicd vibesupertonic-ui vst-ctl; do
    v="$(env -u DISPLAY -u WAYLAND_DISPLAY "$dir/$b" --version 2>&1)" \
        || die "$b could not run here: $v"
    [[ -n "$v" ]] || die "$b printed no version"
    versions+=("$v")
    pass "$b $v"
done
[[ "${versions[0]}" == "${versions[1]}" && "${versions[0]}" == "${versions[2]}" ]] \
    || die "the three binaries disagree on a version: ${versions[*]}"

# ------------------------------------------------------------ 3. the store
step "The daemon can work out where its store goes"
store="$("$dir/vibesupertonicd" --data "$data" --print-store 2>&1)" \
    || die "--print-store failed: $store"
pass "$(echo "$store" | tr '\n' ' ')"

# --------------------------------------------------------- 4. it comes up
#
# With no models. That is the state of a fresh install before the first-run
# screen, and a daemon that refuses to start in it cannot serve the screen that
# fixes it. It must come up, bind, answer, and say what it is missing.
step "The daemon starts with no models and answers on its socket"
env -u DISPLAY -u WAYLAND_DISPLAY \
    "$dir/vibesupertonicd" --data "$data" >"$sandbox/daemon.out" 2>&1 &
daemon_pid=$!

# Protocol.SocketPath(): $XDG_RUNTIME_DIR/vibesupertonic/ctl.sock. Named exactly
# rather than globbed, so a daemon that bound somewhere unexpected fails here
# instead of being found by a wildcard and passing.
sock="$XDG_RUNTIME_DIR/vibesupertonic/ctl.sock"
for _ in $(seq 1 100); do
    kill -0 "$daemon_pid" 2>/dev/null || die "the daemon exited during startup:
$(cat "$sandbox/daemon.out")"
    [[ -S "$sock" ]] && break
    sleep 0.1
done
[[ -S "$sock" ]] || die "no socket at $sock after 10s:
$(cat "$sandbox/daemon.out")"
pass "socket bound at ${sock#"$sandbox/"}"

# The socket must not be reachable by other local users: it speaks, and it reads
# whatever text is selected on the desktop.
mode="$(stat -c%a "$sock")"
[[ "$mode" == "600" ]] || die "the control socket is mode $mode, not 600 — any local user could speak and read the selection"
pass "socket is mode 600"

status="$(timeout 20 "$dir/vst-ctl" status 2>&1)" || die "vst-ctl status failed: $status"
[[ "$status" == *"{"* ]] || die "vst-ctl status did not return JSON: $status"
pass "status: $(echo "$status" | head -c 120)"

config="$(timeout 20 "$dir/vst-ctl" config 2>&1)" || die "vst-ctl config failed: $config"
pass "config answered"

# ------------------------------------------------- 5. the shipped phonemiser
#
# Not a distro one. The daemon probes at startup and logs which library answered
# (EspeakLibrary.Describe), so this is the one place the answer is observable
# without shipping the espeak-ng binary just to test it. "beside the executable"
# is the phrase the probe prints when it found OUR payload; the loader path
# answering instead means the archive is relying on a package the user may not
# have — which is exactly what bundling it was meant to stop.
step "The espeak-ng that answers is the one in the archive"
log="$(cat "$sandbox/daemon.out" "$data/logs/daemon.log" 2>/dev/null || true)"
if [[ -d "$dir/espeak" ]]; then
    grep -q "espeak-ng: .*beside the executable" <<<"$log" \
        || die "the daemon did not bind the espeak-ng in espeak/. It logged:
$(grep -i espeak <<<"$log" || echo '  (nothing about espeak at all)')"
    pass "$(grep -o 'espeak-ng: [^)]*)' <<<"$log" | head -1)"
else
    pass "no espeak/ in this archive — skipped (pre-P5 tarball)"
fi

# ------------------------------------------------------------- 6. it stops
#
# `shutdown` is what install.sh runs before replacing the binaries, so a
# shutdown that does not actually stop the process is an upgrade that leaves the
# old daemon serving every hotkey press.
step "It shuts down when asked"
timeout 20 "$dir/vst-ctl" shutdown >/dev/null 2>&1 || true
for _ in $(seq 1 100); do
    kill -0 "$daemon_pid" 2>/dev/null || break
    sleep 0.1
done
kill -0 "$daemon_pid" 2>/dev/null && die "the daemon is still running after shutdown"
daemon_pid=""
pass "stopped"

printf '\n\033[32mSMOKE TEST PASSED\033[0m — the archive runs here.\n'

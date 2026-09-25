#!/usr/bin/env bash
# Can speech-dispatcher load the SNAP's module? Asked of a private
# speech-dispatcher, so the answer costs nobody their screen reader.
#
#     bash build/check-snap-speechd.sh [--keep]
#
# Run by `pack-snap.sh --test-install` in CI, and on a desktop where the snap is
# installed. It needs the snap installed and speech-dispatcher on the host.
#
# WHY IT EXISTS. On the first real install (revision 2, 2026-09-25, Kubuntu),
# `sandbox-setup.sh speechd-install` registered the snap's module and
# speech-dispatcher then offered NOTHING: `spd-say -O` hung, espeak-ng included,
# although the module answered INIT when run by hand and `--check` said so. The
# user put their config back with `--remove`. speech-dispatcher does not go to
# the background until every declared module has answered INIT, so one module
# that never answers blocks its startup for good, and every client with it.
#
# NOTHING OF YOURS IS TOUCHED. The same rule as spike/speechd-s4-install: this
# speech-dispatcher has its own config (XDG_CONFIG_HOME), socket, pidfile,
# runtime directory and log directory, under one temporary directory. The snap's
# own installer is run against that config with --no-restart, because without
# it the installer stops every speech-dispatcher of this user, yours included.
#
# The temporary directory is under /tmp on purpose: a unix socket path longer
# than ~107 bytes makes speech-dispatcher die claiming it cannot bind.
#
# TWO WAYS OF STARTING IT, because a desktop does not start speech-dispatcher
# the way a terminal does. On Ubuntu and Kubuntu it is SOCKET-ACTIVATED by the
# systemd user manager (speech-dispatcher.socket, `speech-dispatcher -s -t 0`
# as speech-dispatcher.service), so the module is spawned from inside a systemd
# service, and `snap run` moves every app it starts into a scope of its own.
# The terminal-started check passed on the very machine where the desktop's
# speech-dispatcher hung (2026-09-25). So the second check starts a private one
# the desktop's way: `systemd-run --user --socket-property=ListenStream=...`,
# with the user manager's own environment and the real runtime directory, only
# the config (XDG_CONFIG_HOME), socket, pidfile and log moved. Skipped, and said
# so, where there is no systemd user manager.
#
# Exit 0 when speech-dispatcher offers everything it offered before plus
# vibesupertonic, both ways; non-zero with its logs printed otherwise.
set -uo pipefail

keep=0
[[ "${1:-}" == --keep ]] && keep=1

setup="/snap/vibesupertonic/current/sandbox-setup.sh"
installer=(bash "$setup" speechd-install)
# For testing this script without snapd: a speechd-install.sh to run instead.
[[ -n "${VST_PROBE_INSTALLER:-}" ]] && installer=(bash "$VST_PROBE_INSTALLER")

for tool in speech-dispatcher spd-say; do
    command -v "$tool" >/dev/null 2>&1 || { echo "no $tool here; install speech-dispatcher first" >&2; exit 2; }
done
[[ -n "${VST_PROBE_INSTALLER:-}" || -f "$setup" ]] || { echo "the snap is not installed ($setup does not exist)" >&2; exit 2; }

probe="$(mktemp -d /tmp/vst-probe.XXXXXX)"
real_runtime="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export PULSE_SERVER="${PULSE_SERVER:-unix:$real_runtime/pulse/native}"
export XDG_RUNTIME_DIR="$probe/runtime" XDG_CONFIG_HOME="$probe/xdg"
export SPEECHD_ADDRESS="unix_socket:$probe/speechd.sock"
mkdir -p "$XDG_RUNTIME_DIR" "$XDG_CONFIG_HOME" "$probe/log"
chmod 700 "$XDG_RUNTIME_DIR"

say() { printf '  %s\n' "$*"; }
# systemctl --user finds the manager through XDG_RUNTIME_DIR, which this script
# has just moved for the terminal-started speech-dispatcher. So every call to
# the user manager goes through here, with the real one.
sysu() { XDG_RUNTIME_DIR="$real_runtime" "$@"; }
t_start="$(date '+%Y-%m-%d %H:%M:%S')"
unit="vst-probe-speechd-$$"
systemd_ok=0
sysu systemctl --user show-environment >/dev/null 2>&1 && command -v systemd-run >/dev/null 2>&1 && systemd_ok=1
# snap run's tracking scopes for the module that exist before we start, so the
# ones this check creates, and only those, can be stopped afterwards.
scopes() { sysu systemctl --user list-units --plain --no-legend 'snap.vibesupertonic.speechd-*.scope' 2>/dev/null | awk '{ print $1 }' | sort; }
scopes_before="$(scopes)"


# Under a timeout: a module that never answers INIT keeps speech-dispatcher from
# ever finishing its start, which is the failure being looked for.
start() {
    timeout 60 speech-dispatcher -d -t 0 -l 5 -S "$probe/speechd.sock" -P "$probe/speechd.pid" \
        -L "$probe/log" 2>>"$probe/log/stderr" || { say "speech-dispatcher did not finish starting in 60 s"; return 1; }
    local _
    for _ in $(seq 40); do [[ -S "$probe/speechd.sock" ]] && return 0; sleep 0.25; done
    say "speech-dispatcher started but made no socket"
    return 1
}
stop() {
    local pid _
    pid="$(cat "$probe/speechd.pid" 2>/dev/null || true)"
    if [[ -n "$pid" ]]; then
        kill "$pid" 2>/dev/null
        for _ in $(seq 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
        kill -9 "$pid" 2>/dev/null
    fi
    rm -f "$probe/speechd.sock" "$probe/speechd.pid"
}
offered() { timeout 30 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF' | sort | tr '\n' ' '; }

# What failed, in speech-dispatcher's words and the module's. Read to the end,
# no early-exiting consumer in a pipeline (CLAUDE.md).
explain() {
    printf '\n  --- speech-dispatcher, about our module\n'
    grep -i 'vibesupertonic' "$probe/log/speech-dispatcher.log" 2>/dev/null | tail -20 | sed 's/^/  | /'
    printf '  --- speech-dispatcher, last lines\n'
    tail -25 "$probe/log/speech-dispatcher.log" 2>/dev/null | sed 's/^/  | /'
    printf '  --- the module (its stderr, as speech-dispatcher logged it)\n'
    tail -25 "$probe/log/vibesupertonic.log" 2>/dev/null | sed 's/^/  | /'
    printf '  --- speech-dispatcher stderr\n'
    tail -10 "$probe/log/stderr" 2>/dev/null | sed 's/^/  | /'
    if (( systemd_ok )); then
        printf '  --- systemd, about the socket-activated one and snap run\n'
        sysu journalctl --user --since "$t_start" --no-pager -o short-precise 2>/dev/null \
            | grep -Ei "$unit|snap|vibesupertonic|scope" | tail -30 | sed 's/^/  | /'
    fi
    printf '  --- the sandbox, if the kernel log is readable here\n'
    journalctl -k --since "-5min" --no-pager 2>/dev/null \
        | grep -E 'apparmor="DENIED".*(vibesupertonic|speech)|type=1326.*vibesupertonic' \
        | tail -15 | sed 's/^/  | /'
    printf '\n  everything is in %s\n' "$probe"
    keep=1
}
stop_systemd() {
    (( systemd_ok )) || return 0
    sysu systemctl --user stop "$unit.socket" "$unit.service" >/dev/null 2>&1
    sysu systemctl --user reset-failed "$unit.socket" "$unit.service" >/dev/null 2>&1
    local s
    for s in $(comm -13 <(printf '%s\n' "$scopes_before") <(scopes)); do
        sysu systemctl --user stop "$s" >/dev/null 2>&1
    done
    rm -f "$probe/speechd.sock" "$probe/speechd.pid"
}
finish() {
    stop
    stop_systemd
    (( keep )) || rm -rf "$probe"
    exit "$1"
}

echo "Can speech-dispatcher load the snap's module? (a private one, in $probe)"
say "$(speech-dispatcher --version 2>/dev/null | awk 'NR == 1')"

start || { explain; finish 3; }
before="$(offered)"
say "before: ${before:-nothing}"
[[ -n "$before" ]] || { say "the private speech-dispatcher offers nothing even without our module"; explain; finish 3; }

# --force, because the question is whether speech-dispatcher can load the module
# at all, and a build machine has no voices.
"${installer[@]}" --force --no-restart >"$probe/log/installer" 2>&1 \
    || { say "the installer failed:"; sed 's/^/  | /' "$probe/log/installer"; explain; finish 4; }

stop
t0=$SECONDS
start || { say "so it blocks on our module: this is the hang"; explain; finish 5; }
after="$(offered)"
say "after ($((SECONDS - t0)) s to start and answer): ${after:-nothing}"

missing_from() {
    local name out=""
    for name in $before vibesupertonic; do [[ " $1 " == *" $name "* ]] || out+="$name "; done
    printf '%s' "$out"
}
missing="$(missing_from "$after")"
if [[ -n "$missing" ]]; then
    say "missing after our module was declared: $missing"
    explain
    finish 6
fi
say "OK started from a terminal: everything it offered before, and us"
stop

# ------------------------------------------------ the way a desktop starts it
if (( ! systemd_ok )); then
    say "NOT CHECKED: socket-activated by systemd (no systemd user manager here)"
    finish 0
fi
if ! sysu systemd-run --user --quiet --collect --unit="$unit" \
        --socket-property=ListenStream="$probe/speechd.sock" \
        --setenv=XDG_CONFIG_HOME="$XDG_CONFIG_HOME" --setenv=PULSE_SERVER="$PULSE_SERVER" \
        "$(command -v speech-dispatcher)" -s -t 0 -l 5 -P "$probe/speechd.pid" -L "$probe/log" \
        >>"$probe/log/stderr" 2>&1; then
    say "systemd-run could not create a socket-activated speech-dispatcher:"
    sed 's/^/  | /' "$probe/log/stderr"
    explain
    finish 7
fi
t0=$SECONDS
after="$(offered)"
say "socket-activated by systemd ($((SECONDS - t0)) s to start and answer): ${after:-nothing}"
missing="$(missing_from "$after")"
if [[ -n "$missing" ]]; then
    say "missing when systemd starts it, as a desktop does: $missing"
    say "(this is the desktop's hang if it says nothing at all)"
    explain
    finish 8
fi
say "OK socket-activated by systemd, as a desktop starts it"
finish 0

#!/usr/bin/env bash
# The Flatpak's display sockets, read from a built package's metadata file
# ([Context] sockets=...). Run by pack-flatpak.sh on every build.
#
#   bash build/check-flatpak-sockets.sh <path/to/metadata>
#
# WHY IT EXISTS. 0.2.17 shipped `--socket=wayland` with `--socket=fallback-x11`.
# fallback-x11 grants X11 only when there is no Wayland, and the window draws
# only through X11 (Avalonia 11 has no Wayland backend). So on every Wayland
# desktop the window aborted with "XOpenDisplay failed", while every check
# passed: CI's --test-install runs on Xvfb, where there is no Wayland and the
# fallback does hand over X11. Found on Kubuntu on 2026-10-16, the first time
# anyone opened the Flatpak's window on a real desktop.
#
# Required: x11 (the window) and wayland (the daemon reads the selection through
# the Wayland data-control protocol). Refused: fallback-x11, which would mean the
# x11 line was "tidied" back.

set -euo pipefail

meta="${1:?usage: check-flatpak-sockets.sh <metadata>}"
[[ -f "$meta" ]] || { echo "error: no metadata file at $meta" >&2; exit 1; }

# Reads to the end: no early-exiting consumer in a pipeline (CLAUDE.md).
sockets="$(awk -F= '/^\[/{ctx=($0=="[Context]")} ctx && $1=="sockets" && !seen {print $2; seen=1}' "$meta")"
[[ -n "$sockets" ]] || { echo "error: $meta has no [Context] sockets= line" >&2; exit 1; }

has() { [[ ";$sockets;" == *";$1;"* ]]; }

fail=0
if has fallback-x11; then
    echo "error: sockets include fallback-x11 ($sockets)." >&2
    echo "       The window draws only through X11, and fallback-x11 withholds it on" >&2
    echo "       every Wayland desktop: the window aborts with XOpenDisplay failed." >&2
    fail=1
fi
has x11 || { echo "error: sockets lack x11 ($sockets): the window cannot open." >&2; fail=1; }
has wayland || { echo "error: sockets lack wayland ($sockets): the hotkey cannot read the selection on Wayland." >&2; fail=1; }

(( fail == 0 )) || exit 1
echo "    sockets: $sockets (x11 for the window, wayland for the selection)"

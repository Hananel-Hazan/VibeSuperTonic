#!/usr/bin/env bash
#
# Where the DESKTOP's own files go when an AppImage has a portable home.
#
# Sourced, never run:  source appimage-home.sh; vst_resolve_real_home
#
# THE PROBLEM, in one paragraph. An AppImage beside a directory named
# <image>.AppImage.home makes its runtime point $HOME inside that directory —
# AppMan marks every app portable this way, so it is a normal arrangement rather
# than an exotic one. Application state belongs there and that is the whole
# point of it. But a few files are not application state: they are the DESKTOP's
# registration of us, read by another program from one fixed location that no
# portable home can redirect. The hotkey shortcuts are one (kglobalshortcutsrc,
# the launcher .desktop). The Speech Dispatcher config is another: speechd runs
# in the user's session, reads ~/.config/speech-dispatcher, and would never look
# inside a portable home — so a config written there is a module that silently
# never appears, which for a screen reader user is indistinguishable from the
# install having failed.
#
# The real home survives the redirect because the passwd database is not
# something an AppImage can rewrite.
#
# Sets two variables and returns 0 even when there is no portable home — the
# common case, where both are empty and the caller does nothing special:
#
#   vst_portable_home   the portable home in use, or ""
#   vst_real_home       the user's actual home, or "" if it could not be found
#
# It does NOT die: the two callers need to say different things about it (the
# hotkeys can explain the store, this cannot), and a shared function that exits
# on their behalf takes that away.

vst_resolve_real_home() {
    vst_portable_home=""
    vst_real_home=""

    [[ -n "${APPIMAGE:-}" ]] || return 0
    [[ "$(cd "$HOME" 2>/dev/null && pwd)" == "$APPIMAGE.home" ]] || return 0

    vst_portable_home="$APPIMAGE.home"
    # `|| true` is load-bearing under `set -euo pipefail`: without it a failing
    # or absent getent takes the caller out at this assignment, and the
    # explanation it wanted to print — the entire point of the branch — never
    # appears. Found by testing the failure path rather than only the happy one.
    vst_real_home="$(getent passwd "$(id -un)" 2>/dev/null | cut -d: -f6 || true)"

    if [[ -z "$vst_real_home" || ! -d "$vst_real_home" || "$vst_real_home" == "$vst_portable_home" ]]; then
        vst_real_home=""
    fi
    return 0
}

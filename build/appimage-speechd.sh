#!/usr/bin/env bash
#
# Register the Speech Dispatcher module for an AppImage install.
#
#     ./VibeSuperTonic-<version>-x86_64.AppImage speechd-install [--check|--remove]
#
# The tarball's speechd-install.sh does the work; this is the adapter, and it
# exists because two of the AppImage's properties break the config it writes.
#
# 1. A MODULE CONFIG NAMES ONE ABSOLUTE FILE, AND IT CANNOT CARRY ARGUMENTS.
#    Measured 2026-08-28 against speech-dispatcher 0.12.1: an AddModule whose
#    binary field contains a space is exec'd verbatim and fails with
#    "Exec of module ... failed with error 2", while speechd still logs "Module
#    loaded" — so `AddModule "vibesupertonic" "/path/App.AppImage speechd" ""`
#    would look installed and never run. And naming the image with NO argument
#    is worse than useless: AppRun would fall through to its default case and
#    open the window, once per utterance.
#
#    So we install a symlink — ~/.local/bin/vst-speechd -> the image — and name
#    THAT. AppRun dispatches on $ARGV0, which the AppImage runtime sets to the
#    name the caller used, so the symlink runs the module and nothing else. It
#    is the same mechanism the vst-ctl symlink already relies on.
#
#    A symlink rather than a copy, which is what the hotkey client does instead:
#    a copied vst-speechd would resolve espeak/ and vst-ctl "beside me" in
#    ~/.local/bin, find neither, and answer speechd's INIT with 399. The module
#    has to run INSIDE the mount. It is started once per session, so the mount
#    it costs is paid once — the reason vst-ctl is copied does not apply.
#
# 2. A PORTABLE HOME REDIRECTS $HOME, AND SPEECHD DOES NOT FOLLOW.
#    speech-dispatcher runs in the user's session and reads the real
#    ~/.config/speech-dispatcher. A config written inside <image>.AppImage.home
#    is one nothing will ever read: the module simply never appears, with no
#    error, which is the failure mode a blind user cannot investigate. The rule
#    for which files belong in the real home is shared with the hotkeys —
#    appimage-home.sh — because it is the same rule.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

[[ -n "${APPIMAGE:-}" ]] || die "\$APPIMAGE is not set, so this is not running from an AppImage.
       If you have the tarball, run its installer directly:  ./speechd-install.sh"
[[ -f "$APPIMAGE" ]] || die "\$APPIMAGE points at $APPIMAGE, which is not there."

# BEFORE any environment is changed, because the store rule reads the
# environment: LinuxDataPaths consults $APPIMAGE and $XDG_DATA_HOME, and asking
# it first and passing the answer on is what keeps the redirect below from
# quietly moving the store the installer checks for voices.
store="$("$here/vibesupertonicd" --print-store)" || die "could not resolve the store"

# shellcheck source=/dev/null
source "$here/appimage-home.sh"
vst_resolve_real_home
home="$HOME"
if [[ -n "$vst_portable_home" ]]; then
    [[ -n "$vst_real_home" ]] || die "a portable home is in use:  $vst_portable_home

       Your real home could not be found in the passwd database, so there is
       nowhere to write a Speech Dispatcher config that speech-dispatcher will
       actually read — it looks in your real ~/.config and nowhere else.

       Do NOT delete the portable home to work around this: your models and
       settings are inside it, and the daemon would start over with an empty
       store."
    home="$vst_real_home"
    info "portable home in use; writing the desktop's files to $home instead"
fi

bindir="$home/.local/bin"
mkdir -p "$bindir"
ln -sfn "$APPIMAGE" "$bindir/vst-speechd"
info "$bindir/vst-speechd -> $APPIMAGE"

# Rebound to the real home, and only when there is a portable one to escape:
# a user who set XDG_CONFIG_HOME themselves meant it.
[[ -n "$vst_portable_home" ]] && export XDG_CONFIG_HOME="$home/.config"

# What to print when telling the user how to re-run or undo this. Inside an
# AppImage $0 is a path in a mount that disappears with the process, so the
# installer's own closing advice would be untypeable by the time it is read.
export VST_SPEECHD_SELF="$APPIMAGE speechd-install"
export VST_SPEECHD_MODULE="$bindir/vst-speechd"
export VST_SPEECHD_STORE="$store"

rc=0
bash "$here/speechd-install.sh" "$@" || rc=$?

# --remove takes the symlink with it. Left behind, it is a dangling link in the
# user's PATH the day they move the image — and this script's whole job is that
# path staying true.
for arg in "$@"; do
    if [[ "$arg" == "--remove" && $rc -eq 0 ]]; then
        rm -f "$bindir/vst-speechd"
        info "removed $bindir/vst-speechd"
    fi
done

exit $rc

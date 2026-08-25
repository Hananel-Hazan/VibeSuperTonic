#!/usr/bin/env bash
#
# Bind the hotkeys for an AppImage install.
#
#     ./VibeSuperTonic-<version>-x86_64.AppImage bind
#
# Two steps, and the first is the one that is not obvious.
#
# 1. INSTALL THE CLIENT. The key is bound to a copy of vst-ctl in ~/.local/bin,
#    not to the AppImage. vst-ctl is NativeAOT because a managed apphost costs
#    ~100 ms of runtime startup on every press against a 150 ms budget; going
#    through the AppImage instead costs a squashfs mount, measured at +14.7 ms,
#    which passes but is paid forever for a 4 MB copy of a static binary. The
#    copy is accompanied by one line of text naming the AppImage it came from,
#    because a vst-ctl with no vibesupertonicd beside it still has to be able to
#    start one on the first press (R-5).
#
#    Done by the daemon rather than here — `vibesupertonicd --ensure-client` —
#    so that the copy rule and the version comparison have one implementation.
#    The daemon and the window both run it at startup, so an upgrade repairs
#    itself without anyone re-running this script.
#
# 2. BIND THE KEYS, through build/keybindings.sh, which is unchanged code doing
#    what it already does for a tarball install: KDE via kglobalshortcutsrc and
#    a .desktop with one action per verb, Cinnamon via gsettings, and a printed
#    set of manual instructions on anything else. The only difference is that the
#    application-menu entry must open the window inside the image rather than a
#    vibesupertonic-ui that does not exist in ~/.local/bin, which is what
#    VST_KB_UI_COMMAND is for.
#
# Run from inside the AppImage; $APPIMAGE is set by its runtime.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

info() { printf '    %s\n' "$*"; }
warn() { printf '\033[33m    %s\033[0m\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

if [[ -z "${APPIMAGE:-}" ]]; then
    die "\$APPIMAGE is not set, so this is not running from an AppImage.

       If you have the tarball, its hotkeys are bound with:
           source keybindings.sh && vst_bind \"\$PWD\"
       If you have the AppImage, run it as:  ./VibeSuperTonic-*.AppImage bind"
fi

[[ -f "$APPIMAGE" ]] || die "\$APPIMAGE points at $APPIMAGE, which is not there."

printf '\n\033[36m>>> Installing the hotkey client\033[0m\n'

said="$("$here/vibesupertonicd" --ensure-client)" || die "could not install the client"
info "${said:-~/.local/bin/vst-ctl is already this build}"

bindir="$HOME/.local/bin"
[[ -x "$bindir/vst-ctl" ]] || die "$bindir/vst-ctl was not installed, so there is nothing to bind a key to.
       Binding a key to a binary that is not there is a hotkey that silently does nothing."

# Informational, never fatal: the hotkeys use the absolute path and do not care
# about PATH. It is the person typing `vst-ctl status` afterwards who does.
case ":${PATH}:" in
    *":$bindir:"*) ;;
    *) warn "$bindir is not on your PATH. The hotkeys work anyway — they use the"
       warn "full path — but typing 'vst-ctl' in a terminal will not find it." ;;
esac

# FUSE, checked here because here is the only place with a terminal. Without it
# the image cannot be mounted, so the client copied above still works — it is a
# plain binary — but the daemon it autostarts on the first press lives INSIDE the
# image and will not start. That is a hotkey that does nothing, discovered by
# pressing it, which is the exact failure R-5 exists to prevent.
if [[ ! -e /dev/fuse ]]; then
    warn "/dev/fuse is not present on this machine."
    warn ""
    warn "This AppImage is running unpacked, so binding works — but the first hotkey"
    warn "press has to START the daemon, and the daemon is inside the image."
    warn "Install FUSE (libfuse2 on Debian and Ubuntu), or start the daemon yourself"
    warn "after each login with:"
    warn "    $APPIMAGE --appimage-extract-and-run daemon &"
fi

printf '\n\033[36m>>> Binding the keys\033[0m\n'

# The window is inside the image, so the menu entry has to launch the image.
export VST_KB_UI_COMMAND="$APPIMAGE"

# shellcheck source=/dev/null
source "$here/keybindings.sh"
vst_bind "$bindir"

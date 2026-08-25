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

# A portable home makes every write below land somewhere the desktop will never
# read: kwriteconfig6 follows $HOME, and so does the launcher .desktop. Binding
# would report two shortcuts written and no key would do anything — the worst
# available outcome, and the reason this refuses rather than warning.
if [[ "$(cd "$HOME" && pwd)" == "$APPIMAGE.home" ]]; then
    die "a portable home is in use: $APPIMAGE.home

       The AppImage runtime points \$HOME there, so the shortcut configuration
       this writes would land inside that directory and your desktop would never
       read it — two shortcuts written, no key doing anything.

       It also does not do what it looks like it does: models and settings live
       beside the AppImage either way, in $(dirname "$APPIMAGE")/VibeSuperTonic,
       which is already the portable arrangement.

       Rename or remove $APPIMAGE.home and run bind again."
fi

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

# WHAT THE KEYS RAN BEFORE, read before vst_bind overwrites it.
#
# Plasma keeps a shortcut against a .desktop id and launches the action's Exec.
# Rewriting that Exec does not reach a kglobalaccel that is already running, so
# for the rest of the session a press keeps launching the OLD command — and if
# that command was a tarball's vst-ctl which this upgrade has just moved aside,
# every press reports "Could not find the program" until the next login. Found
# by doing exactly that upgrade on the development machine.
desktop="${XDG_DATA_HOME:-$HOME/.local/share}/applications/vibesupertonic.desktop"
previous=""
if [[ -f "$desktop" ]]; then
    previous="$(awk -F= '/^Exec=.*vst-ctl (read|toggle)$/ { split($2, a, " "); print a[1]; exit }' "$desktop")"
fi

# The window is inside the image, so the menu entry has to launch the image.
export VST_KB_UI_COMMAND="$APPIMAGE"

# shellcheck source=/dev/null
source "$here/keybindings.sh"

# `set -e` would take the script out on any non-zero return, which is every
# desktop vst_bind has no backend for — and those are exactly the runs where the
# migration note below is worth printing, because the user is about to bind the
# keys by hand and needs to know which path to use.
status=0
vst_bind "$bindir" || status=$?

if [[ -n "$previous" && "$previous" != "$bindir/vst-ctl" ]]; then
    printf '\n'
    if [[ ! -e "$previous" && -d "$(dirname "$previous")" ]]; then
        # A link rather than a copy: it is obviously a shim, it cannot go stale,
        # and vst-ctl resolves its own directory through /proc/self/exe, so the
        # sidecar naming the AppImage is still found through it.
        if ln -sfn "$bindir/vst-ctl" "$previous" 2>/dev/null; then
            info "your keys were bound to $previous, which this upgrade moved."
            info "Linked it to the new client so presses keep working before you log out."
            info "Safe to delete after the next login."
        fi
    else
        warn "your keys were bound to $previous."
        warn "KDE may keep launching that until you log out. If a press says"
        warn "\"Could not find the program\", log out and back in."
    fi
fi

exit $status

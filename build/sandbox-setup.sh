#!/usr/bin/env bash
#
# Connect a snap or Flatpak install to the desktop: the hotkeys and the Speech
# Dispatcher module. Run it ON THE HOST, from inside the installed package:
#
#   snap:     bash /snap/vibesupertonic/current/sandbox-setup.sh bind
#   Flatpak:  bash "$(flatpak info --show-location io.github.hananel_hazan.VibeSuperTonic)/files/lib/vibesupertonic/sandbox-setup.sh" bind
#
#   verbs:    bind | unbind | status | speechd-install [--check|--remove]
#
# Run inside the sandbox — `vibesupertonic.setup`, or
# `flatpak run --command=sandbox-setup.sh <id>` — it prints the host command
# for this install and changes nothing.
#
# ---------------------------------------------------------------------------
# WHY THIS RUNS OUTSIDE THE SANDBOX. Both things it sets up are the DESKTOP's
# configuration, not ours: kglobalshortcutsrc and a launcher in
# ~/.local/share/applications for the keys, and ~/.config/speech-dispatcher for
# the screen reader. A sandbox cannot write any of them. The snap store can
# grant that access (personal-files) and Flathub can too (--filesystem), but
# only after a manual review, and the grant would then sit on the package
# forever, used once.
#
# A script the user runs once on the host needs no grant. keybindings.sh and
# speechd-install.sh are the same tested code the tarball and the AppImage use,
# unchanged. This file only works out the two commands the desktop must run,
# which are the only things that differ:
#
#   hotkey:   snap     /snap/bin/<name>.ctl — the client inside its
#                      confinement, so it shares the daemon's runtime directory.
#             Flatpak  the deployment's own vst-ctl, run directly from the host.
#                      It is NativeAOT and needs only libc, and `flatpak run`
#                      would add a sandbox setup to every press. It finds the
#                      daemon through the app's shared runtime directory; see
#                      FlatpakPeer in Core.
#
#   speechd:  speech-dispatcher execs ONE absolute path with NO arguments
#             (measured, S4 — see appimage-speechd.sh). A snap already has one:
#             /snap/bin/<name>.speechd. A Flatpak does not, so this writes a
#             two-line wrapper to ~/.local/bin/vst-speechd, the same path the
#             AppImage uses for its symlink.
#
# Both paths are chosen to survive an update. The snap one is /snap/bin. The
# Flatpak one goes through the deployment's `active` link, never the commit
# directory `flatpak info --show-location` prints, because the commit changes on
# every update and a hotkey that named it would stop working at the next one.
# ---------------------------------------------------------------------------

set -euo pipefail

info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
verb="${1:-help}"
[[ $# -gt 0 ]] && shift


# ------------------------------------------------------------ inside? say so
#
# Inside, every write below would fail — or worse, land in the sandbox's
# private home and look like it worked. So print the host command and stop.
if [[ -n "${SNAP:-}" && "$here" == "$SNAP"* ]]; then
    name="${SNAP_INSTANCE_NAME:-${SNAP_NAME:-vibesupertonic}}"
    cat <<EOF
This runs inside the snap, which cannot change your desktop's settings.
Run this in a terminal instead:

    bash /snap/$name/current/sandbox-setup.sh bind              # the hotkeys
    bash /snap/$name/current/sandbox-setup.sh speechd-install   # screen readers
EOF
    exit 0
fi
if [[ -n "${FLATPAK_ID:-}" && -f /.flatpak-info ]]; then
    cat <<EOF
This runs inside the Flatpak, which cannot change your desktop's settings.
Run this in a terminal instead:

    bash "\$(flatpak info --show-location $FLATPAK_ID)/files/lib/vibesupertonic/sandbox-setup.sh" bind
    bash "\$(flatpak info --show-location $FLATPAK_ID)/files/lib/vibesupertonic/sandbox-setup.sh" speechd-install
EOF
    exit 0
fi

# ------------------------------------------------------------ which package?
#
# VST_SANDBOX_SNAP_ROOT replaces /snap, for build/check-sandbox-setup.sh and
# nothing else: that harness builds a fake snap mount under a scratch directory,
# for the reason speechd-install.sh takes its paths from the environment.
snap_root="${VST_SANDBOX_SNAP_ROOT:-/snap}"
kind=""
if [[ "$here" == "$snap_root"/*/* && "${here#"$snap_root"/}" =~ ^([^/]+)/[^/]+$ ]]; then
    kind=snap
    name="${BASH_REMATCH[1]}"
    # /snap/<name>/current, whatever revision this was started from: the path
    # that is still true after the next refresh.
    stable="$snap_root/$name/current"
    ctl_cmd="$snap_root/bin/$name.ctl"
    ui_cmd="$snap_root/bin/$name"
    module_cmd="$snap_root/bin/$name.speechd"
    daemon_cmd=("$snap_root/bin/$name.daemon")
    self="bash $stable/sandbox-setup.sh"
    [[ -x "$ctl_cmd" ]] || die "$ctl_cmd is not there. Is the snap installed, rather than just unpacked?"
elif [[ "$here" == */files/lib/vibesupertonic && -f "$here/../../../metadata" ]]; then
    kind=flatpak
    deploy="$(cd "$here/../../.." && pwd)"
    id="$(awk -F= '/^\[/{app=($0=="[Application]")} app && $1=="name" && !seen {print $2; seen=1}' "$deploy/metadata")"
    [[ -n "$id" ]] || die "no [Application] name in $deploy/metadata"
    # <installation>/app/<id>/<arch>/<branch>/<commit>: swap the commit for
    # `active`, which follows updates.
    stable_deploy="$(dirname "$deploy")/active"
    [[ -f "$stable_deploy/metadata" ]] || stable_deploy="$deploy"
    installation="${deploy%%/app/"$id"/*}"
    stable="$stable_deploy/files/lib/vibesupertonic"
    ctl_cmd="$stable/vst-ctl"
    # flatpak exports one launcher per app, a single path with no arguments.
    ui_cmd="$installation/exports/bin/$id"
    [[ -x "$ui_cmd" ]] || ui_cmd="flatpak"
    daemon_cmd=(flatpak run "--command=/app/lib/vibesupertonic/vibesupertonicd" "$id")
    self="bash \"\$(flatpak info --show-location $id)/files/lib/vibesupertonic/sandbox-setup.sh\""
    module_cmd="$HOME/.local/bin/vst-speechd"
    command -v flatpak >/dev/null 2>&1 || die "flatpak is not on PATH, so nothing could start the daemon."
else
    die "this is not running from an installed snap or Flatpak ($here).
       For the tarball use install.sh; for the AppImage, '<AppImage> bind'."
fi

# ------------------------------------------------------------------- verbs
case "$verb" in
    bind|unbind|status)
        export VST_KB_CTL_COMMAND=""
        [[ "$kind" == snap ]] && VST_KB_CTL_COMMAND="$ctl_cmd"
        # The menu entry keybindings.sh writes carries the shortcuts; it has to
        # open the window, and the window is not a file beside vst-ctl here.
        if [[ "$verb" == bind && "$ui_cmd" == flatpak ]]; then
            die "no exported launcher for $id under $installation/exports/bin.
       Reinstall the Flatpak, or bind the keys by hand to:  $ctl_cmd read"
        fi
        export VST_KB_UI_COMMAND="$ui_cmd"
        # shellcheck source=keybindings.sh
        source "$here/keybindings.sh"
        case "$verb" in
            bind)   info "$kind install; the keys will run $(_vst_kb_command "$stable" read)"
                    vst_bind "$stable" ;;
            unbind) vst_unbind ;;
            status) vst_kb_status ;;
        esac
        ;;

    speechd-install)
        # The store is the daemon's decision — the same rule the module reads.
        store="$("${daemon_cmd[@]}" --print-store 2>/dev/null | tail -1 || true)"
        [[ "$store" == /* ]] || die "could not ask the daemon where its store is (got '$store').
       Start VibeSuperTonic once, then try again."

        if [[ "$kind" == flatpak ]]; then
            # Only an install writes the wrapper. --check must change nothing,
            # and --remove deletes it afterwards.
            installing=1
            for arg in "$@"; do [[ "$arg" == --remove || "$arg" == --check ]] && installing=0; done
            if (( installing )); then
                mkdir -p "$(dirname "$module_cmd")"
                # A file, not a symlink: the command needs an argument, and
                # speechd cannot pass one. See the header.
                cat > "$module_cmd.tmp" <<EOF
#!/bin/sh
# Written by VibeSuperTonic's sandbox-setup.sh. Speech Dispatcher runs this, and
# it runs the module inside the Flatpak. Remove it with:
#   $self speechd-install --remove
exec flatpak run --command=/app/lib/vibesupertonic/vst-speechd $id "\$@"
EOF
                chmod 755 "$module_cmd.tmp"
                mv -f "$module_cmd.tmp" "$module_cmd"
                info "$module_cmd runs the module inside $id"
            fi
        fi

        export VST_SPEECHD_MODULE="$module_cmd"
        export VST_SPEECHD_STORE="$store"
        export VST_SPEECHD_SELF="$self speechd-install"
        rc=0
        bash "$here/speechd-install.sh" "$@" || rc=$?

        # --remove takes the wrapper with it, as the AppImage adapter takes its
        # symlink: a dangling module command left in PATH is the next person's
        # mystery.
        if [[ "$kind" == flatpak && $rc -eq 0 ]]; then
            for arg in "$@"; do
                if [[ "$arg" == --remove && -f "$module_cmd" ]] && grep -c 'sandbox-setup.sh' "$module_cmd" >/dev/null; then
                    rm -f "$module_cmd"
                    info "removed $module_cmd"
                fi
            done
        fi
        exit $rc
        ;;

    help|-h|--help)
        sed -n '3,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
        ;;

    *) die "unknown verb: $verb (bind | unbind | status | speechd-install)" ;;
esac

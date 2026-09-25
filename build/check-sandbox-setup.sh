#!/usr/bin/env bash
#
# Hermetic checks for sandbox-setup.sh, against a fake snap mount and a fake
# Flatpak installation built in a scratch directory.
#
#     bash build/check-sandbox-setup.sh [--negative-control]
#
# Nothing here touches the real desktop: every home, config and data directory
# is redirected, keybindings.sh runs with VST_KB_DRY_RUN=1, and speechd-install.sh
# is replaced by a stub that prints what it was handed. That is the adapter's
# whole job — handing over the right commands — and speechd-install.sh itself
# has its own harness in spike/speechd-s4-install/.
#
# WHAT IT DEFENDS, each silent when wrong:
#   - a snap hotkey that names the revision directory, or the file in the mount
#     rather than /snap/bin/<name>.ctl, works until the next refresh or never;
#   - a Flatpak hotkey that names the commit directory `flatpak info` prints
#     stops working at the next update;
#   - a Speech Dispatcher module command with an argument, or the wrong one,
#     leaves a module speechd lists and never runs;
#   - run inside a sandbox, the script must write nothing: its writes would land
#     in the sandbox's private home and look like success.
#
# --negative-control breaks the script three ways first, and requires each
# break to be caught, before the real result counts. That is the project's
# rule: a check never seen failing is not evidence.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_id="io.github.hananel_hazan.VibeSuperTonic"

red()   { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }

# One run of every check against a given copy of sandbox-setup.sh. Prints the
# first failure and returns 1, or returns 0.
run_checks() {
    local script="$1" t out
    t="$(mktemp -d)"
    # shellcheck disable=SC2064  # expand now: $t is local
    trap "rm -rf '$t'" RETURN

    local home="$t/home" bin="$t/bin"
    mkdir -p "$home" "$bin"
    # No-op KDE tools, so the KDE backend can run its dry run without Plasma.
    for tool in kwriteconfig6 kreadconfig6; do
        printf '#!/bin/sh\nexit 0\n' > "$bin/$tool"; chmod 755 "$bin/$tool"
    done

    # Everything a package ships beside sandbox-setup.sh that it uses.
    populate() {
        local dir="$1"
        mkdir -p "$dir"
        cp "$script" "$dir/sandbox-setup.sh"
        cp "$root/build/keybindings.sh" "$dir/keybindings.sh"
        printf '#!/bin/sh\nexit 0\n' > "$dir/vst-ctl"; chmod 755 "$dir/vst-ctl"
        cat > "$dir/speechd-install.sh" <<'STUB'
#!/usr/bin/env bash
printf 'MODULE=%s\nSTORE=%s\nSELF=%s\nARGS=%s\n' \
    "$VST_SPEECHD_MODULE" "$VST_SPEECHD_STORE" "$VST_SPEECHD_SELF" "$*"
STUB
    }

    local env_base=(env -i PATH="$bin:/usr/bin:/bin" HOME="$home"
                    XDG_CONFIG_HOME="$home/.config" XDG_DATA_HOME="$home/.local/share"
                    VST_KB_DRY_RUN=1 VST_KB_BACKEND=kde)

    # ---------------------------------------------------------------- snap
    local snap="$t/snap" rev="$t/snap/vibesupertonic/x5"
    populate "$rev"
    ln -s x5 "$snap/vibesupertonic/current"
    mkdir -p "$snap/bin"
    for app in vibesupertonic vibesupertonic.ctl vibesupertonic.speechd; do
        printf '#!/bin/sh\nexit 0\n' > "$snap/bin/$app"; chmod 755 "$snap/bin/$app"
    done
    printf '#!/bin/sh\necho %s\n' "$home/snap/vibesupertonic/common" > "$snap/bin/vibesupertonic.daemon"
    chmod 755 "$snap/bin/vibesupertonic.daemon"

    out="$("${env_base[@]}" VST_SANDBOX_SNAP_ROOT="$snap" bash "$rev/sandbox-setup.sh" bind 2>&1)" \
        || { red "snap: bind failed: $out"; return 1; }
    [[ "$out" == *"Exec=$snap/bin/vibesupertonic.ctl read"* ]] \
        || { red "snap: the hotkey does not run /snap/bin/<name>.ctl:"; printf '%s\n' "$out"; return 1; }
    [[ "$out" == *"Exec=$snap/bin/vibesupertonic"$'\n'* ]] \
        || { red "snap: the menu entry does not open /snap/bin/<name>:"; printf '%s\n' "$out"; return 1; }
    [[ "$out" != *"/x5/"* ]] \
        || { red "snap: a command names the revision directory, which the next refresh removes:"; printf '%s\n' "$out"; return 1; }

    out="$("${env_base[@]}" VST_SANDBOX_SNAP_ROOT="$snap" bash "$rev/sandbox-setup.sh" speechd-install 2>&1)" \
        || { red "snap: speechd-install failed: $out"; return 1; }
    [[ "$out" == *"MODULE=$snap/bin/vibesupertonic.speechd"$'\n'* ]] \
        || { red "snap: the module command is not /snap/bin/<name>.speechd:"; printf '%s\n' "$out"; return 1; }
    [[ "$out" == *"STORE=$home/snap/vibesupertonic/common"$'\n'* ]] \
        || { red "snap: the store was not asked of the daemon:"; printf '%s\n' "$out"; return 1; }

    out="$("${env_base[@]}" VST_SANDBOX_SNAP_ROOT="$snap" SNAP="$rev" SNAP_NAME=vibesupertonic \
           bash "$rev/sandbox-setup.sh" bind 2>&1)" \
        || { red "snap, inside: exited non-zero: $out"; return 1; }
    [[ "$out" == *"Run this in a terminal"* && ! -e "$home/.local/share/applications" ]] \
        || { red "snap, inside: it did not refuse, or it wrote something:"; printf '%s\n' "$out"; return 1; }

    # ------------------------------------------------------------- flatpak
    local inst="$t/flatpak" commit active
    commit="$inst/app/$app_id/x86_64/stable/0a1b2c3d"
    active="$inst/app/$app_id/x86_64/stable/active"
    populate "$commit/files/lib/vibesupertonic"
    printf '[Application]\nname=%s\nruntime=org.freedesktop.Platform/x86_64/25.08\n' "$app_id" > "$commit/metadata"
    ln -s 0a1b2c3d "$active"
    mkdir -p "$inst/exports/bin"
    printf '#!/bin/sh\nexit 0\n' > "$inst/exports/bin/$app_id"; chmod 755 "$inst/exports/bin/$app_id"
    # A fake `flatpak` that answers the one question sandbox-setup.sh asks it.
    cat > "$bin/flatpak" <<FAKE
#!/bin/sh
case "\$*" in
    *"run --command=/app/lib/vibesupertonic/vibesupertonicd $app_id --print-store"*)
        echo "$home/.var/app/$app_id/data/vibesupertonic" ;;
    *) echo "fake flatpak: unexpected \$*" >&2; exit 1 ;;
esac
FAKE
    chmod 755 "$bin/flatpak"

    # Run from the COMMIT path, which is what `flatpak info --show-location`
    # prints and therefore what the documented command runs.
    local from="$commit/files/lib/vibesupertonic/sandbox-setup.sh"

    out="$("${env_base[@]}" bash "$from" bind 2>&1)" \
        || { red "flatpak: bind failed: $out"; return 1; }
    [[ "$out" == *"Exec=$active/files/lib/vibesupertonic/vst-ctl read"* ]] \
        || { red "flatpak: the hotkey does not run vst-ctl through the deployment's active link:"; printf '%s\n' "$out"; return 1; }
    [[ "$out" != *"0a1b2c3d"* ]] \
        || { red "flatpak: a command names the commit directory, which the next update removes:"; printf '%s\n' "$out"; return 1; }
    [[ "$out" == *"Exec=$inst/exports/bin/$app_id"$'\n'* ]] \
        || { red "flatpak: the menu entry does not open the exported launcher:"; printf '%s\n' "$out"; return 1; }

    local wrapper="$home/.local/bin/vst-speechd"
    out="$("${env_base[@]}" bash "$from" speechd-install --check 2>&1)" \
        || { red "flatpak: speechd-install --check failed: $out"; return 1; }
    [[ ! -e "$wrapper" ]] \
        || { red "flatpak: --check wrote the module wrapper; a check must change nothing"; return 1; }

    out="$("${env_base[@]}" bash "$from" speechd-install 2>&1)" \
        || { red "flatpak: speechd-install failed: $out"; return 1; }
    [[ "$out" == *"MODULE=$wrapper"$'\n'* && -x "$wrapper" ]] \
        || { red "flatpak: the module command is not an executable ~/.local/bin/vst-speechd:"; printf '%s\n' "$out"; return 1; }
    local exec_line
    exec_line="$(awk '/^exec / && !seen { print; seen = 1 }' "$wrapper")"
    [[ "$exec_line" == "exec flatpak run --command=/app/lib/vibesupertonic/vst-speechd $app_id \"\$@\"" ]] \
        || { red "flatpak: the wrapper runs the wrong thing: $exec_line"; return 1; }
    [[ "$out" == *"STORE=$home/.var/app/$app_id/data/vibesupertonic"$'\n'* ]] \
        || { red "flatpak: the store was not asked of the daemon:"; printf '%s\n' "$out"; return 1; }

    out="$("${env_base[@]}" bash "$from" speechd-install --remove 2>&1)" \
        || { red "flatpak: speechd-install --remove failed: $out"; return 1; }
    [[ ! -e "$wrapper" ]] \
        || { red "flatpak: --remove left the wrapper behind in PATH"; return 1; }

    # The Flatpak's inside-refusal needs a real /.flatpak-info, so it is checked
    # against an installed deployment by pack-flatpak.sh --test-install instead.

    # --------------------------------------------------------------- neither
    out="$("${env_base[@]}" bash "$root/build/sandbox-setup.sh" bind 2>&1)" \
        && { red "outside any package it did not refuse: $out"; return 1; }
    [[ "$out" == *"not running from an installed snap or Flatpak"* ]] \
        || { red "outside any package it refused with the wrong sentence: $out"; return 1; }

    return 0
}

if [[ "${1:-}" == --negative-control ]]; then
    sab="$(mktemp -d)"
    # shellcheck disable=SC2064
    trap "rm -rf '$sab'" EXIT
    # sed programs matching the script's own text, so single quotes are meant.
    # shellcheck disable=SC2016
    declare -A breaks=(
        [flatpak-commit-path]='s|stable_deploy="$(dirname "$deploy")/active"|stable_deploy="$deploy"|'
        [snap-ctl-in-mount]='s|\[\[ "$kind" == snap \]\] \&\& VST_KB_CTL_COMMAND="$ctl_cmd"|:|'
        [wrapper-on-check]='s/\[\[ "$arg" == --remove || "$arg" == --check \]\]/[[ "$arg" == --remove ]]/'
    )
    for name in "${!breaks[@]}"; do
        sed "${breaks[$name]}" "$root/build/sandbox-setup.sh" > "$sab/sandbox-setup.sh"
        if cmp -s "$sab/sandbox-setup.sh" "$root/build/sandbox-setup.sh"; then
            red "negative control '$name' changed nothing; the sabotage no longer matches the script"
            exit 1
        fi
        if run_checks "$sab/sandbox-setup.sh" >/dev/null 2>&1; then
            red "negative control '$name' was NOT caught"
            exit 1
        fi
        green "negative control '$name' caught"
    done
fi

if run_checks "$root/build/sandbox-setup.sh"; then
    green "sandbox-setup.sh: snap and Flatpak commands, module, store and refusals all correct"
else
    exit 1
fi

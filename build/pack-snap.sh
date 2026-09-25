#!/usr/bin/env bash
#
# Build the snap.
#
#     bash build/pack-tar.sh -v X.Y.Z && bash build/pack-snap.sh -v X.Y.Z [--grade stable]
#
# Produces dist/VibeSuperTonic-<version>-amd64.snap.
#
#   --grade devel|stable   devel (the default) cannot be released to the
#                          stable or candidate channels; a release run says stable
#   --destructive-mode     build on this machine rather than in an LXD container.
#                          Only on Ubuntu 24.04 (the core24 base); CI uses it
#   --compose-only         write the snapcraft project and stop, for inspection
#   --check FILE.snap      run only the checks on a snap already built, which is
#                          also how each of them was seen failing
#   --test-install         also install the snap, talk to its daemon under real
#                          confinement, and remove it. Needs root and snapd;
#                          refuses if vibesupertonic is already installed. CI does
#
# ---------------------------------------------------------------------------
# THIS SCRIPT PUBLISHES NOTHING, like pack-appimage.sh. It packages the tree
# build/pack-tar.sh composed at dist/release-linux/VibeSuperTonic, so the
# tarball's assertions are the snap's too, and it re-asks the ones a stale tree
# could break (check-composed-tree.sh). snapcraft only adds the snap metadata and
# the three native libraries the daemon loads. It compiles nothing.
#
# Uploading is not this script's job either. Once the snap name is registered:
#     snapcraft upload --release=edge dist/VibeSuperTonic-<version>-amd64.snap
# ---------------------------------------------------------------------------

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
grade="devel"
destructive=0
compose_only=0
check_only=""
test_install=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version)       version="${2:-}"; shift 2 ;;
        --grade)            grade="${2:-}"; shift 2 ;;
        --destructive-mode) destructive=1; shift ;;
        --compose-only)     compose_only=1; shift ;;
        --check)            check_only="${2:-}"; shift 2 ;;
        --test-install)     test_install=1; shift ;;
        -h|--help)
            sed -n '3,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

[[ "$grade" == devel || "$grade" == stable ]] || die "--grade is devel or stable, not '$grade'"

# Same source of truth as every other packer. See pack-tar.sh.
if [[ -z "$version" ]]; then
    props="$root/Directory.Build.props"
    [[ -f "$props" ]] || die "Directory.Build.props not found at $props"
    version="$(awk '/<VstVersion>/ { sub(/.*<VstVersion>/, ""); sub(/<\/VstVersion>.*/, ""); gsub(/[[:space:]]/, ""); print; exit }' "$props")"
    [[ -n "$version" ]] || die "no non-empty <VstVersion> in $props"
    info "version not supplied; using <VstVersion> $version from Directory.Build.props"
fi

app_id="io.github.hananel_hazan.VibeSuperTonic"
staging="$root/dist/release-linux/VibeSuperTonic"
project="$root/dist/build-linux/snap"
out="$root/dist/VibeSuperTonic-$version-amd64.snap"

# shellcheck source=check-composed-tree.sh
source "$root/build/check-composed-tree.sh"

# Everything up to a finished .snap, skipped by --check.
compose_and_build() {
    # --------------------------------------------------------------- preconditions
    step "Checking the composed tree…"
    vst_check_composed_tree "$staging" "$version"
    vst_check_store_metadata "$staging" "$version"

    # ------------------------------------------------------------------- compose
    step "Composing the snapcraft project…"

    rm -rf "$project"
    mkdir -p "$project/snap/gui" "$project/payload"
    cp -a "$staging/." "$project/payload/"

    # Where snapd and the store look for the desktop entry, the listing and the
    # icon. The tree keeps them in desktop/, which is a tarball's layout, not a
    # desktop's.
    share="$project/payload/usr/share"
    install -Dm644 "$staging/desktop/$app_id.metainfo.xml" "$share/metainfo/$app_id.metainfo.xml"
    install -Dm644 "$staging/desktop/$app_id.png"          "$share/icons/hicolor/256x256/apps/$app_id.png"
    # Icon= as a ${SNAP} path, which snapd expands. A bare theme name would need
    # the host's icon theme to find a file that exists only inside the snap.
    install -d "$share/applications"
    sed "s|^Icon=.*|Icon=\${SNAP}/usr/share/icons/hicolor/256x256/apps/$app_id.png|" \
        "$staging/desktop/$app_id.desktop" > "$share/applications/$app_id.desktop"
    cp "$staging/desktop/$app_id.png" "$project/snap/gui/vibesupertonic.png"

    sed -e "s|@VERSION@|$version|g" -e "s|@GRADE@|$grade|g" \
        "$root/build/snap/snapcraft.yaml.in" > "$project/snap/snapcraft.yaml"
    if grep -c '@[A-Z_]*@' "$project/snap/snapcraft.yaml" >/dev/null; then
        die "a placeholder survived in $project/snap/snapcraft.yaml"
    fi
    info "$project"

    if (( compose_only )); then
        step "Composed; not building (--compose-only)."
        exit 0
    fi

    # --------------------------------------------------------------------- build
    step "Building the snap…"

    command -v snapcraft >/dev/null 2>&1 || die "snapcraft is required and was not found.
           sudo snap install snapcraft --classic
           Or run with --compose-only to inspect the project without building."
    command -v unsquashfs >/dev/null 2>&1 || die "unsquashfs is required (squashfs-tools)"

    rm -f "$out"
    mode=()
    (( destructive )) && mode=(--destructive-mode)
    # snapcraft refuses to write outside its project directory, so the snap
    # lands there first and is moved to dist/ after. Found by CI's first run.
    built="$project/$(basename "$out")"
    rm -f "$built"
    (cd "$project" && snapcraft pack "${mode[@]}" --output "$built") || die "snapcraft pack failed"
    [[ -f "$built" ]] && mv -f "$built" "$out"
    [[ -f "$out" ]] || die "snapcraft reported success and produced no $out"
}

if [[ -n "$check_only" ]]; then
    [[ -f "$check_only" ]] || die "no snap at $check_only"
    out="$(cd "$(dirname "$check_only")" && pwd)/$(basename "$check_only")"
    command -v unsquashfs >/dev/null 2>&1 || die "unsquashfs is required (squashfs-tools)"
else
    compose_and_build
fi

# ---------------------------------------------------------------- assertions
step "Checking what came out…"

extract="$root/dist/build-linux/snap-extract"
rm -rf "$extract"
mkdir -p "$(dirname "$extract")"
unsquashfs -q -d "$extract" "$out" >/dev/null || die "unsquashfs could not read $out"

meta="$extract/meta/snap.yaml"
[[ -f "$meta" ]] || die "no meta/snap.yaml in the snap"

# The version snapd and the store will show.
meta_version="$(awk '/^version:/ && !seen { v = $2; gsub(/["'"'"']/, "", v); print v; seen = 1 }' "$meta")"
[[ "$meta_version" == "$version" ]] || die "meta/snap.yaml says version '$meta_version', not $version"

confinement="$(awk '/^confinement:/ && !seen { print $2; seen = 1 }' "$meta")"
[[ "$confinement" == strict ]] || die "confinement is '$confinement'. It must be strict: classic needs a
       manual store review this package does not need. See snapcraft.yaml.in."

# The apps block of snap.yaml, one app per line with its keys, so the next two
# checks can read it without a YAML library the build machine may not have.
apps="$(awk '
    /^apps:/ { inapps = 1; next }
    inapps && /^[^ ]/ { inapps = 0 }
    inapps && /^  [A-Za-z0-9-]+:/ { name = $1; sub(/:$/, "", name); line[name] = ""; order[++n] = name; next }
    inapps && name != "" { line[name] = line[name] " " $0 }
    END { for (i = 1; i <= n; i++) print order[i] ":" line[order[i]] }' "$meta")"

for app in vibesupertonic daemon ctl speechd setup; do
    [[ "$apps" == *"$app:"* ]] || die "the snap has no '$app' app. sandbox-setup.sh binds keys and the
       Speech Dispatcher module to /snap/bin/vibesupertonic.<app>; without it they name nothing."
done

# THE HOTKEY STAYS BARE. An extension or a command-chain on ctl wraps every key
# press in a launcher script. It would still work, so nothing else would ever
# complain, and every press would be slower for the life of the package.
# An awk that reads to the end, not one that exits on the match: an early exit
# in a pipeline is the SIGPIPE shape CLAUDE.md forbids.
ctl_line="$(awk -F: '$1 == "ctl" && !seen { print; seen = 1 }' <<<"$apps")"
[[ -n "$ctl_line" ]] || die "could not read the ctl app out of meta/snap.yaml"
[[ "$ctl_line" != *command-chain* ]] || die "the ctl app has a command-chain:
       $ctl_line
       It runs on every hotkey press. Keep extensions off it (snapcraft.yaml.in)."

# THE CONTROL SOCKET NEEDS network-bind, on every app that can start the daemon
# (decision 2 in snapcraft.yaml.in), because a daemon inherits its starter's
# seccomp filter. Without it listen() fails under confinement and nowhere else,
# so only --test-install would notice, and only where snapd runs.
for app in vibesupertonic daemon ctl speechd; do
    app_line="$(awk -F: -v a="$app" '$1 == a && !seen { print; seen = 1 }' <<<"$apps")"
    [[ " $app_line " == *" - network-bind "* ]] || die "the '$app' app does not plug network-bind:
       $app_line
       snapd's seccomp filter refuses listen() without it, and a daemon this app
       starts aborts on its control socket. See snapcraft.yaml.in."
    [[ " $app_line " == *" - unity7 "* ]] || die "the '$app' app does not plug unity7:
       $app_line
       a daemon this app starts cannot reach the session bus, and has no tray icon."
done
info "version $version, strict, five apps, ctl is bare, and all four can listen and show a tray"

# Assertions 3 and 4 again, on the finished snap: snapcraft's stage-packages are
# the one thing here that could drag something in.
found="$(find "$extract/models" -type f -print -quit 2>/dev/null || true)"
[[ -z "$found" ]] || die "the snap contains model files ($found)"
found="$(find "$extract" -name 'libonnxruntime_providers_cuda.so' -print -quit)"
[[ -z "$found" ]] || die "the snap contains libonnxruntime_providers_cuda.so"

# The daemon's native libraries, where the top-level LD_LIBRARY_PATH says.
# Without them the daemon starts, cannot open the audio device, and speaks to
# nobody.
for lib in libpulse.so.0 libX11.so.6 libwayland-client.so.0; do
    [[ -e "$extract/lib/native/$lib" ]] || die "$lib is not in the snap's lib/native.
       The daemon loads it by name; see the native-libs part. Staged into
       usr/lib instead, the GNOME extension's gpu cleanup deletes it."
done
info "no models, no CUDA provider, and the daemon's three libraries are staged"

# The binaries as packed, and the module's INIT. It answers 399 when espeak/ did
# not land beside it, which is the one thing this repackaging could break.
for binary in vibesupertonicd vst-ctl vst-speechd; do
    reported="$("$extract/$binary" --version 2>/dev/null | tr -d '[:space:]' || true)"
    [[ "$reported" == "$version" ]] || die "$binary inside the snap reports '$reported', not $version"
done
init="$(printf 'INIT\nQUIT\n' | timeout 30 "$extract/vst-speechd" 2>/dev/null | awk '/^(299|399) / { last = $0 } END { print last }' || true)"
[[ "$init" == 299\ * ]] || die "the module inside the snap answered INIT with: ${init:-nothing}"
info "binaries report $version; the module answers INIT"

rm -rf "$extract"

# ------------------------------------------------------------ under snapd
#
# EVERYTHING ABOVE RUNS THE SNAP'S FILES OUTSIDE ITS CONFINEMENT, which is how
# revision 1 shipped a daemon that could not start: seccomp refused listen()
# (no network-bind plug), so it aborted 1.7 s after every hotkey
# press, and every check here passed. Found by the first person to install it
# (2026-09-25). This section is the check that would have caught it: install the
# snap, start the daemon the way a hotkey does, and make it answer.
if (( test_install )); then
    step "Installing it, and talking to its daemon under confinement…"

    [[ $EUID -eq 0 ]] || die "--test-install installs a snap, so it needs root (sudo)."
    command -v snap >/dev/null 2>&1 || die "--test-install needs snapd, and there is no snap command."
    if snap list vibesupertonic >/dev/null 2>&1; then
        die "vibesupertonic is already installed here, and --test-install would replace
       and then remove it. Remove it first, or run this on a machine without it."
    fi

    snap install --dangerous "$out" >/dev/null || die "snap install --dangerous $out failed"
    trap 'snap remove --purge vibesupertonic >/dev/null 2>&1 || true' EXIT

    # As the person who ran sudo, not as root: the store, the socket and the
    # confinement are all per user, and root's are not the ones a user gets.
    user="${SUDO_USER:-root}"
    user_home="$(getent passwd "$user" | cut -d: -f6)"
    as_user=(sudo -u "$user" -H env HOME="$user_home")

    said="$("${as_user[@]}" timeout 90 snap run vibesupertonic.ctl status 2>&1 || true)"
    if [[ "$said" != *"\"Version\":\"$version\""* ]]; then
        # Say WHY, here, rather than telling somebody to go and find out. The
        # daemon run in the foreground prints its own exception, and the kernel
        # log names what AppArmor or seccomp refused. Both read to the end
        # (tail, not head): no early-exiting consumer in a pipeline.
        printf '    the daemon in the foreground said:\n' >&2
        "${as_user[@]}" timeout 30 snap run vibesupertonic.daemon 2>&1 | tail -25 | sed 's/^/      | /' >&2 || true
        printf '    the sandbox refused:\n' >&2
        journalctl -k --since "-10min" --no-pager 2>/dev/null \
            | grep -E 'apparmor="DENIED".*snap\.vibesupertonic|type=1326.*vibesupertonic' \
            | tail -25 | sed 's/^/      | /' >&2 || true
        die "the daemon did not answer under confinement. vst-ctl said:
       $said"
    fi
    info "the hotkey client started the daemon under confinement, and it answered $version"

    config="$("${as_user[@]}" timeout 30 snap run vibesupertonic.ctl config 2>&1 || true)"
    [[ "$config" == *"\"StoreRoot\":\"$user_home/snap/vibesupertonic/common\""* ]] || die "the daemon's store is not ~/snap/vibesupertonic/common. config said:
       $config"
    info "its store is ~/snap/vibesupertonic/common"

    init="$(printf 'INIT\nQUIT\n' | "${as_user[@]}" timeout 60 snap run vibesupertonic.speechd 2>/dev/null \
            | awk '/^(299|399) / { last = $0 } END { print last }' || true)"
    [[ "$init" == 299\ * ]] || die "the Speech Dispatcher module under confinement answered INIT with: ${init:-nothing}"

    inside="$("${as_user[@]}" timeout 30 snap run vibesupertonic.setup bind 2>&1 || true)"
    [[ "$inside" == *"Run this in a terminal"* ]] || die "vibesupertonic.setup did not refuse inside the snap. It said:
       $inside"
    info "the module answers INIT, and the setup app refuses inside the sandbox"

    # SPEECH-DISPATCHER STARTING THE MODULE ITSELF, which is not the same as the
    # INIT above: on revision 2 the module answered INIT by hand and yet, once
    # declared, speech-dispatcher hung and offered nothing, espeak-ng included.
    # A private speech-dispatcher; see check-snap-speechd.sh, which a desktop
    # with the snap installed can run too.
    if command -v speech-dispatcher >/dev/null 2>&1 && command -v spd-say >/dev/null 2>&1; then
        "${as_user[@]}" bash "$root/build/check-snap-speechd.sh" >&2 \
            || die "speech-dispatcher could not load the snap's module (above). A user who ran
       sandbox-setup.sh speechd-install would lose every voice."
        info "a private speech-dispatcher loads the module and keeps what it offered before"
    else
        info "NOT CHECKED: speech-dispatcher loading the module (no speech-dispatcher here; CI installs it)"
    fi

    # THE TRAY, which needs the session bus, which confinement can refuse
    # outright: revision 2's daemon reported "ConnectException: Permission
    # denied" on a real desktop. There is no tray host here, so the right answer
    # is "nothing on the session bus implements" the watcher; anything about
    # connecting means the plug is missing. A bus of our own at the path a
    # desktop session uses, since that is the path snapd's rules name.
    "${as_user[@]}" timeout 30 snap run vibesupertonic.ctl shutdown >/dev/null 2>&1 || true
    uid="$(id -u "$user")"
    bus="/run/user/$uid/bus"
    bus_pid=""
    if [[ -d "/run/user/$uid" ]] && command -v dbus-daemon >/dev/null 2>&1; then
        if [[ ! -S "$bus" ]]; then
            bus_pid="$("${as_user[@]}" dbus-daemon --session --address="unix:path=$bus" --fork --print-pid 2>/dev/null || true)"
        fi
        for _ in $(seq 20); do [[ -S "$bus" ]] && break; sleep 0.1; done
        # The first status starts the daemon; the tray registers in the
        # background after that, so ask until it has an answer.
        tray="not started"
        for _ in $(seq 20); do
            said="$("${as_user[@]}" DBUS_SESSION_BUS_ADDRESS="unix:path=$bus" timeout 60 snap run vibesupertonic.ctl status 2>&1 || true)"
            tray="$(awk 'match($0, /"Tray":"[^"]*"/) && !seen { print substr($0, RSTART + 8, RLENGTH - 9); seen = 1 }' <<<"$said")"
            [[ "$tray" == "not started" ]] || break
            sleep 0.5
        done
        "${as_user[@]}" timeout 30 snap run vibesupertonic.ctl shutdown >/dev/null 2>&1 || true
        [[ -n "$bus_pid" ]] && kill "$bus_pid" 2>/dev/null
        [[ "$tray" == registered* || "$tray" == *"nothing on the session bus implements"* ]] || die "the daemon's tray under confinement said: ${tray:-nothing}
       With a session bus and no tray host it should say that nothing implements
       the watcher. See unity7 in snapcraft.yaml.in."
        info "the daemon reaches the session bus: $tray"
    else
        info "NOT CHECKED: the tray (no /run/user/$uid or no dbus-daemon here)"
    fi
fi

size="$(du -h "$out" | cut -f1)"
step "Done."
info "$out"
info "$size"
printf '\n'
printf 'Try it:    sudo snap install --dangerous %s\n' "$(basename "$out")"
printf 'Hotkeys:   bash /snap/vibesupertonic/current/sandbox-setup.sh bind\n'
printf 'Publish:   snapcraft upload --release=edge %s\n' "$(basename "$out")"

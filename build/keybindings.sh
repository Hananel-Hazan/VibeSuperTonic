#!/usr/bin/env bash
#
# VibeSuperTonic — desktop keybindings (Phase 5)
#
# Registers two global shortcuts with the desktop rather than grabbing keys
# ourselves:
#
#     Ctrl+`            vst-ctl read      read what is selected NOW, interrupting
#     Ctrl+~            vst-ctl stop      unconditional silence, from any state
#
# The primary key never means stop. It reads the current selection, cutting off
# whatever is playing -- so changing selection and pressing once does the
# obvious thing, instead of costing a press to stop and another to start. That
# is only safe because stop has a binding of its own; with one key doing
# everything, a press that always speaks would leave no way to be quiet.
#
# Ctrl+~ is Ctrl+Shift+` — the tilde is the shifted backtick (keycode 49). On
# CINNAMON the accelerator is therefore written <Control><Shift>grave rather than
# <Control>asciitilde, because what GTK grabs is a keycode plus modifiers and
# binding the tilde keysym directly works on some setups and silently does not on
# others.
#
# THE KDE BACKEND INVERTS THIS and writes Ctrl+~, because Plasma stores a
# QKeySequence — a keysym plus modifiers — so Ctrl+Shift+` would be
# Key_QuoteLeft while the event that arrives carries Key_AsciiTilde, and the two
# never match. Same two keys, two spellings, one per toolkit. See the KDE
# section for the layout evidence that also favours it.
#
# There is NO behaviour in this file. The state machine, the debounce and the
# speak-or-stop decision all live in the daemon and are covered by Core.Tests;
# the client is stateless and sends a verb. If this script ever grows a rule
# about what a press means, that rule is in the wrong place.
#
# Ownership: Phase 5 writes this file; Phase 7's install.sh and uninstall.sh
# SOURCE it and call the two functions. Two phases claimed the keybindings and
# the absolute path below is only knowable at install time, so the split is:
# this file knows how to bind, the installer knows where things landed.
#
#     source build/keybindings.sh
#     vst_bind /opt/vibesupertonic     # install dir containing vst-ctl
#     vst_unbind
#
# Standalone, for a hand install or a desktop we have no backend for:
#
#     bash build/keybindings.sh bind /opt/vibesupertonic
#     bash build/keybindings.sh unbind
#     bash build/keybindings.sh status
#
# Set VST_KB_DRY_RUN=1 to print every mutating call instead of running it. The
# read-only half — schema detection, conflict scan, id allocation — still
# executes, so a dry run answers "what would this do to my desktop" honestly.
#
# ---------------------------------------------------------------------------
# TWO BACKENDS
#
# Cinnamon/GNOME (gsettings) and KDE Plasma (config files) bind the same two
# verbs by completely different mechanisms, so the file is split: a detector
# picks a backend, and each backend owns bind/unbind/status. Everything above
# the detector — the verbs, the accelerators, the "warn, never steal" rule — is
# shared and must stay that way, because a shortcut that means something
# different on KDE than on Cinnamon is a support problem nobody can reproduce.
#
# The KDE backend was added 2026-08-22 when the target machine was reinstalled
# as Ubuntu 26.04 / Plasma 6 / Wayland. Cinnamon remains supported; neither is
# privileged over the other.
# ---------------------------------------------------------------------------
#
# ---------------------------------------------------------------------------
# Why the mechanics below look the way they do
#
# All three of these were read out of Cinnamon's own settings code
# (/usr/share/cinnamon/cinnamon-settings/bin/KeybindingTable.py), not guessed.
# Guessing any of them produces a shortcut that does not appear in System
# Settings, or one that appears and does not fire until the next login.
#
#  1. custom-list holds bare ids ("custom0"), not paths. The path is
#     $VST_KB_BASENAME/<id>/ and the per-entry schema is a relocatable one.
#
#  2. The id is the LOWEST FREE integer, which is how Cinnamon itself allocates.
#     Appending "custom<count>" instead would collide the moment a user has
#     deleted a shortcut from the middle of their list.
#
#  3. cinnamon's keybindings.js rebuilds its grabs only when custom-list
#     CHANGES VALUE. Writing an identical list is a no-op, so re-binding an
#     already-registered shortcut would appear to succeed and not take effect
#     until logout. Cinnamon solves this by toggling a dummy entry in and out
#     of the list to force a change; we do the same, and skip the dummy
#     everywhere we read.
#
# The write order here is deliberately the reverse of Cinnamon's: we fill in
# name/command/binding FIRST and add the id to custom-list LAST. Cinnamon can
# do it the other way because its GUI creates an entry and then waits for the
# user to type an accelerator; a script that does both at once would otherwise
# expose a half-built entry to a rebuild triggered by its own list write.
# ---------------------------------------------------------------------------

VST_KB_SCHEMA_PARENT="org.cinnamon.desktop.keybindings"
VST_KB_SCHEMA_CUSTOM="org.cinnamon.desktop.keybindings.custom-keybinding"
VST_KB_BASENAME="/org/cinnamon/desktop/keybindings/custom-keybindings"
VST_KB_DUMMY="__dummy__"

# The two shortcuts, as parallel arrays. Names are what the user sees in
# System Settings -> Keyboard -> Shortcuts -> Custom Shortcuts, and are also
# how vst_unbind finds what to remove.
VST_KB_NAMES=("VibeSuperTonic: Speak selection" "VibeSuperTonic: Stop speaking")
VST_KB_VERBS=("read" "stop")
VST_KB_ACCELS=("<Control>grave" "<Control><Shift>grave")

# Schemas worth scanning for a conflict. Not exhaustive — a conflict we miss
# costs a warning, not a broken install, and the desktop resolves duplicate
# accelerators by ignoring one of them rather than by breaking.
VST_KB_SCAN_SCHEMAS=(
    "org.cinnamon.desktop.keybindings.wm"
    "org.cinnamon.desktop.keybindings.media-keys"
    "org.cinnamon.settings-daemon.plugins.media-keys"
)

# --- small helpers ---------------------------------------------------------

# The schema list, fetched once.
#
# Not just a cache. `gsettings list-schemas | grep -q X` is a trap under
# `set -o pipefail`: grep -q exits the moment it matches, gsettings is still
# writing, gets SIGPIPE, and the PIPELINE reports failure — so the check says
# "schema absent" when the schema is present. It depends on whether the writer
# happened to finish first, so it fails intermittently, and the symptom is an
# installer telling a Cinnamon user they are not on Cinnamon. Caught exactly
# that way: `status` passed this check and `bind` failed it in the same second.
# Reading into a variable first removes the pipeline and the race with it.
_vst_kb_schemas() {
    if [[ -z "${_VST_KB_SCHEMA_CACHE:-}" ]]; then
        _VST_KB_SCHEMA_CACHE="$(gsettings list-schemas 2>/dev/null || true)"
    fi
    printf '%s' "$_VST_KB_SCHEMA_CACHE"
}

_vst_kb_has_schema() {
    grep -qxF "$1" <<<"$(_vst_kb_schemas)"
}

_vst_kb_info()  { printf '%s\n' "$*"; }
_vst_kb_warn()  { printf 'warning: %s\n' "$*" >&2; }
_vst_kb_error() { printf 'error: %s\n' "$*" >&2; }

# Mutating gsettings calls go through here so VST_KB_DRY_RUN can intercept them.
_vst_kb_set() {
    if [[ "${VST_KB_DRY_RUN:-0}" == "1" ]]; then
        printf '    [dry-run] gsettings'; printf ' %q' "$@"; printf '\n'
        return 0
    fi
    gsettings "$@"
}

# A GVariant string literal: single-quoted, with backslashes and single quotes
# escaped. Install paths with a quote in them are pathological but free to
# handle correctly, and the failure mode otherwise is a malformed setting.
_vst_kb_gv_string() {
    local s="$1"
    s="${s//\\/\\\\}"
    s="${s//\'/\\\'}"
    printf "'%s'" "$s"
}

# Normalise an accelerator so <Control><Shift>grave and <Shift><Control>grave
# compare equal. Modifier ORDER is not significant to the desktop but is
# significant to a string comparison, which is the whole reason this exists.
# Also folds the aliases that mean the same modifier — <Primary> is what GTK
# writes where a human would write <Control>, and both appear in real schemas.
_vst_kb_norm_accel() {
    local a="${1,,}" mods key
    a="${a//<primary>/<control>}"
    a="${a//<ctrl>/<control>}"
    a="${a//<mod4>/<super>}"
    mods="$(grep -oE '<[^>]+>' <<<"$a" | sort | tr -d '\n')"
    key="${a##*>}"
    printf '%s%s' "$mods" "$key"
}

# Emit the current custom-list, one id per line, dummy entry skipped.
_vst_kb_list() {
    gsettings get "$VST_KB_SCHEMA_PARENT" custom-list 2>/dev/null \
        | sed 's/^@as //' \
        | grep -oE "'[^']*'" \
        | sed "s/^'//; s/'\$//" \
        | grep -vxF "$VST_KB_DUMMY" || true
}

# Write custom-list from the ids on stdin, forcing a value change so cinnamon
# rebuilds its grabs (see note 3 in the header).
_vst_kb_write_list() {
    local -a ids=("$@") out=()
    local current_has_dummy=0 current
    current="$(gsettings get "$VST_KB_SCHEMA_PARENT" custom-list 2>/dev/null || true)"
    if grep -qF "'$VST_KB_DUMMY'" <<<"$current"; then
        current_has_dummy=1
    fi

    local id
    for id in "${ids[@]}"; do out+=("$(_vst_kb_gv_string "$id")"); done
    # Toggle the dummy relative to what is there now, so the written value
    # always differs from the current one even when our ids are unchanged.
    (( current_has_dummy )) || out+=("$(_vst_kb_gv_string "$VST_KB_DUMMY")")

    local joined
    if (( ${#out[@]} )); then
        joined="[$(IFS=,; printf '%s' "${out[*]}")]"
    else
        joined="@as []"
    fi
    _vst_kb_set set "$VST_KB_SCHEMA_PARENT" custom-list "$joined"
}

_vst_kb_path()  { printf '%s/%s/' "$VST_KB_BASENAME" "$1"; }
_vst_kb_sp()    { printf '%s:%s' "$VST_KB_SCHEMA_CUSTOM" "$(_vst_kb_path "$1")"; }

_vst_kb_get() {   # <id> <key> -> unquoted value
    gsettings get "$(_vst_kb_sp "$1")" "$2" 2>/dev/null \
        | sed "s/^@as //" | sed "s/^'//; s/'\$//"
}

# The binding key is an array of strings on Cinnamon and a single string on
# GNOME, and it has changed across versions of both. Detect rather than assume:
# writing the wrong GVariant type fails the set outright, which would present as
# a shortcut with no accelerator.
_vst_kb_binding_type() {
    gsettings range "$(_vst_kb_sp "$1")" binding 2>/dev/null | tail -1 | awk '{print $NF}'
}

_vst_kb_alloc_id() {   # lowest free custom<N>, matching cinnamon's allocator
    local -a used; mapfile -t used < <(_vst_kb_list)
    local -A taken=()
    local e n
    for e in "${used[@]}"; do
        [[ "$e" =~ ^custom([0-9]+)$ ]] && taken[${BASH_REMATCH[1]}]=1
    done
    n=0
    while [[ -n "${taken[$n]:-}" ]]; do n=$((n+1)); done
    printf 'custom%d' "$n"
}

# Is this id one of ours? Matched by name first (stable across reinstalls) and
# by command second (catches an entry the user renamed in System Settings).
_vst_kb_is_ours() {
    local id="$1" name cmd i
    name="$(_vst_kb_get "$id" name)"
    cmd="$(_vst_kb_get "$id" command)"
    for i in "${!VST_KB_NAMES[@]}"; do
        [[ "$name" == "${VST_KB_NAMES[$i]}" ]] && return 0
    done
    [[ "$cmd" == *vst-ctl* ]] && for i in "${!VST_KB_VERBS[@]}"; do
        [[ "$cmd" == *" ${VST_KB_VERBS[$i]}" ]] && return 0
    done
    return 1
}

# Find an existing entry of ours for shortcut index <i>, if any. Makes bind
# idempotent: re-running updates in place instead of stacking duplicates.
_vst_kb_find_ours() {
    local want="${VST_KB_NAMES[$1]}" verb="${VST_KB_VERBS[$1]}" id name cmd
    while read -r id; do
        [[ -z "$id" ]] && continue
        name="$(_vst_kb_get "$id" name)"
        cmd="$(_vst_kb_get "$id" command)"
        if [[ "$name" == "$want" ]] || { [[ "$cmd" == *vst-ctl* ]] && [[ "$cmd" == *" $verb" ]]; }; then
            printf '%s' "$id"; return 0
        fi
    done < <(_vst_kb_list)
    return 1
}

# Anything already holding this accelerator, excluding our own entries.
# Emits "schema key" per line.
_vst_kb_conflicts() {
    local want; want="$(_vst_kb_norm_accel "$1")"
    local schema line key values v id

    for schema in "${VST_KB_SCAN_SCHEMAS[@]}"; do
        _vst_kb_has_schema "$schema" || continue
        while read -r line; do
            [[ -z "$line" ]] && continue
            key="$(awk '{print $2}' <<<"$line")"
            values="$(grep -oE "'[^']*'" <<<"${line#* * }" | sed "s/^'//; s/'\$//")"
            while read -r v; do
                [[ -z "$v" ]] && continue
                [[ "$(_vst_kb_norm_accel "$v")" == "$want" ]] && printf '%s %s\n' "$schema" "$key"
            done <<<"$values"
        done < <(gsettings list-recursively "$schema" 2>/dev/null)
    done

    while read -r id; do
        [[ -z "$id" ]] && continue
        _vst_kb_is_ours "$id" && continue
        while read -r v; do
            [[ -z "$v" ]] && continue
            [[ "$(_vst_kb_norm_accel "$v")" == "$want" ]] && \
                printf 'custom shortcut "%s"\n' "$(_vst_kb_get "$id" name)"
        done < <(gsettings get "$(_vst_kb_sp "$id")" binding 2>/dev/null \
                    | grep -oE "'[^']*'" | sed "s/^'//; s/'\$//")
    done < <(_vst_kb_list)
}

# The route that works on every desktop, and the only route on some. Printed
# whenever the automatic path is unavailable — nobody should have to discover
# from a field report that KDE and XFCE are hand-configured.
_vst_kb_manual() {
    local dir="$1" i
    cat <<EOF

Set these up by hand instead — this works on any desktop:

  System Settings -> Keyboard -> Shortcuts -> Custom Shortcuts -> Add custom shortcut

EOF
    for i in "${!VST_KB_NAMES[@]}"; do
        printf '  Name:     %s\n'    "${VST_KB_NAMES[$i]}"
        printf '  Command:  %s\n'    "$(_vst_kb_command "$dir" "${VST_KB_VERBS[$i]}")"
        printf '  Shortcut: %s\n\n'  "${VST_KB_ACCELS[$i]}"
    done
    cat <<'EOF'
The command must be the ABSOLUTE path. A bare "vst-ctl" depends on the desktop
session's PATH, which is not your shell's — the classic "works in the terminal,
does nothing on the key".
EOF
}

# Absolute command string. The executable is quoted only when it needs to be:
# the desktop splits this with shell-like word splitting, so an install path
# containing a space breaks an unquoted command.
_vst_kb_command() {
    local exe="$1/vst-ctl" verb="$2"
    if [[ "$exe" == *[[:space:]]* ]]; then
        printf '"%s" %s' "$exe" "$verb"
    else
        printf '%s %s' "$exe" "$verb"
    fi
}

_vst_kb_preflight() {
    if ! command -v gsettings >/dev/null 2>&1; then
        _vst_kb_error "gsettings is not installed, so shortcuts cannot be written automatically."
        return 1
    fi
    if ! _vst_kb_has_schema "$VST_KB_SCHEMA_PARENT"; then
        _vst_kb_error "the schema $VST_KB_SCHEMA_PARENT is not present."
        _vst_kb_error "This is the Cinnamon keybinding schema; you are probably not on Cinnamon."
        return 1
    fi
    return 0
}

# --- KDE Plasma backend ----------------------------------------------------
#
# Plasma binds a key to a COMMAND by pointing kglobalaccel at a .desktop file,
# so there are two artefacts rather than one: the .desktop (what to run) and an
# entry in kglobalshortcutsrc (which key runs it). Both are files. That is a
# deliberate choice and the most important comment in this section:
#
#   DO NOT register these over D-Bus. On 2026-08-22, calling
#   org.kde.kglobalaccel setShortcutKeys (signature asa(ai)u) with a
#   hand-marshalled message SIGABRT'd kwin_wayland on Plasma 6.6.6 and took the
#   whole session down with it — every Wayland client dies with its compositor,
#   so the user lost their browser and a video call. In Plasma 6 kglobalaccel is
#   hosted INSIDE kwin, so a malformed body does not bounce off a daemon, it
#   kills the desktop. The file route cannot do that. It costs one thing, and
#   the cost is stated plainly to the user at the end of a bind: shortcuts load
#   when kglobalaccel next starts, i.e. at the next login.
#
# Mechanics, all read off this machine's own kglobalshortcutsrc and the .desktop
# files it points at rather than guessed:
#
#  1. The section is [services][<desktop-file-id>], and the KEYS INSIDE IT ARE
#     DESKTOP ACTION NAMES. org.kde.spectacle.desktop declares
#     Actions=...;RecordRegion;... and its section carries RecordRegion=...
#     An entry with no actions uses the reserved key _launch instead, which is
#     what org.kde.kate.desktop does. We declare two actions, so we use their
#     names and never _launch.
#
#  2. The value is the accelerator alone — NOT the three-field
#     "active,default,friendly" form used in component sections like [kwin].
#     Writing the three-field form here produces a shortcut that reads back
#     correctly and never fires.
#
#  3. A tab separates alternate accelerators, and a trailing tab means "and no
#     second one". A single binding needs neither, so we write neither.
#
#  4. The entry is a NORMAL, VISIBLE launcher whose Exec opens the window, and
#     the two hotkeys are Desktop Actions on it. An earlier version set
#     NoDisplay=true to keep it out of the application menu, on the reasoning
#     that NoDisplay hides an entry from menus without hiding the service from
#     KService — which is what kglobalaccel resolves the id through. That
#     reasoning is probably correct and it was never verified: the obvious check
#     (look for the id in the sycoca cache) could not find a KNOWN-WORKING entry
#     either, so it proved nothing. Since the only way to test the real thing is
#     to make the user log out, the unverified ingredient was removed instead of
#     defended. Hidden=true genuinely would break it and must never be used.
#
#     Consequence: this file is also the application-menu entry, so install.sh
#     does not write one of its own and there is exactly one "VibeSuperTonic" in
#     the menu rather than two.
#
# ACCELERATORS ARE SPELLED DIFFERENTLY HERE THAN ON CINNAMON, and it is not a
# style choice. The Cinnamon half writes <Control><Shift>grave because GTK grabs
# a KEYCODE plus modifiers, and the tilde is merely the shifted backtick.
#
# Plasma stores a QKeySequence, which is a KEYSYM plus modifiers, so the same
# reasoning inverts:
#
#   Ctrl+Shift+`  parses as Ctrl|Shift|Key_QuoteLeft
#   the keypress  delivers  Ctrl|Shift|Key_AsciiTilde   <- shift already applied
#
# so the stored sequence never matches what arrives. Ctrl+~ is Ctrl|Key_AsciiTilde,
# which is exactly the event. Corrected 2026-08-22 on the user's instruction.
#
# It is also the layout-proof spelling on this machine, which has us,il installed:
#
#   keycode 49 = grave asciitilde | semicolon asciitilde
#                └─ us ─┘           └─ il (Hebrew) ─┘
#
# asciitilde is the shifted keysym in BOTH groups, so stop works whichever layout
# is active. `grave` exists only in the Latin group, so READ is the fragile one.
#
# THAT PREDICTION CAME TRUE, AND IT COST WEEKS. Written here 2026-08-22 as "if it
# stops working while typing Hebrew, that is why" — and then reported on
# 2026-09-06 as a hotkey that "sometimes does nothing", chased through the daemon,
# the selection capture and the autostart before anyone re-read this paragraph.
# Two real bugs were found and fixed on the way; neither was the one the user had.
#
# The reason it hid so well is in the line above: STOP kept working. The user's
# own words were "I hit the hotkey for stopping and then the hotkey to read and
# it is still not reading" — which reads as a daemon that is stuck, and is
# actually one key arriving and the other not. SwitchMode=Window puts a different
# layout on every window, so it follows the app rather than the clock: dead in the
# browser they type Hebrew in, alive in a freshly opened Kate.
#
# THE FIX IS A SECOND SPELLING, NOT A DIFFERENT KEY. The note above said a
# different key, which would have cost every existing user their muscle memory for
# a fault that is one keysym wide. The PHYSICAL key is the same in both groups —
# keycode 49 — so binding both keysyms it can emit makes the same finger movement
# work in either layout. Plasma stores alternates tab-separated, which is what
# ALT below becomes.
#
# A layout that maps keycode 49 to something else again would still be dark, and
# the honest answer for that is a key of the user's choosing in System Settings.

VST_KB_KDE_DESKTOP_ID="vibesupertonic.desktop"
VST_KB_KDE_ACTIONS=("Read" "Stop")
VST_KB_KDE_ACCELS=('Ctrl+`' 'Ctrl+~')

# Alternate spellings of THE SAME PHYSICAL KEY, per action, empty where none is
# needed. `Ctrl+;` is keycode 49 under the Hebrew group; Stop needs none because
# asciitilde is already shared by both.
VST_KB_KDE_ALT_ACCELS=('Ctrl+;' '')
VST_KB_KDE_ACTION_NAMES=("Speak selection" "Stop speaking")

# A .desktop Exec field, quoted only when it needs to be. The freedesktop spec
# splits this with shell-like rules, so an install path containing a space breaks
# an unquoted command — the same hazard _vst_kb_command handles for the verbs.
_vst_kb_quote() {
    case "$1" in
        *[[:space:]]*) printf '"%s"' "$1" ;;
        *)             printf '%s' "$1" ;;
    esac
}

_vst_kde_apps_dir() {
    printf '%s/applications' "${XDG_DATA_HOME:-$HOME/.local/share}"
}

_vst_kde_desktop_file() {
    printf '%s/%s' "$(_vst_kde_apps_dir)" "$VST_KB_KDE_DESKTOP_ID"
}

_vst_kde_rc() {
    printf '%s/kglobalshortcutsrc' "${XDG_CONFIG_HOME:-$HOME/.config}"
}

# Mutating kwriteconfig6 calls funnel through here so VST_KB_DRY_RUN works on
# this backend exactly as it does on the Cinnamon one.
_vst_kde_write() {
    if [[ "${VST_KB_DRY_RUN:-0}" == "1" ]]; then
        printf '    [dry-run] kwriteconfig6'; printf ' %q' "$@"; printf '\n'
        return 0
    fi
    kwriteconfig6 "$@"
}

# Normalise a Plasma accelerator for comparison: case-folded, modifiers sorted,
# aliases folded. Same purpose as _vst_kb_norm_accel, different syntax — Plasma
# writes "Ctrl+Shift+X" where GTK writes "<Control><Shift>x".
_vst_kde_norm_accel() {
    local a="${1,,}" part mods=() key=""
    a="${a//meta+/super+}"
    a="${a//control+/ctrl+}"
    local IFS='+'
    for part in $a; do
        case "$part" in
            ctrl|shift|alt|super) mods+=("$part") ;;
            "") ;;
            *) key="$part" ;;
        esac
    done
    printf '%s|%s' "$(printf '%s\n' "${mods[@]}" | sort | tr -d '\n')" "$key"
}

# Anything in kglobalshortcutsrc already holding this accelerator, excluding our
# own section. Emits "section key" per line. Best-effort, like the Cinnamon
# scan: a missed conflict costs a warning, not a broken install.
_vst_kde_conflicts() {
    local want; want="$(_vst_kde_norm_accel "$1")"
    local rc; rc="$(_vst_kde_rc)"
    [[ -r "$rc" ]] || return 0

    local section="" line lhs rhs cand
    while IFS= read -r line; do
        if [[ "$line" =~ ^\[(.*)\]$ ]]; then
            section="${BASH_REMATCH[1]}"
            continue
        fi
        [[ "$line" == *=* ]] || continue
        [[ "$section" == *"$VST_KB_KDE_DESKTOP_ID"* ]] && continue
        lhs="${line%%=*}"
        rhs="${line#*=}"
        [[ "$lhs" == _k_friendly_name ]] && continue
        # Split alternates on a literal \t, then take the first comma field of
        # each — component sections store "active,default,friendly".
        local piece
        while IFS= read -r piece; do
            [[ -z "$piece" ]] && continue
            cand="${piece%%,*}"
            [[ -z "$cand" || "$cand" == "none" ]] && continue
            if [[ "$(_vst_kde_norm_accel "$cand")" == "$want" ]]; then
                printf '[%s] %s\n' "$section" "$lhs"
            fi
        # printf '%s\n', NOT '%s'. Without the trailing newline `read` hits EOF
        # on the last (and usually only) field, returns non-zero, and the loop
        # body never runs — so EVERY accelerator scanned clean and the conflict
        # check silently passed on keys that were already taken. Caught by
        # asserting the scanner FINDS a known conflict; a test that only checks
        # "our keys are free" agrees with a scanner that can see nothing at all.
        done < <(printf '%s\n' "$rhs" | sed 's/\\t/\n/g')
    done < "$rc"
}

_vst_kde_install_desktop() {   # <install-dir>
    local dir="$1" path body i
    path="$(_vst_kde_desktop_file)"

    # Exec opens the WINDOW, not a verb: this is the application-menu entry as
    # well as the shortcut carrier, and an entry that spoke your selection when
    # clicked from the menu would be a surprising thing to hand somebody.
    #
    # VST_KB_UI_COMMAND exists for the AppImage, where the window is not a file
    # in the same directory as vst-ctl: the client is copied to ~/.local/bin and
    # the window lives inside a squashfs image that only its own runtime can
    # open. Unset for a tarball install, which is every other case.
    local ui="${VST_KB_UI_COMMAND:-$dir/vibesupertonic-ui}"
    body="[Desktop Entry]
Type=Application
Name=VibeSuperTonic
Comment=Read selected text aloud
Exec=$(_vst_kb_quote "$ui")
Icon=audio-speakers
Terminal=false
Categories=Utility;Accessibility;
X-KDE-StartupNotify=false
Actions=$(IFS=';'; printf '%s;' "${VST_KB_KDE_ACTIONS[*]}")
"
    for i in "${!VST_KB_KDE_ACTIONS[@]}"; do
        body+="
[Desktop Action ${VST_KB_KDE_ACTIONS[$i]}]
Name=${VST_KB_KDE_ACTION_NAMES[$i]}
Exec=$(_vst_kb_command "$dir" "${VST_KB_VERBS[$i]}")
"
    done

    if [[ "${VST_KB_DRY_RUN:-0}" == "1" ]]; then
        printf '    [dry-run] would write %s:\n' "$path"
        printf '%s\n' "$body" | sed 's/^/      | /'
        return 0
    fi

    mkdir -p "$(_vst_kde_apps_dir)"
    printf '%s' "$body" > "$path"
    chmod 644 "$path"
}

_vst_kde_bind() {   # <install-dir>
    local dir="$1" rc backup i accel conflicts bound=0 unassigned=0
    rc="$(_vst_kde_rc)"

    # Back up before touching it. Clobbering somebody's whole shortcut config is
    # unforgivable and a single bad kwriteconfig6 invocation away.
    if [[ -f "$rc" && "${VST_KB_DRY_RUN:-0}" != "1" ]]; then
        backup="${rc}.vst-backup.$(date +%Y%m%d-%H%M%S)"
        cp -p "$rc" "$backup"
        _vst_kb_info "backed up $rc"
        _vst_kb_info "        -> $backup"
    fi

    _vst_kde_install_desktop "$dir"
    [[ "${VST_KB_DRY_RUN:-0}" == "1" ]] || _vst_kb_info "installed $(_vst_kde_desktop_file)"

    for i in "${!VST_KB_KDE_ACTIONS[@]}"; do
        accel="${VST_KB_KDE_ACCELS[$i]}"
        alt="${VST_KB_KDE_ALT_ACCELS[$i]:-}"
        conflicts="$(_vst_kde_conflicts "$accel")"
        if [[ -n "$conflicts" ]]; then
            _vst_kb_warn "$accel is already bound to:"
            while read -r c; do
                [[ -n "$c" ]] && printf '           %s\n' "$c" >&2
            done <<<"$conflicts"
            _vst_kb_warn "leaving ${VST_KB_NAMES[$i]} WITHOUT an accelerator — assign one in"
            _vst_kb_warn "System Settings -> Keyboard -> Shortcuts."
            accel=""
            unassigned=$((unassigned+1))
        else
            bound=$((bound+1))
        fi

        # The alternate rides along only when the primary was taken, and only
        # when it is itself free — a second spelling that collides with somebody
        # else's shortcut is a second way to steal one.
        written="$accel"
        if [[ -n "$accel" && -n "$alt" ]]; then
            if [[ -n "$(_vst_kde_conflicts "$alt")" ]]; then
                _vst_kb_warn "$alt is already bound elsewhere, so ${VST_KB_NAMES[$i]} has only $accel"
                _vst_kb_warn "— it will not fire while a non-Latin keyboard layout is active."
            else
                written="$accel"$'\t'"$alt"
                _vst_kb_info "${VST_KB_NAMES[$i]}: $accel and $alt (the same physical key in either layout)"
            fi
        fi

        _vst_kde_write --file kglobalshortcutsrc \
            --group services --group "$VST_KB_KDE_DESKTOP_ID" \
            --key "${VST_KB_KDE_ACTIONS[$i]}" "$written"
    done

    # Without this the service id does not resolve and the shortcut is inert.
    if [[ "${VST_KB_DRY_RUN:-0}" == "1" ]]; then
        printf '    [dry-run] kbuildsycoca6\n'
    else
        kbuildsycoca6 --noincremental >/dev/null 2>&1 || \
            _vst_kb_warn "kbuildsycoca6 failed; the shortcut may not resolve until next login."
    fi

    _vst_kb_info ""
    _vst_kb_info "$bound shortcut(s) written, $unassigned left unassigned."
    _vst_kb_info ""
    _vst_kb_info "These take effect when kglobalaccel next starts — LOG OUT AND BACK IN."
    _vst_kb_info "Plasma 6 hosts kglobalaccel inside kwin, and there is no way to make it"
    _vst_kb_info "re-read its config that does not risk restarting your compositor."
    _vst_kb_info "Check them afterwards in System Settings -> Keyboard -> Shortcuts."
    return 0
}

_vst_kde_unbind() {
    local rc backup path
    rc="$(_vst_kde_rc)"
    path="$(_vst_kde_desktop_file)"

    if ! grep -qF "[services][$VST_KB_KDE_DESKTOP_ID]" "$rc" 2>/dev/null && [[ ! -f "$path" ]]; then
        _vst_kb_info "no VibeSuperTonic shortcuts were registered; nothing to do."
        return 0
    fi

    if [[ -f "$rc" && "${VST_KB_DRY_RUN:-0}" != "1" ]]; then
        backup="${rc}.vst-backup.$(date +%Y%m%d-%H%M%S)"
        cp -p "$rc" "$backup"
        _vst_kb_info "backed up $rc -> $backup"
    fi

    # Exactly our section, by id. Nothing else in the file is read or rewritten.
    _vst_kde_write --file kglobalshortcutsrc \
        --group services --group "$VST_KB_KDE_DESKTOP_ID" --delete-group

    if [[ "${VST_KB_DRY_RUN:-0}" == "1" ]]; then
        printf '    [dry-run] rm -f %q\n' "$path"
    else
        rm -f "$path"
        kbuildsycoca6 --noincremental >/dev/null 2>&1 || true
    fi

    _vst_kb_info "removed the VibeSuperTonic shortcuts and launcher."
    _vst_kb_info "The keys are released at the next login."
    return 0
}

_vst_kde_status() {
    local rc path i val found=0
    rc="$(_vst_kde_rc)"
    path="$(_vst_kde_desktop_file)"

    if [[ -f "$path" ]]; then
        printf 'launcher: %s\n' "$path"
        printf '  Exec:     %s\n' "$(grep -m1 '^Exec=' "$path" | cut -d= -f2-)"
    else
        printf 'launcher: not installed (%s)\n' "$path"
    fi

    for i in "${!VST_KB_KDE_ACTIONS[@]}"; do
        val="$(kreadconfig6 --file kglobalshortcutsrc \
                 --group services --group "$VST_KB_KDE_DESKTOP_ID" \
                 --key "${VST_KB_KDE_ACTIONS[$i]}" 2>/dev/null || true)"
        if [[ -n "$val" ]]; then
            found=$((found+1))
            printf '%s\n' "${VST_KB_NAMES[$i]}"
            printf '  action:   %s\n' "${VST_KB_KDE_ACTIONS[$i]}"
            printf '  binding:  %s\n' "$val"
        fi
    done
    (( found )) || _vst_kb_info "no VibeSuperTonic shortcuts are registered."
    return 0
}

# --- which desktop are we on -----------------------------------------------
#
# Detected by CAPABILITY, with $XDG_CURRENT_DESKTOP only as a tiebreak. A user
# running Cinnamon's schema inside a KDE session is rare but real, and the
# question that actually matters is "which mechanism will work here", not "what
# does the session call itself".
_vst_kb_backend() {
    if [[ -n "${VST_KB_BACKEND:-}" ]]; then
        printf '%s' "$VST_KB_BACKEND"; return 0
    fi
    local kde=0 cinnamon=0
    command -v kwriteconfig6 >/dev/null 2>&1 && kde=1
    command -v gsettings >/dev/null 2>&1 && _vst_kb_has_schema "$VST_KB_SCHEMA_PARENT" && cinnamon=1

    if (( kde && cinnamon )); then
        case "${XDG_CURRENT_DESKTOP:-}" in
            *KDE*) printf 'kde' ;;
            *)     printf 'cinnamon' ;;
        esac
    elif (( kde )); then
        printf 'kde'
    elif (( cinnamon )); then
        printf 'cinnamon'
    else
        printf 'unknown'
    fi
}

# --- the two entry points --------------------------------------------------

vst_bind() {
    local dir="${1:-}"
    if [[ -z "$dir" ]]; then
        _vst_kb_error "vst_bind needs the install directory containing vst-ctl."
        return 64
    fi
    dir="$(cd "$dir" 2>/dev/null && pwd)" || {
        _vst_kb_error "install directory does not exist: ${1}"
        return 64
    }
    if [[ ! -x "$dir/vst-ctl" ]]; then
        _vst_kb_error "no executable vst-ctl in $dir"
        _vst_kb_error "Binding a key to a binary that is not there is a hotkey that silently does nothing."
        return 64
    fi

    case "$(_vst_kb_backend)" in
        kde)      _vst_kde_bind "$dir"; return $? ;;
        cinnamon) ;;   # falls through to the gsettings path below
        *)
            _vst_kb_error "no backend for this desktop — neither Plasma's kwriteconfig6"
            _vst_kb_error "nor Cinnamon's $VST_KB_SCHEMA_PARENT schema is present."
            _vst_kb_manual "$dir"
            return 2
            ;;
    esac

    _vst_kb_preflight || { _vst_kb_manual "$dir"; return 2; }

    local btype probe; probe="$(_vst_kb_alloc_id)"
    btype="$(_vst_kb_binding_type "$probe")"
    if [[ "$btype" != "as" && "$btype" != "s" ]]; then
        _vst_kb_error "cannot tell what type the 'binding' key is (got: ${btype:-nothing})."
        _vst_kb_manual "$dir"
        return 3
    fi

    local -a ids; mapfile -t ids < <(_vst_kb_list)
    local i id cmd accel conflicts bound=0 unassigned=0

    for i in "${!VST_KB_NAMES[@]}"; do
        cmd="$(_vst_kb_command "$dir" "${VST_KB_VERBS[$i]}")"
        accel="${VST_KB_ACCELS[$i]}"

        if id="$(_vst_kb_find_ours "$i")"; then
            _vst_kb_info "updating existing shortcut $id — ${VST_KB_NAMES[$i]}"
        else
            id="$(_vst_kb_alloc_id_excluding "${ids[@]}")"
            ids+=("$id")
            _vst_kb_info "adding shortcut $id — ${VST_KB_NAMES[$i]}"
        fi

        # Warn, never steal. An accelerator already spoken for is left
        # unassigned so the entry still shows up in System Settings and the
        # user can pick their own key, rather than us silently winning or
        # silently losing a fight over it.
        conflicts="$(_vst_kb_conflicts "$accel")"
        if [[ -n "$conflicts" ]]; then
            _vst_kb_warn "$accel is already bound to:"
            while read -r c; do
                [[ -n "$c" ]] && printf '           %s\n' "$c" >&2
            done <<<"$conflicts"
            _vst_kb_warn "leaving ${VST_KB_NAMES[$i]} WITHOUT an accelerator — assign one in"
            _vst_kb_warn "System Settings -> Keyboard -> Shortcuts -> Custom Shortcuts."
            accel=""
            unassigned=$((unassigned+1))
        else
            bound=$((bound+1))
        fi

        _vst_kb_set set "$(_vst_kb_sp "$id")" name    "$(_vst_kb_gv_string "${VST_KB_NAMES[$i]}")"
        _vst_kb_set set "$(_vst_kb_sp "$id")" command "$(_vst_kb_gv_string "$cmd")"
        if [[ "$btype" == "as" ]]; then
            if [[ -n "$accel" ]]; then
                _vst_kb_set set "$(_vst_kb_sp "$id")" binding "[$(_vst_kb_gv_string "$accel")]"
            else
                _vst_kb_set set "$(_vst_kb_sp "$id")" binding "@as []"
            fi
        else
            _vst_kb_set set "$(_vst_kb_sp "$id")" binding "$(_vst_kb_gv_string "$accel")"
        fi
    done

    # List last, so no rebuild can observe a half-written entry.
    _vst_kb_write_list "${ids[@]}"

    _vst_kb_info ""
    _vst_kb_info "$bound shortcut(s) bound, $unassigned left unassigned."
    _vst_kb_info "Check them in System Settings -> Keyboard -> Shortcuts -> Custom Shortcuts."
    return 0
}

# Allocate the lowest free id given a set of ids already spoken for in THIS
# run — _vst_kb_alloc_id only sees what is committed, so two new shortcuts in
# one pass would otherwise both be handed custom0.
_vst_kb_alloc_id_excluding() {
    local -A taken=()
    local e n
    for e in "$@"; do
        [[ "$e" =~ ^custom([0-9]+)$ ]] && taken[${BASH_REMATCH[1]}]=1
    done
    while read -r e; do
        [[ "$e" =~ ^custom([0-9]+)$ ]] && taken[${BASH_REMATCH[1]}]=1
    done < <(_vst_kb_list)
    n=0
    while [[ -n "${taken[$n]:-}" ]]; do n=$((n+1)); done
    printf 'custom%d' "$n"
}

vst_unbind() {
    case "$(_vst_kb_backend)" in
        kde)      _vst_kde_unbind; return $? ;;
        cinnamon) ;;
        *)        _vst_kb_error "no backend for this desktop; nothing to unbind."; return 2 ;;
    esac

    _vst_kb_preflight || return 2

    local -a keep=()
    local id removed=0

    while read -r id; do
        [[ -z "$id" ]] && continue
        if _vst_kb_is_ours "$id"; then
            _vst_kb_info "removing shortcut $id — $(_vst_kb_get "$id" name)"
            _vst_kb_set reset "$(_vst_kb_sp "$id")" name
            _vst_kb_set reset "$(_vst_kb_sp "$id")" command
            _vst_kb_set reset "$(_vst_kb_sp "$id")" binding
            removed=$((removed+1))
        else
            keep+=("$id")
        fi
    done < <(_vst_kb_list)

    if (( removed == 0 )); then
        _vst_kb_info "no VibeSuperTonic shortcuts were registered; nothing to do."
        return 0
    fi

    # Exactly the entries we added, by name or by command. Everything else is
    # written back untouched — clobbering somebody's shortcuts is unforgivable
    # and very easy to do by accident.
    _vst_kb_write_list "${keep[@]}"
    _vst_kb_info "removed $removed shortcut(s)."
    return 0
}

vst_kb_status() {
    case "$(_vst_kb_backend)" in
        kde)      _vst_kde_status; return $? ;;
        cinnamon) ;;
        *)        _vst_kb_error "no backend for this desktop."; return 2 ;;
    esac

    _vst_kb_preflight || return 2
    local id found=0
    while read -r id; do
        [[ -z "$id" ]] && continue
        if _vst_kb_is_ours "$id"; then
            found=$((found+1))
            printf '%s\n' "$id"
            printf '  name:     %s\n' "$(_vst_kb_get "$id" name)"
            printf '  command:  %s\n' "$(_vst_kb_get "$id" command)"
            printf '  binding:  %s\n' "$(gsettings get "$(_vst_kb_sp "$id")" binding 2>/dev/null)"
        fi
    done < <(_vst_kb_list)
    (( found )) || _vst_kb_info "no VibeSuperTonic shortcuts are registered."
    return 0
}

# --- standalone ------------------------------------------------------------

_vst_kb_main() {
    set -euo pipefail
    case "${1:-help}" in
        bind)   shift; vst_bind "${1:-}" ;;
        unbind) vst_unbind ;;
        status) vst_kb_status ;;
        *)
            cat <<EOF
VibeSuperTonic desktop keybindings.

  bash build/keybindings.sh bind <install-dir>   register the shortcuts
  bash build/keybindings.sh unbind               remove exactly ours
  bash build/keybindings.sh status               show what is registered

  Ctrl+\`            vst-ctl read
  Ctrl+~            vst-ctl stop

VST_KB_DRY_RUN=1 prints every mutating call instead of making it.
VST_KB_BACKEND=kde|cinnamon forces a backend instead of detecting one.

Backends: Cinnamon/GNOME via gsettings, KDE Plasma via a .desktop launcher plus
kglobalshortcutsrc. On KDE the shortcuts load at the NEXT LOGIN — kglobalaccel
lives inside kwin there and cannot be asked to re-read its config without
risking the compositor.

Known limit: screen lockers, some fullscreen games and open menus take a
keyboard grab, and global shortcuts do not fire under one. That is a property
of the display server, not a bug here.
EOF
            ;;
    esac
}

# Sourced by install.sh -> define functions only. Executed -> run main.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    _vst_kb_main "$@"
fi

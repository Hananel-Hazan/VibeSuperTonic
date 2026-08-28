#!/usr/bin/env bash
#
# VibeSuperTonic — register the Speech Dispatcher output module.
#
#     ./speechd-install.sh            install, and re-declare everything else
#     ./speechd-install.sh --check    say what is configured and whether it works
#     ./speechd-install.sh --remove   put back what was there
#
# WHAT THIS TOUCHES, AND WHY THAT IS FRIGHTENING. Speech Dispatcher is how a
# blind user's screen reader speaks. A mistake here does not produce a bug
# report; it produces a desktop that has gone silent for the one person who
# cannot see the error message. Every decision below is made in that direction:
# re-declare rather than replace, back up before writing, and check that the
# OTHER modules still answer afterwards.
#
# Three traps, all measured 2026-08-27 (docs/SPEECHD-PLAN.md):
#
#   1. speechd auto-detects output modules ONLY while speechd.conf declares
#      none. The distro file ships all nineteen AddModule lines commented out
#      and offers espeak-ng anyway. Add one line — ours — and auto-detection
#      stops dead: `spd-say -O` then lists ours AND NOTHING ELSE. So this script
#      enumerates what is offered now and re-declares each one explicitly before
#      adding ours.
#   2. ~/.config/speech-dispatcher/speechd.conf is read INSTEAD OF the system
#      file, not after it. Creating one silently discards the user's audio
#      backend, default module and rate. So we copy the system file first.
#   7. speechd caches its config for the life of the process, so a restart is
#      part of installing, not a suggestion — and the first client call after
#      one can take 20 seconds.
#
# Nothing here needs root, nothing is copied into /usr, and NO WINDOW EVER
# OPENS: this must be runnable from a text console by someone who cannot see.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ---------------------------------------------------------------- overrides
#
# Every path this script writes to is an environment variable with a real
# default, because the test that matters for this feature — "does espeak-ng
# still answer afterwards" — cannot be run against the developer's own desktop
# without risking the exact failure the script exists to prevent. The harness in
# spike/speechd-s4-install/ points all three at a scratch directory and runs a
# private speech-dispatcher over the result.
conf_dir="${VST_SPEECHD_CONF_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/speech-dispatcher}"
system_conf_dir="${VST_SPEECHD_SYSTEM_CONF_DIR:-/etc/speech-dispatcher}"
conf="$conf_dir/speechd.conf"
state="$conf_dir/.vibesupertonic-install"

# The command speechd will run. For a tarball it is the module beside this
# script. An AppImage passes its own path plus the `speechd` verb — trap 13: the
# contents live in a FUSE mount whose path changes every run, so a config naming
# anything inside it works exactly once.
module_cmd="${VST_SPEECHD_MODULE:-$here/vst-speechd}"

# How to tell the user to run this again. `$0` is right for a tarball and WRONG
# for an AppImage, where it is a path inside a FUSE mount that exists only while
# this process does — trap 13, arriving in the one place nobody checks: the help
# text. appimage-speechd.sh passes the verb form instead. Found by running it
# for real, which printed /tmp/.mount_VibeSufMkDbi/... as the way to undo it.
self="${VST_SPEECHD_SELF:-$0}"

MARK_BEGIN='# >>> VibeSuperTonic (speechd-install.sh) >>>'
MARK_END='# <<< VibeSuperTonic (speechd-install.sh) <<<'
MODULE_NAME=vibesupertonic

action=install
restart=1
force=0
for arg in "$@"; do
    case "$arg" in
        --check)      action=check ;;
        --remove)     action=remove ;;
        --no-restart) restart=0 ;;
        --force)      force=1 ;;
        -h|--help)    sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $arg" >&2; exit 2 ;;
    esac
done

# ASKING THE OUTSIDE WORLD IS ALLOWED TO FAIL, AND MUST NOT BE FATAL.
# Under `set -euo pipefail` an unguarded `x="$(cmd | ...)"` takes the whole
# script out when cmd fails or times out — with no message, because the die that
# would have explained it never runs. That is how --check came to exit non-zero
# in the middle of its own report the first time speech-dispatcher was slow to
# answer: the packer showed a report that simply stopped. Every substitution
# that asks speechd, the filesystem or a config file a question therefore ends
# in `|| true`, and the emptiness is handled where it is read.
say()  { printf '%s\n' "$*"; }
warn() { printf '%s\n' "$*" >&2; }
die()  { printf 'speechd-install: %s\n' "$*" >&2; exit 1; }

# ------------------------------------------------------- does it even work?
#
# Trap 14, and the rule the whole feature is built on: NEVER EXIT 0 HAVING
# PRODUCED NO AUDIO. A module that is registered and cannot speak is worse than
# one that is absent, because speechd will route to it and the user hears
# nothing with no error anywhere they can reach.
#
# INIT is the only reply speechd acts on. The module answers 299 when it has at
# least the espeak voice beside it and 399 when it has nothing at all, so this
# is the same question speechd is about to ask, asked first — while there is
# still a person here to read the answer.
module_init_reply() {
    local cmd=("$@") out
    out="$(printf 'INIT\nQUIT\n' | timeout 20 "${cmd[@]}" 2>/dev/null || true)"
    printf '%s\n' "$out" | grep -E '^(299|399) ' | tail -1
}

module_speaks() {
    [[ "$(module_init_reply "$@")" == 299\ * ]]
}

# Where models actually live. ASKED OF THE DAEMON, never guessed: for a tarball
# it is beside the binaries, for an AppImage it is a store beside the .AppImage
# file chosen by a four-branch rule with one genuine subtlety in it, and a guess
# that got it wrong would tell an AppImage user with 383 MB of voices that they
# have none. --print-store is the same call AppRun makes.
resolve_store() {
    if [[ -n "${VST_SPEECHD_STORE:-}" ]]; then
        printf '%s\n' "$VST_SPEECHD_STORE"
    elif [[ -x "$here/vibesupertonicd" ]]; then
        "$here/vibesupertonicd" --print-store 2>/dev/null || printf '%s\n' "$here"
    else
        printf '%s\n' "$here"
    fi
}

# What counts as an installed voice, and it is deliberately the same rule the
# module's own LIST VOICES uses: a style is a .json under voice_styles, a Piper
# voice is a directory holding BOTH the .onnx and its .onnx.json.
count_voices() {
    local models="$1/models" n=0 f
    for f in "$models"/voice_styles/*.json; do [[ -f "$f" ]] && n=$((n + 1)); done
    for f in "$models"/piper/*/*.onnx; do
        [[ -f "$f" && -f "$f.json" ]] && n=$((n + 1))
    done
    printf '%s\n' "$n"
}

# ------------------------------------------------- what speechd offers today
#
# Trap 1 lives here. Two ways to ask, and the difference between them matters:
#
#   - `spd-say -O` is the truth, because it is speechd itself answering, but it
#     needs a running server and a client to be installed.
#   - Otherwise: replicate auto-detection by looking for the module binaries the
#     distro's commented AddModule lines name. That is what auto-detection does,
#     and it is a fallback rather than the default because "what speechd would
#     offer" is a question only speechd can answer without guessing.
#
# If BOTH fail we have no list, and re-declaring nothing is precisely trap 1. So
# an empty enumeration refuses rather than proceeding.
modules_dir() {
    local d
    for d in /usr/lib/speech-dispatcher-modules \
             /usr/lib64/speech-dispatcher-modules \
             /usr/lib/*/speech-dispatcher-modules \
             /usr/local/lib/speech-dispatcher-modules; do
        [[ -d "$d" ]] && { printf '%s\n' "$d"; return; }
    done
}

enumerate_offered() {
    local names=()
    if command -v spd-say >/dev/null 2>&1; then
        # First line is the header "OUTPUT MODULES"; the rest are names. A
        # server that is not running makes this fail, which is not an error —
        # it is the fallback's cue.
        mapfile -t names < <(timeout 25 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF')
    fi
    if (( ${#names[@]} == 0 )); then
        local dir; dir="$(modules_dir || true)"
        if [[ -n "$dir" ]]; then
            while read -r name binary; do
                [[ -x "$dir/$binary" ]] && names+=("$name")
            done < <(system_declarations)
        fi
    fi
    # `printf '%s\n' "${names[@]}"` on an EMPTY array prints one blank line, not
    # nothing — so the caller counts one module, the trap-1 refusal never fires,
    # and the config we write declares ours and nothing else. That is the exact
    # catastrophe this function exists to prevent, and it shipped past a reading
    # of the code; the S4 harness caught it by asking speechd through a dead
    # socket. Guard here AND filter in the caller.
    (( ${#names[@]} )) || return 0
    printf '%s\n' "${names[@]}"
}

# The distro's own AddModule lines, commented out, in the system file: name,
# binary and conf. We read them rather than inventing "sd_$name" because the
# mapping is not mechanical — six of the nineteen are sd_generic under six
# different names, and a re-declaration that pointed espeak-mbrola-generic at a
# binary called sd_espeak-mbrola-generic would remove a working module while
# reporting success.
system_declarations() {
    [[ -f "$system_conf_dir/speechd.conf" ]] || return 0
    sed -n 's/^[[:space:]]*#\?[[:space:]]*AddModule[[:space:]]\+"\([^"]*\)"[[:space:]]*"\([^"]*\)"[[:space:]]*"\([^"]*\)".*/\1 \2 \3/p' \
        "$system_conf_dir/speechd.conf"
}

declaration_for() {
    local want="$1" name binary cfg
    while read -r name binary cfg; do
        if [[ "$name" == "$want" ]]; then
            printf 'AddModule "%s" "%s" "%s"\n' "$name" "$binary" "$cfg"
            return 0
        fi
    done < <(system_declarations)
    # Not in the system file: a module someone installed by hand, or a distro
    # that ships no commented list. The conventional mapping is the only thing
    # left, and it is right for every module that follows the convention.
    printf 'AddModule "%s" "sd_%s" "%s.conf"\n' "$want" "$want" "$want"
}

# Active — not commented — AddModule names in a file.
active_declarations() {
    [[ -f "$1" ]] || return 0
    sed -n 's/^[[:space:]]*AddModule[[:space:]]\+"\([^"]*\)".*/\1/p' "$1"
}

# --------------------------------------------------------------- restarting
#
# Trap 7. speechd caches its configuration for the life of the process, so
# nothing we write takes effect until the running one is gone. It is not
# started again here: every client autospawns one, and starting a server with no
# client waiting is how you get one that idles out mid-test.
#
# Found through /proc/<pid>/exe, never `pkill -f`. That pattern matches this
# script's own command line as readily as the daemon, and during the Linux port
# it killed a test harness outright.
stop_speechd() {
    local exe target pid stopped=0 self="$$"
    for exe in /proc/[0-9]*/exe; do
        target="$(readlink "$exe" 2>/dev/null || true)"
        case "$target" in
            */speech-dispatcher)
                pid="${exe#/proc/}"; pid="${pid%/exe}"
                [[ "$pid" == "$self" ]] && continue
                # Only ours. A system-wide speechd belongs to root and killing
                # it would take down every user's speech, which is the failure
                # this whole script is written to avoid.
                [[ "$(stat -c %u "/proc/$pid" 2>/dev/null || echo -1)" == "$(id -u)" ]] || continue
                kill "$pid" 2>/dev/null || true
                stopped=1
                ;;
        esac
    done
    if (( stopped )); then
        # Wait for it to actually go. NOT with `pgrep -x speech-dispatcher`:
        # "speech-dispatcher" is 17 characters, pgrep matches the 15-character
        # comm name and REFUSES a longer pattern outright — so that loop would
        # exit on the first iteration and the config would be rewritten under a
        # server that had not finished dying. Found by the S4 harness, which hit
        # it as "Speech Dispatcher already running".
        local waited=0
        while (( waited < 50 )) && kill -0 "$pid" 2>/dev/null; do
            sleep 0.1; waited=$((waited + 1))
        done
        say "  stopped the running speech-dispatcher (it restarts on the next request)"
    else
        say "  no speech-dispatcher of yours was running"
    fi
}

# The first client call after a restart hung long enough to hit a 20 s timeout,
# twice, on the machine this was measured on; the second answered immediately.
# So the warm-up is part of installing — otherwise the user's first test of the
# thing we just installed is the one that appears to hang.
warm_speechd() {
    command -v spd-say >/dev/null 2>&1 || return 0
    timeout 30 spd-say -O >/dev/null 2>&1 || true
}

# ============================================================== --check
if [[ "$action" == check ]]; then
    rc=0
    say "VibeSuperTonic — Speech Dispatcher module"
    say ""
    say "  module command   $module_cmd"

    if [[ -x "${module_cmd%% *}" || -x "$module_cmd" ]]; then
        say "  it exists        yes"
    else
        say "  it exists        NO — nothing at that path, or it is not executable"
        rc=1
    fi

    reply="$(module_init_reply $module_cmd)"
    if [[ "$reply" == 299\ * ]]; then
        say "  it answers INIT  $reply"
    else
        say "  it answers INIT  ${reply:-nothing at all} — speechd would drop it"
        rc=1
    fi

    store="$(resolve_store)"
    voices="$(count_voices "$store")"
    say "  voices installed $voices  (in $store/models)"
    (( voices > 0 )) || say "                   — with none, the module offers only the espeak echo voice"

    say ""
    if [[ -f "$conf" ]]; then
        say "  config           $conf"
        declared="$(sed -n "s/^[[:space:]]*AddModule[[:space:]]\+\"$MODULE_NAME\"[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$conf" | tail -1 || true)"
        if [[ -z "$declared" ]]; then
            say "  declares us      no"
        else
            say "  declares us      $declared"
            # The assertion that catches the AppImage trap and a moved install:
            # a config naming a path that no longer exists is a module that
            # never starts, and speechd reports that as the module simply not
            # being there.
            if [[ -e "${declared%% *}" ]]; then
                say "  that path exists yes"
            else
                say "  that path exists NO — the config names something that is not there."
                say "                   If you moved or renamed the install, run this again."
                rc=1
            fi
        fi
        say "  other modules    $(active_declarations "$conf" | grep -vx "$MODULE_NAME" | tr '\n' ' ')"
    else
        say "  config           none at $conf (speechd is using $system_conf_dir/speechd.conf)"
        say "  declares us      no"
    fi

    if command -v spd-say >/dev/null 2>&1; then
        offered="$(timeout 25 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF' | tr '\n' ' ' || true)"
        say ""
        say "  speechd offers   ${offered:-(no answer — is speech-dispatcher installed and able to start?)}"
        if [[ -f "$conf" ]] && grep -q "AddModule \"$MODULE_NAME\"" "$conf" 2>/dev/null; then
            case " $offered " in
                *" $MODULE_NAME "*) ;;
                *) say "                   — ours is configured but NOT offered; see the module's log in ~/.cache/speech-dispatcher/log/"
                   rc=1 ;;
            esac
        fi
    fi
    exit $rc
fi

# ============================================================== --remove
if [[ "$action" == remove ]]; then
    say "VibeSuperTonic — removing the Speech Dispatcher module"

    if [[ ! -f "$conf" ]]; then
        say "  no $conf — nothing to remove"
        exit 0
    fi

    created=0; backup=""
    if [[ -f "$state" ]]; then
        # shellcheck source=/dev/null
        created="$(sed -n 's/^created=//p' "$state" | tail -1 || true)"; created="${created:-0}"
        backup="$(sed -n 's/^backup=//p' "$state" | tail -1 || true)"
    fi

    if [[ -n "$backup" && -f "$backup" ]]; then
        # There WAS a config before us. Putting it back beats editing ours,
        # because it restores settings we never understood.
        cp "$backup" "$conf"
        say "  restored $conf from $backup"
    elif (( created )); then
        # We created it, which means before us speechd was reading the system
        # file. Deleting ours is what "put back what was there" means — an
        # edited-down copy of the system file is NOT the same thing, because it
        # would stop tracking that file forever.
        rm -f "$conf"
        say "  removed $conf — speechd goes back to $system_conf_dir/speechd.conf"
    else
        # No state file: someone removed it, or this is an install from before
        # the state file existed. Strip our block and leave everything else.
        tmp="$(mktemp)"
        awk -v b="$MARK_BEGIN" -v e="$MARK_END" '
            $0 == b { skip = 1 } skip == 0 { print } $0 == e { skip = 0 }' "$conf" > "$tmp"
        grep -v "AddModule \"$MODULE_NAME\"" "$tmp" > "$conf"
        rm -f "$tmp"
        say "  stripped our block from $conf (no record of what came before it)"
    fi
    rm -f "$state"

    if (( restart )); then
        stop_speechd
        warm_speechd
    fi

    # THE TEST THAT MATTERS, run as part of the operation rather than left to
    # the user: after removing ourselves, does the machine still speak?
    if command -v spd-say >/dev/null 2>&1; then
        offered="$(timeout 30 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF' | tr '\n' ' ' || true)"
        say "  speechd now offers: ${offered:-nothing}"
        [[ -n "$offered" ]] || die "speech-dispatcher now offers NO output modules.
       That is the failure this script exists to prevent, and it has to be
       fixed before you log out. The config is $conf — deleting it returns
       speechd to $system_conf_dir/speechd.conf, which is what it used before
       VibeSuperTonic was installed."
    fi
    say "Done."
    exit 0
fi

# ============================================================== install
say "VibeSuperTonic — registering the Speech Dispatcher module"
say ""

command -v speech-dispatcher >/dev/null 2>&1 \
    || warn "  note: speech-dispatcher is not on PATH. Writing the config anyway;
        it takes effect when it is installed."

[[ -e "${module_cmd%% *}" ]] || die "no module at ${module_cmd%% *}.
       This script belongs beside vst-speechd in an extracted release."

# Refuse to register something that cannot speak — before writing any config,
# because a config that names a broken module is how a screen reader ends up
# routing to silence.
reply="$(module_init_reply $module_cmd)"
[[ "$reply" == 299\ * ]] || die "the module does not answer INIT with 299; it said: ${reply:-nothing}.
       speechd would drop it and it would never appear in \`spd-say -O\`.
       Most likely the espeak-ng payload is missing from this install."
say "  the module answers INIT — ${reply#299 }"

# Trap 14's first state: registered, perfect, and silent because there is no
# voice. The espeak echo voice would still work, but a module offering only that
# duplicates the distro's own espeak-ng and is not what anybody installed this
# for — so refuse, and say what to do instead.
store="$(resolve_store)"
voices="$(count_voices "$store")"
if (( voices == 0 && force == 0 )); then
    die "no voices are installed in $store/models.
       Open VibeSuperTonic once, accept the model licence and let the voices
       download, then run this again. (--force registers anyway; the module
       will offer only the espeak keystroke-echo voice.)"
fi
say "  $voices voice(s) installed in $store/models"

# --- trap 1: what is offered NOW, before we change anything -----------------
mapfile -t offered < <(enumerate_offered | awk 'NF')
if (( ${#offered[@]} == 0 )); then
    die "could not determine which output modules speech-dispatcher offers.
       Adding ours without re-declaring the others would leave you with ONLY
       ours — see docs/SPEECHD-PLAN.md trap 1 — so this refuses instead.
       Start speech-dispatcher (\`spd-say -O\` should list something) and retry."
fi
say "  speech-dispatcher currently offers: ${offered[*]}"

mkdir -p "$conf_dir"

created=0
backup=""
if [[ -f "$conf" ]]; then
    backup="$conf.vst-backup.$(date +%Y%m%d-%H%M%S)"
    cp "$conf" "$backup"
    say "  backed up your config to $backup"
    # Remove any previous block of ours so re-running does not stack them, and
    # any stray AddModule of ours from a hand edit.
    tmp="$(mktemp)"
    awk -v b="$MARK_BEGIN" -v e="$MARK_END" '
        $0 == b { skip = 1 } skip == 0 { print } $0 == e { skip = 0 }' "$conf" > "$tmp"
    grep -v "AddModule \"$MODULE_NAME\"" "$tmp" > "$conf"
    rm -f "$tmp"
else
    # Trap 2: the user file REPLACES the system one. Copying it first is what
    # keeps the audio backend, the log level and the default rate the user
    # already had — and it is also what makes trap 1 recoverable, because the
    # commented AddModule lines come with it.
    if [[ -f "$system_conf_dir/speechd.conf" ]]; then
        cp "$system_conf_dir/speechd.conf" "$conf"
        say "  copied $system_conf_dir/speechd.conf to $conf"
        say "        (a user config REPLACES the system one — copying keeps your settings)"
    else
        : > "$conf"
        warn "  no $system_conf_dir/speechd.conf to copy; writing a minimal config"
    fi
    created=1
fi

# --- the block --------------------------------------------------------------
#
# Everything we add is inside one marked block so --remove can find it, and the
# re-declarations come FIRST so that a reader of the file sees why they are
# there before they see ours.
{
    printf '\n%s\n' "$MARK_BEGIN"
    printf '# Written by speechd-install.sh on %s. Do not edit inside the markers.\n' "$(date -Is)"
    printf '#\n'
    printf '# The lines below re-declare the modules speech-dispatcher was offering\n'
    printf '# before VibeSuperTonic was added. THEY ARE NOT OPTIONAL: speechd only\n'
    printf '# auto-detects modules while this file declares NONE, so adding one line\n'
    printf '# turns detection off for every other module at once.\n'
} >> "$conf"

already="$(active_declarations "$conf" || true)"
for name in "${offered[@]}"; do
    [[ "$name" == "$MODULE_NAME" ]] && continue
    grep -qx "$name" <<< "$already" && continue
    printf '%s\n' "$(declaration_for "$name")" >> "$conf"
done

{
    printf '#\n'
    printf '# And ours. The third field is the module config file speechd would pass\n'
    printf '# as argv[1]; this module needs none and reads none.\n'
    printf 'AddModule "%s" "%s" ""\n' "$MODULE_NAME" "$module_cmd"
    printf '%s\n' "$MARK_END"
} >> "$conf"

say "  declared: $(active_declarations "$conf" | tr '\n' ' ')"

cat > "$state" <<STATE
# Written by speechd-install.sh. --remove reads this.
created=$created
backup=$backup
module=$module_cmd
offered=${offered[*]}
STATE

# --- trap 7: it does not take effect until the old process is gone ----------
if (( restart )); then
    stop_speechd
    warm_speechd
else
    say "  --no-restart given; the change takes effect when speech-dispatcher restarts"
fi

# --- did we just break the machine? -----------------------------------------
#
# The check that matters is not that ours appears. It is that the module the
# user was already relying on still does.
if (( restart )) && command -v spd-say >/dev/null 2>&1; then
    mapfile -t now < <(timeout 30 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF')
    say ""
    say "  speech-dispatcher now offers: ${now[*]:-nothing}"
    missing=()
    for name in "${offered[@]}"; do
        printf '%s\n' "${now[@]}" | grep -qx "$name" || missing+=("$name")
    done
    if (( ${#missing[@]} )); then
        warn ""
        warn "  WARNING: these modules were offered before and are not now: ${missing[*]}"
        warn "  Undo it with:  $self --remove"
    fi
fi

cat <<EOF

Done. Try it:

    spd-say -o $MODULE_NAME "hello from VibeSuperTonic"

To point your screen reader at it, choose "$MODULE_NAME" as the speech
synthesizer in its preferences (Orca: Preferences > Voice > Speech system).

    $self --check     what is configured, and whether it works
    $self --remove    put back what was there
EOF

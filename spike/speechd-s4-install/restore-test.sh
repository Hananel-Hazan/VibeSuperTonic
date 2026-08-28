#!/usr/bin/env bash
#
# S4's test that matters: after installing our module, DOES ESPEAK-NG STILL
# ANSWER — and does it still answer after --remove.
#
# Not "does ours work". Ours failing is a disappointment; espeak-ng failing is a
# blind user's desktop going silent, and trap 1 makes that the DEFAULT outcome
# of a naive installer rather than an unlucky one.
#
#     bash spike/speechd-s4-install/restore-test.sh [--compose] [--speak]
#
# --compose  rebuild the scratch install first (~1 min of dotnet publish)
# --speak    also send a real utterance; off by default so running this does not
#            make the machine talk while somebody is working at it
#
# NOTHING HERE TOUCHES ~/.config/speech-dispatcher. ONE variable is redirected —
# XDG_CONFIG_HOME — and both halves follow it: speech-dispatcher reads its user
# config from $XDG_CONFIG_HOME/speech-dispatcher (measured, 0.12.1) and the
# installer writes there by default. So the installer under test resolves every
# path exactly as it will on a user's machine, including the module command and
# the store, rather than being told the answers.
#
# /etc/speech-dispatcher is read, never written: it is the SOURCE of trap 2's
# copy, and using the real one is the point.
#
# `-C` was tried first and is wrong for this: it replaces the system directory
# wholesale, so the scratch dir plays both roles at once and the trap-2 copy has
# nowhere to copy FROM. It also refuses to start when the directory has no
# speechd.conf, which makes the auto-detection baseline untestable.
#
# Two traps in the harness itself, inherited from the S3 spike and re-learned
# here would have cost an hour each:
#   - the run directory must be SHORT: AF_UNIX truncates around 108 bytes and a
#     socket under a long path makes speechd die claiming it cannot bind.
#   - PULSE_SERVER is pinned by hand, because PulseAudio also finds its socket
#     through XDG_RUNTIME_DIR, which this moves.
set -uo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

INSTALL=/tmp/vst-s4-install
RUN=/tmp/vst-s4-run
compose=0; speak=0
for a in "$@"; do case "$a" in
    --compose) compose=1 ;;
    --speak)   speak=1 ;;
    *) echo "unknown argument: $a" >&2; exit 2 ;;
esac; done

pass=0; fail=0
ok()   { printf '  \033[32mok\033[0m   %s\n' "$*"; pass=$((pass + 1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail=$((fail + 1)); }
head_() { printf '\n\033[36m== %s\033[0m\n' "$*"; }

# ------------------------------------------------------------------ compose
if (( compose )) || [[ ! -x "$INSTALL/vst-speechd" ]]; then
    VST_SCRATCH_INSTALL="$INSTALL" bash spike/speechd-s3-voices/compose-install.sh >/dev/null \
        || { echo "compose failed" >&2; exit 1; }
fi
# The shipped installer, in the place it ships: beside the module. pack-tar.sh
# copies the same file; testing a copy in build/ would be testing a path that
# does not exist in a release.
cp "${VST_INSTALLER_SRC:-build/speechd-install.sh}" "$INSTALL/speechd-install.sh"
chmod 755 "$INSTALL/speechd-install.sh"
echo "install: $INSTALL ($("$INSTALL/vst-speechd" --version))"

# --------------------------------------------------------- private speechd
export PULSE_SERVER="${PULSE_SERVER:-unix:${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/pulse/native}"
export XDG_RUNTIME_DIR="$RUN/runtime"
export XDG_CONFIG_HOME="$RUN/xdg"
export SPEECHD_ADDRESS="unix_socket:$RUN/speechd.sock"
CONF_DIR="$XDG_CONFIG_HOME/speech-dispatcher"
CONF="$CONF_DIR/speechd.conf"

speechd_start() {
    mkdir -p "$XDG_RUNTIME_DIR" "$RUN/out"; chmod 700 "$XDG_RUNTIME_DIR"
    speech-dispatcher -d -t 0 -l 4 \
        -S "$RUN/speechd.sock" -P "$RUN/speechd.pid" -L "$RUN/out" 2>/dev/null
    local _
    for _ in $(seq 40); do [[ -S "$RUN/speechd.sock" ]] && return 0; sleep 0.25; done
    return 1
}
# Wait for the PROCESS, not for the socket file: speechd does not unlink its
# socket on SIGTERM, so a loop watching the file times out while the server is
# already gone — and, worse, returns while it is still alive if it is slow,
# which the next start reports as "Speech Dispatcher already running" three
# scenarios later. `pgrep -x` is not an option either; see the installer.
speechd_stop() {
    local pid="" waited=0
    [[ -f "$RUN/speechd.pid" ]] && pid="$(cat "$RUN/speechd.pid" 2>/dev/null)"
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null
        while (( waited < 60 )) && kill -0 "$pid" 2>/dev/null; do sleep 0.1; waited=$((waited + 1)); done
        kill -0 "$pid" 2>/dev/null && { kill -9 "$pid" 2>/dev/null; sleep 0.5; }
    fi
    rm -f "$RUN/speechd.sock" "$RUN/speechd.pid"
    return 0
}
speechd_restart() { speechd_stop; speechd_start || { echo "speechd did not start" >&2; exit 1; }; }

offered()   { timeout 30 spd-say -O 2>/dev/null | tail -n +2 | awk 'NF'; }
voicerows() { timeout 30 spd-say -o "$1" -L 2>/dev/null | tail -n +2 | awk 'NF' | wc -l; }

# No VST_* overrides on purpose: the installer resolves the config directory
# from XDG_CONFIG_HOME, the module from beside itself and the store from the
# daemon, which is what it will do on a real machine. Redirecting one variable
# and letting it work everything else out is the difference between testing the
# installer and testing a set of arguments.
installer() { bash "$INSTALL/speechd-install.sh" "$@"; }

cleanup() { speechd_stop; "$INSTALL/vst-ctl" --no-start shutdown >/dev/null 2>&1; }
trap cleanup EXIT

# A scratch XDG_CONFIG_HOME with nothing in it. Module configs are NOT copied:
# measured 2026-08-28, speechd resolves espeak-ng.conf from the system directory
# even when the config it read came from the user's, so a user config does not
# have to carry them — and if that ever stops being true, espeak-ng's 14,806
# voice rows are what will notice.
rm -rf "$RUN"; mkdir -p "$RUN/xdg"

# ===========================================================================
head_ "baseline — no speechd.conf at all, which is the auto-detection case"
speechd_restart
base_offered="$(offered)"
base_rows="$(voicerows espeak-ng)"
echo "  offers: $(tr '\n' ' ' <<< "$base_offered")"
echo "  espeak-ng publishes $base_rows voices"
grep -qx espeak-ng <<< "$base_offered" && ok "espeak-ng is offered before we touch anything" \
                                       || bad "espeak-ng was not offered even BEFORE the install — fix the harness, not the product"
(( base_rows > 100 )) && ok "espeak-ng answers LIST VOICES ($base_rows rows)" \
                      || bad "espeak-ng answered $base_rows rows"

# ===========================================================================
head_ "scenario A — a machine with no user config (trap 2's copy path)"
installer --no-restart > "$RUN/install-a.log" 2>&1
rc=$?
(( rc == 0 )) && ok "the installer succeeded" || { bad "the installer exited $rc"; sed 's/^/    /' "$RUN/install-a.log"; }
[[ -f "$CONF" ]] && ok "it wrote $CONF" || bad "no config was written"
# ACTIVE declarations only. `grep 'AddModule "espeak-ng"'` also matches the
# nineteen COMMENTED lines the copied system config brings with it, so it
# reported a re-declaration that was not there — found by sabotage, which
# removed the re-declaration and watched this check pass.
active() { grep -E "^[[:space:]]*AddModule[[:space:]]+\"$1\"" "$CONF"; }
[[ -n "$(active vibesupertonic)" ]] && ok "ours is declared" || bad "ours is not declared"
[[ -n "$(active espeak-ng)" ]] && ok "espeak-ng was RE-DECLARED (trap 1)" || bad "espeak-ng was not re-declared — this is trap 1, and it is the whole point"
# Trap 2: the copy is what keeps the user's settings. The system file's own
# comment banner is the cheapest proof the copy happened rather than a minimal
# file being written.
grep -q 'DefaultRate' "$CONF" && ok "the system config was copied in, not replaced (trap 2)" || bad "the config does not look like a copy of the system one"

speechd_restart
now="$(offered)"
echo "  offers: $(tr '\n' ' ' <<< "$now")"
grep -qx espeak-ng      <<< "$now" && ok "espeak-ng STILL ANSWERS after our install" || bad "espeak-ng disappeared — trap 1 happened"
grep -qx vibesupertonic <<< "$now" && ok "ours is offered too" || bad "ours is not offered"
rows="$(voicerows espeak-ng)"
(( rows == base_rows )) && ok "espeak-ng publishes the same $rows voices as before" || bad "espeak-ng published $rows rows, was $base_rows"
ours="$(voicerows vibesupertonic)"
(( ours > 0 )) && ok "ours publishes $ours voices" || bad "ours published no voices"

if (( speak )); then
    timeout 30 spd-say -o espeak-ng -w -i -100 "one" >/dev/null 2>&1 \
        && ok "espeak-ng spoke an utterance" || bad "espeak-ng could not speak"
fi

installer --check > "$RUN/check-a.log" 2>&1 && ok "--check is happy" || { bad "--check exited non-zero"; sed 's/^/    /' "$RUN/check-a.log"; }

# ===========================================================================
head_ "scenario A — --remove puts it back"
installer --remove --no-restart > "$RUN/remove-a.log" 2>&1
rc=$?
(( rc == 0 )) && ok "--remove succeeded" || { bad "--remove exited $rc"; sed 's/^/    /' "$RUN/remove-a.log"; }
# We created the file, so "what was there" is NO file: speechd goes back to
# reading the system one. An edited-down copy would be a different machine.
[[ -f "$CONF" ]] && bad "the config we created was left behind" || ok "the config we created is gone"
speechd_restart
now="$(offered)"
grep -qx espeak-ng      <<< "$now" && ok "espeak-ng answers again after --remove" || bad "espeak-ng is gone AFTER --remove"
grep -qx vibesupertonic <<< "$now" && bad "ours is still offered after --remove" || ok "ours is no longer offered"
(( $(voicerows espeak-ng) == base_rows )) && ok "espeak-ng is byte-for-byte the module it was at baseline" || bad "espeak-ng's voice list changed"

# ===========================================================================
head_ "scenario B — a machine that ALREADY has a user config"
# This is the likely one: spd-conf writes such a file, and a blind user setting
# up a screen reader has almost certainly run it.
mkdir -p "$CONF_DIR"
cat > "$CONF" <<CONF
# A user's own config, as spd-conf would leave it.
DefaultRate 42
DefaultModule "espeak-ng"
AddModule "espeak-ng" "sd_espeak-ng" "espeak-ng.conf"
CONF
cp "$CONF" "$RUN/user-original.conf"
speechd_restart
installer --no-restart > "$RUN/install-b.log" 2>&1
rc=$?
(( rc == 0 )) && ok "the installer succeeded" || { bad "the installer exited $rc"; sed 's/^/    /' "$RUN/install-b.log"; }
grep -q '^DefaultRate 42' "$CONF" && ok "the user's own settings survived" || bad "the user's settings were lost"
ls "$CONF_DIR"/speechd.conf.vst-backup.* >/dev/null 2>&1 && ok "a backup was taken" || bad "no backup was taken"
# It was already declared, so we must not declare it twice: speechd on a
# duplicate AddModule is not something to find out about in the field.
(( $(active espeak-ng | wc -l) == 1 )) \
    && ok "espeak-ng is declared exactly once" || bad "espeak-ng is declared twice"
speechd_restart
now="$(offered)"
grep -qx espeak-ng      <<< "$now" && ok "espeak-ng still answers" || bad "espeak-ng disappeared"
grep -qx vibesupertonic <<< "$now" && ok "ours is offered"        || bad "ours is not offered"

head_ "scenario B — --remove restores the user's file"
installer --remove --no-restart > "$RUN/remove-b.log" 2>&1
if diff -q "$RUN/user-original.conf" "$CONF" >/dev/null 2>&1; then
    ok "the user's config is byte-identical to what it was"
else
    bad "the restored config differs from the original"
    diff "$RUN/user-original.conf" "$CONF" | sed 's/^/    /' | head -20
fi
speechd_restart
grep -qx espeak-ng <<< "$(offered)" && ok "espeak-ng answers after the second --remove" || bad "espeak-ng is gone"

# ===========================================================================
head_ "re-running the installer does not stack blocks"
installer --no-restart >/dev/null 2>&1
installer --no-restart >/dev/null 2>&1
(( $(active vibesupertonic | wc -l) == 1 )) \
    && ok "ours is declared exactly once after two installs" || bad "two installs left $(active vibesupertonic | wc -l) declarations"
(( $(grep -c '>>> VibeSuperTonic' "$CONF") == 1 )) \
    && ok "one marked block, not two" || bad "the block was stacked"
installer --remove --no-restart >/dev/null 2>&1

# ===========================================================================
head_ "scenario C — a machine with no voices downloaded (trap 14)"
# The likeliest first-time state: the archive is extracted, the first-run screen
# has never been accepted, and there is not one model on disk. Registering here
# gives a screen reader a synthesizer that offers only the espeak echo voice —
# a worse copy of the espeak-ng module it already had. It must refuse, and it
# must refuse BEFORE writing anything.
rm -rf "$CONF_DIR"; mkdir -p "$CONF_DIR" /tmp/vst-s4-empty
VST_SPEECHD_STORE=/tmp/vst-s4-empty installer --no-restart > "$RUN/install-c.log" 2>&1
rc=$?
(( rc != 0 )) && ok "it refused (exit $rc)" || bad "it registered a module with no voices"
grep -qi "no voices are installed" "$RUN/install-c.log" && ok "it said why, and what to do about it" || bad "the refusal does not name the problem"
[[ -f "$CONF" ]] && bad "it wrote a config before refusing" || ok "it wrote no config"
# --force is the escape hatch, and it must actually work: a user who wants the
# echo voice alone is not wrong, they are unusual.
VST_SPEECHD_STORE=/tmp/vst-s4-empty installer --no-restart --force > "$RUN/install-c2.log" 2>&1 \
    && ok "--force registers anyway" || bad "--force did not override the refusal"
rm -rf "$CONF_DIR"; mkdir -p "$CONF_DIR"

# ===========================================================================
head_ "scenario D — an install whose phonemiser is missing (trap 14, INIT gate)"
# The module answers INIT with 399 when it has no espeak beside it, and speechd
# responds to that by dropping the module — which the user sees as ours simply
# not being in the list, with no clue why. Asking the same question the server
# is about to ask, while a person is still here to read the answer, is the whole
# value of the gate.
mv "$INSTALL/espeak" "$INSTALL/espeak.hidden"
installer --no-restart > "$RUN/install-d.log" 2>&1
rc=$?
(( rc != 0 )) && ok "it refused (exit $rc)" || bad "it registered a module that cannot start"
grep -q "399" "$RUN/install-d.log" && ok "it quoted what the module actually said" || bad "the refusal does not show the module's reply"
[[ -f "$CONF" ]] && bad "it wrote a config before refusing" || ok "it wrote no config"
installer --check > "$RUN/check-d.log" 2>&1 && bad "--check passed a broken install" || ok "--check fails on a module that cannot start"
mv "$INSTALL/espeak.hidden" "$INSTALL/espeak"

# ===========================================================================
head_ "scenario E — the install moved after it was registered (trap 13)"
# A config names an ABSOLUTE command. Move or rename the folder — or, for an
# AppImage, the image file — and speechd is left pointing at nothing: the module
# never starts, and the only symptom is that ours is missing from the list. A
# blind user gets no error at all, which is why --check has to be able to say
# this out loud.
rm -rf "$CONF_DIR"; mkdir -p "$CONF_DIR"
installer --no-restart > "$RUN/install-e.log" 2>&1
# RESTART FIRST, and check ours is offered. Without this, --check fails anyway
# because a module that is configured but not offered is its own error — so the
# scenario would pass while saying nothing about the path at all. Sabotage found
# it: disabling the path check left the test still red, for the wrong reason.
speechd_restart
grep -qx vibesupertonic <<< "$(offered)" && ok "ours is offered before the move" || bad "ours is not offered, so the move proves nothing"
mv "$INSTALL" "$INSTALL.moved"
bash "$INSTALL.moved/speechd-install.sh" --check > "$RUN/check-e.log" 2>&1
rc=$?
mv "$INSTALL.moved" "$INSTALL"
(( rc != 0 )) && ok "--check fails when the configured path is gone" || bad "--check passed a config naming a path that does not exist"
grep -qi "that path exists NO" "$RUN/check-e.log" && ok "it names the problem as the path, not the module" || bad "--check does not say the path is missing"
installer --remove --no-restart >/dev/null 2>&1

# ===========================================================================
head_ "scenario F — speechd cannot be asked what it offers"
# Trap 1 in its worst form: we cannot enumerate, so we cannot re-declare, so
# adding our one line would leave the machine with our module and nothing else.
# Guessing is not available here — "what speechd would offer" is a question only
# speechd can answer — so the only safe move is to refuse and say so.
rm -rf "$CONF_DIR"; mkdir -p "$CONF_DIR" /tmp/vst-s4-emptyetc
SPEECHD_ADDRESS="unix_socket:/tmp/vst-s4-no-such.sock" \
VST_SPEECHD_SYSTEM_CONF_DIR=/tmp/vst-s4-emptyetc \
    installer --no-restart > "$RUN/install-f.log" 2>&1
rc=$?
(( rc != 0 )) && ok "it refused (exit $rc)" || bad "it wrote a config it could not verify"
[[ -f "$CONF" ]] && bad "it wrote a config with nothing re-declared" || ok "it wrote no config"
rm -rf "$CONF_DIR"; mkdir -p "$CONF_DIR"

printf '\n%s passed, %s failed\n' "$pass" "$fail"
exit $(( fail > 0 ))

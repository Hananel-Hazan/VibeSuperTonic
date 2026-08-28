#!/usr/bin/env bash
#
# Is the Speech Dispatcher module in this composed tree one that will work?
#
#     bash build/check-speechd-payload.sh <composed-tree>
#
# Called by pack-tar.sh as assertion 3e, and a separate file for the same reason
# check-piper-catalog.py is: an assertion that can only run inside a four-minute
# packer run is one nobody sabotages, and a check that has never been observed
# failing is not evidence. spike/speechd-s4-install/payload-sabotage.sh breaks
# each of these against a copy of a real composed tree in about a second each.
#
# THE MODULE IS NOT A PROGRAM ANYBODY RUNS, which is what makes every failure
# here quiet. speech-dispatcher spawns it, reads one line, and drops it on
# anything it does not like — the user's only symptom is that VibeSuperTonic is
# missing from the synthesizer list, with no error on any screen they can reach.
# So the checks are behavioural: ask it what the server is about to ask.
set -uo pipefail

staging="${1:-}"
[[ -n "$staging" && -d "$staging" ]] || { echo "usage: $0 <composed-tree>" >&2; exit 2; }

die() { printf '\033[31mcheck-speechd-payload: %s\033[0m\n' "$*" >&2; exit 1; }
info() { printf '    %s\n' "$*"; }

module="$staging/vst-speechd"
installer="$staging/speechd-install.sh"

[[ -x "$module" ]]    || die "vst-speechd is missing or not executable in the archive"
[[ -x "$installer" ]] || die "speechd-install.sh is missing or not executable in the archive"
bash -n "$installer" || die "speechd-install.sh does not parse.
       It is a shell script nobody runs during a build, so nothing looks at it
       until a user does."

# INIT is the reply the server acts on: 299 means loaded, 399 means the module
# refused — which it does when the espeak payload is not beside it. Running the
# SHIPPED binary in the COMPOSED tree is the point; it proves espeak/ landed
# where the module looks for it, which no file listing can.
reply="$(printf 'INIT\nQUIT\n' | timeout 30 "$module" 2>/dev/null | grep -E '^(299|399) ' | tail -1)"
[[ "$reply" == 299\ * ]] || die "the shipped vst-speechd answered INIT with: ${reply:-nothing}.
       speech-dispatcher drops a module that does not answer 299, and the user
       sees only that VibeSuperTonic is not in the list. 399 means it could not
       find espeak/espeak-ng beside itself in the composed tree."
info "vst-speechd answers INIT — ${reply#299 }"

# A scratch config home, so this reads and writes nothing of the build machine's
# own. The installer resolves everything else — module, store — from its own
# location, which is what makes this a test of the ARCHIVE rather than of a set
# of arguments.
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

out="$(XDG_CONFIG_HOME="$scratch" "$installer" --check 2>&1)" \
    || die "speechd-install.sh --check failed on the composed tree:
$(sed 's/^/       /' <<< "$out")"
grep -qF "$module" <<< "$out" || die "--check does not name $module; it said:
$(sed 's/^/       /' <<< "$out")"

# TRAP 13, in the only form a packer can ask it: the path a config would be
# given is a path that exists. --check is what a user runs after moving the
# folder, so a --check that cannot tell is a support answer nobody can give.
cat > "$scratch/probe.conf" <<CONF
AddModule "vibesupertonic" "$staging/no-such-module" ""
CONF
mkdir -p "$scratch/speech-dispatcher"
cp "$scratch/probe.conf" "$scratch/speech-dispatcher/speechd.conf"
# The MESSAGE, not just the exit code. --check has other reasons to fail — on a
# build machine that runs speech-dispatcher, "configured but not offered" is one
# — so a non-zero exit here proves nothing about the path. Sabotage found this:
# disabling the path test left this probe green.
probe_out="$(XDG_CONFIG_HOME="$scratch" "$installer" --check 2>&1)" || true
grep -q "that path exists NO" <<< "$probe_out" \
    || die "--check did not notice a config naming a module path that does not exist.
       That is what a moved or renamed install looks like, and speechd reports it
       as our module simply being absent. --check said:
$(sed 's/^/       /' <<< "$probe_out")"
rm -f "$scratch/speech-dispatcher/speechd.conf"

# TRAP 14. The archive ships no voices (assertion 3), so a real install attempt
# from a fresh extract must refuse: a screen reader pointed at a synthesizer
# with nothing to say hears silence, with no error anywhere.
if out="$(XDG_CONFIG_HOME="$scratch" "$installer" --no-restart 2>&1)"; then
    die "speechd-install.sh REGISTERED the module from an archive that ships no
       voices. It must refuse until the first-run download has happened."
fi
grep -qi "no voices are installed" <<< "$out" || die "the installer refused, but for the wrong reason:
$(sed 's/^/       /' <<< "$out")"
[[ -f "$scratch/speech-dispatcher/speechd.conf" ]] \
    && die "the installer wrote a config before refusing"

info "speechd-install.sh checks out, and refuses to register a voiceless install"
exit 0

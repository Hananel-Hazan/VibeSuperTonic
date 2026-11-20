#!/usr/bin/env bash
# Review item 98: the daemon autostart entry is opt-in, and both desktop entries
# install.sh writes still run their program from a folder with an awkward name.
# Extracts install.sh and uninstall.sh from pack-tar.sh (no pack needed) and runs
# them against a scratch HOME, twice: from a plain folder and from one whose name
# holds a space, " ` $ \ and %. Plain install must create no autostart entry;
# --autostart must create exactly one, whose Exec=, parsed the way the desktop
# entry spec parses it, is exactly [<dir>/vibesupertonicd, --preload]; the menu
# entry's Exec= must be [<dir>/vibesupertonic-ui]; uninstall must remove the
# autostart entry; an unknown option must be refused.
# Usage: check-autostart.sh [path/to/pack-tar.sh]
set -euo pipefail
packer="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/pack-tar.sh}"
work="$(mktemp -d "${TMPDIR:-/tmp}/vst-autostart.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# Exec= as the spec reads it: string unescape, then quoted-argument split, then
# %% to %. Prints one argument per line, NUL-free names assumed.
cat > "$work/exec-argv.py" <<'PY'
import sys
line = next(l for l in open(sys.argv[1], encoding="utf-8") if l.startswith("Exec="))
v = line[len("Exec="):].rstrip("\n")
s, i = "", 0
while i < len(v):                      # string-level escapes
    if v[i] == "\\" and i + 1 < len(v):
        s += {"s": " ", "n": "\n", "t": "\t", "r": "\r", "\\": "\\"}.get(v[i+1], "\\" + v[i+1]); i += 2
    else:
        s += v[i]; i += 1
args, cur, inq, have, i = [], "", False, False, 0
while i < len(s):                      # argument split
    c = s[i]
    if inq:
        if c == "\\" and i + 1 < len(s) and s[i+1] in '"`$\\': cur += s[i+1]; i += 2; continue
        if c == '"': inq = False
        else: cur += c
    elif c == '"': inq = have = True
    elif c == " ":
        if have or cur: args.append(cur); cur, have = "", False
    else: cur += c
    i += 1
if inq: sys.exit("unterminated quote in Exec=")
if have or cur: args.append(cur)
for a in args: print(a.replace("%%", "%"))
PY

fail=0
ok()  { echo "ok   $1"; }
bad() { echo "FAIL $1"; fail=1; }

run_in() {   # $1 = package folder name
    local pkg="$work/$1" h="$work/h" label="[$1]"
    mkdir -p "$pkg"
    awk '/^cat > "\$staging\/install.sh" <<.INSTALLER.$/{f=1;next} /^INSTALLER$/{f=0} f' "$packer" > "$pkg/install.sh"
    awk '/^cat > "\$staging\/uninstall.sh" <<.UNINSTALLER.$/{f=1;next} /^UNINSTALLER$/{f=0} f' "$packer" > "$pkg/uninstall.sh"
    printf '#!/bin/sh\nexit 0\n' > "$pkg/vst-ctl"
    printf 'vst_bind(){ :; }\nvst_unbind(){ :; }\n' > "$pkg/keybindings.sh"
    chmod +x "$pkg"/*
    local entry="$h/.config/autostart/vibesupertonic-daemon.desktop"
    local menu="$h/.local/share/applications/vibesupertonic-ui.desktop"
    inst() { rm -rf "$h"; mkdir -p "$h"
             HOME="$h" XDG_CONFIG_HOME="$h/.config" XDG_DATA_HOME="$h/.local/share" \
               bash "$pkg/install.sh" "$@" >/dev/null 2>&1; }

    inst
    [[ ! -e "$entry" ]] && ok "$label plain install creates no autostart entry" || bad "$label plain install created the autostart entry"
    if [[ -f "$menu" ]] && [[ "$(python3 -I "$work/exec-argv.py" "$menu")" == "$pkg/vibesupertonic-ui" ]]; then
        ok "$label menu entry runs vibesupertonic-ui from this folder"
    else bad "$label menu entry Exec= does not parse to $pkg/vibesupertonic-ui"; fi
    inst --no-bind
    [[ ! -e "$entry" ]] && ok "$label --no-bind creates no autostart entry" || bad "$label --no-bind created the autostart entry"
    inst --autostart
    if [[ -f "$entry" ]] && [[ "$(python3 -I "$work/exec-argv.py" "$entry")" == "$pkg/vibesupertonicd"$'\n'"--preload" ]]; then
        ok "$label --autostart entry runs vibesupertonicd --preload from this folder"
    else bad "$label --autostart entry missing, or its Exec= does not parse to [$pkg/vibesupertonicd, --preload]"; fi
    local n=0 f
    for f in "$h/.config/autostart"/*; do [[ -f "$f" ]] && n=$((n + 1)); done
    (( n == 1 )) && ok "$label exactly one autostart file" || bad "$label $n autostart files"
    HOME="$h" XDG_CONFIG_HOME="$h/.config" XDG_DATA_HOME="$h/.local/share" bash "$pkg/uninstall.sh" >/dev/null 2>&1
    [[ ! -e "$entry" ]] && ok "$label uninstall removes the entry" || bad "$label uninstall left the entry"
    if HOME="$h" bash "$pkg/install.sh" --bogus >/dev/null 2>&1; then bad "$label unknown option accepted"; else ok "$label unknown option refused"; fi
}

run_in pkg
run_in 'my "odd" `apps` $HOME \x 100%'
exit $fail

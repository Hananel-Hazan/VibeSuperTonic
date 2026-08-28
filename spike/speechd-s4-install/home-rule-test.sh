#!/usr/bin/env bash
#
# build/appimage-home.sh, on its own.
#
# The rule it holds — "the desktop's own files go in the REAL home, even when an
# AppImage has redirected $HOME" — is one that 0.2.10 shipped wrong and had to
# be fixed in 0.2.11: a portable home made the hotkeys unbindable, and the first
# remedy for it could orphan the store. S4 gave it a second caller (speechd
# reads the real ~/.config and nowhere else), which is what moved it into a file
# of its own. A shared rule with no test is how the second caller drifts from
# the first.
#
# Nothing here touches a real home: HOME and APPIMAGE are set per case against
# scratch directories, and the failure case fakes `getent` on PATH rather than
# breaking anything.
set -uo pipefail
cd "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"

pass=0; fail=0
ok()  { printf '  \033[32mok\033[0m   %s\n' "$*"; pass=$((pass + 1)); }
bad() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail=$((fail + 1)); }

T=/tmp/vst-s4-home; rm -rf "$T"; mkdir -p "$T/normal" "$T/img.AppImage.home" "$T/fakebin"
: > "$T/img.AppImage"

run_case() {  # name, APPIMAGE, HOME, extra PATH
    local out
    out="$(APPIMAGE="$2" HOME="$3" PATH="${4:+$4:}$PATH" bash -c '
        source build/appimage-home.sh
        vst_resolve_real_home
        printf "%s|%s\n" "$vst_portable_home" "$vst_real_home"')"
    printf '%s' "$out"
}

echo "== appimage-home.sh =="

r="$(run_case "no appimage" "" "$T/normal")"
[[ "$r" == "|" ]] && ok "no \$APPIMAGE: nothing is claimed" || bad "no \$APPIMAGE gave '$r'"

r="$(run_case "ordinary home" "$T/img.AppImage" "$T/normal")"
[[ "$r" == "|" ]] && ok "an AppImage with an ordinary home: nothing is claimed" || bad "ordinary home gave '$r'"

# The case 0.2.10 got wrong.
r="$(run_case "portable" "$T/img.AppImage" "$T/img.AppImage.home")"
[[ "$r" == "$T/img.AppImage.home|$(getent passwd "$(id -un)" | cut -d: -f6)" ]] \
    && ok "a portable home is detected, and the real home comes from passwd" \
    || bad "portable home gave '$r'"

# passwd says the home is the portable one — the loop the caller must refuse.
cat > "$T/fakebin/getent" <<EOF
#!/bin/sh
echo "\$(id -un):x:$(id -u):$(id -g)::$T/img.AppImage.home:/bin/bash"
EOF
chmod 755 "$T/fakebin/getent"
r="$(run_case "passwd loops back" "$T/img.AppImage" "$T/img.AppImage.home" "$T/fakebin")"
[[ "$r" == "$T/img.AppImage.home|" ]] \
    && ok "passwd pointing back at the portable home is not an answer" \
    || bad "the loop case gave '$r'"

# No getent at all: the `|| true` that keeps `set -e` from taking the caller out
# before it can explain itself.
cat > "$T/fakebin/getent" <<'EOF'
#!/bin/sh
exit 2
EOF
chmod 755 "$T/fakebin/getent"
r="$(run_case "no getent" "$T/img.AppImage" "$T/img.AppImage.home" "$T/fakebin")"
[[ "$r" == "$T/img.AppImage.home|" ]] \
    && ok "a failing getent leaves the real home empty rather than killing the caller" \
    || bad "the failing-getent case gave '$r'"

printf '\n%s passed, %s failed\n' "$pass" "$fail"
exit $(( fail > 0 ))

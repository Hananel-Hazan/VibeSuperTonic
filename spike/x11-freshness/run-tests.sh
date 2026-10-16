#!/usr/bin/env bash
#
# The five cases behind the 2026-08-16 selection-freshness work, end to end
# against a real X server.
#
#   dotnet build spike/x11-freshness/SelCheck.csproj -c Release
#   bash spike/x11-freshness/run-tests.sh
#
# Runs on a NESTED X server, and that is not tidiness. On the live display the
# user selecting text while the cases run takes PRIMARY away mid-case, and the
# result becomes a coin toss -- which happened, and briefly looked like a code
# failure rather than a test failure.
set -u

S="$(cd "$(dirname "$0")" && pwd)"
SEL="$S/bin/Release/net10.0/selcheck"
DISPLAY_NUM="${VST_TEST_DISPLAY:-:77}"

if [[ ! -x "$SEL" ]]; then
    echo "build it first:  dotnet build $S/SelCheck.csproj -c Release" >&2
    exit 1
fi

command -v Xephyr >/dev/null || { echo "needs Xephyr (package xserver-xephyr)" >&2; exit 1; }
python3 -c "import Xlib" 2>/dev/null || { echo "needs python3-xlib" >&2; exit 1; }

Xephyr "$DISPLAY_NUM" -screen 400x300 -nolisten tcp >/dev/null 2>&1 &
XEPHYR=$!
trap 'kill $XEPHYR 2>/dev/null' EXIT
export DISPLAY="$DISPLAY_NUM"

for _ in $(seq 40); do
    python3 -c "from Xlib import display; display.Display('$DISPLAY_NUM')" 2>/dev/null && break
    sleep 0.1
done
python3 -c "from Xlib import display; display.Display('$DISPLAY_NUM')" 2>/dev/null \
    || { echo "Xephyr did not come up on $DISPLAY_NUM" >&2; exit 1; }

echo "isolated display $DISPLAY_NUM"
echo

# Each case gets its own owner: killing it releases the selection, so no case
# can inherit state from the one before.
run_case() {
    local title="$1"; shift
    local setup="$1"; shift
    echo "═══ $title"
    coproc OWNER { python3 "$S/owner.py"; }
    read -r -u "${OWNER[0]}" _ready
    printf '%b' "$setup" >&"${OWNER[1]}"
    sleep 0.4
    "$SEL" "$@"
    printf 'quit\n' >&"${OWNER[1]}" 2>/dev/null
    wait "$OWNER_PID" 2>/dev/null
    echo
}

echo "### 1 · one selection, never re-asserted — the non-publishing viewer case"
run_case "expect: second read carries the unchanged notice" \
    'primary The sea is everything.\n' first second

echo "### 2 · a new selection between captures — the ordinary case"
coproc OWNER { python3 "$S/owner.py"; }
read -r -u "${OWNER[0]}" _ready
printf 'primary The sea is everything.\n' >&"${OWNER[1]}"; sleep 0.4
"$SEL" first
printf 'primary A new selection entirely.\n' >&"${OWNER[1]}"; sleep 0.4
"$SEL" second
printf 'quit\n' >&"${OWNER[1]}" 2>/dev/null; wait "$OWNER_PID" 2>/dev/null
echo

echo "### 3 · stale selection, newer clipboard, fallback OFF"
run_case "expect: clipboard ignored, unchanged notice" \
    'primary The sea is everything.\nclipboard COPIED FROM THE VIEWER\n' \
    first second

echo "### 4 · stale selection, newer clipboard, fallback ON"
run_case "expect: the clipboard is read" \
    'primary The sea is everything.\nclipboard COPIED FROM THE VIEWER\n' \
    --clipboard-fallback first second

echo "### 5 · stale selection, OLDER clipboard, fallback ON"
run_case "expect: clipboard ignored — it predates the selection" \
    'clipboard COPIED EARLIER\nprimary The sea is everything.\n' \
    --clipboard-fallback first second

#!/usr/bin/env bash
#
# Observe build/check-openvino-pack.sh FAILING, once per clause, and passing on
# the unbroken tree. A check that has never been seen failing is not evidence.
#
#     bash build/openvino-pack-sabotage.sh
#
# Hermetic: builds a scratch "composed tree" holding only the files the check
# reads, so it needs no pack run and takes about a second.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
check="$here/check-openvino-pack.sh"
csproj="$here/../src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj"
work=$(mktemp -d "${TMPDIR:-/tmp}/vst-ov-sabotage-XXXXXX")
trap 'rm -rf "$work"' EXIT

failures=0

fresh() {
    rm -rf "$work/tree"
    mkdir -p "$work/tree"
    cp "$here/install-openvino.sh" "$work/tree/install-openvino.sh"
    chmod 755 "$work/tree/install-openvino.sh"
}

expect_pass() {
    fresh
    if bash "$check" "$work/tree" "$csproj" >/dev/null 2>&1; then
        echo "ok    unbroken tree passes"
    else
        echo "FAIL  unbroken tree was rejected:"
        bash "$check" "$work/tree" "$csproj" 2>&1 | sed 's/^/        /'
        failures=$((failures + 1))
    fi
}

# expect_fail <label> <expected-text> <mutation...>   (mutation runs in the tree)
expect_fail() {
    local label="$1" needle="$2"; shift 2
    fresh
    ( cd "$work/tree" && "$@" )
    local out
    out=$(bash "$check" "$work/tree" "$csproj" 2>&1)
    local rc=$?
    if [[ $rc -ne 0 && "$out" == *"$needle"* ]]; then
        echo "ok    caught: $label"
    else
        echo "FAIL  NOT caught: $label (rc=$rc)"
        echo "$out" | sed 's/^/        /'
        failures=$((failures + 1))
    fi
}

expect_pass

expect_fail "a pack library in the archive root" "is in the archive" \
    touch libopenvino.so.2510
expect_fail "the provider library in the archive root" "is in the archive" \
    touch libonnxruntime_providers_openvino.so
expect_fail "a runtime/openvino directory in the archive" "is in the archive" \
    mkdir -p runtime/openvino
expect_fail "the installer missing" "is not in the archive" \
    rm install-openvino.sh
expect_fail "the installer not executable" "not executable" \
    chmod 644 install-openvino.sh
expect_fail "the native minor version drifting from the managed one" "A different minor version" \
    sed -i 's/^ORT_VERSION="1\.22\.0"/ORT_VERSION="1.23.0"/' install-openvino.sh
expect_fail "the hash removed" "no usable WHEEL_SHA256" \
    sed -i 's/^WHEEL_SHA256="[0-9a-f]*"/WHEEL_SHA256=""/' install-openvino.sh
expect_fail "a hash that is not 64 hex characters" "no usable WHEEL_SHA256" \
    sed -i 's/^WHEEL_SHA256="\(.*\)[0-9a-f]"/WHEEL_SHA256="\1"/' install-openvino.sh
expect_fail "a bumped version that forgot to re-pin" "pins a hash for ONNX Runtime" \
    sed -i 's/^WHEEL_SHA256_FOR="1\.22\.0"/WHEEL_SHA256_FOR="1.22.9"/' install-openvino.sh
expect_fail "a non-https URL" "is not an https URL" \
    sed -i 's#^WHEEL_URL="https://#WHEEL_URL="http://#' install-openvino.sh
expect_fail "a URL on another host" "is not an https URL" \
    sed -i 's#files.pythonhosted.org#example.com#' install-openvino.sh
expect_fail "a URL for another version" "does not name onnxruntime_openvino" \
    sed -i 's#onnxruntime_openvino-1.22.0-#onnxruntime_openvino-1.21.0-#' install-openvino.sh

if [[ $failures -eq 0 ]]; then
    echo "all clauses observed failing"
else
    echo "$failures sabotage case(s) did not behave"
    exit 1
fi

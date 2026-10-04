#!/usr/bin/env bash
#
# Observe build/check-webgpu-pack.sh FAILING, once per clause, and passing on
# the unbroken tree. A check that has never been seen failing is not evidence.
#
#     bash build/webgpu-pack-sabotage.sh
#
# Hermetic: builds a scratch "composed tree" holding only the files the check
# reads, so it needs no pack run, no network and takes a few seconds.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
check="$here/check-webgpu-pack.sh"
csproj="$here/../src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj"
work=$(mktemp -d "${TMPDIR:-/tmp}/vst-wg-sabotage-XXXXXX")
trap 'rm -rf "$work"' EXIT

failures=0

fresh() {
    rm -rf "$work/tree"
    mkdir -p "$work/tree"
    cp "$here/install-webgpu.sh" "$work/tree/install-webgpu.sh"
    chmod 755 "$work/tree/install-webgpu.sh"
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

# 1
expect_fail "the pack's runtime renamed into the archive root" "is in the archive" \
    touch libonnxruntime.so.1.27.0
expect_fail "Dawn in the archive root" "is in the archive" \
    touch libwebgpu_dawn.so
expect_fail "a runtime/webgpu directory in the archive" "is in the archive" \
    mkdir -p runtime/webgpu
# 2
expect_fail "the installer missing" "is not in the archive" \
    rm install-webgpu.sh
expect_fail "the installer not executable" "not executable" \
    chmod 644 install-webgpu.sh
# 3
expect_fail "a native runtime older than the managed binding" "OLDER than the managed binding" \
    sed -i 's/^ORT_VERSION="1\.27\.0"/ORT_VERSION="1.21.0"/' install-webgpu.sh
expect_fail "the managed binding moving past what was measured" "validated against" \
    sed -i 's/^MANAGED_VALIDATED="1\.22"/MANAGED_VALIDATED="1.21"/' install-webgpu.sh
expect_fail "MANAGED_VALIDATED removed" "no MANAGED_VALIDATED" \
    sed -i '/^MANAGED_VALIDATED=/d' install-webgpu.sh
# 4
expect_fail "the hash removed" "no usable WHEEL_SHA256" \
    sed -i 's/^WHEEL_SHA256="[0-9a-f]*"/WHEEL_SHA256=""/' install-webgpu.sh
expect_fail "a hash that is not 64 hex characters" "no usable WHEEL_SHA256" \
    sed -i 's/^WHEEL_SHA256="\(.*\)[0-9a-f]"/WHEEL_SHA256="\1"/' install-webgpu.sh
expect_fail "a bumped version that forgot to re-pin" "pins a hash for ONNX Runtime" \
    sed -i 's/^WHEEL_SHA256_FOR="1\.27\.0"/WHEEL_SHA256_FOR="1.27.9"/' install-webgpu.sh
# 5
expect_fail "a non-https URL" "is not an https URL" \
    sed -i 's#^WHEEL_URL="https://#WHEEL_URL="http://#' install-webgpu.sh
expect_fail "a URL on another host" "is not an https URL" \
    sed -i 's#files.pythonhosted.org#example.com#' install-webgpu.sh
expect_fail "a URL for another version" "does not name onnxruntime_webgpu" \
    sed -i 's#onnxruntime_webgpu-1.27.0-#onnxruntime_webgpu-1.25.1-#' install-webgpu.sh
expect_fail "a wheel above the glibc floor" "the product's floor is 2.34" \
    sed -i 's#manylinux_2_27_x86_64#manylinux_2_35_x86_64#' install-webgpu.sh
expect_fail "a wheel with no manylinux tag" "no manylinux" \
    sed -i 's#manylinux_2_27_x86_64.manylinux_2_28_x86_64#linux_x86_64#; s#"onnxruntime_webgpu-1.27.0-cp312-cp312-linux#"&#' install-webgpu.sh
# 6
expect_fail "the libvulkan refusal removed (installer carries on and fetches)" "BEFORE downloading" \
    sed -i '/grep -c "libvulkan/s/.*/if false; then/' install-webgpu.sh
expect_fail "the libvulkan refusal that exits 0" "exited 0 on a system without libvulkan" \
    sed -i '/^  echo "(mesa-vulkan-drivers is the driver/,/^  exit 1$/s/^  exit 1$/  exit 0/' install-webgpu.sh
expect_fail "the libvulkan refusal that does not say why" "did not say why" \
    sed -i 's/libvulkan\.so\.1 is not on this system/the thing is not on this system/; s/(libvulkan\.so\.1)//; /^ *echo .*libvulkan1 mesa/d; /^  echo "(mesa-vulkan/d' install-webgpu.sh
# 7
expect_fail "the hash comparison removed" "exited 0 for a download that is not the pinned wheel" \
    sed -i 's/if \[ "\$actual" != "\$WHEEL_SHA256" \]; then/if false; then/' install-webgpu.sh
expect_fail "the hash mismatch that complains and installs anyway" "installed a pack from bytes that failed the hash" \
    sed -i -e '/happens twice, do not work around it/{n;s/exit 1/:/}' -e '/^  rm -f "\$wheel"$/d' install-webgpu.sh

if [[ $failures -eq 0 ]]; then
    echo "all clauses observed failing"
else
    echo "$failures sabotage case(s) did not behave"
    exit 1
fi

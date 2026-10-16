#!/usr/bin/env bash
#
# Is the OpenVINO pack's installer honest, and is the pack absent from the archive?
#
#     bash build/check-openvino-pack.sh <composed-tree> [csproj]
#
# Called by pack-tar.sh beside the CUDA pack's assertion 4, and a separate file
# for the reason check-speechd-payload.sh and check-budgets.sh are: a check that
# can only run inside a four-minute pack is a check nobody sabotages, and a check
# that has never been observed failing is not evidence. Every clause below can be
# broken against a copy of a tree in about a second - build/openvino-pack-sabotage.sh
# does exactly that, one clause at a time.
#
# FIVE CLAUSES, EACH A FAILURE THAT IS SILENT UNTIL A USER'S MACHINE.
#
#   1. NONE of the pack is in the archive. It is ~160 MB; nothing else in the
#      packer names these files, so a publish that started copying them would
#      pass every other assertion and only make the download bigger.
#   2. The installer ships, and is executable.
#   3. Its native ONNX Runtime has the SAME MAJOR.MINOR as the managed
#      Microsoft.ML.OnnxRuntime the daemon is built against. The patch may
#      differ (the wheel is 1.22.0, the managed side 1.22.1 - measured to load and
#      run); a minor drift is an OrtApi the managed code asks for and the native
#      library does not have.
#   4. The wheel is pinned by a SHA-256, and the pin is for the version fetched.
#      It is code that is dlopen'd into the daemon.
#   5. The URL is https, from PyPI's file host, and names that version.
set -uo pipefail

tree="${1:?usage: check-openvino-pack.sh <composed-tree> [csproj]}"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
csproj="${2:-$here/../src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj}"
script="$tree/install-openvino.sh"

fail=0
bad() { printf 'check-openvino-pack: %s\n' "$*" >&2; fail=1; }

# 1. Nothing of the pack in the archive. Globs rather than find|grep -q, for the
#    SIGPIPE reason in CLAUDE.md.
shopt -s nullglob
for f in "$tree"/libopenvino* "$tree"/libtbb* "$tree"/libonnxruntime_providers_openvino.so* \
         "$tree"/runtime/openvino*; do
    # A pattern with no wildcard survives nullglob as a literal path, so existence
    # is asked explicitly. (The first version of this loop reported the provider
    # as shipped in a tree that had nothing in it; the unbroken-tree case in the
    # sabotage script is what showed it.)
    [[ -e "$f" ]] || continue
    bad "$(basename "$f") is in the archive; the OpenVINO pack is fetched by install-openvino.sh and must not ship"
done
shopt -u nullglob

# 2. The installer.
if [[ ! -f "$script" ]]; then
    bad "install-openvino.sh is not in the archive"
    exit 1
fi
[[ -x "$script" ]] || bad "install-openvino.sh is not executable"

# 3. Version agreement, major.minor.
managed=$(awk -F'"' '/Microsoft.ML.OnnxRuntime.Gpu.Linux" Version=/ { print $4; exit }' "$csproj")
native=$(awk -F'"' '/^ORT_VERSION=/ { print $2; exit }' "$script")
[[ -n "$managed" ]] || bad "could not read the managed ONNX Runtime version from $csproj"
[[ -n "$native" ]] || bad "install-openvino.sh has no ORT_VERSION"
if [[ -n "$managed" && -n "$native" ]]; then
    mm() { local IFS=.; set -- $1; printf '%s.%s' "${1:-}" "${2:-}"; }
    if [[ "$(mm "$managed")" != "$(mm "$native")" ]]; then
        bad "install-openvino.sh fetches ONNX Runtime $native but this build links $managed.
       A different minor version means an OrtApi the managed code requests and
       the pack's runtime may not have."
    fi
fi

# 4. The pin, and what it is for.
sha=$(awk -F'"' '/^WHEEL_SHA256=/ { print $2; exit }' "$script")
sha_for=$(awk -F'"' '/^WHEEL_SHA256_FOR=/ { print $2; exit }' "$script")
[[ "$sha" =~ ^[0-9a-f]{64}$ ]] || bad "install-openvino.sh has no usable WHEEL_SHA256 (64 hex characters).
       The wheel is dlopen'd into the daemon; unpinned, TLS is the whole defence."
[[ "$sha_for" == "$native" ]] || bad "install-openvino.sh pins a hash for ONNX Runtime ${sha_for:-nothing} but fetches ${native:-nothing}"

# 5. The URL.
url=$(awk -F'"' '/^WHEEL_URL=/ { print $2; exit }' "$script")
[[ "$url" == https://files.pythonhosted.org/packages/* ]] || bad "WHEEL_URL is not an https URL on files.pythonhosted.org: ${url:-empty}"
[[ "$url" == *"onnxruntime_openvino-${native}-"*"manylinux"*"x86_64.whl" ]] || bad "WHEEL_URL does not name onnxruntime_openvino $native for manylinux x86_64: ${url:-empty}"

exit "$fail"

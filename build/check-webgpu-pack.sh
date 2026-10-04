#!/usr/bin/env bash
#
# Is the WebGPU pack's installer honest, and is the pack absent from the archive?
#
#     bash build/check-webgpu-pack.sh <composed-tree> [csproj]
#
# Called by pack-tar.sh beside the OpenVINO pack's assertion 4b, and a separate
# file for the reason check-openvino-pack.sh is: a check that can only run inside
# a four-minute pack is a check nobody sabotages, and a check that has never been
# observed failing is not evidence. Every clause below can be broken against a
# copy of a tree in about a second - build/webgpu-pack-sabotage.sh does exactly
# that, one clause at a time.
#
# SEVEN CLAUSES, EACH A FAILURE THAT IS SILENT UNTIL A USER'S MACHINE.
#
#   1. NONE of the pack is in the archive. It is ~32 MB of Dawn-linked runtime;
#      nothing else in the packer names it, so a publish that started copying it
#      would pass every other assertion and only make the download bigger.
#   2. The installer ships, and is executable.
#   3. The pack's native ONNX Runtime is NOT OLDER than the managed binding (an
#      older runtime lacks OrtApi entries the binding asks for), and the managed
#      version equals MANAGED_VALIDATED in the installer. This pack pairs a 1.27
#      runtime with the 1.22 binding - five minors apart, on purpose, because the
#      OrtApi table is append-only and the pairing was MEASURED (see the header of
#      install-webgpu.sh). A bump of the binding therefore fails here until
#      someone repeats that measurement and edits the one line; the OpenVINO
#      check's "same minor" would be the wrong rule for a pack that is supposed
#      to differ.
#   4. The wheel is pinned by a SHA-256, and the pin is for the version fetched.
#      It is code that is dlopen'd into the daemon.
#   5. The URL is https, from PyPI's file host, names that version, is
#      x86_64 manylinux, and its glibc tag is at or below the 2.34 floor the rest
#      of the product holds (CLAUDE.md, assertion 5). A wheel retagged
#      manylinux_2_35 would install cleanly and fail to load on 22.04.
#   6. BEHAVIOUR, no libvulkan: with a system that has no libvulkan.so.1 the
#      installer refuses (exit non-zero, says libvulkan.so.1) BEFORE it fetches
#      anything and leaves no pack directory. Greps for the message would pass a
#      script that printed it and carried on; this runs the script.
#   7. BEHAVIOUR, wrong bytes: with the loader present and a download that is not
#      the pinned wheel, the installer refuses on the hash and leaves no pack
#      directory. A pin nothing enforces is a comment.
#
# Clauses 6 and 7 need python3 (the installer does too) and put stub `ldconfig` and `curl` first on PATH. No network, no
# Vulkan, no ONNX Runtime is involved.
set -uo pipefail

tree="${1:?usage: check-webgpu-pack.sh <composed-tree> [csproj]}"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
csproj="${2:-$here/../src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj}"
script="$tree/install-webgpu.sh"

fail=0
bad() { printf 'check-webgpu-pack: %s\n' "$*" >&2; fail=1; }

# 1. Nothing of the pack in the archive. Globs rather than find|grep -q, for the
#    SIGPIPE reason in CLAUDE.md. libonnxruntime_providers_shared.so is NOT on the
#    list: the shipped runtime has one of its own.
shopt -s nullglob
for f in "$tree"/libwebgpu_dawn* "$tree"/libdawn* "$tree"/libonnxruntime.so.1.2[3-9]* \
         "$tree"/runtime/webgpu*; do
    [[ -e "$f" ]] || continue
    bad "$(basename "$f") is in the archive; the WebGPU pack is fetched by install-webgpu.sh and must not ship"
done
shopt -u nullglob

# 2. The installer.
if [[ ! -f "$script" ]]; then
    bad "install-webgpu.sh is not in the archive"
    exit 1
fi
[[ -x "$script" ]] || bad "install-webgpu.sh is not executable"

# 3. Version agreement: native >= managed, and the managed one is the one measured.
managed=$(awk -F'"' '/Microsoft.ML.OnnxRuntime.Gpu.Linux" Version=/ { print $4; exit }' "$csproj")
native=$(awk -F'"' '/^ORT_VERSION=/ { print $2; exit }' "$script")
validated=$(awk -F'"' '/^MANAGED_VALIDATED=/ { print $2; exit }' "$script")
[[ -n "$managed" ]] || bad "could not read the managed ONNX Runtime version from $csproj"
[[ -n "$native" ]] || bad "install-webgpu.sh has no ORT_VERSION"
[[ -n "$validated" ]] || bad "install-webgpu.sh has no MANAGED_VALIDATED"
if [[ -n "$managed" && -n "$native" ]]; then
    IFS=. read -r m_major m_minor _ <<<"$managed"
    IFS=. read -r n_major n_minor _ <<<"$native"
    if ! [[ "$m_major$m_minor$n_major$n_minor" =~ ^[0-9]+$ ]] \
       || (( n_major < m_major || (n_major == m_major && n_minor < m_minor) )); then
        bad "install-webgpu.sh fetches ONNX Runtime $native but this build links $managed.
       A runtime OLDER than the managed binding lacks OrtApi entries the binding asks for."
    fi
    if [[ -n "$validated" && "$m_major.$m_minor" != "$validated" ]]; then
        bad "this build links ONNX Runtime $managed but the WebGPU pack was validated against $validated.x.
       Repeat the measurement in install-webgpu.sh's header (managed binding + the
       pack's runtime: append, create a session, run) and update MANAGED_VALIDATED."
    fi
fi

# 4. The pin, and what it is for.
sha=$(awk -F'"' '/^WHEEL_SHA256=/ { print $2; exit }' "$script")
sha_for=$(awk -F'"' '/^WHEEL_SHA256_FOR=/ { print $2; exit }' "$script")
[[ "$sha" =~ ^[0-9a-f]{64}$ ]] || bad "install-webgpu.sh has no usable WHEEL_SHA256 (64 hex characters).
       The wheel is dlopen'd into the daemon; unpinned, TLS is the whole defence."
[[ "$sha_for" == "$native" ]] || bad "install-webgpu.sh pins a hash for ONNX Runtime ${sha_for:-nothing} but fetches ${native:-nothing}"

# 5. The URL, and its glibc tag against the floor.
url=$(awk -F'"' '/^WHEEL_URL=/ { print $2; exit }' "$script")
[[ "$url" == https://files.pythonhosted.org/packages/* ]] || bad "WHEEL_URL is not an https URL on files.pythonhosted.org: ${url:-empty}"
[[ "$url" == *"onnxruntime_webgpu-${native}-"*"manylinux"*"x86_64.whl" ]] || bad "WHEEL_URL does not name onnxruntime_webgpu $native for manylinux x86_64: ${url:-empty}"
if [[ "$url" =~ manylinux_2_([0-9]+)_x86_64 ]]; then
    (( BASH_REMATCH[1] <= 34 )) || bad "WHEEL_URL needs glibc 2.${BASH_REMATCH[1]}; the product's floor is 2.34"
else
    bad "WHEEL_URL carries no manylinux_2_N_x86_64 tag, so its glibc requirement is unknown: ${url:-empty}"
fi

# 6 and 7. Run the installer against stubs.
work=$(mktemp -d "${TMPDIR:-/tmp}/vst-webgpu-check-XXXXXX")
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin" "$work/store/data"

cat >"$work/bin/curl" <<'STUB'
#!/usr/bin/env bash
# Records that it was called, and "downloads" something that is not the wheel.
echo "$*" >>"$STUB_LOG"
out=""
while [ $# -gt 0 ]; do
  case "$1" in -o) out="$2"; shift 2 ;; *) shift ;; esac
done
# A VALID wheel with every member the installer wants, so that a hash check that
# fails to stop the install would go on to install it - an unreadable file would
# be refused by the unpacking step and hide that.
python3 - "$out" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], 'w') as z:
    for n in ('onnxruntime/capi/libonnxruntime.so.9.9.9',
              'onnxruntime/capi/libonnxruntime_providers_shared.so',
              'onnxruntime/LICENSE', 'onnxruntime/ThirdPartyNotices.txt'):
        z.writestr(n, 'not the pinned wheel\n')
PY
exit 0
STUB
cat >"$work/bin/ldconfig" <<'STUB'
#!/usr/bin/env bash
# `ldconfig -p`: a loader cache with, or without, the Vulkan loader.
if [ "${STUB_VULKAN:-0}" = 1 ]; then
  echo "	libvulkan.so.1 (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/libvulkan.so.1"
fi
echo "	libc.so.6 (libc6,x86-64) => /lib/x86_64-linux-gnu/libc.so.6"
exit 0
STUB
chmod 755 "$work/bin/curl" "$work/bin/ldconfig"

run_installer() {   # run_installer <vulkan 0|1>   -> output in $work/out, rc in $rc
    : >"$work/curl.log"
    PATH="$work/bin:$PATH" STUB_LOG="$work/curl.log" STUB_VULKAN="$1" \
        bash "$script" --dir "$work/store" -y >"$work/out" 2>&1 </dev/null
    rc=$?
}

# 6.
run_installer 0
if [[ $rc -eq 0 ]]; then
    bad "install-webgpu.sh exited 0 on a system without libvulkan.so.1; it must refuse"
elif ! grep -qF 'libvulkan.so.1' "$work/out"; then
    bad "install-webgpu.sh refused without libvulkan.so.1 but did not say why (output: $(head -c 300 "$work/out"))"
fi
if [[ -s "$work/curl.log" ]]; then
    bad "install-webgpu.sh fetched something on a system without libvulkan.so.1; it must refuse BEFORE downloading"
fi
[[ ! -e "$work/store/runtime/webgpu" ]] || bad "install-webgpu.sh left a pack directory after refusing for lack of libvulkan.so.1"

# 7.
run_installer 1
if [[ $rc -eq 0 ]]; then
    bad "install-webgpu.sh exited 0 for a download that is not the pinned wheel"
elif ! grep -qF 'does not match its pinned hash' "$work/out"; then
    bad "install-webgpu.sh did not refuse a wrong download on its hash (output: $(head -c 300 "$work/out"))"
fi
[[ -s "$work/curl.log" ]] || bad "the stub curl was never called with the loader present; the installer did not try to fetch"
[[ ! -e "$work/store/runtime/webgpu" ]] || bad "install-webgpu.sh installed a pack from bytes that failed the hash"

exit "$fail"

#!/usr/bin/env bash
# VibeSuperTonic — install (or remove) the optional WebGPU (Vulkan) GPU pack.
#
# WHAT IT IS FOR. This is the vendor-neutral GPU pack: it lets any GPU with a
# Vulkan driver — AMD above all, which has no pack of its own, but also Intel
# and NVIDIA — do the synthesis, through ONNX Runtime's WebGPU execution
# provider (Dawn, Google's WebGPU implementation, linked statically into the
# runtime, talking to the GPU through Vulkan). Where a vendor-specific pack
# exists (install-gpu.sh for NVIDIA, install-openvino.sh for Intel) the daemon
# prefers it: this one is the answer for everything else.
#
# WHY IT IS NOT IN THE ARCHIVE. About 60 MB unpacked, for a machine that may
# have no usable GPU. Fetched on request, like the other packs, from the one
# place the build is published: PyPI (the onnxruntime-webgpu wheel, MIT).
#
# WHY IT BRINGS ITS OWN libonnxruntime.so. The WebGPU provider is compiled into
# the runtime, not a plug-in; the 1.22.1 runtime the archive ships has none. The
# pack is that one library (plus ORT's tiny providers_shared stub), loaded by the
# daemon INSTEAD of the shipped one through a DllImport resolver. It is ONNX
# Runtime 1.27.0 and the daemon's managed binding is 1.22.1 — five minor
# versions apart. That is deliberate and was measured, not assumed: the OrtApi
# is an append-only table the binding indexes by the version it requests (22),
# which a newer runtime still serves. With the 1.27.0 library loaded under the
# 1.22.1 binding the daemon's own code path (AppendExecutionProvider "WebGPU",
# session creation, Run) produced correct output on a Vulkan device, and fell
# back to the CPU cleanly on a machine with none. build/check-webgpu-pack.sh
# pins the managed version this was validated against, so a binding bump forces
# that measurement to be repeated.
#
#   bash install-webgpu.sh [--dir <install>] [--remove] [-y]
#
# With no --dir it installs beside itself, which is where the tarball puts it.
#
# WHAT IT NEEDS FROM YOUR SYSTEM. The Vulkan loader, libvulkan.so.1, plus a
# Vulkan driver for your GPU. This script REFUSES to install without the loader
# (the runtime dlopens it at startup and can do nothing without it). On
# Debian/Ubuntu:
#   sudo apt install libvulkan1 mesa-vulkan-drivers     # AMD (RADV) and Intel
# (NVIDIA's proprietary driver ships its own Vulkan ICD. Fedora: vulkan-loader
# mesa-vulkan-drivers.) With the loader but no driver, or a GPU the driver does
# not support, the daemon logs that no adapter was found and keeps using the CPU.
#
# NOT MEASURED ON HARDWARE. The pack was verified to load and run a model under
# the daemon's ONNX Runtime binding against Mesa's software Vulkan (lavapipe)
# and against no Vulkan device at all. No physical AMD GPU was available, and the
# Supertonic models' operators have not been run on it: the WebGPU provider
# leaves operators it lacks on the CPU, so this is slower rather than wrong when
# coverage is partial. Whether it beats the CPU on yours is exactly what
# `vst-ctl benchmark` is for: it measures the GPU as another row and only picks
# it if it wins.

set -euo pipefail

# The native runtime in the wheel. It must be NEWER than or equal to the managed
# Microsoft.ML.OnnxRuntime the daemon is built against (1.22.x) so every OrtApi
# the binding asks for exists; build/check-webgpu-pack.sh asserts that when the
# archive is packed.
ORT_VERSION="1.27.0"

# The managed binding major.minor this pairing was MEASURED against. If the
# daemon's binding moves, the check fails until someone repeats the measurement
# (see the header) and edits this line.
MANAGED_VALIDATED="1.22"

# THE WHEEL, PINNED BY SHA-256 - the digest PyPI itself publishes for the file,
# re-derivable without downloading it:
#
#   curl -s https://pypi.org/pypi/onnxruntime-webgpu/1.27.0/json | python3 -c \
#     'import json,sys; [print(f["digests"]["sha256"], f["size"], f["url"]) for f in json.load(sys.stdin)["urls"] if "cp312-cp312-manylinux" in f["filename"]]'
#
# The file is code that is dlopen'd into the daemon, so it is checked before
# anything is unpacked. Any CPython tag carries the same native files; cp312 is
# the one that was measured. The wheel is tagged manylinux_2_27: it needs glibc
# 2.27 or newer, below the 2.34 floor the rest of the product already has.
WHEEL_URL="https://files.pythonhosted.org/packages/24/11/6cd5f840be97819cbd172bacc1868726ab5fa54ceaaa79508c5a64e61233/onnxruntime_webgpu-1.27.0-cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64.whl"
WHEEL_SHA256="fe5e49e00ad71bc667316d57670bfbf525438783d4a3468e0d337314ec985cbe"
# WHICH VERSION THAT HASH IS FOR, spelled out so a bump of ORT_VERSION that
# forgets to re-pin fails the check below before anything is fetched.
WHEEL_SHA256_FOR="1.27.0"

if [ "$WHEEL_SHA256_FOR" != "$ORT_VERSION" ]; then
  echo "WHEEL_SHA256 is pinned for ONNX Runtime $WHEEL_SHA256_FOR but this script fetches $ORT_VERSION." >&2
  echo "Re-pin it from PyPI's own metadata (the comment above says how)." >&2
  exit 1
fi

# What is taken from the wheel: the runtime and its shared-provider stub, and
# the two licence files (ONNX Runtime is MIT; Dawn and the rest are in the
# notices). Not the Python extension, which is 33 MB of nothing we load.
PACK_MEMBERS_REGEX='^onnxruntime/(capi/(libonnxruntime\.so\.[0-9.]+|libonnxruntime_providers_shared\.so)|LICENSE|ThirdPartyNotices\.txt)$'

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
install_dir="$here"
remove=0
assume_yes=0

while [ $# -gt 0 ]; do
  case "$1" in
    --dir) install_dir=$(cd "$2" && pwd); shift 2 ;;
    --remove) remove=1; shift ;;
    -y|--yes) assume_yes=1; shift ;;
    -h|--help)
      sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Its own directory, beside runtime/cuda and runtime/openvino. Removing the pack
# is removing this.
pack_dir="$install_dir/runtime/webgpu"

# Same rule as the other installers: the daemon decides where the pack goes, and
# a directory with no daemon in it is legitimate as long as it is recognisably ours.
if [ ! -f "$install_dir/vibesupertonicd" ] && [ ! -d "$install_dir/models" ] && [ ! -d "$install_dir/data" ]; then
  echo "$install_dir has no vibesupertonicd, models/ or data/ in it, so it is not" >&2
  echo "an install or a store. Pass --dir <directory>." >&2
  exit 2
fi

_vst_stop_daemon() {
  # By /proc/<pid>/exe where that identifies it, otherwise by asking the client.
  # Never pkill -f. The daemon holds the pack's libraries open while it runs.
  for pid in $(pgrep -x vibesupertonicd 2>/dev/null || true); do
    if [ "$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)" = "$install_dir/vibesupertonicd" ]; then
      echo "stopping the running daemon (pid $pid) first"
      "$install_dir/vst-ctl" shutdown --no-start >/dev/null 2>&1 || kill "$pid" 2>/dev/null || true
      sleep 1
      return 0
    fi
  done

  for ctl in "$install_dir/vst-ctl" "$HOME/.local/bin/vst-ctl"; do
    if [ -x "$ctl" ]; then
      if "$ctl" shutdown --no-start >/dev/null 2>&1; then
        echo "stopped the running daemon first"
        sleep 1
      fi
      return 0
    fi
  done
}

# ---------------------------------------------------------------- removing

if [ "$remove" = 1 ]; then
  if [ -d "$pack_dir" ]; then
    _vst_stop_daemon
  fi
  rm -rf "$pack_dir"
  echo "removed the WebGPU pack. The next daemon start will use the CPU,"
  echo "and \`vst-ctl config\` will say so."
  exit 0
fi

# ---------------------------------------------------------------- checking

# The Vulkan loader is not optional. The runtime dlopens libvulkan.so.1 when the
# daemon asks for the GPU, and without it every start would log "no adapter" and
# use the CPU - a pack that cannot work is refused here, not installed. grep -c
# rather than grep -q: with `set -o pipefail`, grep -q exits on the first match,
# ldconfig dies of SIGPIPE, and the pipeline reports failure on exactly the
# machines that HAVE the library.
if [ "$(ldconfig -p 2>/dev/null | grep -c "libvulkan\.so\.1" || true)" -eq 0 ]; then
  echo "The Vulkan loader (libvulkan.so.1) is not on this system, and this pack cannot" >&2
  echo "work without it. Nothing has been downloaded. On Debian/Ubuntu:" >&2
  echo >&2
  echo "    sudo apt install libvulkan1 mesa-vulkan-drivers" >&2
  echo >&2
  echo "(mesa-vulkan-drivers is the driver for AMD and Intel GPUs; NVIDIA's own driver" >&2
  echo "includes one. Fedora: vulkan-loader mesa-vulkan-drivers.) Then run this again." >&2
  exit 1
fi

if [ -d "$install_dir/runtime/cuda" ]; then
  echo "Note: the CUDA pack is installed too. When both are present the daemon uses" >&2
  echo "CUDA, so this pack would sit unused. Run install-gpu.sh --remove first if you" >&2
  echo "want the WebGPU pack." >&2
elif [ -d "$install_dir/runtime/openvino" ]; then
  echo "Note: the OpenVINO pack is installed too. When both are present the daemon uses" >&2
  echo "OpenVINO (a process loads one ONNX Runtime), so this pack would sit unused. Run" >&2
  echo "install-openvino.sh --remove first if you want the WebGPU pack." >&2
fi

# A loader with no driver behind it is a warning, not a refusal: the driver is
# something a person may install next, and it can be a vendor's own package that
# puts its ICD somewhere unusual.
icd_found=0
for d in /usr/share/vulkan/icd.d /etc/vulkan/icd.d /usr/local/share/vulkan/icd.d \
         "${XDG_DATA_HOME:-$HOME/.local/share}/vulkan/icd.d"; do
  for f in "$d"/*.json; do
    [ -e "$f" ] && icd_found=1
  done
done
if [ "$icd_found" = 0 ]; then
  echo "Warning: no Vulkan driver (ICD) was found under /usr/share/vulkan/icd.d. The"
  echo "loader is present, but with no driver the GPU is not reachable. On"
  echo "Debian/Ubuntu:  sudo apt install mesa-vulkan-drivers"
  echo
fi

if ! command -v python3 >/dev/null 2>&1 && ! command -v unzip >/dev/null 2>&1; then
  echo "python3 (or unzip) is required to unpack the pack." >&2
  exit 1
fi

if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
  echo "curl or wget is required to fetch the pack." >&2
  exit 1
fi

echo "This downloads about 25 MB (unpacking to about 33 MB) into $pack_dir:"
echo "  - ONNX Runtime $ORT_VERSION with the WebGPU provider (Dawn, over Vulkan)"
echo "    (the onnxruntime-webgpu wheel, from PyPI, pinned by SHA-256)"
echo

if [ "$assume_yes" != 1 ]; then
  read -r -p "Continue? [y/N] " answer || answer=""   # EOF (no tty): a no, said out loud
  case "$answer" in [yY]*) ;; *) echo "nothing was downloaded."; exit 0 ;; esac
fi

# ---------------------------------------------------------------- fetching

# Into a staging directory and moved into place at the end, so an interrupted
# download cannot leave a half-populated pack that the daemon then tries to load.
staging=$(mktemp -d "${TMPDIR:-/tmp}/vst-webgpu-XXXXXX")
trap 'rm -rf "$staging"' EXIT

wheel="$staging/ort-webgpu.whl"
echo "==> onnxruntime-webgpu $ORT_VERSION"
if command -v curl >/dev/null 2>&1; then
  curl -fL --progress-bar -o "$wheel" "$WHEEL_URL"
else
  wget -q --show-progress -O "$wheel" "$WHEEL_URL"
fi

# VERIFY BEFORE UNPACKING. sha256sum is coreutils; python3 is the fallback so
# the check cannot be skipped on a machine missing one tool - a skipped
# verification reports success.
if command -v sha256sum >/dev/null 2>&1; then
  actual=$(sha256sum "$wheel" | cut -d' ' -f1)
elif command -v python3 >/dev/null 2>&1; then
  actual=$(python3 -c 'import hashlib,sys;print(hashlib.sha256(open(sys.argv[1],"rb").read()).hexdigest())' "$wheel")
else
  echo "neither sha256sum nor python3 is available to verify the download." >&2
  exit 1
fi

if [ "$actual" != "$WHEEL_SHA256" ]; then
  rm -f "$wheel"
  echo >&2
  echo "The WebGPU package does not match its pinned hash." >&2
  echo "  expected  $WHEEL_SHA256" >&2
  echo "  got       $actual" >&2
  echo >&2
  echo "Nothing has been installed and the download has been deleted. These libraries" >&2
  echo "are loaded into the speech daemon, so this is not something to install anyway." >&2
  echo "A corrupted or truncated download is the likely cause - try again. If it" >&2
  echo "happens twice, do not work around it." >&2
  exit 1
fi
echo "    verified against the pinned SHA-256"

mkdir -p "$staging/pack"
if command -v python3 >/dev/null 2>&1; then
  python3 - "$wheel" "$staging/pack" "$PACK_MEMBERS_REGEX" <<'PY'
import os, re, shutil, sys, zipfile
wheel, out, pattern = sys.argv[1:4]
rx = re.compile(pattern)
n = 0
with zipfile.ZipFile(wheel) as z:
    for name in z.namelist():
        if not rx.match(name):
            continue
        base = os.path.basename(name)
        # The runtime is shipped as libonnxruntime.so.<version>; the daemon asks
        # for libonnxruntime.so.
        if re.match(r'^libonnxruntime\.so\.[0-9.]+$', base):
            base = 'libonnxruntime.so'
        with z.open(name) as src, open(os.path.join(out, base), 'wb') as dst:
            shutil.copyfileobj(src, dst)
        n += 1
print(f"    unpacked {n} files")
PY
else
  unzip -q -o -j "$wheel" 'onnxruntime/capi/libonnxruntime.so.*' onnxruntime/capi/libonnxruntime_providers_shared.so \
        onnxruntime/LICENSE onnxruntime/ThirdPartyNotices.txt -d "$staging/pack"
  mv "$staging/pack/libonnxruntime.so.$ORT_VERSION" "$staging/pack/libonnxruntime.so"
fi

# What the daemon needs, checked here rather than discovered at the first
# hotkey press.
for lib in libonnxruntime.so libonnxruntime_providers_shared.so LICENSE ThirdPartyNotices.txt; do
  [ -s "$staging/pack/$lib" ] || { echo "the package did not provide $lib" >&2; exit 1; }
done

# ---------------------------------------------------------------- installing

# The daemon must not be holding the old files while they are replaced.
_vst_stop_daemon

rm -rf "$pack_dir"
mkdir -p "$(dirname "$pack_dir")"
mv "$staging/pack" "$pack_dir"

size=$(du -sh "$pack_dir" | cut -f1)
echo
echo "installed:"
echo "  $pack_dir  ($size)"
echo
echo "The daemon loads this ONNX Runtime instead of the shipped one at startup, so"
echo "nothing needs to be exported. Check it took:"
echo
echo "  $install_dir/vst-ctl config      # inference should name WebGPU or say why not"
echo "  $install_dir/vst-ctl benchmark   # measures the GPU as another row and picks it"
echo "                                   # only if it wins"
echo
echo "If no GPU is reachable the daemon uses the CPU. A software Vulkan device"
echo "(llvmpipe) can show up as an 'adapter'; the benchmark will find it slower and"
echo "keep the CPU. On battery the daemon uses the CPU regardless; GpuOnBattery in"
echo "settings.json (or the Tune tab) changes that."

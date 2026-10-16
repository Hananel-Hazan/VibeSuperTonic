#!/usr/bin/env bash
# VibeSuperTonic — install (or remove) the optional Intel OpenVINO GPU pack.
#
# WHAT IT IS FOR. install-gpu.sh is NVIDIA-only. This is the second vendor: it
# lets an Intel GPU (integrated graphics or Arc) do the synthesis, through
# ONNX Runtime's OpenVINO execution provider. It does nothing for AMD; there is
# no AMD pack, and `vst-ctl config` says so on a machine that has one.
#
# WHY IT IS NOT IN THE ARCHIVE. About 160 MB unpacked against a 60 MB tarball,
# for a machine that may have no Intel GPU. Fetched on request, like the CUDA
# pack, from the one place the matching build is published: PyPI.
#
# WHY IT BRINGS ITS OWN libonnxruntime.so. Measured 2026-10-03 with the
# ONNX Runtime the archive ships (1.22.1, the Gpu.Linux build): asking it for
# OpenVINO fails with "OpenVINO execution provider is not supported in this
# build" EVEN WITH libonnxruntime_providers_openvino.so beside it. The
# OpenVINO support is compiled into Intel's build of the runtime, not into the
# provider library alone. So this pack is a self-consistent set — runtime,
# provider, OpenVINO 2025.1 and TBB, all from one wheel — that the daemon loads
# INSTEAD of the shipped runtime (no re-exec: the libraries find each other by
# $ORIGIN). That build is 1.22.0 and the managed code is 1.22.1; measured to
# load and run under it, and the pack-time check pins the minor version.
#
#   bash install-openvino.sh [--dir <install>] [--remove] [-y]
#
# With no --dir it installs beside itself, which is where the tarball puts it.
#
# WHAT IT NEEDS FROM YOUR SYSTEM. The OpenVINO GPU plugin talks to the Intel
# compute runtime through the OpenCL loader (libOpenCL.so.1). On Debian/Ubuntu:
#   sudo apt install intel-opencl-icd
# (Fedora: intel-compute-runtime.) Without it the daemon logs that no Intel GPU
# was found and keeps using the CPU; nothing breaks.
#
# NOT MEASURED ON HARDWARE. The pack was verified to load and run a model under
# the daemon's ONNX Runtime binding on a machine WITHOUT an Intel GPU. Whether
# it is faster than the CPU on yours is exactly what `vst-ctl benchmark` is for:
# it measures the GPU as another row and only picks it if it wins.

set -euo pipefail

# The native runtime in the wheel. Its MAJOR.MINOR must equal the managed
# Microsoft.ML.OnnxRuntime the daemon is built against (1.22.x);
# build/check-openvino-pack.sh asserts that when the archive is packed.
ORT_VERSION="1.22.0"

# THE WHEEL, PINNED BY SHA-256 — the digest PyPI itself publishes for the file,
# re-derivable without downloading it:
#
#   curl -s https://pypi.org/pypi/onnxruntime-openvino/1.22.0/json | python3 -c \
#     'import json,sys; [print(f["digests"]["sha256"], f["url"]) for f in json.load(sys.stdin)["urls"] if "cp312-cp312-manylinux" in f["filename"]]'
#
# The file is code that is dlopen'd into the daemon, so it is checked before
# anything is unpacked. Any CPython tag carries the same native files; cp312 is
# the one that was measured.
WHEEL_URL="https://files.pythonhosted.org/packages/58/79/7ca125aaa651ed4084f755d2f2e10f949a7eaad6d01aaf9a9f8b9a153248/onnxruntime_openvino-1.22.0-cp312-cp312-manylinux_2_28_x86_64.whl"
WHEEL_SHA256="000811fd42b027663cd15fcbbde049871164f7353ed28cab0219a1c10eef8e6d"
# WHICH VERSION THAT HASH IS FOR, spelled out so a bump of ORT_VERSION that
# forgets to re-pin fails the check below before anything is fetched.
WHEEL_SHA256_FOR="1.22.0"

if [ "$WHEEL_SHA256_FOR" != "$ORT_VERSION" ]; then
  echo "WHEEL_SHA256 is pinned for ONNX Runtime $WHEEL_SHA256_FOR but this script fetches $ORT_VERSION." >&2
  echo "Re-pin it from PyPI's own metadata (the comment above says how)." >&2
  exit 1
fi

# What is taken from the wheel: the runtime, the provider and its shared stub,
# OpenVINO and TBB. Not the Python extension, which is 29 MB of nothing we load.
PACK_MEMBERS_REGEX='^onnxruntime/capi/(libonnxruntime\.so\.[0-9.]+|libonnxruntime_providers_(shared|openvino)\.so|libopenvino[A-Za-z_]*\.so(\.[0-9]+)?|libtbb[A-Za-z_]*\.so(\.[0-9]+)?)$'

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
      sed -n '2,36p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Its own directory, beside runtime/cuda. Removing the pack is removing this.
pack_dir="$install_dir/runtime/openvino"

# Same rule as install-gpu.sh: the daemon decides where the pack goes, and a
# directory with no daemon in it is legitimate as long as it is recognisably ours.
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
  echo "removed the OpenVINO pack. The next daemon start will use the CPU,"
  echo "and \`vst-ctl config\` will say so."
  exit 0
fi

# ---------------------------------------------------------------- checking

if [ -d "$install_dir/runtime/cuda" ]; then
  echo "Note: the CUDA pack is installed too. When both are present the daemon uses" >&2
  echo "CUDA (it plugs into the runtime that ships; this pack replaces it), so this" >&2
  echo "pack would sit unused. Run install-gpu.sh --remove first if you want OpenVINO." >&2
fi

if ! command -v python3 >/dev/null 2>&1 && ! command -v unzip >/dev/null 2>&1; then
  echo "python3 (or unzip) is required to unpack the pack." >&2
  exit 1
fi

if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
  echo "curl or wget is required to fetch the pack." >&2
  exit 1
fi

# grep -c rather than grep -q: with `set -o pipefail`, grep -q exits on the first
# match, ldconfig dies of SIGPIPE, and the pipeline reports failure on exactly
# the machines that HAVE the library. This is a warning, never a refusal: the
# pack is useless without the OpenCL loader, but installing it is harmless and
# the compute runtime is something a person may install next.
if [ "$(ldconfig -p 2>/dev/null | grep -c "libOpenCL\.so\.1" || true)" -eq 0 ]; then
  echo "Warning: the OpenCL loader (libOpenCL.so.1) is not on this system. The Intel GPU"
  echo "is only reachable through it. On Debian/Ubuntu:  sudo apt install intel-opencl-icd"
  echo
fi

echo "This downloads about 64 MB (unpacking to about 160 MB) into $pack_dir:"
echo "  - ONNX Runtime $ORT_VERSION with the OpenVINO provider, OpenVINO 2025.1 and TBB"
echo "    (the onnxruntime-openvino wheel, from PyPI, pinned by SHA-256)"
echo

if [ "$assume_yes" != 1 ]; then
  read -r -p "Continue? [y/N] " answer || answer=""   # EOF (no tty): a no, said out loud
  case "$answer" in [yY]*) ;; *) echo "nothing was downloaded."; exit 0 ;; esac
fi

# ---------------------------------------------------------------- fetching

# Into a staging directory and moved into place at the end, so an interrupted
# download cannot leave a half-populated pack that the daemon then tries to load.
staging=$(mktemp -d "${TMPDIR:-/tmp}/vst-openvino-XXXXXX")
trap 'rm -rf "$staging"' EXIT

wheel="$staging/ort-openvino.whl"
echo "==> onnxruntime-openvino $ORT_VERSION"
if command -v curl >/dev/null 2>&1; then
  curl -fL --progress-bar -o "$wheel" "$WHEEL_URL"
else
  wget -q --show-progress -O "$wheel" "$WHEEL_URL"
fi

# VERIFY BEFORE UNPACKING. sha256sum is coreutils; python3 is the fallback so
# the check cannot be skipped on a machine missing one tool — a skipped
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
  echo "The OpenVINO package does not match its pinned hash." >&2
  echo "  expected  $WHEEL_SHA256" >&2
  echo "  got       $actual" >&2
  echo >&2
  echo "Nothing has been installed and the download has been deleted. These libraries" >&2
  echo "are loaded into the speech daemon, so this is not something to install anyway." >&2
  echo "A corrupted or truncated download is the likely cause — try again. If it" >&2
  echo "happens twice, do not work around it." >&2
  exit 1
fi
echo "    verified against the pinned SHA-256"

# python3's zipfile when there is one: it can select by pattern and rename the
# runtime in one step; unzip would need the names spelled out.
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
print(f"    unpacked {n} libraries")
PY
else
  unzip -q -o -j "$wheel" 'onnxruntime/capi/lib*.so*' -d "$staging/pack"
  rm -f "$staging/pack/libonnxruntime_providers_cuda.so" "$staging/pack/libonnxruntime_providers_tensorrt.so"
  mv "$staging/pack/libonnxruntime.so.$ORT_VERSION" "$staging/pack/libonnxruntime.so"
fi

# What the daemon needs, checked here rather than discovered at the first
# hotkey press: a missing one reads, in the log, as "OpenVINO unavailable" and
# names the wrong file.
for lib in libonnxruntime.so libonnxruntime_providers_shared.so \
           libonnxruntime_providers_openvino.so libopenvino.so.2510 \
           libopenvino_onnx_frontend.so.2510 libopenvino_intel_gpu_plugin.so libtbb.so.12; do
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
echo "  $pack_dir  ($size, $(find "$pack_dir" -name '*.so*' | wc -l) libraries)"
echo
echo "The daemon loads this ONNX Runtime instead of the shipped one at startup, so"
echo "nothing needs to be exported. Check it took:"
echo
echo "  $install_dir/vst-ctl config      # inference should name OpenVINO or say why not"
echo "  $install_dir/vst-ctl benchmark   # measures the GPU as another row and picks it"
echo "                                   # only if it wins"
echo
echo "To target a different device (an NPU, or a second GPU), set VST_OPENVINO_DEVICE"
echo "for the daemon, e.g. NPU or GPU.1. On battery the daemon uses the CPU"
echo "regardless; GpuOnBattery in settings.json (or the Tune tab) changes that."

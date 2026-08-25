#!/usr/bin/env bash
# VibeSuperTonic — install (or remove) the optional CUDA provider pack.
#
# WHY THIS IS NOT IN THE ARCHIVE. The pack is ~3.1 GB: 330 MB of ONNX Runtime
# CUDA provider plus CUDA and cuDNN. The release tarball is 51 MB. Shipping the
# pack by default would multiply the download by sixty for a machine that may
# have no NVIDIA GPU at all — so it is fetched on request, by this script, from
# the two places that already publish it: nuget.org and PyPI.
#
# WHAT IT IS WORTH. Measured 2026-08-24 on an RTX A2000 8GB Laptop GPU, against
# the CPU path at its benchmarked thread count:
#
#     first audio   802 ms -> 77 ms
#     RTF           0.180  -> 0.018
#
# First audio is what you wait for after pressing the hotkey, and it is the
# number Phase 3 measured as ~600 ms of fixed model cost that nothing else could
# move.
#
#   bash install-gpu.sh [--dir <install>] [--remove] [-y]
#
# With no --dir it installs beside itself, which is where the tarball puts it.

set -euo pipefail

# Must match the Microsoft.ML.OnnxRuntime.Gpu.Linux version the daemon was built
# against: the provider library and libonnxruntime.so are one build split across
# two files, and a mismatch fails at load with a message about a missing symbol
# rather than about versions. build/pack-tar.sh asserts this equals the version
# in src/VibeSuperTonic.Onnx.Ort/VibeSuperTonic.Onnx.Ort.csproj.
ORT_VERSION="1.22.1"

# The seven the provider actually links against (ldd), plus the two packages
# that carry their own dependencies. Deliberately not "everything NVIDIA
# publishes": the pack is already 3.1 GB.
CUDA_WHEELS=(
  nvidia-cuda-runtime-cu12
  nvidia-cublas-cu12
  nvidia-cufft-cu12
  nvidia-curand-cu12
  nvidia-cudnn-cu12
  nvidia-cuda-nvrtc-cu12
)

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
      sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

pack_dir="$install_dir/runtime/cuda"

# INSIDE the pack, not beside libonnxruntime.so.
#
# It used to go in the install root, which for a tarball is the directory
# holding libonnxruntime.so — so ORT found it through that library's own
# RUNPATH. An AppImage has no writable directory there at all: the root is a
# read-only squashfs mount at a path that changes every start. Putting the
# provider in the pack works for both, because the daemon re-execs itself with
# the pack on LD_LIBRARY_PATH before ORT is touched, and ORT dlopens this
# library by bare name.
#
# It also means the whole 3.1 GB lives in one directory, which is what --remove
# always wanted to be able to say.
provider="$pack_dir/libonnxruntime_providers_cuda.so"

# WHERE THE PACK GOES is the daemon's decision, not this script's: for a tarball
# it is the install directory, for an AppImage it is the store beside the image.
# AppRun passes it with --dir, having asked `vibesupertonicd --print-store`.
# So a directory with no daemon binary in it is legitimate now, as long as it is
# recognisably ours.
if [ ! -f "$install_dir/vibesupertonicd" ] && [ ! -d "$install_dir/models" ] && [ ! -d "$install_dir/data" ]; then
  echo "$install_dir has no vibesupertonicd, models/ or data/ in it, so it is not" >&2
  echo "an install or a store. Pass --dir <directory>, or run the AppImage as:" >&2
  echo "    ./VibeSuperTonic-<version>-x86_64.AppImage gpu-install" >&2
  exit 2
fi

_vst_stop_daemon() {
  # By /proc/<pid>/exe where that can identify it — a tarball daemon — and
  # otherwise by asking the client, because an AppImage daemon's exe is a path
  # inside a mount that this script has no way to predict. Never pkill -f: the
  # port plan records a session where that matched nothing and an old daemon
  # answered on behalf of the build under test.
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
  # Only when there is something to remove. The daemon holds the provider open
  # while it runs, so it has to stop before the files go — but stopping a
  # perfectly happy daemon because someone typed --remove against a directory
  # with no pack in it is a side effect nobody asked for, and the client
  # fallback cannot tell one daemon from another.
  if [ -d "$pack_dir" ] || [ -f "$install_dir/libonnxruntime_providers_cuda.so" ]; then
  # The daemon holds the provider library open for as long as it runs, and
  # deleting it underneath a running process leaves a daemon that works until it
  # restarts and then cannot explain itself. Same rule, and the same /proc
  # lookup, as install.sh.
    _vst_stop_daemon
  fi

  rm -rf "$pack_dir"
  # The old location, for anyone who installed the pack before the provider
  # moved into it. Harmless when it is not there.
  rm -f "$install_dir/libonnxruntime_providers_cuda.so"
  echo "removed the CUDA provider pack. The next daemon start will use the CPU,"
  echo "and \`vst-ctl config\` will say so."
  exit 0
fi

# ---------------------------------------------------------------- checking

# grep -c rather than grep -q, and the reason is set -o pipefail above: grep -q
# exits the moment it matches, ldconfig dies of SIGPIPE writing the rest, and
# pipefail hands the pipeline ldconfig's 141. The check then reports "no driver"
# on every machine that HAS one — which is exactly what it did, first run, on a
# box with an RTX A2000 in it. grep -c reads its input to the end.
if [ "$(ldconfig -p 2>/dev/null | grep -c "libcuda\.so\.1" || true)" -eq 0 ]; then
  echo "No NVIDIA driver found (libcuda.so.1 is not on this system)."
  echo
  echo "The pack needs a working driver — it ships CUDA and cuDNN, which talk to"
  echo "the driver, and cannot substitute for it. Install the NVIDIA driver for"
  echo "your distribution first. Nothing was downloaded."
  exit 1
fi

if command -v nvidia-smi >/dev/null 2>&1; then
  echo "GPU: $(nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>/dev/null | head -1)"
fi

if ! command -v python3 >/dev/null 2>&1 || ! python3 -m pip --version >/dev/null 2>&1; then
  echo "python3 with pip is required: the CUDA and cuDNN libraries are fetched"
  echo "from PyPI, which is where NVIDIA publishes them for exactly this purpose."
  echo "Install python3-pip and run this again."
  exit 1
fi

if ! command -v curl >/dev/null 2>&1 && ! command -v wget >/dev/null 2>&1; then
  echo "curl or wget is required to fetch the ONNX Runtime provider." >&2
  exit 1
fi

if ! command -v unzip >/dev/null 2>&1 && ! python3 -c "import zipfile" >/dev/null 2>&1; then
  echo "unzip (or python3's zipfile) is required to unpack the provider." >&2
  exit 1
fi

echo
echo "This downloads about 3.1 GB into $pack_dir and $provider:"
echo "  - ONNX Runtime $ORT_VERSION CUDA provider (330 MB, from nuget.org)"
echo "  - CUDA runtime, cuBLAS, cuFFT, cuRAND, NVRTC and cuDNN (from PyPI)"
echo

if [ "$assume_yes" != 1 ]; then
  read -r -p "Continue? [y/N] " answer
  case "$answer" in [yY]*) ;; *) echo "nothing was downloaded."; exit 0 ;; esac
fi

# ---------------------------------------------------------------- fetching

# Into a staging directory and moved into place at the end, so an interrupted
# download cannot leave a half-populated pack that the daemon then tries to load.
staging=$(mktemp -d "${TMPDIR:-/tmp}/vst-gpu-XXXXXX")
trap 'rm -rf "$staging"' EXIT

echo
echo "==> ONNX Runtime CUDA provider $ORT_VERSION"
nupkg="$staging/ort.nupkg"
url="https://www.nuget.org/api/v2/package/Microsoft.ML.OnnxRuntime.Gpu.Linux/$ORT_VERSION"
if command -v curl >/dev/null 2>&1; then
  curl -fL --progress-bar -o "$nupkg" "$url"
else
  wget -q --show-progress -O "$nupkg" "$url"
fi

inner="runtimes/linux-x64/native/libonnxruntime_providers_cuda.so"
if command -v unzip >/dev/null 2>&1; then
  unzip -q -o -j "$nupkg" "$inner" -d "$staging"
else
  python3 - "$nupkg" "$inner" "$staging" <<'PY'
import sys, zipfile, os, shutil
pkg, inner, out = sys.argv[1:4]
with zipfile.ZipFile(pkg) as z, open(os.path.join(out, os.path.basename(inner)), 'wb') as f:
    shutil.copyfileobj(z.open(inner), f)
PY
fi

got="$staging/libonnxruntime_providers_cuda.so"
[ -s "$got" ] || { echo "the provider library was not in the package" >&2; exit 1; }

echo "==> CUDA and cuDNN from PyPI"
python3 -m pip install --quiet --upgrade --target "$staging/wheels" "${CUDA_WHEELS[@]}"

# Flattened into one directory: every one of these libraries carries RPATH
# $ORIGIN, so a flat layout is what lets cuDNN find its own engine libraries and
# cuBLAS find cuBLASLt without any further path configuration.
mkdir -p "$staging/flat"
found=0
for d in "$staging"/wheels/nvidia/*/lib; do
  [ -d "$d" ] || continue
  for so in "$d"/*.so*; do
    [ -e "$so" ] || continue
    cp -a "$so" "$staging/flat/"
    found=$((found + 1))
  done
done
[ "$found" -gt 0 ] || { echo "no shared libraries came out of the wheels" >&2; exit 1; }

# The seven the provider links against. Checked here rather than discovered at
# the first hotkey press: a missing one produces "Failed to load library
# libonnxruntime_providers_cuda.so" at run time, which names the wrong file.
for lib in libcudart.so.12 libcublas.so.12 libcublasLt.so.12 \
           libcufft.so.11 libcurand.so.10 libnvrtc.so.12 libcudnn.so.9; do
  [ -e "$staging/flat/$lib" ] || { echo "the wheels did not provide $lib" >&2; exit 1; }
done

# ---------------------------------------------------------------- installing


# The daemon must not be holding the old files while they are replaced — same
# reason install.sh stops it, and the same /proc lookup rather than pkill -f.
_vst_stop_daemon

rm -rf "$pack_dir"
mkdir -p "$(dirname "$pack_dir")"
mv "$staging/flat" "$pack_dir"

# INTO runtime/cuda with the others, as of 2026-08-24. It used to go beside
# libonnxruntime.so, which for a tarball is the install root — and an AppImage
# has no writable directory there at all, so the pack installed perfectly and
# could not be loaded. ORT dlopens this library by bare name, so the pack being
# on LD_LIBRARY_PATH is enough, and the daemon puts it there by re-executing
# itself once before anything touches ORT.
mv "$got" "$provider"

size=$(du -sh "$pack_dir" | cut -f1)
echo
echo "installed:"
echo "  $provider"
echo "  $pack_dir  ($size, $(find "$pack_dir" -name '*.so*' | wc -l) libraries)"
echo
echo "The daemon puts $pack_dir on its library path by re-executing itself once at"
echo "startup, so nothing needs to be exported. Check it took:"
echo
echo "  $install_dir/vst-ctl config      # Provider should read cuda after a benchmark"
echo "  $install_dir/vst-ctl benchmark   # measures the GPU as another row and picks"
echo
echo "On battery the daemon uses the CPU regardless, which is the default and can"
echo "be changed with GpuOnBattery in settings.json or the Tune tab."

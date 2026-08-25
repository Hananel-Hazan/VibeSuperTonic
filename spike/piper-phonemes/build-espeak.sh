#!/usr/bin/env bash
#
# Build the espeak-ng that P1 measures parity against and P5 will ship.
#
#     bash spike/piper-phonemes/build-espeak.sh [output-dir]
#
# Prints the two environment variables the comparison needs. Roughly two minutes
# on a warm machine, most of it compiling dictionaries.
#
# ---------------------------------------------------------------------------
# THREE THINGS HERE ARE LOAD-BEARING AND NONE OF THEM ARE OBVIOUS.
#
# 1. THE PIN IS A COMMIT, NOT A RELEASE. piper builds espeak-ng at 724808c5 --
#    1.52.0-229-g724808c5 -- and the function its bridge calls,
#    espeak_TextToPhonemesWithTerminator, DOES NOT EXIST at the 1.52.0 tag. A
#    build from the release compiles, links, phonemises, and cannot tell you
#    whether a clause ended a sentence, so every input collapses to one
#    sentence and the trailing punctuation the model was trained on is missing.
#    docs/PIPER-PLAN.md said to call espeak_TextToPhonemes; that was wrong, and
#    this is where it was found.
#
# 2. -std=gnu17 SETS THE GLIBC FLOOR. GCC 15 defaults to C23, under which plain
#    sscanf and strtol bind to __isoc23_sscanf and __isoc23_strtol -- symbols
#    that appeared in GLIBC 2.38. The result runs on the build machine and tells
#    a user on Ubuntu 22.04 that GLIBC_2.38 was not found. With this flag the
#    floor is 2.33, under the 2.34 the Linux packer already asserts. Measured
#    both ways: 2.38 without, 2.33 with.
#
# 3. THE DISABLED FEATURES ARE WHY THIS COSTS TWO LIBRARIES INSTEAD OF FOUR.
#    Ubuntu's libespeak-ng needs libpcaudio and libsonic; they play audio and
#    time-stretch it, and we do neither with this library. Ours needs libm and
#    libc, nothing else. These flags are also, independently, exactly what
#    piper's own CMakeLists passes -- which is worth knowing, because it means
#    the parity below is not being bought with a different configuration.
# ---------------------------------------------------------------------------

set -euo pipefail

PIN=724808c5   # 1.52.0-229-g724808c5, the commit piper pins, 2026-Apr-06
out="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/espeak}"
src="$out/src"

for tool in git cmake make gcc; do
    command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }
done

if [[ ! -d "$src/.git" ]]; then
    echo ">>> cloning espeak-ng"
    git clone --filter=blob:none https://github.com/espeak-ng/espeak-ng.git "$src"
fi

git -C "$src" checkout -q "$PIN"
echo ">>> at $(git -C "$src" describe --tags)"

echo ">>> configuring"
cmake -S "$src" -B "$src/build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$out/install" \
    -DCMAKE_C_FLAGS="-std=gnu17" \
    -DBUILD_SHARED_LIBS=ON \
    -DUSE_ASYNC=OFF -DUSE_MBROLA=OFF -DUSE_LIBSONIC=OFF \
    -DUSE_LIBPCAUDIO=OFF -DUSE_KLATT=OFF -DUSE_SPEECHPLAYER=OFF \
    -DEXTRA_cmn=ON -DEXTRA_ru=ON >/dev/null

echo ">>> building"
cmake --build "$src/build" -j"$(nproc)" >/dev/null
cmake --install "$src/build" >/dev/null

lib="$(ls "$out"/install/lib/libespeak-ng.so.*.* | head -1)"
data="$out/install/share/espeak-ng-data"

# The three properties that make this the right library, asserted rather than
# assumed -- each fails on a user's machine and nowhere else.
echo ">>> checking what came out"

nm -D "$lib" | grep -q espeak_TextToPhonemesWithTerminator \
    || { echo "the terminator function is missing -- wrong revision?" >&2; exit 1; }
echo "    espeak_TextToPhonemesWithTerminator is exported"

needed="$(objdump -p "$lib" | awk '/NEEDED/ {print $2}' | sort | tr '\n' ' ')"
[[ "$needed" == "libc.so.6 libm.so.6 " ]] \
    || { echo "links more than libc and libm: $needed" >&2; exit 1; }
echo "    links libc and libm and nothing else"

floor="$(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -V | tail -1)"
[[ "$floor" < "GLIBC_2.35" ]] \
    || { echo "glibc floor is $floor, above the 2.34 the packer asserts" >&2; exit 1; }
echo "    glibc floor $floor"

echo
echo "export VST_ESPEAK_LIB=$lib"
echo "export VST_ESPEAK_DATA=$data"

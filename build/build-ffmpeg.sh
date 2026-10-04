#!/usr/bin/env bash
#
# Build the minimal ffmpeg the SNAP and the FLATPAK carry for MP3, AAC and FLAC
# export. Never the tarball or the AppImage: those use the host's ffmpeg.
#
#     bash build/build-ffmpeg.sh --out DIR --fetch [--work DIR]
#     bash build/build-ffmpeg.sh --out DIR --ffmpeg-src DIR --lame-src DIR [--work DIR]
#
#   --out DIR          where the result goes: DIR/ffmpeg plus its licence files
#   --fetch            clone ffmpeg and download LAME here, at the pins below,
#                      and verify both (snapcraft's part uses this)
#   --ffmpeg-src DIR   an ffmpeg checkout at FFMPEG_TAG (a Flatpak git source)
#   --lame-src DIR     an extracted LAME FFMPEG_LAME_VERSION (a Flatpak archive source)
#   --work DIR         build directory (default: a temporary one, removed after)
#   --jobs N           parallel make jobs (default: nproc)
#
# ---------------------------------------------------------------------------
# WHY THE STORE PACKAGES CARRY THEIR OWN. Export pipes the daemon's WAV into an
# ffmpeg found at runtime (FfmpegDetector). A snap or a Flatpak cannot see the
# host's, so without this MP3, AAC and FLAC are simply unavailable there. The
# tarball's design (use the distro's ffmpeg, kept patched by the distro, no
# encoder in a 75 MiB archive) is unchanged.
#
# WHAT IT IS. One recipe, used by all three store manifests (snapcraft.yaml.in,
# both Flatpak manifests), so the three cannot drift into three ffmpegs:
#
#   - ffmpeg at a pinned tag, verified by commit; LAME at a pinned tarball,
#     verified by SHA-256. Both pins live HERE; pack-flatpak.sh refuses a
#     manifest whose copies of them disagree.
#   - LGPL only: no --enable-gpl, no --enable-nonfree, so no libfdk_aac and no
#     x264. The native `aac` encoder does AAC.
#   - --disable-everything, then exactly what ExportRunner's argument list
#     (FfmpegTools.BuildArgs) uses: a WAV of 16-bit PCM on a pipe in; the
#     libmp3lame, aac and flac encoders; the mp3, ipod (.m4a) and flac muxers;
#     the file and pipe protocols; and the aresample and aformat filters the
#     command line inserts to convert sample formats (aac wants planar float).
#     No network, no devices, no hardware acceleration, no ffprobe or ffplay.
#   - ffmpeg's libraries and LAME linked statically into the one binary; libc
#     and libm stay dynamic, from the runtime it is built in and runs in.
#   - --disable-x86asm: no nasm needed in either build environment, and speech
#     at 44.1 kHz encodes far faster than real time without it.
#
# If BuildArgs ever gains an option this build lacks, export breaks inside the
# sandboxes and nowhere else. check-ffmpeg-bundle.sh runs those exact arguments
# against the built binary, and ExportTests holds that script's copy of them to
# BuildArgs's.
#
# The result is LGPL-2.1-or-later (ffmpeg) with LGPL-2.0-or-later LAME linked
# in. LICENSE-FFMPEG.txt, written below, names both versions and their sources,
# which is the source offer, as LICENSE-PHONEMIZER.txt is for espeak-ng.
# ---------------------------------------------------------------------------

set -euo pipefail

# THE PINS. Change them together with the copies in build/flatpak/*.yml.in;
# pack-flatpak.sh compares them.
FFMPEG_VERSION=8.1.3
FFMPEG_TAG=n8.1.3
FFMPEG_COMMIT=1041abdc962f4cc4f394aa8de9dc5236c0c3b9e7
FFMPEG_GIT=https://github.com/FFmpeg/FFmpeg.git
FFMPEG_LAME_VERSION=3.100
FFMPEG_LAME_URL=https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz
FFMPEG_LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e

# THE CONFIGURATION, one flag per line so a diff names what changed. Printed
# into BUILD-INFO beside the binary.
FFMPEG_CONFIGURE=(
    --disable-everything
    --disable-autodetect
    --disable-network
    --disable-doc
    --disable-debug
    --disable-ffplay
    --disable-ffprobe
    --disable-avdevice
    --disable-swscale
    --disable-x86asm
    --enable-static
    --disable-shared
    --enable-small
    --enable-libmp3lame
    --enable-protocol=pipe,file
    --enable-demuxer=wav
    --enable-decoder=pcm_s16le
    --enable-encoder=libmp3lame,aac,flac
    --enable-muxer=mp3,ipod,flac
    --enable-filter=aresample,aformat,anull
)
LAME_CONFIGURE=(
    --enable-static
    --disable-shared
    --disable-frontend
    --disable-decoder
    --disable-gtktest
)

# Sourced by pack-flatpak.sh for the pins alone.
[[ "${BASH_SOURCE[0]}" != "$0" ]] && return 0

out=""; fetch=0; ffmpeg_src=""; lame_src=""; work=""; jobs="$(nproc 2>/dev/null || echo 2)"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --out)        out="${2:-}"; shift 2 ;;
        --fetch)      fetch=1; shift ;;
        --ffmpeg-src) ffmpeg_src="${2:-}"; shift 2 ;;
        --lame-src)   lame_src="${2:-}"; shift 2 ;;
        --work)       work="${2:-}"; shift 2 ;;
        --jobs)       jobs="${2:-}"; shift 2 ;;
        -h|--help)    sed -n '3,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

[[ -n "$out" ]] || die "--out DIR is required"
if (( fetch )); then
    [[ -z "$ffmpeg_src$lame_src" ]] || die "--fetch or --ffmpeg-src/--lame-src, not both"
else
    [[ -d "$ffmpeg_src" && -d "$lame_src" ]] || die "give --fetch, or both --ffmpeg-src DIR and --lame-src DIR"
fi

cleanup_work=0
if [[ -z "$work" ]]; then work="$(mktemp -d)"; cleanup_work=1; fi
mkdir -p "$work" "$out"
work="$(cd "$work" && pwd)"; out="$(cd "$out" && pwd)"
(( cleanup_work )) && trap 'rm -rf "$work"' EXIT

# ------------------------------------------------------------------ sources
if (( fetch )); then
    step "Fetching ffmpeg $FFMPEG_TAG and LAME $FFMPEG_LAME_VERSION…"
    command -v git  >/dev/null 2>&1 || die "git is required for --fetch"
    command -v curl >/dev/null 2>&1 || die "curl is required for --fetch"
    rm -rf "$work/ffmpeg-src" "$work/lame-src"
    git -c advice.detachedHead=false clone -q --depth 1 --branch "$FFMPEG_TAG" "$FFMPEG_GIT" "$work/ffmpeg-src" || die "could not clone $FFMPEG_GIT at $FFMPEG_TAG"
    curl -fsSL --retry 3 -o "$work/lame.tar.gz" "$FFMPEG_LAME_URL" || die "could not download $FFMPEG_LAME_URL"
    got="$(sha256sum "$work/lame.tar.gz" | cut -d' ' -f1)"
    [[ "$got" == "$FFMPEG_LAME_SHA256" ]] || die "LAME's tarball hashes to $got, not the pinned $FFMPEG_LAME_SHA256"
    mkdir -p "$work/lame-src"
    tar -xzf "$work/lame.tar.gz" -C "$work/lame-src" --strip-components=1
    ffmpeg_src="$work/ffmpeg-src"; lame_src="$work/lame-src"
fi
ffmpeg_src="$(cd "$ffmpeg_src" && pwd)"; lame_src="$(cd "$lame_src" && pwd)"

# The checkouts are what the pins say. A git checkout is checked by commit
# (flatpak-builder's git source and --fetch both keep .git); without one, by
# the RELEASE file, which is the only version an exported tree carries.
if [[ -e "$ffmpeg_src/.git" ]] && command -v git >/dev/null 2>&1; then
    head_commit="$(git -c safe.directory='*' -C "$ffmpeg_src" rev-parse HEAD)"
    [[ "$head_commit" == "$FFMPEG_COMMIT" ]] || die "the ffmpeg checkout is at $head_commit, not $FFMPEG_TAG ($FFMPEG_COMMIT)"
fi
release="$(tr -d '[:space:]' < "$ffmpeg_src/RELEASE")"
[[ "$release" == "$FFMPEG_VERSION" ]] || die "the ffmpeg source says RELEASE $release, not $FFMPEG_VERSION"
lame_version="$(awk -F"'" '/^PACKAGE_VERSION=/ { v = $2 } END { print v }' "$lame_src/configure")"
[[ "$lame_version" == "$FFMPEG_LAME_VERSION" ]] || die "the LAME source is version '$lame_version', not $FFMPEG_LAME_VERSION"
info "ffmpeg $FFMPEG_VERSION ($FFMPEG_COMMIT), LAME $FFMPEG_LAME_VERSION"

# --------------------------------------------------------------------- LAME
step "Building LAME (static, encoder only)…"
rm -rf "$work/lame-build" "$work/prefix"
cp -a "$lame_src" "$work/lame-build"
# gnu17: LAME 3.100 predates C23, which newer GCCs default to.
( cd "$work/lame-build" \
  && CFLAGS="-O2 -std=gnu17 -fPIC" ./configure --prefix="$work/prefix" "${LAME_CONFIGURE[@]}" >"$work/lame-configure.log" 2>&1 \
  && make -j"$jobs" >"$work/lame-make.log" 2>&1 \
  && make install >>"$work/lame-make.log" 2>&1 ) \
  || { tail -40 "$work"/lame-*.log >&2; die "LAME did not build"; }
[[ -f "$work/prefix/lib/libmp3lame.a" ]] || die "LAME built but left no libmp3lame.a"

# ------------------------------------------------------------------- ffmpeg
step "Configuring ffmpeg (LGPL, export only)…"
rm -rf "$work/ffmpeg-build"
mkdir -p "$work/ffmpeg-build"
( cd "$work/ffmpeg-build" \
  && "$ffmpeg_src/configure" --prefix="$work/prefix" "${FFMPEG_CONFIGURE[@]}" \
         --extra-cflags="-I$work/prefix/include" --extra-ldflags="-L$work/prefix/lib" \
         >"$work/ffmpeg-configure.log" 2>&1 ) \
  || { tail -40 "$work/ffmpeg-configure.log" >&2; tail -40 "$work/ffmpeg-build/ffbuild/config.log" >&2 2>/dev/null; die "ffmpeg did not configure"; }

# LGPL, and nothing else, as configure itself records it.
license="$(awk -F'"' '/^#define FFMPEG_LICENSE / { v = $2 } END { print v }' "$work/ffmpeg-build/config.h")"
[[ "$license" == "LGPL version 2.1 or later" ]] || die "ffmpeg configured as '$license', not LGPL 2.1 or later"

step "Building ffmpeg…"
( cd "$work/ffmpeg-build" && make -j"$jobs" ffmpeg >"$work/ffmpeg-make.log" 2>&1 ) \
  || { tail -40 "$work/ffmpeg-make.log" >&2; die "ffmpeg did not build"; }

# ------------------------------------------------------------------ install
rm -rf "$out/ffmpeg" "$out"/COPYING.* "$out/LICENSE-FFMPEG.txt" "$out/BUILD-INFO"
install -m755 "$work/ffmpeg-build/ffmpeg" "$out/ffmpeg"
strip "$out/ffmpeg" 2>/dev/null || true
install -m644 "$ffmpeg_src/COPYING.LGPLv2.1" "$out/COPYING.LGPLv2.1"
install -m644 "$lame_src/COPYING"            "$out/COPYING.LAME"

{
    echo "ffmpeg      $FFMPEG_VERSION"
    echo "commit      $FFMPEG_COMMIT"
    echo "lame        $FFMPEG_LAME_VERSION"
    echo "lame-sha256 $FFMPEG_LAME_SHA256"
    echo "license     $license"
    echo "configure   ${FFMPEG_CONFIGURE[*]}"
    echo "lame-flags  ${LAME_CONFIGURE[*]}"
} > "$out/BUILD-INFO"

cat > "$out/LICENSE-FFMPEG.txt" <<EOF
ffmpeg and LAME — LGPL
======================

This package INCLUDES a minimal build of ffmpeg, in this folder, with the LAME
MP3 encoder linked into it. It is what turns the voice's audio into MP3, AAC and
FLAC files in the Export tab and in \`vst-ctl render --out\`; nothing else uses
it. It is built for this package only: the tarball and AppImage builds of
VibeSuperTonic use the ffmpeg your distribution installs instead.

  ffmpeg    $FFMPEG_VERSION
            commit $FFMPEG_COMMIT
            source $FFMPEG_GIT (tag $FFMPEG_TAG)
            terms  GNU Lesser General Public License v2.1 or later; the full
                   text is in COPYING.LGPLv2.1. Built WITHOUT --enable-gpl and
                   WITHOUT --enable-nonfree, so no GPL or non-free code is in it.

  LAME      $FFMPEG_LAME_VERSION
            source $FFMPEG_LAME_URL
            sha256 $FFMPEG_LAME_SHA256
            terms  GNU Library General Public License v2 or later; the full
                   text is in COPYING.LAME.

Both were built UNMODIFIED, with the configuration recorded in BUILD-INFO beside
this file, by build/build-ffmpeg.sh in VibeSuperTonic's repository:
https://github.com/Hananel-Hazan/VibeSuperTonic — that script is the complete
recipe, so the binary can be rebuilt, or relinked against a modified LAME, from
the two sources above.

THE SOURCE, which is the obligation this file discharges, can be obtained from
the upstream locations above, which name exactly the trees that were built, for
as long as this package is offered. If either is unreachable, open an issue at
https://github.com/Hananel-Hazan/VibeSuperTonic/issues and a copy will be
provided.
EOF

size="$(stat -c%s "$out/ffmpeg")"
step "Done."
info "$out/ffmpeg  ($size bytes)"

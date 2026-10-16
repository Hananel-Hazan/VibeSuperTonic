#!/usr/bin/env bash
#
# Check the ffmpeg a store package carries for export (build/build-ffmpeg.sh).
#
#     bash build/check-ffmpeg-bundle.sh DIR [VERSION]
#
# DIR is the folder holding the bundled `ffmpeg`, as installed in the package:
# $SNAP/ffmpeg in the snap, /app/lib/vibesupertonic/ffmpeg in the Flatpak.
# VERSION, when given, is the ffmpeg version it must report.
#
# SELF-CONTAINED ON PURPOSE: it sources nothing, so it can run INSIDE a sandbox
# by being fed on stdin, where the repository does not exist:
#
#     flatpak run --command=bash <id> -s -- /app/lib/vibesupertonic/ffmpeg 8.1.3 < build/check-ffmpeg-bundle.sh
#     snap run --shell vibesupertonic.ctl -c 'exec bash -s -- "$SNAP/ffmpeg" 8.1.3' < build/check-ffmpeg-bundle.sh
#
# Needs bash, coreutils (mktemp, od, stat) and nothing else, which both
# runtimes have. Exit 0 and a last line starting "ok:" when everything holds.
#
# WHAT EACH CHECK IS FOR. All of these fail silently in the product: the Export
# tab offers WAV only, or offers MP3 and the encode fails, and only on the store
# packages, which nobody in this repository runs day to day.
#
#   - the binary and its licence files are where the code and the LGPL say
#   - it runs, reports VERSION, and was configured without GPL or non-free code
#   - `ffmpeg -encoders` lists libmp3lame, aac and flac, read the way
#     FfmpegTools.ParseEncoders reads it (a seven-column flag field, then the name)
#   - a 0.1 s silent WAV with the daemon's streaming header (both sizes
#     0xFFFFFFFF), arriving on a PIPE, encodes to a non-empty file in each format
#     with the right magic, using EXACTLY FfmpegTools.BuildArgs's arguments. The
#     args_* lines below are compared to BuildArgs by ExportTests, so a new
#     option there that this minimal build lacks fails a unit test instead of
#     every MP3 export in a snap.
#
# Sabotage: build/ffmpeg-bundle-sabotage.sh.

set -euo pipefail

dir="${1:-}"
want_version="${2:-}"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# FfmpegTools.BuildArgs, without the output path. Keep in step with it; the
# test Bundled_ffmpeg_check_uses_the_exact_export_arguments compares them.
args_mp3="-hide_banner -loglevel error -nostats -y -f wav -i pipe:0 -vn -map_metadata -1 -c:a libmp3lame -b:a 128k -f mp3"
args_aac="-hide_banner -loglevel error -nostats -y -f wav -i pipe:0 -vn -map_metadata -1 -c:a aac -b:a 128k -movflags +faststart -f ipod"
args_flac="-hide_banner -loglevel error -nostats -y -f wav -i pipe:0 -vn -map_metadata -1 -c:a flac -f flac"

[[ -n "$dir" ]] || fail "usage: check-ffmpeg-bundle.sh DIR [VERSION]"
ff="$dir/ffmpeg"

# ---------------------------------------------------------------- the files
[[ -f "$ff" && -x "$ff" ]] || fail "no executable ffmpeg at $ff"
for f in LICENSE-FFMPEG.txt COPYING.LGPLv2.1 COPYING.LAME BUILD-INFO; do
    [[ -s "$dir/$f" ]] || fail "$dir/$f is missing or empty. The LGPL travels with the binary."
done

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# ----------------------------------------------------------------- it runs
"$ff" -hide_banner -version > "$tmp/version.txt" 2>&1 || fail "$ff -version failed: $(cat "$tmp/version.txt")"
first="$(awk 'NR == 1 { print }' "$tmp/version.txt")"
[[ "$first" == "ffmpeg version "* ]] || fail "$ff -version printed '$first'"
if [[ -n "$want_version" ]]; then
    [[ "$first" == "ffmpeg version n$want_version "* || "$first" == "ffmpeg version $want_version "* ]] \
        || fail "the bundled ffmpeg is '$first', not $want_version. build-ffmpeg.sh's pin and the package disagree."
fi
config="$(awk '/^configuration:/ { print }' "$tmp/version.txt")"
[[ -n "$config" ]] || fail "$ff -version printed no configuration line"
for bad in --enable-gpl --enable-nonfree --enable-version3; do
    [[ "$config" != *"$bad"* ]] || fail "the bundled ffmpeg was configured with $bad. It must be LGPL-2.1-or-later only."
done

# ------------------------------------------------------------- the encoders
"$ff" -hide_banner -encoders > "$tmp/encoders.txt" 2>/dev/null || fail "$ff -encoders failed"
# As ParseEncoders reads it: ' A.....  name'. An awk that reads to the end.
encoders=" $(awk 'substr($0, 1, 2) == " A" && substr($0, 8, 1) == " " { print $2 }' "$tmp/encoders.txt" | tr '\n' ' ')"
for e in libmp3lame aac flac; do
    [[ "$encoders" == *" $e "* ]] || fail "the bundled ffmpeg has no '$e' encoder; it lists:$encoders"
done

# ---------------------------------------------------------------- an encode
# The daemon's WAV, as RenderWav.WriteHeader writes it.
le() { local v=$1 n=$2 i; for ((i = 0; i < n; i++)); do printf "\\x$(printf %02x $(( (v >> (8 * i)) & 255 )))"; done; }
for rate in 44100 22050; do
    samples=$(( rate / 10 ))
    {
        printf 'RIFF'; le 4294967295 4; printf 'WAVEfmt '; le 16 4; le 1 2; le 1 2
        le "$rate" 4; le $(( rate * 2 )) 4; le 2 2; le 16 2; printf 'data'; le 4294967295 4
        head -c $(( samples * 2 )) /dev/zero
    } > "$tmp/in-$rate.wav"
    (( $(stat -c%s "$tmp/in-$rate.wav") == 44 + samples * 2 )) || fail "could not write the test WAV"

    for fmt in mp3 aac flac; do
        out="$tmp/out-$rate.$fmt"
        var="args_$fmt"
        # shellcheck disable=SC2086  # the args are words, as BuildArgs's list is
        # stdin a PIPE, as in ExportRunner, via process substitution so the
        # producer's status cannot end this script (no pipeline at all).
        if ! "$ff" ${!var} "$out" < <(cat "$tmp/in-$rate.wav") > "$tmp/encode.log" 2>&1; then
            fail "encoding a ${rate} Hz WAV to $fmt failed: $(cat "$tmp/encode.log")"
        fi
        [[ -s "$out" ]] || fail "encoding a ${rate} Hz WAV to $fmt produced an empty file"
        magic="$(od -An -c -N12 "$out" | tr -d ' \n')"
        case "$fmt" in
            mp3)  [[ "$magic" == 377* || "$magic" == ID3* ]] || fail "the $fmt output does not start like an MP3: $magic" ;;
            aac)  [[ "$magic" == *ftyp* ]] || fail "the $fmt output is not an MP4 (.m4a) file: $magic" ;;
            flac) [[ "$magic" == fLaC* ]] || fail "the $fmt output is not a FLAC file: $magic" ;;
        esac
    done
done

printf 'ok: %s; encoders libmp3lame, aac, flac; a piped 0.1 s WAV at 44.1 and 22.05 kHz encodes to MP3, AAC (.m4a) and FLAC\n' "$first"

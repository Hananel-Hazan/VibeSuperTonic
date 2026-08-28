#!/usr/bin/env bash
#
# Build the libespeak-ng that the Piper engine phonemises with, and compose the
# espeak/ payload the release archive ships.
#
#     bash build/build-espeak.sh [-o DIR] [--full-data] [--clean]
#
# Roughly two minutes on a warm machine, most of it compiling dictionaries. Run
# it before build/pack-tar.sh; the packer refuses to compose an archive whose
# espeak payload is missing or built from a different revision.
#
# Moved out of spike/piper-phonemes/ on 2026-08-27 (P5). It was already a script
# rather than a command line — P1 did that — but it lived where a release run
# does not look, and the GPL obligation is to offer the exact source WE built,
# which a script nobody runs at release time cannot discharge.
#
# ---------------------------------------------------------------------------
# FIVE THINGS HERE ARE LOAD-BEARING AND NONE OF THEM ARE OBVIOUS.
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
#    P1's parity is not being bought with a different configuration.
#
# 4. THE DICTIONARIES ARE PRUNED, AND ESPEAK ITSELF DECIDES WHICH TO KEEP.
#    Upstream ships 117 of them, 25 MB, for languages this catalog has no voice
#    for. Keeping only what the catalog needs is 2.6 MB. What it must NOT be is
#    a mapping table: the catalog's language codes, the espeak voice names and
#    the dictionary filenames are three different things that disagree in ways
#    no rule predicts --
#
#        no_NO   ->  voice nb        ->  no_dict
#        es_MX   ->  voice es-419    ->  es_dict
#        pt_BR   ->  voice pt-br     ->  pt_dict
#
#    -- so the set is discovered by ASKING espeak. Run a voice against a data
#    directory with no dictionaries in it and it names the file it wanted, on
#    stderr, by path. Copy that, run again, repeat. No table to drift.
#
# 5. A MISSING DICTIONARY EXITS 0. That is the whole reason step 4 exists and
#    the reason the check after it is behavioural. espeak-ng prints "Can't read
#    dictionary file" to stderr, returns success, and produces NO phonemes --
#    which reaches the product as a voice that was downloaded, verified,
#    installed, selected, and then says nothing. So a voice passes here only if
#    it exits 0, writes something to stdout, and writes NOTHING to stderr.
# ---------------------------------------------------------------------------

set -euo pipefail

# The commit piper pins, 1.52.0-229-g724808c5, 2026-Apr-06. pack-tar.sh reads
# this same line out of this file and compares it with what the payload records,
# so a stale build cannot ship beside a moved pin.
PIN=724808c5

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/build/espeak-out"
full_data=0
clean=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -o|--out)    out="${2:?}"; shift 2 ;;
        --full-data) full_data=1; shift ;;
        --clean)     clean=1; shift ;;
        -h|--help)
            sed -n '3,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 64 ;;
    esac
done

step() { printf '\n\033[36m>>> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die()  { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }

for tool in git cmake make gcc nm objdump strip python3; do
    command -v "$tool" >/dev/null || die "$tool is required"
done

mkdir -p "$out"
out="$(cd "$out" && pwd)"
src="$out/src"
prefix="$out/install"
payload="$out/espeak"

(( clean )) && rm -rf "$prefix" "$payload" "$src/build"

# ------------------------------------------------------------------ source
if [[ ! -d "$src/.git" ]]; then
    step "Cloning espeak-ng"
    git clone --filter=blob:none https://github.com/espeak-ng/espeak-ng.git "$src"
fi

git -C "$src" checkout -q "$PIN" 2>/dev/null || {
    git -C "$src" fetch -q origin
    git -C "$src" checkout -q "$PIN"
}
described="$(git -C "$src" describe --tags)"
full_sha="$(git -C "$src" rev-parse HEAD)"
info "at $described ($full_sha)"

# The tree must be exactly upstream's. The GPL offer says "the source we built",
# and a stray local edit would make that sentence false — quietly, since a dirty
# working tree builds and ships like a clean one.
[[ -z "$(git -C "$src" status --porcelain --untracked-files=no)" ]] \
    || die "the espeak-ng checkout at $src has local modifications.
       The GPL offer names an upstream commit; a patched build makes that a lie.
       Commit the patch upstream, or carry it here deliberately and say so."

# ------------------------------------------------------------------ build
step "Configuring and building"
#
# THE $ORIGIN RPATH IS LOAD-BEARING, AND ITS ABSENCE WAS SILENT.
# The binary links the SONAME libespeak-ng.so.1, and naming the shipped library
# that was necessary but NOT sufficient: the loader does not search a binary's
# own directory unless told to. Without this, cmake bakes in the RUNPATH of the
# BUILD MACHINE's install prefix — a path that exists here and nowhere else — so
# the shipped binary resolved the library through the ordinary loader path. On a
# machine with espeak-ng installed that silently loads the DISTRO's library; on
# one without, the binary does not start at all:
#
#   error while loading shared libraries: libespeak-ng.so.1: cannot open ...
#
# Which is every machine bundling exists to serve. Verified 2026-08-28 in a bare
# ubuntu:22.04 with no libespeak-ng present. See docs/SPEECHD-PLAN.md trap 18.
#
# IT IS A LINKER FLAG RATHER THAN -DCMAKE_INSTALL_RPATH BECAUSE UPSTREAM WINS
# THAT ARGUMENT: espeak-ng's own src/CMakeLists.txt sets the target property
# INSTALL_RPATH to "${CMAKE_INSTALL_PREFIX}/lib", and a target property beats the
# cache variable — which is why the first attempt at this changed nothing at all.
# Patching upstream's CMakeLists instead would be worse than it looks: the GPL
# source offer below is `git archive HEAD`, so a working-tree patch would ship a
# tarball that does not correspond to the binary beside it. The flag adds $ORIGIN
# ahead of the build machine's path, the loader tries it first, and the stale
# absolute entry is simply a directory that does not exist on a user's machine.
cmake -S "$src" -B "$src/build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$prefix" \
    -DCMAKE_EXE_LINKER_FLAGS='-Wl,-rpath,$ORIGIN' \
    -DCMAKE_C_FLAGS="-std=gnu17" \
    -DBUILD_SHARED_LIBS=ON \
    -DUSE_ASYNC=OFF -DUSE_MBROLA=OFF -DUSE_LIBSONIC=OFF \
    -DUSE_LIBPCAUDIO=OFF -DUSE_KLATT=OFF -DUSE_SPEECHPLAYER=OFF \
    -DEXTRA_cmn=ON -DEXTRA_ru=ON >/dev/null

cmake --build "$src/build" -j"$(nproc)" >/dev/null
rm -rf "$prefix"
cmake --install "$src/build" >/dev/null

lib="$(ls "$prefix"/lib/libespeak-ng.so.*.* | head -1)"
espeak_bin="$prefix/bin/espeak-ng"
full_dir="$prefix/share/espeak-ng-data"
[[ -x "$espeak_bin" ]] || die "no espeak-ng binary at $espeak_bin"
[[ -d "$full_dir" ]]   || die "no espeak-ng-data at $full_dir"

# ------------------------------------------------------- what came out of it
#
# The three properties that make this the right library, asserted rather than
# assumed — each fails on a user's machine and nowhere else.
step "Checking the library"

nm -D "$lib" | grep -q espeak_TextToPhonemesWithTerminator \
    || die "the terminator function is missing — wrong revision?"
info "espeak_TextToPhonemesWithTerminator is exported"

needed="$(objdump -p "$lib" | awk '/NEEDED/ {print $2}' | sort | tr '\n' ' ')"
[[ "$needed" == "libc.so.6 libm.so.6 " ]] \
    || die "links more than libc and libm: $needed"
info "links libc and libm and nothing else"

floor="$(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -uV | tail -1)"
[[ "$(printf '2.34\n%s\n' "$floor" | sort -V | tail -1)" == "2.34" ]] \
    || die "glibc floor is $floor, above the 2.34 the packer asserts"
info "glibc floor $floor"

# ----------------------------------------------------------------- payload
step "Composing the espeak/ payload"
rm -rf "$payload"
mkdir -p "$payload/espeak-ng-data"

# ONE REAL FILE, NAMED AS ITS SONAME, AND NO SYMLINKS.
#
# Two requirements that pull in opposite directions, met by picking the right
# single name rather than by shipping a link:
#
#   * No symlink. An archive carrying both a link and its target ships something
#     a hand-untar, a `cp` without -a, or a zip round-trip can turn into a
#     dangling link — and a dangling libespeak-ng is a voice that says nothing.
#   * The name must be the SONAME. The P/Invoke resolves an explicit path and
#     never consults it, so this looked free — but espeak-ng's own BINARY links
#     against `libespeak-ng.so.1`, and that binary is what renders the echo
#     voice (docs/SPEECHD-PLAN.md). Shipping the versioned name only would give
#     us a 31 KB executable that cannot find the library sitting beside it.
#
# So the real file is called what the linker asks for. The true version lives in
# BUILD-INFO, which is where the packer reads it from anyway.
soname="$(objdump -p "$lib" | awk '/SONAME/ {print $2; exit}')"
[[ -n "$soname" ]] || die "the built library declares no SONAME"
install -m 644 "$lib" "$payload/$soname"
strip --strip-unneeded "$payload/$soname"

# The renderer for short utterances — 31 KB, and it links nothing but the
# library above and libc. Measured 2026-08-27 at 3.3 ms to first audio for a
# single character, against 383 ms for the neural path and 6.6 ms for the
# distro's espeak-ng. It is why the archive needs no espeak-ng package.
install -m 755 "$espeak_bin" "$payload/espeak-ng"
strip --strip-unneeded "$payload/espeak-ng"

# The base: everything that is not a dictionary. phondata, phontab, phonindex,
# intonations, lang/ and voices/ — about 2 MB, and every voice needs all of it.
( cd "$full_dir" && find . -type f ! -name '*_dict' -print0 | \
    tar --null -cf - --files-from=- ) | ( cd "$payload/espeak-ng-data" && tar -xf - )

if (( full_data )); then
    step "Keeping every dictionary (--full-data)"
    cp -a "$full_dir"/*_dict "$payload/espeak-ng-data/"
else
    # --- ask espeak which dictionaries the catalog actually needs -----------
    #
    # See note 4 in the header. The catalog names an espeak voice per entry;
    # espeak names the dictionary file per voice, by failing to open it. Three
    # rounds because a voice can want more than one — a language whose rules
    # defer to another's — and because the second failure only appears once the
    # first is satisfied.
    catalog="$root/piper-voices.json"
    [[ -f "$catalog" ]] || die "piper-voices.json is missing from $root.
       The dictionary set is derived from the voices the catalog offers.
       Generate it with: python3 build/gen-piper-catalog.py"

    # TWO SOURCES, and the second one is not decoration.
    #
    #   The catalog  — every voice a user can install. Obvious, and necessary.
    #   P1's corpus  — every voice PARITY WAS MEASURED ON. Not the same set: the
    #                  corpus covers it_IT, and the catalog does not offer an
    #                  Italian voice because no it_IT MODEL_CARD states a licence.
    #
    # Without the second, spike/piper-phonemes run against the shipped payload
    # reports "NOT PARITY — 5 of 327 sentences differ" — five Italian sentences
    # phonemising to nothing because it_dict was pruned. The phonemiser is fine;
    # the data directory is not the one parity was measured against. That is a
    # false alarm on the project's own go/no-go evidence, and it costs 95 KB to
    # not have. Measured 2026-08-27, which is the day it happened.
    mapfile -t voices < <(python3 -c '
import json, os, sys
want = set()
c = json.load(open(sys.argv[1]))
cat = {v.get("espeakVoice", "") for v in c["voices"]} - {""}
if not cat:
    sys.exit("the catalog names no espeakVoice; regenerate it with build/gen-piper-catalog.py")
want |= cat
if os.path.exists(sys.argv[2]):
    corpus = json.load(open(sys.argv[2]))
    want |= {e["espeak_voice"] for e in corpus.get("entries", []) if e.get("espeak_voice")}
print("\n".join(sorted(want)))
' "$catalog" "$root/spike/piper-phonemes/corpus.json")

    # mapfile reports ITS OWN success, not the subshell's, so a python that died
    # leaves an empty array — and every loop below then does nothing: no
    # dictionary is copied, and the behavioural check that would have caught it
    # iterates over nothing and passes. A payload with no dictionaries at all,
    # asserted clean.
    (( ${#voices[@]} > 0 )) || die "no espeak voices came back from $catalog.
       Regenerate it: python3 build/gen-piper-catalog.py"
    info "${#voices[@]} espeak voices — the catalog's, plus the ones P1 measured"

    # A sentence rather than a word: dictionaries are consulted per word, and a
    # single token can be answered by letter rules while the rest of a clause
    # cannot. Digits and punctuation are in deliberately — number and clause
    # handling read the dictionary too.
    probe="1 2 3, this is a test."

    for round in 1 2 3; do
        wanted=()
        for v in "${voices[@]}"; do
            err="$("$espeak_bin" --path="$payload" -v "$v" -q --ipa -- "$probe" 2>&1 >/dev/null || true)"
            while IFS= read -r line; do
                [[ "$line" == *"Can't read dictionary file:"* ]] || continue
                f="${line#*\'}"; f="${f%\'*}"
                wanted+=("$(basename "$f")")
            done <<< "$err"
        done
        (( ${#wanted[@]} )) || break
        for d in $(printf '%s\n' "${wanted[@]}" | sort -u); do
            [[ -f "$payload/espeak-ng-data/$d" ]] && continue
            [[ -f "$full_dir/$d" ]] || die "espeak wants $d and the build produced no such file"
            cp "$full_dir/$d" "$payload/espeak-ng-data/$d"
            info "round $round: + $d"
        done
    done
fi

kept=$(ls "$payload/espeak-ng-data"/*_dict 2>/dev/null | wc -l)
info "$kept dictionaries of $(ls "$full_dir"/*_dict | wc -l)"

# --- the behavioural check, which is the one that matters -------------------
#
# Note 5: a missing dictionary exits 0 and says nothing to the caller. So every
# voice the catalog offers is run against the payload as composed, and all three
# of exit code, stdout and stderr have to be right.
step "Speaking one probe sentence per catalog voice"
if (( full_data )); then
    mapfile -t voices < <(python3 -c '
import json, os, sys
c = json.load(open(sys.argv[1]))
want = {v.get("espeakVoice", "") for v in c["voices"]} - {""}
if os.path.exists(sys.argv[2]):
    corpus = json.load(open(sys.argv[2]))
    want |= {e["espeak_voice"] for e in corpus.get("entries", []) if e.get("espeak_voice")}
print("\n".join(sorted(want)))
' "$root/piper-voices.json" "$root/spike/piper-phonemes/corpus.json")
    (( ${#voices[@]} > 0 )) || die "no espeak voices came back from the catalog"
fi
probe="${probe:-1 2 3, this is a test.}"
failed=0
for v in "${voices[@]}"; do
    err_file="$out/.probe.err"
    ipa="$("$espeak_bin" --path="$payload" -v "$v" -q --ipa -- "$probe" 2>"$err_file")" || {
        echo "  $v: exited $?" >&2; failed=1; continue
    }
    err="$(cat "$err_file")"
    if [[ -n "$err" ]]; then
        echo "  $v: $err" >&2; failed=1; continue
    fi
    if [[ -z "${ipa//[[:space:]]/}" ]]; then
        echo "  $v: produced no phonemes" >&2; failed=1; continue
    fi
done
rm -f "$out/.probe.err"
(( failed == 0 )) || die "one or more catalog voices cannot be phonemised by this payload.
       A voice that phonemises to nothing is a download, a licence acceptance and
       an install that ends in silence."
info "all ${#voices[@]} voices phonemise cleanly"

# ------------------------------------------------------------- GPL offer
#
# The obligation is source availability for the library we DISTRIBUTE, and the
# only honest way to say "the exact source we built" is to have built it from a
# tree we can hand over. This is that tree, archived at the commit, unmodified —
# checked above — so it is byte-identical to upstream's and can be offered from
# either place. Uploaded as a release asset beside the tarball.
step "Archiving the source for the GPL offer"
src_tar="$out/espeak-ng-$PIN-src.tar.gz"
rm -f "$src_tar"
git -C "$src" archive --format=tar --prefix="espeak-ng-$PIN/" HEAD | gzip -9 > "$src_tar"
info "$(du -h "$src_tar" | cut -f1) → ${src_tar#"$root/"}"

install -m 644 "$src/COPYING" "$payload/COPYING"

# ------------------------------------------------------------- BUILD-INFO
#
# What the packer reads. A payload is a directory of files with no version in
# them, and the failure it prevents is the quiet one: a pin that moves in this
# script while an espeak-out/ from last month still sits on disk, so the archive
# ships a library from a revision nobody chose.
cat > "$payload/BUILD-INFO" <<EOF
espeak-ng, built for VibeSuperTonic by build/build-espeak.sh
commit      $full_sha
described   $described
pin         $PIN
library     $soname
data        $kept dictionaries$( (( full_data )) && echo " (--full-data)" )
configure   -DCMAKE_C_FLAGS=-std=gnu17 -DBUILD_SHARED_LIBS=ON -DUSE_ASYNC=OFF
            -DUSE_MBROLA=OFF -DUSE_LIBSONIC=OFF -DUSE_LIBPCAUDIO=OFF
            -DUSE_KLATT=OFF -DUSE_SPEECHPLAYER=OFF -DEXTRA_cmn=ON -DEXTRA_ru=ON
source      https://github.com/espeak-ng/espeak-ng/tree/$full_sha
licence     GPL-3.0-or-later — see COPYING beside this file
EOF

step "Done"
info "payload   $(du -sh "$payload" | cut -f1)  ${payload#"$root/"}"
info "library   $(numfmt --to=iec "$(stat -c%s "$payload/$soname")")"
info "data      $(du -sh "$payload/espeak-ng-data" | cut -f1)"
echo
echo "The packer picks this up automatically. To use it by hand:"
echo "  export VST_ESPEAK_LIB=$payload/$soname"
echo "  export VST_ESPEAK_DATA=$payload/espeak-ng-data"

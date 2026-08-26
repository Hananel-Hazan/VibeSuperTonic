#!/usr/bin/env python3
"""Check a generated piper-voices.json before it ships.

    python3 build/check-piper-catalog.py piper-voices.json

Called by build/pack-tar.sh as assertion 3b, and runnable by hand. A separate
file rather than a heredoc inside the packer because the packer is bash and this
is the third language in it; a script can also be run on its own when the
catalog is regenerated, which is exactly when these need checking.

WHAT IT IS FOR. Every failure here produces a catalog that parses perfectly, the
Voices tab renders happily, and the product is wrong:

  * A voice with no sha256 downloads UNVERIFIED. ModelDownloader treats an empty
    hash as "nothing to check" — deliberately, for the Supertonic manifest's
    small JSON blobs — so this is the one field whose absence is silent all the
    way to a corrupt model on a user's disk.
  * A voice with no https url cannot download at all, and the failure arrives
    after the user has accepted a licence and clicked Download.
  * A voice with no licence text puts an empty gate in front of a person. The
    gate is the whole of trap 7's answer, and an empty one is worse than none
    because it looks like consent was obtained.
  * A duplicate id means two catalog rows share one directory, and whichever was
    installed second wins with the first one's name still on screen.

The size ceiling is in the packer rather than here, because it is a fact about
the archive rather than about the catalog.
"""
import json
import re
import sys

HEX64 = re.compile(r"^[0-9a-f]{64}$")


def main():
    if len(sys.argv) != 2:
        sys.stderr.write(__doc__)
        return 2

    path = sys.argv[1]
    try:
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
    except Exception as e:                                       # noqa: BLE001
        sys.stderr.write(f"{path} is not readable JSON: {e}\n")
        return 1

    voices = doc.get("voices") or []
    problems = []

    if not voices:
        problems.append("the catalog lists no voices")

    seen = {}
    for v in voices:
        vid = v.get("id") or "(no id)"

        if vid in seen:
            problems.append(f"{vid}: appears twice; two rows would share one directory")
        seen[vid] = True

        files = v.get("files") or []
        if len(files) != 2:
            problems.append(f"{vid}: has {len(files)} files, expected the .onnx and its .onnx.json")

        for f in files:
            name = f.get("name") or "(no name)"

            if not HEX64.match((f.get("sha256") or "").lower()):
                problems.append(f"{vid}/{name}: no sha256 — it would download unverified")

            if not (f.get("url") or "").startswith("https://"):
                problems.append(f"{vid}/{name}: no https url")

            if not isinstance(f.get("bytes"), int) or f["bytes"] <= 0:
                problems.append(f"{vid}/{name}: no size")

            # The id names the directory AND the filename stem. A mismatch
            # installs a voice where the store does not look, and the download
            # reports success while doing it.
            if not name.startswith(vid + "."):
                problems.append(f"{vid}/{name}: filename does not belong to this voice id")

        licence = v.get("licence") or {}
        if not (licence.get("name") or "").strip():
            problems.append(f"{vid}: states no licence, and the download gate has nothing to show")

        speakers = v.get("speakers")
        names = v.get("speakerNames") or []
        if isinstance(speakers, int) and speakers > 1 and len(names) != speakers:
            problems.append(
                f"{vid}: claims {speakers} speakers but names {len(names)}; "
                "the picker indexes that list by sid")

    if problems:
        sys.stderr.write(f"{path}: {len(problems)} problem(s)\n")
        for p in problems:
            sys.stderr.write(f"  {p}\n")
        return 1

    source = doc.get("source") or {}
    print(f"catalog: {len(voices)} voices pinned to "
          f"{source.get('repo', '?')}@{source.get('revision', '?')}, "
          "all hashed, all stating a licence")
    return 0


if __name__ == "__main__":
    sys.exit(main())

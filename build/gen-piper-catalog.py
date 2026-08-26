#!/usr/bin/env python3
"""Generate piper-voices.json — the curated, hash-pinned Piper voice catalog.

    python3 build/gen-piper-catalog.py [--revision v1.0.0] [--policy C] [-o FILE]

Why a generated file rather than the live upstream index, and why curated rather
than all 142: docs/PIPER-PLAN.md decided it on 2026-08-24. Three reasons, and the
third is the one that matters. 142 voices "is not a dropdown". Integrity would
depend on an index we do not control. And trap 7 requires reading each voice's
MODEL_CARD before it goes in a catalog, which is possible for thirty-five and
theatre for a thousand.

WHAT THIS COSTS TO RUN, and it is the reason the script exists in this shape:
about 400 KB of downloads, not 2.5 GB. The .onnx weights are git-LFS objects, and
a git-LFS pointer's oid IS the sha256 of the content — verified against a real
download on 2026-08-25, byte for byte — so the Hugging Face tree API hands over
every weight hash without transferring a single weight. Only the ~5 KB .onnx.json
configs and the ~300 byte MODEL_CARDs are actually fetched, because they are
plain git blobs with no LFS hash and there is no way to pin them without reading
them.

THE TWO LICENCE AXES ARE NOT THE SAME AXIS (trap 7). The engine is
GPL-3.0-or-later. The voices are separate, vary per voice, and this script's
entire selection rule is built on the licence line in each MODEL_CARD:

  * A voice whose MODEL_CARD cannot state a licence — "See URL", "Unknown",
    "See LICENSE file", or no line at all — IS NOT ELIGIBLE, under any policy.
    33 of the 142 are in this state. That is what costs the catalog zh_CN, it_IT,
    ar_JO, is_IS, ka_GE, ml_IN and sw_CD entirely, and excluding them is the
    whole point of curating: a catalog we cannot state the terms of is one we
    cannot put a licence gate in front of, and the gate is not optional.

  * Everything else is classified and carried through to the catalog, because
    the Voices tab shows each voice's licence BEFORE its bytes arrive and gates
    the download on it. A NonCommercial voice has to say so at that moment.

SELECTION, once eligibility is settled:

  1. One voice NAME per language, chosen by licence class first (a CC0 voice
     beats a NonCommercial one) then by the best tier it offers.
  2. Then EVERY TIER of that name goes in, not just the best. The default is the
     highest tier available, but 114 MB against 20 MB is a choice a person on a
     metered connection is entitled to make, so the smaller tiers stay one click
     away with their size and rate shown.

Re-run it when the pinned revision moves. It is deterministic apart from the
generated-on date: same revision and same policy produce the same file.
"""
import argparse, collections, concurrent.futures, hashlib, json, re, sys, urllib.parse, urllib.request

REPO = "rhasspy/piper-voices"
UA = {"User-Agent": "VibeSuperTonic-catalog-generator/1.0"}

# Licence classes, ordered best-first. The number is the preference used to
# choose between two voices for the same language; the name is what the Voices
# tab shows and what the download gate makes the user accept.
LICENCE_ORDER = ["public", "by", "apache", "by-sa", "agpl", "nc"]

POLICIES = {
    # Policy letters are the ones docs/PIPER-PLAN.md put to the user on
    # 2026-08-25. C was chosen: 35 voices, 35 languages, NonCommercial terms
    # shown per voice on the gate.
    "A": {"public", "by", "apache"},
    "B": {"public", "by", "apache", "by-sa"},
    "C": {"public", "by", "apache", "by-sa", "agpl", "nc"},
}

TIER_ORDER = {"high": 0, "medium": 1, "low": 2, "x_low": 3}


def classify(line):
    """Map a MODEL_CARD 'License:' line onto a class, or None when it says nothing.

    Deliberately conservative: anything this does not recognise is unresolved
    rather than guessed, because a wrong guess here is a licence claim we make
    on a user's behalf.
    """
    s = (line or "").lower().strip().rstrip(".")
    if not s:
        return None
    # "See URL" and friends are not licences. Neither is the Blizzard 2013
    # lessac licence, which is a page of bespoke terms behind a link.
    if s in ("see url", "unknown", "see license file", "see the url", "n/a", "none"):
        return None
    if "blizzard" in s:
        return None
    words = re.split(r"[-\s/]+", s)
    if "nc" in words or "by-nc" in s or "noncommercial" in s or "non commercial" in s:
        return "nc"
    if "cc0" in s or "public domain" in s or "unlicense" in s:
        return "public"
    if "agpl" in s:
        return "agpl"
    if "apache" in s:
        return "apache"
    if "sa" in words or "sharealike" in s:
        return "by-sa"
    if "by" in words or "/licenses/by/" in s or s == "cc-by" or "attribution" in s:
        return "by"
    return None


def fetch(url, timeout=90):
    req = urllib.request.Request(urllib.parse.quote(url, safe=":/?&=%"), headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def raw_url(revision, path):
    """A download URL, percent-encoded.

    Escaped here rather than left to the client because one voice id is
    `pt_PT-tugão-medium` and the path carries the ã raw. Every HTTP client has
    SOME rule for that — .NET normalises it, python's urllib refuses it outright
    — and a pinned catalog should not be the place where those rules differ.
    """
    return (f"https://huggingface.co/{REPO}/resolve/{revision}/"
            + urllib.parse.quote(path))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--revision", default="v1.0.0",
                    help="upstream tag or commit to pin (default: v1.0.0)")
    ap.add_argument("--policy", default="C", choices=sorted(POLICIES),
                    help="which licence classes are eligible (default: C)")
    ap.add_argument("--generated", default=None,
                    help="date stamp to record; defaults to today (UTC)")
    ap.add_argument("-o", "--out", default="piper-voices.json")
    args = ap.parse_args()

    allowed = POLICIES[args.policy]
    generated = args.generated
    if generated is None:
        import datetime
        generated = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")

    sys.stderr.write(f"index: {REPO}@{args.revision}\n")
    index = json.loads(fetch(raw_url(args.revision, "voices.json")))
    sys.stderr.write(f"  {len(index)} voices, "
                     f"{len({v['language']['code'] for v in index.values()})} languages\n")

    # --- eligibility: read every MODEL_CARD ---------------------------------
    cards = {}

    def card_of(key):
        paths = [p for p in index[key]["files"] if p.endswith("MODEL_CARD")]
        if not paths:
            return key, None
        try:
            return key, fetch(raw_url(args.revision, paths[0])).decode("utf-8", "replace")
        except Exception as e:                                   # noqa: BLE001
            sys.stderr.write(f"  MODEL_CARD failed for {key}: {e}\n")
            return key, None

    with concurrent.futures.ThreadPoolExecutor(12) as ex:
        for key, text in ex.map(card_of, index):
            cards[key] = text

    licences = {}
    for key, text in cards.items():
        lic = url = None
        if text:
            m = re.search(r"^\s*\*\s*License:\s*(.+)$", text, re.M | re.I)
            lic = m.group(1).strip() if m else None
            m = re.search(r"^\s*\*\s*URL:\s*(.+)$", text, re.M | re.I)
            url = m.group(1).strip() if m else None
        licences[key] = (lic, url, classify(lic))

    eligible = {k for k, (_, _, c) in licences.items() if c in allowed}
    unresolved = sum(1 for _, _, c in licences.values() if c is None)
    sys.stderr.write(f"  {unresolved} voices state no usable licence and are excluded\n")
    sys.stderr.write(f"  {len(eligible)} eligible under policy {args.policy} "
                     f"({'/'.join(sorted(allowed))})\n")

    # --- selection: one NAME per language, then every tier of that name -----
    by_lang = collections.defaultdict(list)
    for key in sorted(eligible):
        v = index[key]
        by_lang[v["language"]["code"]].append(key)

    chosen = []
    for code in sorted(by_lang):
        keys = by_lang[code]

        # The id is the final tiebreak, and it is not decoration. Languages
        # routinely offer several CC0 medium voices, which tie on both real
        # criteria; without a total order the winner is whichever the iteration
        # happened to reach first, and `eligible` is a set — so re-running this
        # script swapped seven voices between two runs on the same revision.
        # A curated catalog whose contents move when you regenerate it is one
        # whose MODEL_CARD review no longer describes what shipped.
        def rank(k):
            return (LICENCE_ORDER.index(licences[k][2]), TIER_ORDER[index[k]["quality"]], k)

        best = min(keys, key=rank)
        name = index[best]["name"]
        # Every tier of the winning name that is ITSELF eligible. A name can span
        # tiers whose datasets differ, so the licence is re-checked per tier
        # rather than inherited from the one that won.
        tiers = [k for k in keys if index[k]["name"] == name]
        tiers.sort(key=lambda k: TIER_ORDER[index[k]["quality"]])
        chosen.extend(tiers)

    langs = {index[k]["language"]["code"] for k in chosen}
    sys.stderr.write(f"  selected {len(chosen)} entries across {len(langs)} languages\n")

    # --- pinning: LFS oids for the weights, real downloads for the configs ---
    trees = {}

    def tree_of(dirpath):
        u = (f"https://huggingface.co/api/models/{REPO}/tree/"
             f"{args.revision}/{dirpath}")
        return dirpath, json.loads(fetch(u))

    dirs = sorted({p.rsplit("/", 1)[0]
                   for k in chosen for p in index[k]["files"] if p.endswith(".onnx")})
    with concurrent.futures.ThreadPoolExecutor(12) as ex:
        for dirpath, entries in ex.map(tree_of, dirs):
            trees[dirpath] = {e["path"]: e for e in entries}

    def config_of(key):
        path = [p for p in index[key]["files"] if p.endswith(".onnx.json")][0]
        blob = fetch(raw_url(args.revision, path))
        return key, path, blob

    configs = {}
    with concurrent.futures.ThreadPoolExecutor(12) as ex:
        for key, path, blob in ex.map(config_of, chosen):
            configs[key] = (path, blob)

    voices = []
    for key in chosen:
        v = index[key]
        onnx_path = [p for p in v["files"] if p.endswith(".onnx")][0]
        cfg_path, cfg_blob = configs[key]
        cfg = json.loads(cfg_blob)

        entry = trees[onnx_path.rsplit("/", 1)[0]].get(onnx_path)
        if entry is None or "lfs" not in entry:
            sys.stderr.write(f"  !! {key}: no LFS record for {onnx_path}; skipped\n")
            continue
        onnx_sha = entry["lfs"]["oid"]
        onnx_bytes = entry["lfs"]["size"]
        if onnx_bytes != v["files"][onnx_path]["size_bytes"]:
            sys.stderr.write(f"  !! {key}: index and tree disagree on size; skipped\n")
            continue

        speakers = cfg.get("num_speakers", v.get("num_speakers", 1))
        smap = cfg.get("speaker_id_map") or v.get("speaker_id_map") or {}
        # Ordered by speaker id, so index N in this list IS sid N and the picker
        # never has to carry the map around.
        names = [n for n, _ in sorted(smap.items(), key=lambda kv: kv[1])] if smap else []

        lic, lic_url, lic_class = licences[key]
        voices.append({
            "id": key,
            "name": v["name"],
            "language": {
                "code": v["language"]["code"],
                "english": v["language"]["name_english"],
                "native": v["language"]["name_native"],
                "country": v["language"].get("country_english", ""),
            },
            "quality": v["quality"],
            "sampleRate": cfg["audio"]["sample_rate"],
            "speakers": speakers,
            "speakerNames": names,
            "licence": {"name": lic, "class": lic_class, "url": lic_url or ""},
            "files": [
                {
                    "name": f"{key}.onnx",
                    "url": raw_url(args.revision, onnx_path),
                    "mirrors": [],
                    "sha256": onnx_sha,
                    "bytes": onnx_bytes,
                },
                {
                    "name": f"{key}.onnx.json",
                    "url": raw_url(args.revision, cfg_path),
                    "mirrors": [],
                    "sha256": hashlib.sha256(cfg_blob).hexdigest(),
                    "bytes": len(cfg_blob),
                },
            ],
        })

    voices.sort(key=lambda e: (e["language"]["code"], e["name"], TIER_ORDER[e["quality"]]))
    total = sum(f["bytes"] for e in voices for f in e["files"])
    sys.stderr.write(f"  {len(voices)} voices pinned, "
                     f"{total / 1e6:.0f} MB if every one were installed\n")

    doc = {
        "version": "1",
        "_comment": (
            "Curated, hash-pinned Piper voice catalog. GENERATED — edit "
            "build/gen-piper-catalog.py and re-run, never this file. "
            f"Pinned to {REPO}@{args.revision}, so the sizes and SHA-256 below stay "
            "valid even if upstream pushes new commits. The .onnx weights are git-LFS "
            "objects whose sha256 IS their LFS content hash; the .onnx.json configs are "
            "plain git blobs that this generator downloaded and hashed itself, so both "
            "files of every voice are fully verified rather than size-checked. "
            "VOICE LICENCES ARE A SEPARATE AXIS FROM THE ENGINE'S: each entry carries "
            "the terms from its own MODEL_CARD, the Voices tab shows them before any "
            "bytes arrive, and the download is gated on accepting them. A voice whose "
            "MODEL_CARD could not state a licence is not in this file at all."
        ),
        "source": {
            "repo": REPO,
            "revision": args.revision,
            "generated": generated,
            "policy": args.policy,
            "policyClasses": sorted(allowed),
        },
        "voices": voices,
    }

    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)
        f.write("\n")
    sys.stderr.write(f"wrote {args.out}\n")

    counts = collections.Counter(e["licence"]["class"] for e in voices)
    sys.stderr.write("  licence classes: "
                     + ", ".join(f"{c}={n}" for c, n in counts.most_common()) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())

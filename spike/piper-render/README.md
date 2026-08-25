# spike/piper-render — Phase P0

Proves a Piper voice's graph runs on the ONNX Runtime this repository already
ships, called from C#, with no Piper code in the process.
[The plan](../../docs/PIPER-PLAN.md#p0).

```bash
# fetch the voice once — 61 MB, never committed
B=https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium
mkdir -p voice
curl -sSL -o voice/en_US-lessac-medium.onnx      $B/en_US-lessac-medium.onnx
curl -sSL -o voice/en_US-lessac-medium.onnx.json $B/en_US-lessac-medium.onnx.json

dotnet run --project spike/piper-render                       # writes out.wav
dotnet run --project spike/piper-render -- --repeat 6         # cold vs warm
```

Flags: `--cuda`, `--repeat N`, `--length-scale`, `--noise-scale`, `--noise-w`,
`--no-normalize`, `-o <path>`.

CUDA needs the provider pack on the loader path, the same way the daemon does:

```bash
LD_LIBRARY_PATH="$(vst-ctl config | python3 -c 'import json,sys;print(json.load(sys.stdin)["StoreRoot"])')/../runtime/cuda" \
  dotnet run --project spike/piper-render -- --cuda --repeat 6
```

## The one thing to keep straight

**The phoneme ids are hardcoded**, captured from piper's own phonemizer into
[reference-ids.json](reference-ids.json). That is the point rather than a
shortcut: P0 asks whether the *graph* runs. Producing those ids ourselves is
[P1](../../docs/PIPER-PLAN.md#p1), which is the go/no-go for the whole project,
and mixing the two would let a phonemiser bug read as a graph that does not work.

Regenerate them for another sentence with the venv the plan provisions:

```bash
~/.venvs/piper/bin/python - <<'PY'
import json
from piper.phonemize_espeak import EspeakPhonemizer
from piper.phoneme_ids import phonemes_to_ids
cfg = json.load(open('voice/en_US-lessac-medium.onnx.json'))
ph = EspeakPhonemizer().phonemize(cfg['espeak']['voice'], "Your sentence here.")
print(phonemes_to_ids(ph[0], cfg['phoneme_id_map']))
PY
```

## How it was verified

Not by listening alone. With `--noise-scale 0 --noise-w 0` the graph is
deterministic, so this and `python -m piper` can be compared sample for sample —
and are **byte-identical**, WAV header included:

```bash
dotnet run --project spike/piper-render -- --noise-scale 0 --noise-w 0 --no-normalize -o /tmp/ours.wav
echo "The quick brown fox jumps over the lazy dog." | \
  ~/.venvs/piper/bin/python -m piper -m voice/en_US-lessac-medium.onnx \
    --noise-scale 0 --noise-w-scale 0 --no-normalize -f /tmp/piper.wav
cmp /tmp/ours.wav /tmp/piper.wav
```

That is the difference between "it made speech" and "we are calling it exactly
the way piper does". Keep the comparison deterministic if you change anything
here: with the default scales both sides sample their own noise and no
comparison means anything.

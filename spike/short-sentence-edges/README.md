# spike/short-sentence-edges: Supertonic garbles the edges of short sentences

Investigated 2026-11-18. **Reported by the user:** when a short sentence is read,
the first word, the last word, or both come out missing or badly pronounced, and
which one varies from press to press.

## Cause

**The model's own speed.** At `SupertonicOptions.Speed` above about 1.1, the model
squeezes or drops the edge sounds of very short sentences (1 to 3 words). The
audio is not truncated: every take still has 0.25 to 0.4 s of silence before and
after the speech. The words themselves are malformed. Each render draws fresh
noise (`SampleNoisyLatent`), so the same sentence fails differently every time,
which is the "sometimes first, sometimes last, sometimes both" in the report.

Not the cause, by measurement: the time-stretch (`TimeStretch.Stretch`), and the
total pace. The same 1.4x reached through the stretch scored clean. Bluetooth was
not measured; the daemon keeps its PulseAudio stream open and uncorked while idle,
so the sink stays RUNNING between readings, and the user judges it not involved.

The user's settings ask for `EngineSpeed` 1.4 with the shipped
`RateClampCeiling` 1.3, so `SpeechRate.Compute` sent the model to 1.3 and the
stretch made up 1.077.

## Results

Judge: faster-whisper `small.en` (CPU, int8, beam 5), first and last word
compared to the reference text. Voice F2, 12 steps unless stated. The
user listened to three of the pairs and agreed on two ("Open" and "Turn left h"
lost at 1.3); on "Hello" Whisper heard "you" where the user heard "Hello", so the
counts somewhat overstate. The direction holds.

16 sentences ([texts.txt](texts.txt)), 3 takes each:

| Condition | Model speed | Stretch | Total | First wrong | Last wrong |
|---|---|---|---|---|---|
| The user's daemon, `vst-ctl render` (CUDA) | 1.3 | 1.077 | 1.4 | 9 / 48 | 11 / 48 |
| Harness, same split (CPU) | 1.3 | 1.077 | 1.4 | 4 / 48 | 6 / 48 |
| Model only | 1.3 | 1.0 | 1.3 | 8 / 48 | 8 / 48 |
| Same total from the stretch | 1.05 | 1.333 | 1.4 | 0 / 48 | 0 / 48 |
| Normal speed | 1.0 | 1.0 | 1.0 | 0 / 48 | 0 / 48 |

Almost every error fell in the four sentences of 1 to 3 words.

Speed sweep, model only, the 4 short sentences ([short.txt](short.txt)), 5 takes
each (first wrong / last wrong, out of 20): 1.05: 0/0, 1.15: 2/2, 1.2: 3/4,
1.25: 7/10, 1.3: 14/13.

All 10 voices, short sentences, 3 takes (out of 120):

| | First wrong | Last wrong |
|---|---|---|
| Model 1.3, stretch 1.077 | 29 | 49 |
| Model 1.05, stretch 1.333 | 1 | 2 |

Every voice failed at 1.3 (3 to 13 errors of 24 per voice). All three errors of
the stretch route were M2.

## The change made on the user's machine

`RateClampCeiling` 1.3 → **1.05** in the AppImage install's `data/settings.json`
(backup beside it: `settings.json.bak-ceiling-20261118`), then `vst-ctl reload`.
Re-rendered through the daemon: 0 of 12 wrong on the short sentences (was 6 of
12), and the pace is unchanged ("Open the door." 1045 ms against 1035 ms).

**Not verified yet:** how long passages sound now that they get 1.333x of stretch
instead of 1.077x. The user is listening over the following days.

**Nothing in the repository changed.** The shipped default ceiling
(`SpeechRate.DefaultCeiling`, and `SapiEngine.ComputeSpeed` on Windows) is still
1.3, so every other user with `EngineSpeed` above 1.1 has the same problem.

## Open decision

1. If long reading sounds fine at 1.05, lower the shipped default ceiling. This
   touches `SpeechRate` and `SapiEngine.ComputeSpeed` together, because the two
   must stay one rule (see `SpeechRate`'s summary).
2. If it does not, make the model speed depend on length: keep 1.3 for long
   chunks and cap short ones (about 3 words, or under about 1.5 s of predicted
   speech) near 1.05, with the stretch making up the rest. Same two-platform
   constraint.

## Reproduce

```bash
cd spike/short-sentence-edges
dotnet build -c Release -o bin
M=<store>/models                       # the folder holding onnx/ and voice_styles/
# args: models out voice steps modelSpeed stretch takes texts
bin/ShortEdges $M ../../.work/edges/A F2 12 1.3  1.0769 3 texts.txt
bin/ShortEdges $M ../../.work/edges/D F2 12 1.05 1.3333 3 texts.txt

python3 -m venv ../../.work/edges/venv
../../.work/edges/venv/bin/pip install faster-whisper numpy soundfile
../../.work/edges/venv/bin/python analyze.py texts.txt ../../.work/edges/A ../../.work/edges/D
```

The live path is `vst-ctl render --out f.wav -- "text"`; name files
`<sentence index, two digits>_r<take>.wav` so `analyze.py` can match them to the
text. The harness runs `OrtSynthesizer` on the CPU and `TimeStretch` from Core,
so it does not touch a running daemon. Whisper downloads about 500 MB on first
use; keep it under `.work/`.

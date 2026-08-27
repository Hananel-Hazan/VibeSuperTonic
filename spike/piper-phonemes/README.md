# spike/piper-phonemes — Phase P1, the go/no-go

Does our own P/Invoked espeak-ng produce the **same phoneme ids** piper does?
If not, the whole Piper plan stops: no non-GPL phonemiser produces the inventory
these voices were trained on, so there is no fallback.
[The plan](../../docs/PIPER-PLAN.md#p1).

**It does. 327 sentences, 8 languages, zero divergences.**

```bash
bash build/build-espeak.sh                         # ~2 min, prints the two vars
export VST_ESPEAK_LIB=…  VST_ESPEAK_DATA=…

dotnet run --project spike/piper-phonemes -- --negative-control   # prove it can fail
dotnet run --project spike/piper-phonemes                          # then the parity run
dotnet run --project spike/piper-phonemes -- -v                    # every divergence, untruncated
```

## What is being compared, and against what

[corpus.json](corpus.json) holds 289 texts, the phonemes piper produced for each
and the ids it assembled — generated once by
[build-corpus.py](build-corpus.py) from piper itself, and **committed**. The C#
run needs no Python and no network, which is the point: the number has to be
re-checkable on a machine that has never had piper installed.

The corpus is not volume for its own sake. Every group targets one of the five
behaviours a reimplementation gets wrong — all six clause terminators, the
`, ; :` rule that does *not* end a sentence, text with no final punctuation,
multi-sentence input, NFD decomposition, and language-switch markers — wrapped in
enough ordinary prose (numbers, dates, money, abbreviations, quotes, ALL CAPS,
empty strings) that a bug has somewhere to hide.

## Run the negative control before believing a pass

`--negative-control` runs the corpus once per deliberate sabotage and requires
every one to diverge:

```
caught  SkipNfd              5 sentence(s) diverge
caught  SkipLanguageStrip    7 sentence(s) diverge
caught  SkipTerminator     265 sentence(s) diverge
caught  SkipClauseSpace    132 sentence(s) diverge
caught  SplitEveryClause   173 sentence(s) diverge
```

**This is not decoration — it found a hole on its first run.** `SkipLanguageStrip`
diverged on **zero** sentences, meaning the corpus never triggered a `(lang)`
marker and the strip was never exercised. Deleting that line of production code
would have passed every test here.

The reason is worth keeping: the corpus had sentences full of obvious loanwords
— *Schadenfreude*, *boeuf bourguignon*, *Volkswagen* — and an English voice
switches for **none** of them, because English's espeak dictionary has no `_^_`
entries at all. The switch runs the other way: German and Dutch flag borrowed
*English* words, so it takes a German sentence containing "Account" to emit
`(en)ɐkˈaʊnt(de)`.

`SkipNfd` tripping on only 5 is not a hole, and the reason is the same kind of
detail: NFD only changes phonemes Unicode can *compose*. German `ç` (U+00E7) has
a precomposed form and splits; French nasals `ɛ̃ ɑ̃ ɔ̃` have none and are already
base-plus-mark before NFD runs.

## What P1 found that the plan had wrong

**The function is `espeak_TextToPhonemesWithTerminator`.** The plan said
`espeak_TextToPhonemes` for six days. The plain one returns phonemes and discards
the clause terminator, so there is no way to know whether a clause ended a
sentence: every input collapses to one sentence and the trailing punctuation the
model was trained on never reaches it. It is also **newer than the 1.52.0
release** — it exists at the commit piper pins and not at the tag, so a build
from the release would have compiled, linked, run, and been wrong.

Both are why [build-espeak.sh](../../build/build-espeak.sh) pins a commit and asserts the
symbol is exported rather than trusting a version number.

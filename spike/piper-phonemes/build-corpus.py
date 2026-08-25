#!/usr/bin/env python3
"""Build P1's corpus and its expected phoneme ids, from piper itself.

    ~/.venvs/piper/bin/python build-corpus.py

Writes corpus.json: every sentence, the espeak voice, the phonemes piper
produced and the ids it assembled. That file is the REFERENCE the C# side is
diffed against, and it is committed — so the comparison is a test in the
repository rather than a session's transcript, which is
docs/PIPER-PLAN.md#p1's actual exit criterion.

Regenerating it needs this venv and piper's pinned espeak-ng. Comparing against
it needs neither, which is the point: the number has to be re-checkable on a
machine that has never had Python on it.

WHAT THE CORPUS IS FOR. Not volume for its own sake. Every group below targets
one of the five behaviours PIPER-PLAN lists as the things a reimplementation
gets wrong, plus the ordinary text they hide inside:

  - clause terminators, all six, and the , ; : rule that does NOT end a sentence
  - text with no final punctuation, which is still one complete sentence
  - multi-sentence input, where the split has to land in the same places
  - accented characters, which NFD decomposes into separate "phonemes"
  - language-switch markers, which espeak emits as (xx) and piper strips
"""

import itertools
import json
import sys

try:
    from piper.phonemize_espeak import EspeakPhonemizer
    from piper.phoneme_ids import phonemes_to_ids
except ImportError:
    sys.exit("run this with ~/.venvs/piper/bin/python — see docs/PIPER-PLAN.md")

import urllib.request

# The voices whose id maps the corpus is scored against. One per script family
# we can check cheaply; the id map is per-voice, so the map travels with the
# sentence rather than being assumed global.
# The espeak voice name is NOT guessable from the language code — it is a field
# in each voice's own config, and guessing "en-gb" for en_GB fails outright.
VOICES = {
    "en_US": "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json",
    "en_GB": "en/en_GB/alan/medium/en_GB-alan-medium.onnx.json",
    "de_DE": "de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx.json",
    "fr_FR": "fr/fr_FR/siwis/medium/fr_FR-siwis-medium.onnx.json",
    "es_ES": "es/es_ES/davefx/medium/es_ES-davefx-medium.onnx.json",
    "it_IT": "it/it_IT/paola/medium/it_IT-paola-medium.onnx.json",
    "nl_NL": "nl/nl_NL/mls/medium/nl_NL-mls-medium.onnx.json",
    "ru_RU": "ru/ru_RU/dmitri/medium/ru_RU-dmitri-medium.onnx.json",
}

BASE = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/"

TERMINATORS = [".", "?", "!", ",", ";", ":"]

ENGLISH = [
    # Ordinary prose, the baseline everything else is a deviation from.
    "The quick brown fox jumps over the lazy dog",
    "She sells sea shells by the sea shore",
    "This machine reads what you select, out loud",
    "Nothing about this sentence is unusual",
    "A neural voice that runs on your own computer",
    "Press the key again to stop it speaking",
    "The daemon holds the model warm between utterances",
    "Every control on this window has a verb",
    # Numbers, dates, money, units — where a phonemiser earns its keep.
    "It cost $4.50 and she paid with a 20",
    "Read pages 3 to 147 before Friday",
    "The meeting is on 2026-08-25 at 14:30",
    "Temperature fell to -8 degrees overnight",
    "She ran 26.2 miles in 3 hours and 41 minutes",
    "Version 0.2.10 replaced version 0.2.9",
    "1,024 bytes is not quite 1 kilobyte",
    "50% of 3,000 is 1,500",
    # Abbreviations and initialisms.
    "Dr. Smith and Mr. Jones met at 9 a.m.",
    "The USB and HDMI ports are on the left",
    "See fig. 4, p. 12, for the full table",
    "NASA, ESA and JAXA all confirmed it",
    # Punctuation that is not a terminator.
    "It was — how to put this — unexpected",
    "The answer (which nobody liked) was no",
    'She said "stop" and everybody stopped',
    "Wait... there is one more thing",
    "Half-finished, badly-lit, and far too cold",
    "Is it 'quoted' or “curly quoted”?",
    # Casing and symbols.
    "ALL CAPS SHOUTING IS STILL A SENTENCE",
    "email me at name@example.com about it",
    "The ratio is 16:9 and the file is 4K",
    "C# and F# are both .NET languages",
]

# Accented and non-ASCII text, which NFD decomposes. The plan calls this out
# explicitly: accents become separate "phonemes" BEFORE the id lookup.
ACCENTED = [
    "Zürich, naïve, café — the loanwords keep their accents",
    "Renée moved to São Paulo in the spring",
    "The piñata split and everybody cheered",
    "A Kraków–Poznań train leaves at noon",
    "Motörhead and Blue Öyster Cult, in that order",
    "Señor Álvarez owns the corner shop",
    "The façade needed work; the roof did not",
    "Björk, Sigur Rós, and a very long winter",
]

# Words espeak flags with a (lang) switch marker, which piper strips.
#
# THESE WERE FOUND BY MEASUREMENT, NOT BY GUESSING, and the first attempt was
# wrong in a way worth keeping: sentences full of obvious loanwords —
# Schadenfreude, boeuf bourguignon, Volkswagen — trigger NOTHING in an English
# voice. English's dictionary has no _^_ entries at all. The switch lives in the
# OTHER direction: German and Dutch flag borrowed English words, so it is a
# German sentence containing "Account" that emits (en)…(de).
#
# The negative control is what caught this. Without it the corpus looked like it
# covered language switching, the strip was never exercised, and deleting that
# line of code would have passed every test in this file.
SWITCHING_DE = [
    "Mein Account bei der Bank ist gesperrt",
    "Er hat in America studiert",
    "Der Computer braucht ein Update",
    "Das Team hat einen guten Job gemacht",
    "Sie lud das Image herunter und startete neu",
]

SWITCHING_NL = [
    "Mijn Android telefoon is stuk",
    "De airlines hebben het geannuleerd",
    "Druk op delete om het te verwijderen",
]

# Per-language prose, so parity is not an English-only claim.
OTHER = {
    "de_DE": [
        # The ich-laut. German ç is U+00E7, which HAS a precomposed form, so NFD
        # actually splits it — unlike the French nasals ɛ̃ ɑ̃ ɔ̃, which have no
        # precomposed form and are already base-plus-mark before NFD runs. That
        # is why the NFD sabotage trips on so few sentences: the step only
        # changes anything for the handful of phonemes Unicode can compose.
        "Ich möchte nicht, dass du dich fürchtest",
        "Wirklich wichtig ist, dass ich mich nicht irre",
        "Manchmal reicht ein Gesicht im Licht",
        "Der schnelle braune Fuchs springt über den faulen Hund",
        "Ich lese den markierten Text laut vor",
        "Es kostet 4,50 Euro und sie zahlte mit einem Zwanziger",
        "Die Straße war nass, kalt und völlig leer",
        "Herr Müller kam um 9 Uhr, Frau Schäfer später",
    ]
    + [t + "." for t in SWITCHING_DE],
    "fr_FR": [
        # Nasal vowels, which espeak emits with a COMBINING TILDE — the only
        # thing in this corpus that exercises the NFD step, and the reason
        # French carries more weight here than its five sentences suggest.
        "Un bon vin blanc, un grand pain rond",
        "Cinquante enfants chantent en même temps",
        "Mon oncle Jean prend son temps",
        "Les champions français ont gagné en novembre",
        "Le vif renard brun saute par-dessus le chien paresseux",
        "Je lis à voix haute le texte sélectionné",
        "Ça coûte 4,50 euros et elle a payé avec un billet",
        "L'élève a écrit très près de la fenêtre",
        "Ç'aurait été plus simple, n'est-ce pas ?",
    ],
    "es_ES": [
        "El veloz zorro marrón salta sobre el perro perezoso",
        "Leo en voz alta el texto seleccionado",
        "Cuesta 4,50 euros y pagó con un billete de veinte",
        "¿Cuántos años tiene el niño pequeño?",
        "¡Qué día más largo, señor Álvarez!",
    ],
    "it_IT": [
        "La rapida volpe marrone salta sopra il cane pigro",
        "Leggo ad alta voce il testo selezionato",
        "Costa 4,50 euro e ha pagato con venti",
        "Perché non me l'hai detto prima?",
        "Città, università, perù: gli accenti contano",
    ],
    "nl_NL": [
        "De snelle bruine vos springt over de luie hond",
        "Ik lees de geselecteerde tekst hardop voor",
        "Het kost 4,50 euro en ze betaalde met twintig",
        "'s Ochtends is het altijd rustiger",
        "Wie het weet mag het zeggen, zei hij",
    ]
    + [t + "." for t in SWITCHING_NL],
    "ru_RU": [
        "Быстрая бурая лиса прыгает через ленивую собаку",
        "Я читаю выделенный текст вслух",
        "Это стоит 450 рублей, она заплатила пятьсот",
        "Кто не работает, тот не ест",
        "Здравствуйте! Как ваши дела?",
    ],
    "en_GB": [
        "The quick brown fox jumps over the lazy dog",
        "I shall read the selected text aloud",
        "It cost £4.50 and she paid with a tenner",
        "Colour, favour, realise — the spelling differs",
        "Shall we say half four, or is that too early?",
    ],
}


def english_corpus() -> list[str]:
    """English sentences, times every way of ending them."""
    out: list[str] = []

    # Each base sentence with each of the six terminators, and with none.
    for text, term in itertools.product(ENGLISH, TERMINATORS + [""]):
        out.append(text + term)

    # Accented text, terminated normally. Language switching is not here: an
    # English voice never switches, so it lives with de_DE and nl_NL below.
    out += [t + "." for t in ACCENTED]

    # Multi-sentence inputs: the split has to land in the same places.
    out += [
        "First sentence. Second sentence. Third one ends here.",
        "It works. Does it? Yes! Definitely.",
        "One, two, three; then four: finally five.",
        "Stop. Wait, no — carry on.",
        "A short one. And a considerably longer one that keeps going for a while.",
        "Two sentences, no space between them.Second starts here.",
        "Ends with a question? Then a statement. Then an exclamation!",
    ]

    # Degenerate input, which is where reimplementations diverge quietly.
    out += [
        "",
        " ",
        ".",
        "?",
        "...",
        "a",
        "I",
        "42",
        "-",
        "—",
        "   leading and trailing   ",
        "MiXeD CaSe WoRdS hErE",
        "hyphen-joined-words-everywhere",
        "“Quoted from the very start”, she said.",
    ]
    return out


def main() -> None:
    phonemizer = EspeakPhonemizer()
    entries = []
    id_maps: dict[str, dict] = {}
    espeak_voices: dict[str, str] = {}

    for lang, config_path in VOICES.items():
        print(f"{lang}: fetching config…", file=sys.stderr)
        with urllib.request.urlopen(BASE + config_path, timeout=60) as f:
            cfg = json.load(f)
        id_map = id_maps[lang] = cfg["phoneme_id_map"]
        espeak_voice = espeak_voices[lang] = cfg["espeak"]["voice"]

        texts = english_corpus() if lang == "en_US" else OTHER[lang]
        for text in texts:
            sentences = phonemizer.phonemize(espeak_voice, text)
            entries.append(
                {
                    "lang": lang,
                    "espeak_voice": espeak_voice,
                    "text": text,
                    "sentences": [
                        {"phonemes": s, "ids": phonemes_to_ids(s, id_map)}
                        for s in sentences
                    ],
                }
            )
        print(f"{lang}: {len(texts)} texts, espeak voice {espeak_voice!r}", file=sys.stderr)

    out = {
        "generated": "piper-tts in ~/.venvs/piper, espeak-ng pinned at 724808c5",
        "voices": espeak_voices,
        "id_maps": id_maps,
        "entries": entries,
    }
    with open("corpus.json", "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)

    total = sum(len(e["sentences"]) for e in entries)
    print(f"\n{len(entries)} texts, {total} sentences -> corpus.json", file=sys.stderr)


if __name__ == "__main__":
    main()

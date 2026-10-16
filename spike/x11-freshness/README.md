# spike/x11-freshness — testing what `x11-select` cannot

Built 2026-08-16, during the Phase 4 application pass, to answer a question the
existing spike could not: **is this selection the one the user just made, or the
one the last application left behind?**

`spike/x11-select` serves PRIMARY with fixed text and proves the capture
protocol. It cannot exercise any of the following, all of which turned out to
matter:

- **Ownership timestamps.** ICCCM's `TIMESTAMP` target is what makes a stale
  selection detectable at all — see `SelectionFreshness` in Core. `x11-select`
  does not answer it.
- **CLIPBOARD.** The opt-in fallback compares the two selections' claim times,
  so a test needs to own both and control which was claimed first.
- **Re-asserting ownership**, which is what a real application does on every new
  selection, and whose absence is the entire bug.

## The bug this exists for

Gmail's in-frame attachment viewer in Brave renders selectable text and never
claims PRIMARY. X11 has no empty state for a selection, so the previous owner's
text stays there and the daemon reads it — correctly by the protocol, and wrongly
by any user's expectation. Because the stale text is usually the utterance
already playing, re-reading it is indistinguishable from the hotkey doing
nothing. Full account in
[the plan](../../docs/LINUX-PORT-PLAN.md#phase-4-apps).

## Contents

| File | What it is |
| --- | --- |
| `owner.py` | A selection owner that honours `TARGETS`, `TIMESTAMP` and `UTF8_STRING`, for **both** PRIMARY and CLIPBOARD, and re-asserts on command |
| `SelCheck.csproj`, `Program.cs` | `selcheck` — captures through the real `X11SelectionSource`, printing text, reason and notice, without the daemon or a model |
| `run-tests.sh` | The five cases, on a nested Xephyr display |
| `probe.py` | Ad-hoc: who owns PRIMARY, what targets they offer, what it holds. `probe.py watch` follows ownership as it changes |
| `why.py` | Field diagnosis: on every hotkey press, print owner, claim time and size for both selections. Prints no selection text, so it can be run on someone's real desktop |

## Running

```bash
dotnet build spike/x11-freshness/SelCheck.csproj -c Release
bash spike/x11-freshness/run-tests.sh
```

Needs `Xephyr` (`xserver-xephyr`) and `python3-xlib`. Both were already on the
Mint box.

## Two things learned the hard way

**These cases cannot run on the live display.** A person selecting text while the
suite runs takes PRIMARY away mid-case, and a real pass or failure becomes a coin
toss. One case "failed" while PRIMARY held Hebrew text from the user's editor.
Hence Xephyr.

**`selcheck` keeps one `X11SelectionSource` across all captures on purpose.** The
staleness check compares against what that instance saw last time, so a fresh
instance per capture reports everything as fresh and tests nothing.

# VibeSuperTonic

> **Heads up — this is my first vibe-coding project.** I've wanted a good neural voice in plain old SAPI for a long, long time and never had a free weekend. Huge thanks to [Supertone](https://supertone.ai/) for [Supertonic](https://github.com/supertone-inc/supertonic) — the actually-hard part (the model) is theirs. And shout-out to Claude Opus, who in roughly one day of pair-debugging turned "I want this" into a thing that ships.
>
> **Work in progress, please be gentle.** A few rough edges to know about up front:
>
> - **First run needs administrator rights.** Hooking a voice into Windows SAPI requires writing the voice token under `HKLM` — that's a system-wide registry hive and Windows guards it with UAC. The Control Panel self-elevates, you'll see the standard UAC prompt once, and after that the folder is fully portable (move it anywhere, no admin needed).
> - **First sentence is slow.** The engine loads ~380 MB of ONNX models into memory on first use; expect 2–5 s of "did it crash?" silence before the first word on CPU (less on GPU). Subsequent sentences start in well under a second.
> - **GPU is on by default, falls back to CPU automatically.** DirectML / Direct3D 12 inference if your hardware supports it — typical 2-5× speedup on iGPU, more on dGPU. If DirectML init fails (no DX12, driver issue, etc.) the engine quietly drops to CPU and keeps working.
> - **DSP rate up to 2.0×.** Sonic (pitch-synchronous overlap-add) handles the speed-up cleanly across the whole range. Voice formants stay put because each output pitch period is bit-perfect from the input — no robotic / metallic edge at high stretch.
>
> If those tradeoffs are fine, you've got 10 surprisingly good neural voices that work in literally any SAPI app on Windows. Read on.

---

**Two products from one repository**, sharing a platform-neutral core:

- **Windows** — a portable SAPI 5 TTS engine wrapping Supertone's [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS. Ten English voices that show up in any SAPI 5 client — Balabolka, NVDA, Microsoft Narrator, System.Speech, Edge Read Aloud, NaturallySpeaking, Lingoes, and so on.
- **Linux** — a background daemon with a global hotkey that speaks whatever text you have selected, a window to follow along in, **two engines** (Supertonic and [Piper](https://github.com/rhasspy/piper), 65 hash-pinned voices across 35 languages), and a **Speech Dispatcher module**, so the same voices are available to Orca and anything else on the desktop that speaks. [Jump to Linux](#linux).

The engine is written in pure C# / .NET 10 and registers via .NET ComHosting. The portable folder can live anywhere — USB stick, OneDrive, network share — and a one-time UAC prompt registers the voice tokens. A full Control Panel (the same `VibeSuperTonic.exe`) handles install, integrity checks, knob tuning, live monitoring, and uninstall.

## Status

**Windows — v0.2.x**, out of "spike" stage. Working install with 10 voices, full SAPI event surface (word boundaries, sentence boundaries, bookmarks, end-of-stream), per-fragment SSML rate control, sentence-level skip support, pipelined synthesis for smooth long-form playback, GPU acceleration via DirectML, and pitch-preserving DSP time-stretch (Sonic — pitch-synchronous overlap-add) up to 2.0×.

**Linux — shipping since 0.2.8**, as a tarball and an AppImage. The most recent release is **0.2.11**, which is where Piper and the Speech Dispatcher module arrived. See [Roadmap](#roadmap) for what is next, and [Linux](#linux) for what it is.

> **Licence note for the Linux artifacts.** From 0.2.11 the Linux archive contains espeak-ng, so **the archive as a whole is GPL-3.0-or-later**. This repository's own source stays MIT, and every release up to and including 0.2.10 contains no espeak-ng and is unaffected. Details in [License](#license).

## Features

- 10 English voices (M1–M5 male, F1–F5 female) at 44.1 kHz mono 16-bit
- Visible to **all** SAPI 5 clients — both 32-bit and 64-bit
- Portable: move the folder anywhere, re-run `VibeSuperTonic.exe`, no admin needed after first install
- **Control Panel GUI** — Status / Tune / Benchmark / Monitor / Advanced / About tabs in one EXE; CLI flags preserved for scripting (`--register`, `--unregister`, `--repair`, `--bench`, `--set k=v`)
- **GPU acceleration via DirectML** — opt-in (default on); falls back to CPU automatically on hardware/driver issues
- **Sonic time-stretch** — pitch-synchronous overlap-add up to 2.0× with the voice's formants preserved (no robotic edge, no formant smearing — each output pitch period is bit-perfect from the input)
- **Live engine telemetry** — RTF, latency, CPU/RAM, voice and resolved knob values, updated 5 Hz
- **Self-fixing install** — Status tab runs 15 integrity checks (registry, model files, both runtimes in both architectures, voice tokens, and whether 32-bit clients can actually use the voices) with one-click Repair; it can install a missing runtime for you, and it is lock-aware, telling you which process is holding the engine DLLs
- Hybrid registration: HKLM voice token (one-time admin write) + HKCU CLSID (rewritten on every launch from current path)
- Pipelined synthesis — next sentence renders while the current one plays, eliminating mid-paragraph gaps
- SAPI rate slider works
- Volume control honored, plus a separate dB trim knob in the Tune tab
- Word/sentence boundary events fire correctly (highlight-while-reading apps work)
- Bookmark events for SSML `<mark>` tags
- SSML `<prosody rate>` honored per-fragment (different sentences can have different rates)
- Sentence-level skip via `SPVES_SKIP` (Pause/Resume + Skip Sentence in SAPI clients)
- Per-voice settings overrides (any knob can be pinned per-voice via Tune tab's scope dropdown)

## Requirements

- Windows 10 or 11 (x64)
- **Two runtimes — [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) and the [Visual C++ redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist). Read the note below; this is the one thing that trips people up.**
- ~500 MB disk space (~380 MB ONNX models + ~50 MB engine + runtime)
- ~1 GB RAM during synthesis (CPU); ~400 MB VRAM additional when GPU is active
- (Optional, for GPU acceleration) any DirectX 12-capable adapter

> The plain **.NET Runtime** is enough. The **Desktop Runtime** also works — it
> contains the plain one — but it is a much larger download than this needs.

### ⚠️ You probably need the 32-bit runtimes too

The speech engine is a COM in-process server: it loads **inside** your reader, so
it needs both runtimes **in the same bitness as that program** — not the same
bitness as Windows.

Most SAPI clients are still 32-bit: **Balabolka, Lingoes, and many NVDA setups**.
So on a normal 64-bit Windows box you usually want all four:

```powershell
# 64-bit clients
winget install Microsoft.DotNet.Runtime.10
winget install Microsoft.VCRedist.2015+.x64

# 32-bit clients — Balabolka, Lingoes, most NVDA setups
winget install Microsoft.DotNet.Runtime.10 --architecture x86 --force
winget install Microsoft.VCRedist.2015+.x86
```

Installing all of them is the safe default and costs little. The x64 Visual C++
redistributable is usually already present because something else installed it;
the x86 one usually is not.

**The two missing runtimes fail differently**, which is the whole difficulty:

| Missing | Symptom |
| --- | --- |
| .NET 10 runtime (x86) | The reader lists **no** VibeSuperTonic voices at all. Nothing is registered for a bitness that cannot run it. |
| Visual C++ runtime (x86) | The voices **are** listed, and are **silent**. ONNX Runtime's native DLL links against it and cannot load without it — Windows reports this as `onnxruntime.dll or one of its dependencies (0x8007007E)`, naming the file that *is* present. |

In both cases the Control Panel's own Test button keeps working perfectly,
because the Control Panel is 64-bit and self-contained. That combination looks
exactly like "the app is fine, my reader is broken", which is why it gets its own
warning in the app and this heading here.

**The app can install them for you.** If anything is missing, the Control Panel
says so on launch and offers to install it via winget (with a UAC prompt), or you
can double-click that row on the Status tab. Nothing is installed silently — a
machine-wide runtime install is always something you confirm.

After installing, press **Repair all** and **restart your reader** — a reader
that was already running keeps the old, failed engine loaded until it does.

## Installation (end user)

1. Install the runtimes above — or let the Control Panel do it in step 4.
2. Download the latest `VibeSuperTonic-<version>-win.zip` from [Releases](https://github.com/Hananel-Hazan/VibeSuperTonic/releases).
3. Extract anywhere — your home folder, `C:\Tools`, a USB stick, all fine.
4. Double-click `VibeSuperTonic.exe`. The Control Panel opens on the Status tab, which lists what is missing and how to fix each item.
5. Press **Repair all**. This registers the voice tokens (one UAC prompt) and downloads the ~380 MB of models. A fresh extract shows several red rows until you do — that is expected, not a fault.
6. Once everything is green, open any SAPI client (Balabolka, NVDA, Narrator, etc.) — voices appear as `VibeSuperTonic M1` … `VibeSuperTonic F5`.

If you install a runtime *after* step 5, press **Repair all** again — the newly
usable bitness only gets registered once it can actually run.

### Why can't the runtime just be bundled?

Because .NET does not allow it for this kind of component. Publishing the engine
with `--self-contained` alongside `EnableComHosting` fails the build outright:

```
NETSDK1128: COM hosting does not support self-contained deployments.
```

So the runtime is a genuine prerequisite. What the app *can* do — and now does —
is detect exactly which architecture is missing, say what it will break in plain
words, and offer to install it for you.

### Verifying an install

The ZIP ships a verification harness that drives the engine through a real SAPI
client and checks what it actually did:

```
tools\VibeSuperTonic.TestHarness.exe
```

Exit code 0 means every check passed. Run it after the voices are registered and
the models have downloaded — it speaks out loud, because the word-boundary checks
need real audio timing to fire.

It covers COM activation, voice enumeration, sync and async speech, cancellation,
SSML bookmarks, prosody and language tags, and — the part worth watching —
whether word-boundary offsets land on the right characters when sentences are
separated by different whitespace, and when a length-changing pronunciation rule
is active. Those are the checks a highlight-while-reading client depends on, and
they cannot be verified by listening: a wrong offset sounds exactly like a right
one. If you ever file a bug about the highlight drifting, this output is the most
useful thing you can attach.

`--stress` runs a longer concurrency and recovery suite instead.

### Picking which GPU to use

On laptops with both an iGPU and a dGPU, configure the preferred GPU in Windows Settings:

> **Settings → System → Display → Graphics → Add an app → `VibeSuperTonic.exe`**, then pick **High performance** (uses dGPU) or **Power saving** (uses iGPU).

The in-app GPU picker was removed because DirectML's device-id mapping doesn't reliably match any DXGI enumeration on all systems. Windows Graphics Settings is the canonical control surface.

### Moving the folder

Move the whole folder anywhere, then run `VibeSuperTonic.exe` once at the new location. The Status tab detects the new path and updates HKCU registry entries — no admin needed.

### Uninstall

Open the Advanced tab → **Danger zone → Unregister VibeSuperTonic**, or from a console:

```powershell
.\VibeSuperTonic.exe --unregister
```

UAC prompts to clean HKLM voice tokens. Then delete the folder.

## Usage

In any SAPI 5 client, pick `VibeSuperTonic <id>` from the voice dropdown and hit Play.

The 10 voices have distinct timbres — try a few to find one you like:

| ID | Gender | Style |
|---|---|---|
| M1–M5 | Male | Range from warm to crisp |
| F1–F5 | Female | Range from gentle to bright |

The rate slider works in any SAPI client. For larger speed-ups, use the Tune tab's **DSP rate** knob (0.5×–2.0×, pitch-preserving Sonic time-stretch) instead of pushing the SAPI rate past 1.3× — the model itself drops syllables above that, but the DSP path keeps audio clean.

### Tune tab

| Knob | What it does |
|---|---|
| **Quality preset** | Bundles totalStep into Draft / Balanced / Quality / HiFi (4 / 6 / 8 / 12 diffusion iterations) |
| **Diffusion steps** | Direct totalStep slider 2–16. Linear CPU cost — 8 takes 2× longer than 4 |
| **Engine speed** | Locked at 1.0× (the model truncates phonemes above 1.0; speedup goes through DSP) |
| **DSP rate** | Sonic pitch-synchronous overlap-add time-stretch, 0.5×–2.0× |
| **Volume trim** | dB gain layered on top of the SAPI client's volume slider |
| **Default voice** | Used when a SAPI client doesn't pick one |

Per-voice overrides: pick "Per voice: M3" (etc.) in the **Apply to** dropdown at the top of the tab — every knob you change while in that scope is recorded only for that voice.

### SSML

```xml
<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">
  <prosody rate="x-fast">First sentence reads fast.</prosody>
  <break time="500ms"/>
  <prosody rate="slow">Second sentence reads slow.</prosody>
  <mark name="end-of-paragraph"/>
</speak>
```

Supported: `<prosody rate>`, `<break>`, `<mark>`, sentence/word boundaries.

Not supported (Supertonic model limitations):
- `<prosody pitch>` — model has no pitch parameter
- `<phoneme>` — model is graphemic (synthesizes from spelling)

<a name="linux"></a>

## Linux

A different product with the same voice. There is no SAPI on Linux, so instead
of an engine other applications load, this is a **daemon** that owns the model
and the audio device, a **global hotkey** that speaks whatever you have
selected, and a **window** to follow along in.

```bash
tar -xzf VibeSuperTonic-<version>-linux-x64.tar.gz
cd VibeSuperTonic
./install.sh          # binds the hotkeys and adds a menu entry; nothing autostarts
```

Or the AppImage: one file, `chmod +x`, run it. Models and data live beside it.

**Stop the daemon before replacing either** — Linux lets you overwrite a running
executable, and the result is a new binary on disk with the old one still
answering every hotkey press. `install.sh` does it for you; by hand it is
`./vst-ctl shutdown`.

### What you get

| | |
| --- | --- |
| `vibesupertonicd` | the daemon: warm model, audio device, tray icon. Started by the first hotkey press |
| `vibesupertonic-ui` | Reader, Voices, Tune, Pronunciations, Status |
| `vst-ctl` | the command-line client: speak, stop, render to a WAV, install voices, benchmark |
| `vst-speechd` | the Speech Dispatcher module — see below |

Press the hotkey with text selected and it reads it, highlighting each word as it
goes; click a word in the Reader to jump there. The daemon measures the machine
once (`vst-ctl benchmark`) and picks a thread count and provider from the result
rather than guessing.

### Two engines

**Supertonic** is the same model the Windows engine uses: ten styles, 31
languages, ~830 MB resident, and it is what the hotkey speaks by default.

**Piper** is a second engine, added in 0.2.11 — one voice per download, each
trained for one language, from a catalog of **65 voices across 35 languages**
pinned by SHA-256. The Voices tab downloads them and shows each voice's licence
*before* anything is fetched, because five of the English voices are
NonCommercial. Multi-speaker voices (LibriTTS has 904) pick a speaker per voice.

Both engines answer the same rate control, and settings can be scoped to **all
voices, one engine, or one voice** in the Tune tab.

### Screen readers

`speechd-install.sh` registers a Speech Dispatcher module, after which
VibeSuperTonic appears in Orca's list of synthesizers — and in anything else on
the desktop that speaks through speechd. On the AppImage the same thing is
`./VibeSuperTonic.AppImage speechd-install`.

Two things it deliberately will not do. It **refuses to register an install with
no voices downloaded**, because a synthesizer that appears in the list and cannot
speak is worse than one that is absent. And it **puts every other module back**,
including espeak-ng: a user configuration replaces the system one rather than
extending it, so a naive installer removes every other voice on the machine, and
the symptom is a blind user's desktop going quiet.

**Keystroke echo stays on espeak.** Measured: 383 ms for a single letter against
espeak-ng's 4 ms. The neural voices are excellent for reading a document and are
not an echo, so the module routes character and key events to the bundled
espeak-ng and everything else to the neural voice. That is a product decision
taken from a measurement, not a limitation nobody noticed.

### Requirements

- glibc 2.34 or newer — Ubuntu 22.04+, Debian 12+, RHEL 9+, Fedora 35+
- PulseAudio or PipeWire
- X11, or Wayland on KDE/wlroots. **GNOME/Wayland cannot work**: selection
  capture needs `ext-data-control`, which GNOME declines to implement on
  security grounds
- No runtime to install. All four binaries carry what they need

Every release is extracted into a bare `ubuntu:22.04` container in CI and started
there — no .NET, no display, no audio device, no models — so "it runs on a
supported distro" is a test rather than a claim.

---

## Architecture

```
SAPI client (Balabolka, NVDA, Lingoes, …)
       │ CoCreateInstance({F2A8C7B1-…})
       ▼
HKCU\SOFTWARE\Classes\CLSID\…\InprocServer32
       │ → engine\<arch>\VibeSuperTonic.Engine.comhost.dll
       ▼
.NET ComHost loads .NET 10 runtime
       ▼
SapiEngine (C#, [ComVisible])
   ├── ISpTTSEngine  (Speak, GetOutputFormat)
   ├── ISpObjectWithToken
   ├── EngineSettings cache (registry-backed, version-counter-invalidated)
   ├── Walks SPVTEXTFRAG list → typed Speak plan
   ├── Sentence-chunks text + balances chunk sizes
   ├── Synthesizes via SupertonicAdapter (ONNX shared across voices, DirectML if enabled)
   ├── Sonic time-stretch (Synth/Sonic.cs via Synth/TimeStretch.cs)
   ├── Real-time write throttle into SAPI buffer (prevents trailing-word cuts)
   ├── Drain wait at end of Speak (audio device finishes pulling before return)
   ├── Pipelines synth(N+1) with write(N)
   ├── Emits word/sentence/bookmark/end-of-stream events
   ├── Telemetry shared-memory snapshot at Local\VibeSuperTonic.Telemetry
   └── Streams 16-bit PCM via raw vtable to ISpTTSEngineSite::Write
```

### Hybrid registration

```
HKLM\SOFTWARE\Microsoft\Speech\Voices\Tokens\VibeSuperTonic_<id>           ← static, written once with admin
HKLM\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens\VibeSuperTonic_<id>   ← static OneCore mirror (Narrator/Edge)
HKCU\SOFTWARE\Classes\CLSID\{F2A8C7B1-…}\InprocServer32                    ← rewritten on every launch
HKCU\SOFTWARE\VibeSuperTonic\BaseDir                                       ← current portable folder path
HKCU\SOFTWARE\VibeSuperTonic\Settings\Default\…                            ← user-tunable knobs
HKCU\SOFTWARE\VibeSuperTonic\Settings\PerVoice\<voice>\…                   ← per-voice overrides
HKCU\SOFTWARE\VibeSuperTonic\Settings\Version (DWORD)                      ← cache-invalidation counter
HKCU\SOFTWARE\VibeSuperTonic\Settings\SchemaVersion (DWORD)                ← migration version
```

Voice tokens stay valid forever — they just point at our CLSID. The CLSID's actual file path lives in HKCU and is updated whenever you run the Control Panel from a new location. Settings live entirely in HKCU; the engine reads the version DWORD on every Speak (microseconds) and reloads the snapshot only when the launcher has bumped it.

## Project structure

```
src/
  VibeSuperTonic.Engine/        Pure-C# SAPI engine (ComHosting → comhost.dll)
    Interop/                    SAPI COM interfaces, structs, constants
    Settings/                   Engine-side EngineSettings + cache
    Synth/                      Supertonic SDK wrapper, Sonic time-stretch
    Telemetry/                  Shared-memory writer
    SapiEngine.cs               ISpTTSEngine + ISpObjectWithToken implementation
  VibeSuperTonic.Launcher/      Self-elevating Control Panel + CLI EXE
    Bench/                      On-demand preset benchmark harness
    Integrity/                  Status checks, model downloader, lock probe (Restart Manager)
    Telemetry/                  Shared-memory reader for the Monitor tab
    Ui/                         WinForms tabs (Status, Tune, Benchmark, Monitor, Advanced, About)
    EngineSettings.cs           Launcher-side settings model + registry I/O + schema migrations
    Registration.cs             Extracted SAPI registration logic (used by GUI Repair + CLI flags)
  VibeSuperTonic.TestHarness/   System.Speech smoke tests + SSML/event verification
external/
  supertonic-main/              Upstream Supertonic source (csharp/Helper.cs is what we wrap)
build/
  pack-zip.ps1                  Build a release ZIP from publish outputs
samples/
  twenty-thousand-leagues.txt   Public-domain Verne excerpt for the Benchmark tab
models-manifest.json            Manifest of model files (path, URL, SHA-256, bytes) for ModelDownloader
```

## Building from source

```powershell
git clone https://github.com/Hananel-Hazan/VibeSuperTonic.git
cd VibeSuperTonic
# .NET 10 SDK required
dotnet publish src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj -c Release -r win-x64 --no-self-contained
dotnet publish src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj -c Release -r win-x86 --no-self-contained
dotnet publish src\VibeSuperTonic.Launcher\VibeSuperTonic.Launcher.csproj -c Release -r win-x64
.\build\pack-zip.ps1                    # composes dist\VibeSuperTonic-<version>-win.zip
```

The first run downloads the Supertonic ONNX models (~380 MB) from Hugging Face into `models\onnx\` and `models\voice_styles\`. The Status tab also writes optimized graph copies under `models\onnx-optimized\` on first ORT load — subsequent cold starts skip the optimization pass entirely.

## Limitations

- **DSP rate cap 2.0×** — Sonic's crossfade quality degrades sharply past that as the source pitch periods are sampled too sparsely. Lifting it would need a different algorithm class (e.g., a true PSOLA with explicit F0 tracking, or a commercial Élastique-class library).
- **Engine speed locked at 1.0×** — the Supertonic model under-renders the trailing phoneme above 1.0×. All speedup goes through the DSP path instead.
- **First-byte latency 2-5 s on CPU** (less on GPU) — model is heavy on first load.
- **Windows is English only** — Supertonic supports 31 languages but voice tokens for other languages aren't registered yet. On Linux the Tune tab sets the language per voice, and Piper adds 35 more.
- **No pitch / phoneme override** — Supertonic is graphemic with no pitch parameter.
- **Multi-adapter GPU selection via Windows Settings** — DirectML's device-id mapping doesn't match any single DXGI enumeration on all systems, so the in-app picker was removed in favor of Windows Graphics Settings.

## Roadmap

- [x] Phase 1: Control Panel GUI replacing the CLI launcher
- [x] Phase 2: registry-backed settings + live telemetry
- [x] Phase 3: clean 0.5×–2× DSP time-stretch (phase vocoder → Sonic PSOLA after perceptual A/B)
- [x] Phase 3: GPU acceleration via DirectML
- [x] Phase 4: portable data folder — settings.json, logs, and per-PID session telemetry under `<install>\data\`, user-configurable
- [x] Phase 4: live multi-client Monitor tab with per-session reset (no need to kill Lingoes/Balabolka/etc. when the engine wedges)
- [x] Phase 4: GPU device-loss recovery — auto-rebuild ONNX session on TDR / driver reset, with CPU latch after repeated failures
- [ ] Phase 4: parallel ONNX sessions (x64 only, doubles RAM, eliminates inter-chunk gaps on CPU)
- [x] Phase 4: more languages — all 31 the model speaks, as a global or per-voice setting in the Tune tab; SSML `xml:lang` overrides it per passage, and voice tokens advertise every language so clients can find the voice for non-English text
- [ ] Phase 4: pitch shifting (separate from time-stretch)
- [x] **Phase 5: pronunciation dictionary** *(shipped in 0.2.2 as the Pronunciations tab)* — user-editable rewrite table (regex / whole-word) applied before chunking, so abbreviations, symbols, and proper nouns the model mispronounces ("etc." → "et cetera", "kg" → "kilograms", "Tcl" → "tickle", "—" → " — ", "i.e." → "that is", domain jargon, names) come out right. Per-voice and per-language scopes; lives in `data\dictionary.json` so it's portable. Control Panel tab to edit + test entries against a live sample. Probably backs onto the same chunker-pre-pass that already does emoji stripping in `UnicodeProcessor.PreprocessText`.
- [ ] Phase 5: signed binaries (avoids SmartScreen prompt on first run)
- [x] **Phase 6: shared `VibeSuperTonic.Core`** *(0.2.7)* — the text pipeline, DSP, model manifest/downloader and telemetry contract extracted into one platform-neutral assembly with 187 tests, so the engine and the Control Panel stop keeping duplicate copies of the same logic in sync by hand. Fixed two live word-boundary offset bugs on the way out.
- [x] **Phase 6: verification harness in the box** *(0.2.7.4)* — `tools\VibeSuperTonic.TestHarness.exe`, which drives the engine through a real SAPI client and checks the offsets a highlight depends on. It found a bug on its first run that five releases had shipped.
- [x] **Phase 6: Linux port** *(shipped 0.2.8 as a tarball, 0.2.9 added the AppImage)* — background daemon, global hotkey, reads the highlighted text (`docs/LINUX-PORT-PLAN.md`): daemon, `vst-ctl` client, selection capture, hotkeys, per-machine benchmark, a Reader window with a live highlight and click-a-word-to-jump, a tray icon, and a first-run screen that downloads the voices. The model renders ~25% faster on Linux than on Windows on the same machine.
- [x] **Phase 7: Piper as a second voice family** *(shipped 0.2.11, Linux only)* — beside Supertonic and never replacing it: **65 hash-pinned voices across 35 languages**, ~63 MB per voice against Supertonic's ~830 MB resident. It runs Piper's ONNX graph on the runtime already in the box rather than embedding Piper, so the engine takes no new dependency — and the two research phases that could have ended it both passed: the graph renders byte-identically to `python -m piper`, and phoneme parity against piper's own output is **327 sentences, 8 languages, zero divergences**, re-run on every push. `docs/PIPER-PLAN.md`.
- [x] **Phase 8: Speech Dispatcher module** *(shipped 0.2.11, Linux only)* — the voices reach Orca and every other speechd client, through a native module rather than a shell wrapper. It refuses to register an install with no voices, and it puts every other module back, because adding one to a user configuration otherwise removes them all. `docs/SPEECHD-PLAN.md`.
- [ ] Windows catches up: the audit in `docs/WINDOWS-PLAN.md` found ten things, four of them fixed. What is left is a benchmark tab that ranks presets on noise, a DirectML default that has never been shown to win, split-bitness upgrade safety, and two settings schemas that already disagree.

## Contributing

Issues and PRs welcome. The engine intentionally avoids dependencies beyond Supertonic and ONNX Runtime — keep it that way unless there's a strong reason. The Linux port adds exactly one: Avalonia, in the `vibesupertonic-ui` window and nowhere else — not in `VibeSuperTonic.Core`, and not in the daemon, which has to start on a machine with no display. The Windows engine is unaffected and ships no new dependency. Test changes with the harness:

```powershell
dotnet build src\VibeSuperTonic.TestHarness\VibeSuperTonic.TestHarness.csproj -c Release
.\src\VibeSuperTonic.TestHarness\bin\Release\net10.0-windows\VibeSuperTonic.TestHarness.exe
```

All steps should pass.

The project's design notes and lessons learned (SAPI interop quirks, EngineSiteSapi.Write `pcbWritten` bug, sample-rate landmines, DirectML adapter-id mismatch, etc.) live in the spike plan — ask if you want a copy.

## License

- **This repository's code**: MIT — see [LICENSE](LICENSE). No `.cs` file has changed licence.
- **The Linux archive as a whole, from 0.2.11 onward: GPL-3.0-or-later**, because it distributes espeak-ng as the phonemiser the Piper voices need. `LICENSE-PHONEMIZER.txt` inside the archive carries the terms and the written offer for its source, which is reproducible from `build/build-espeak.sh`. MIT is GPL-compatible, so the source stays MIT and the *archive* is what carries the stronger terms. **Releases up to and including 0.2.10 contain no espeak-ng and are unaffected — this is not retroactive**, and the Windows ZIP ships no phonemiser and is not affected at all.
- **Supertonic models**: [OpenRAIL-M](https://huggingface.co/Supertone/supertonic-3) (Supertone's terms). Nothing redistributes the models — they download at install time so end users accept the licence directly.
- **Piper voices**: each carries its own, shown in the Voices tab before anything downloads. Several are NonCommercial; the catalog records the licence per voice and the packer refuses one that states none.
- **ONNX Runtime / DirectML**: MIT (Microsoft)

## Credits

- [Supertone](https://supertone.ai/) for the [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS model
- Microsoft for SAPI 5, ONNX Runtime, and DirectML
- [Rhasspy](https://github.com/rhasspy/piper) for Piper and its voices, and the voice contributors each `MODEL_CARD` names
- [espeak-ng](https://github.com/espeak-ng/espeak-ng), which is what turns text into the phonemes a Piper voice was trained on
- The [Speech Dispatcher](https://freebsoft.org/speechd) project, whose module protocol is what puts these voices in front of a screen reader
- The .NET ComHosting team for making pure-C# COM servers tractable

# VibeSuperTonic

> **Heads up — this is my first vibe-coding project.** I've wanted a good neural voice in plain old SAPI for a long, long time and never had a free weekend. Huge thanks to [Supertone](https://supertone.ai/) for [Supertonic](https://github.com/supertone-inc/supertonic) — the actually-hard part (the model) is theirs. And shout-out to Claude Opus, who in roughly one day of pair-debugging turned "I want this" into a thing that ships.
>
> **Work in progress, please be gentle.** A few rough edges to know about up front:
>
> - **First run needs administrator rights.** Hooking a voice into Windows SAPI requires writing the voice token under `HKLM` — that's a system-wide registry hive and Windows guards it with UAC. The launcher self-elevates, you'll see the standard UAC prompt once, and after that the folder is fully portable (move it anywhere, no admin needed).
> - **First sentence is slow.** The engine loads ~380 MB of ONNX models into memory on first use; expect 2-5 s of "did it crash?" silence before the first word. Subsequent sentences start in well under a second.
> - **CPU is doing all the work.** Synthesis runs entirely on your processor. If your laptop fan kicks on, that's why. (This is exactly the problem GPUs were made for. Supertonic's SDK has GPU support flagged "not implemented yet" — when that lands, this will fly.)
> - **Speed cap at 1.3×.** The model is well-behaved up to ~1.3× and starts dropping words past that. The slider stops there for a reason.
>
> If those tradeoffs are fine, you've got 10 surprisingly good neural voices that work in literally any SAPI app on Windows. Read on.

---

Portable Windows SAPI 5 TTS engine wrapping Supertone's [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS. Ten English voices that show up in any SAPI 5 client — Balabolka, NVDA, Microsoft Narrator, System.Speech, Edge Read Aloud, NaturallySpeaking, and so on.

The engine is written in pure C# / .NET 10 and registers via .NET ComHosting. The portable folder can live anywhere — USB stick, OneDrive, network share — and a one-time UAC prompt registers the voice tokens.

## Status

Working spike with 10 voices, full SAPI event surface (word boundaries, sentence boundaries, bookmarks, end-of-stream), per-fragment SSML rate control, sentence-level skip support, and pipelined synthesis for smooth long-form playback. See [Roadmap](#roadmap) for what's next.

## Features

- 10 English voices (M1–M5 male, F1–F5 female) at 44.1 kHz mono 16-bit
- Visible to **all** SAPI 5 clients — both 32-bit and 64-bit
- Portable: move the folder anywhere, re-run the launcher, no admin needed after first install
- Hybrid registration: HKLM voice token (one-time admin write) + HKCU CLSID (rewritten on every launch from current path)
- Pipelined synthesis — next sentence renders while the current one plays, eliminating mid-paragraph gaps
- SAPI rate slider works (0.9× – 1.3×, model-safe range)
- Volume control honored
- Word/sentence boundary events fire correctly (highlight-while-reading apps work)
- Bookmark events for SSML `<mark>` tags
- SSML `<prosody rate>` honored per-fragment (different sentences can have different rates)
- Sentence-level skip via `SPVES_SKIP` (Pause/Resume + Skip Sentence in SAPI clients)

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — install x64 (and x86 if you want 32-bit SAPI clients to see the voices)
- ~500 MB disk space (380 MB ONNX models + ~50 MB engine + runtime)
- ~1 GB RAM during synthesis

## Installation (end user)

1. Download the latest `VibeSuperTonic-<version>-win.zip` from [Releases](https://github.com/your-username/VibeSuperTonic/releases).
2. Extract anywhere — your home folder, `C:\Tools`, a USB stick, all fine.
3. Double-click `VibeSuperTonic.exe`.
4. Accept the UAC prompt (one-time — writes voice tokens to HKLM).
5. The launcher reports success and lists 10 voices.
6. Open any SAPI client (Balabolka, NVDA, Narrator, etc.) — voices appear as `VibeSuperTonic M1` … `VibeSuperTonic F5`.

If x86 .NET 10 Desktop Runtime is missing, the launcher prints a note and registers x64 only. To enable 32-bit clients (some Balabolka builds, Lingoes, etc.):

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10 --architecture x86 --force
```

Then re-run `VibeSuperTonic.exe` to register the x86 mirror.

### Moving the folder

Move the whole folder anywhere, then run `VibeSuperTonic.exe` once at the new location. The launcher detects its current path and updates HKCU registry entries — no admin needed.

### Uninstall

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

The rate slider works in any SAPI client (effective range 0.9× to 1.3×). Volume is also honored.

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

## Architecture

```
SAPI client (Balabolka, NVDA, …)
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
   ├── Walks SPVTEXTFRAG list → typed Speak plan
   ├── Sentence-chunks text + balances chunk sizes
   ├── Synthesizes via SupertonicAdapter (ONNX shared across voices)
   ├── Pipelines synth(N+1) with write(N)
   ├── Emits word/sentence/bookmark/end-of-stream events
   └── Streams 16-bit PCM via raw vtable to ISpTTSEngineSite::Write
```

### Hybrid registration

```
HKLM\SOFTWARE\Microsoft\Speech\Voices\Tokens\VibeSuperTonic_<id>      ← static, written once with admin
HKLM\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens\VibeSuperTonic_<id>  ← static OneCore mirror (Narrator/Edge)
HKCU\SOFTWARE\Classes\CLSID\{F2A8C7B1-…}\InprocServer32                ← rewritten on every launch
HKCU\SOFTWARE\VibeSuperTonic\BaseDir                                   ← current portable folder path
```

Voice tokens stay valid forever — they just point at our CLSID. The CLSID's actual file path lives in HKCU and is updated whenever you run the launcher from a new location.

## Project structure

```
src/
  VibeSuperTonic.Engine/        Pure-C# SAPI engine (ComHosting → comhost.dll)
    Interop/                    SAPI COM interfaces, structs, constants
    Synth/                      Supertonic SDK wrapper, time-stretching DSP
    SapiEngine.cs               ISpTTSEngine + ISpObjectWithToken implementation
  VibeSuperTonic.Launcher/      Self-elevating registration EXE (UAC for HKLM)
  VibeSuperTonic.TestHarness/   System.Speech smoke tests + SSML/event verification
external/
  supertonic-main/              Upstream Supertonic source (csharp/Helper.cs is what we wrap)
build/
  pack-zip.ps1                  Build a release ZIP from publish outputs
```

## Building from source

```powershell
git clone https://github.com/your-username/VibeSuperTonic.git
cd VibeSuperTonic
# .NET 10 SDK required
dotnet publish src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj -c Release -r win-x64 --no-self-contained
dotnet publish src\VibeSuperTonic.Engine\VibeSuperTonic.Engine.csproj -c Release -r win-x86 --no-self-contained
dotnet publish src\VibeSuperTonic.Launcher\VibeSuperTonic.Launcher.csproj -c Release -r win-x64
.\build\pack-zip.ps1                    # composes dist\VibeSuperTonic-<version>-win.zip
```

The first run downloads the Supertonic ONNX models (~380 MB) from Hugging Face into `models\onnx\` and `models\voice_styles\`.

## Limitations

- **Speed cap 1.3×** — Supertonic's model drops words above ~1.34× synth speed. We empirically cap at 1.3 for clean output. Lifting this requires a phase vocoder or SoundTouch integration.
- **First-byte latency 2-5 s on CPU** — model is CPU-bound; GPU mode is marked "not implemented" in the Supertonic SDK.
- **English only** — Supertonic supports 31 languages but voice tokens for other languages aren't registered yet.
- **Inter-sentence gaps at high rates** — pipeline can't fully cover synth time when chunks are very small AND the rate is high. Eliminated by parallel ONNX sessions (deferred, x64 only).
- **No pitch / phoneme override** — Supertonic is graphemic with no pitch parameter.

## Roadmap

- [ ] Phase 4: phase vocoder / SoundTouch for clean 0.5×–2× speed range
- [ ] Phase 4: parallel ONNX sessions (x64 only, doubles RAM, eliminates inter-chunk gaps)
- [ ] Phase 5: GPU support (when Supertonic SDK lands it)
- [ ] Phase 5: more languages (31 supported by model)
- [ ] Phase 5: settings GUI for voice preview, default voice, model location

## Contributing

Issues and PRs welcome. The engine intentionally avoids dependencies beyond Supertonic and ONNX Runtime — keep it that way unless there's a strong reason. Test changes with the harness:

```powershell
dotnet build src\VibeSuperTonic.TestHarness\VibeSuperTonic.TestHarness.csproj -c Release
.\src\VibeSuperTonic.TestHarness\bin\Release\net10.0-windows\VibeSuperTonic.TestHarness.exe
```

All 6 steps should pass.

The project's design notes and lessons learned (SAPI interop quirks, EngineSiteSapi.Write `pcbWritten` bug, sample-rate landmines, etc.) live in the spike plan — ask if you want a copy.

## License

- **Code**: MIT — see [LICENSE](LICENSE)
- **Supertonic models**: [OpenRAIL-M](https://huggingface.co/Supertone/supertonic-3) (Supertone's terms). The launcher does not redistribute the models — it downloads them at install time so end users accept the license directly.
- **ONNX Runtime**: MIT (Microsoft)

## Credits

- [Supertone](https://supertone.ai/) for the [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS model
- Microsoft for SAPI 5 and ONNX Runtime
- The .NET ComHosting team for making pure-C# COM servers tractable

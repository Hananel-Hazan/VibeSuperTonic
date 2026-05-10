# VibeSuperTonic

> **Heads up — this is my first vibe-coding project.** I've wanted a good neural voice in plain old SAPI for a long, long time and never had a free weekend. Huge thanks to [Supertone](https://supertone.ai/) for [Supertonic](https://github.com/supertone-inc/supertonic) — the actually-hard part (the model) is theirs. And shout-out to Claude Opus, who in roughly one day of pair-debugging turned "I want this" into a thing that ships.
>
> **Work in progress, please be gentle.** A few rough edges to know about up front:
>
> - **First run needs administrator rights.** Hooking a voice into Windows SAPI requires writing the voice token under `HKLM` — that's a system-wide registry hive and Windows guards it with UAC. The Control Panel self-elevates, you'll see the standard UAC prompt once, and after that the folder is fully portable (move it anywhere, no admin needed).
> - **First sentence is slow.** The engine loads ~380 MB of ONNX models into memory on first use; expect 2–5 s of "did it crash?" silence before the first word on CPU (less on GPU). Subsequent sentences start in well under a second.
> - **GPU is on by default, falls back to CPU automatically.** DirectML / Direct3D 12 inference if your hardware supports it — typical 2-5× speedup on iGPU, more on dGPU. If DirectML init fails (no DX12, driver issue, etc.) the engine quietly drops to CPU and keeps working.
> - **DSP rate up to 2.0×.** A phase-locked phase vocoder handles the speed-up cleanly. Quality starts to fall off around 1.6–1.8× depending on the voice — your mileage will vary.
>
> If those tradeoffs are fine, you've got 10 surprisingly good neural voices that work in literally any SAPI app on Windows. Read on.

---

Portable Windows SAPI 5 TTS engine wrapping Supertone's [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS. Ten English voices that show up in any SAPI 5 client — Balabolka, NVDA, Microsoft Narrator, System.Speech, Edge Read Aloud, NaturallySpeaking, Lingoes, and so on.

The engine is written in pure C# / .NET 10 and registers via .NET ComHosting. The portable folder can live anywhere — USB stick, OneDrive, network share — and a one-time UAC prompt registers the voice tokens. A full Control Panel (the same `VibeSuperTonic.exe`) handles install, integrity checks, knob tuning, live monitoring, and uninstall.

## Status

**v0.2.0** — out of "spike" stage. Working install with 10 voices, full SAPI event surface (word boundaries, sentence boundaries, bookmarks, end-of-stream), per-fragment SSML rate control, sentence-level skip support, pipelined synthesis for smooth long-form playback, GPU acceleration via DirectML, and a phase-locked phase vocoder for time-stretch up to 2.0×. See [Roadmap](#roadmap) for what's next.

## Features

- 10 English voices (M1–M5 male, F1–F5 female) at 44.1 kHz mono 16-bit
- Visible to **all** SAPI 5 clients — both 32-bit and 64-bit
- Portable: move the folder anywhere, re-run `VibeSuperTonic.exe`, no admin needed after first install
- **Control Panel GUI** — Status / Tune / Benchmark / Monitor / Advanced / About tabs in one EXE; CLI flags preserved for scripting (`--register`, `--unregister`, `--repair`, `--bench`, `--set k=v`)
- **GPU acceleration via DirectML** — opt-in (default on); falls back to CPU automatically on hardware/driver issues
- **Phase-locked phase vocoder** — time-stretch up to 2.0× without the robotic / dropped-syllable artifacts the original WSOLA had
- **Live engine telemetry** — RTF, latency, CPU/RAM, voice and resolved knob values, updated 5 Hz
- **Self-fixing install** — Status tab runs 12 integrity checks (registry, model files, .NET runtime, voice tokens) with one-click Repair; lock-aware (tells you which process is holding the engine DLLs)
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
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — install x64 (and x86 if you want 32-bit SAPI clients to see the voices)
- ~500 MB disk space (~380 MB ONNX models + ~50 MB engine + runtime)
- ~1 GB RAM during synthesis (CPU); ~400 MB VRAM additional when GPU is active
- (Optional, for GPU acceleration) any DirectX 12-capable adapter

## Installation (end user)

1. Download the latest `VibeSuperTonic-<version>-win.zip` from [Releases](https://github.com/Hananel-Hazan/VibeSuperTonic/releases).
2. Extract anywhere — your home folder, `C:\Tools`, a USB stick, all fine.
3. Double-click `VibeSuperTonic.exe`. The Control Panel opens.
4. The Status tab runs the integrity check on launch. Accept the UAC prompt if it asks (one-time — writes voice tokens to HKLM).
5. Once everything is green, open any SAPI client (Balabolka, NVDA, Narrator, etc.) — voices appear as `VibeSuperTonic M1` … `VibeSuperTonic F5`.

If x86 .NET 10 Desktop Runtime is missing, the Status tab marks that row yellow and prints a fix command. To enable 32-bit clients (some Balabolka builds, Lingoes, etc.):

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10 --architecture x86 --force
```

Then click Repair on that row.

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

The rate slider works in any SAPI client. For larger speed-ups, use the Tune tab's **DSP rate** knob (0.5×–2.0×, pitch-preserving phase vocoder) instead of pushing the SAPI rate past 1.3× — the model itself drops syllables above that, but the DSP path keeps audio clean.

### Tune tab

| Knob | What it does |
|---|---|
| **Quality preset** | Bundles totalStep into Draft / Balanced / Quality / HiFi (4 / 6 / 8 / 12 diffusion iterations) |
| **Diffusion steps** | Direct totalStep slider 2–16. Linear CPU cost — 8 takes 2× longer than 4 |
| **Engine speed** | Locked at 1.0× (the model truncates phonemes above 1.0; speedup goes through DSP) |
| **DSP rate** | Phase-locked phase vocoder time-stretch, 0.5×–2.0× |
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
   ├── Phase-vocoder time-stretch (Synth/TimeStretch.cs)
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
    Synth/                      Supertonic SDK wrapper, phase vocoder, FFT
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

- **DSP rate cap 2.0×** — phase vocoder quality degrades sharply past that. Lifting it requires a more complex algorithm (e.g., Élastique-class commercial pitch shifter).
- **Engine speed locked at 1.0×** — the Supertonic model under-renders the trailing phoneme above 1.0×. All speedup goes through the DSP path instead.
- **First-byte latency 2-5 s on CPU** (less on GPU) — model is heavy on first load.
- **English only** — Supertonic supports 31 languages but voice tokens for other languages aren't registered yet.
- **No pitch / phoneme override** — Supertonic is graphemic with no pitch parameter.
- **Multi-adapter GPU selection via Windows Settings** — DirectML's device-id mapping doesn't match any single DXGI enumeration on all systems, so the in-app picker was removed in favor of Windows Graphics Settings.

## Roadmap

- [x] Phase 1: Control Panel GUI replacing the CLI launcher
- [x] Phase 2: registry-backed settings + live telemetry
- [x] Phase 3: phase vocoder for clean 0.5×–2× speed range
- [x] Phase 3: GPU acceleration via DirectML
- [ ] Phase 4: parallel ONNX sessions (x64 only, doubles RAM, eliminates inter-chunk gaps on CPU)
- [ ] Phase 4: more languages (31 supported by model)
- [ ] Phase 4: pitch shifting (separate from time-stretch)
- [ ] Phase 5: signed binaries (avoids SmartScreen prompt on first run)

## Contributing

Issues and PRs welcome. The engine intentionally avoids dependencies beyond Supertonic and ONNX Runtime — keep it that way unless there's a strong reason. Test changes with the harness:

```powershell
dotnet build src\VibeSuperTonic.TestHarness\VibeSuperTonic.TestHarness.csproj -c Release
.\src\VibeSuperTonic.TestHarness\bin\Release\net10.0-windows\VibeSuperTonic.TestHarness.exe
```

All steps should pass.

The project's design notes and lessons learned (SAPI interop quirks, EngineSiteSapi.Write `pcbWritten` bug, sample-rate landmines, DirectML adapter-id mismatch, etc.) live in the spike plan — ask if you want a copy.

## License

- **Code**: MIT — see [LICENSE](LICENSE)
- **Supertonic models**: [OpenRAIL-M](https://huggingface.co/Supertone/supertonic-3) (Supertone's terms). The launcher does not redistribute the models — it downloads them at install time so end users accept the license directly.
- **ONNX Runtime / DirectML**: MIT (Microsoft)

## Credits

- [Supertone](https://supertone.ai/) for the [Supertonic](https://github.com/supertone-inc/supertonic) neural TTS model
- Microsoft for SAPI 5, ONNX Runtime, and DirectML
- The .NET ComHosting team for making pure-C# COM servers tractable

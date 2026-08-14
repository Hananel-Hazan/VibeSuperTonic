# VibeSuperTonic v0.2.2

*Still cooking — more in the oven.*

This release teaches the engine that "NOT" is not, in fact, an acronym; lets the Monitor tab count higher than one (yes, really); gives the GPU permission to fail gracefully instead of dragging your whole audio session down with it; and finally answers the question "how do I get this voice onto my phone?" with an **Export** tab that writes proper MP3 and AAC files.

Drop-in replacement for v0.2.1. No schema migration, no re-registration, no apology required.

---

## Added

- **Export tab.** A new tab between **Pronunciations** and **Benchmark** for converting text to a portable audio file you can send to a phone, tablet, or anything that plays MP3/AAC. Paste text, pick a voice, hit **Preview** to hear the first few seconds, then **Save…** to render the whole thing. Because this is an offline render, the defaults are pinned to *max quality*: TotalStep 16 (slider goes to 24), DirectML force-enabled regardless of the global GPU toggle, and the export's quality knobs don't touch your global Tune/SAPI defaults — they apply for the render and restore on completion. Pronunciation rules are honored automatically.
  - **Formats:** MP3 (up to 320 kbps) and AAC/.m4a (up to 192 kbps, the Microsoft AAC encoder's ceiling). Both are universally playable. Encoding goes through Media Foundation — zero extra binaries shipped.
  - **Chapter-length text supported.** Synthesis streams to a temp WAV and is transcoded on the fly; RAM stays bounded.
  - **GPU first, every time.** The engine's normal CPU fallback still kicks in if DirectML init genuinely can't proceed, but the export never *chooses* CPU on its own.
- **Pronunciations tab.** A new tab between **Tune** and **Benchmark** where you can tell the engine "say *this* instead of *that*" before the words ever reach the model. Per-rule **Whole word** and **Case** toggles, a master kill-switch, and a live **Test** pane so you can verify your fix before saving it. Rules persist to `<DataDir>\pronunciations.json` — share it, back it up, mail it to your friend with the same TTS quirks.
  - Stops the engine from spelling all-caps words letter by letter ("NOT" → set the replacement to `not!` and breathe a sigh of relief).
  - Handles glyphs the model has never heard of (`Bᵠ` → `B phi`).
  - Hot-reloaded on every sentence — no restart of the engine, the Control Panel, or your screen reader.

## Changed

- **The Monitor tab now shows every running engine, not just whichever one spoke last.** Telemetry moved from a single shared-memory slot to per-PID JSON files under `<DataDir>\sessions\`. Lingoes, Balabolka, NVDA, Word — all visible at once with their own live state.
- **The "Currently synthesizing" pane is no longer truncated at 4 KB.** A limit nobody asked for, finally retired alongside the shared-memory backend.
- **Reset selected** (Monitor tab) now writes a small sentinel file the engine watches for, so it can drop and rebuild its ONNX session on demand. The host process (Lingoes, etc.) does **not** need to restart.

## Fixed

- **A GPU hiccup no longer means audio death until restart.** When DirectML loses the device — Windows TDR, sleep/resume, the usual GPU drama — the engine used to politely report the error and then produce silence forever after. Now it dusts itself off, rebuilds the ONNX session, and retries the sentence. You hear a one-time blip; everything keeps working.
- **…unless the GPU is genuinely having a day.** A second device-loss inside 60 seconds flips a process-local latch that quietly rebuilds on CPU instead. Your saved settings stay GPU-on (a driver tantrum shouldn't downgrade your config); use **Reset selected** to give the GPU another shot once the driver has had its coffee. The Monitor tab's `LastError` column shows what happened.

## Upgrading

None of the work, all of the benefit:

1. Drop the new ZIP over your existing install (or use the pre-staged folder under `dist\release\VibeSuperTonic\`).
2. Run `VibeSuperTonic.exe` once if you want the registry's launcher path to refresh; otherwise, just keep going.

That's it. No `--unregister`, no UAC prompt, no clicking through migration dialogs.

---

## Under the hood

For the curious, and the people who scrolled this far on principle:

- **Pronunciations.** Rules are read by `PronunciationsCache` once per `pronunciations.json` mtime change, regexes pre-compiled, applied inside `BuildSpeakPlan` *before* chunking — so a rule can never be split across two chunks. Whole-word matching wraps the escaped match in `\b…\b`; .NET's regex treats Unicode letters (including U+1D60 modifier-letter small Greek phi) as word characters, so boundaries land correctly around exotic glyphs.
- **Export.** Two-stage pipeline. Stage 1 drives `SAPI.SpVoice` with its audio output bound to a `SAPI.SpFileStream` writing 44.1 kHz 16-bit mono WAV — the standard SAPI-to-file path, which means pronunciations, the engine's DirectML self-heal, and every other Speak-path behavior come along for free. Settings are mutated through `EngineSettingsRegistry` for the render and restored in a `finally` block (same pattern as Benchmark), so a crash mid-export never leaves your SAPI clients running at TotalStep 16. Stage 2 transcodes the WAV via Media Foundation's `SourceReader` → `SinkWriter` to MP3 (`MFAudioFormat_MP3`) or AAC (`MFAudioFormat_AAC`, raw payload type 0, AAC LC profile 0x29). Bitrates are clamped to the encoders' supported sets — the MS AAC encoder only accepts 96/128/160/192 kbps, so a request for 256 lands on 192. Zero NuGet adds: `MfApi.cs` declares the minimal COM interfaces (`IMFMediaType`, `IMFSourceReader`, `IMFSinkWriter`) with stub slots for the vtable methods we don't call.
- **Telemetry.** Per-PID JSON files at `<DataDir>\sessions\<pid>.json`, atomic tmp+rename per write. Mtime doubles as a liveness signal (>5 s old = host probably crashed). Graceful process exits delete their own file via `AppDomain.ProcessExit`.
- **DirectML self-heal.** Device-loss exceptions are caught in `SupertonicAdapter`, the shared session is disposed and rebuilt, and the failing synthesis retries once. A sliding window of loss timestamps drives the latch (threshold = 2 inside 60 s). Never persisted to disk.
- **Reset signal.** The launcher writes `<pid>.reset` next to the session file; the engine polls for it on each telemetry tick and deletes the marker after acting on it.

## Known quirk

SAPI word-boundary events (used by some screen readers to underline the spoken word) report offsets in the *original* source text. If a pronunciation rule changes the substituted text's length, the underline may land a few characters off the spoken word. Audio is unaffected — only the visual cursor tells a small lie.

---

## VibeSuperTonic v0.2.1

DSP-path overhaul. The phase vocoder shipped in v0.2.0 produced audible artifacts at higher DSP rates that no amount of parameter tuning could escape — five rounds of perceptual A/B converged on a configuration that was *the* best phase vocoder configuration for this material, and was still not good enough. This release swaps it out for **Sonic** (Bill Cook's BSD-licensed library, ported to pure C#), a pitch-synchronous overlap-add algorithm purpose-built for speech speedup. End result: the voice sounds dramatically closer to the original at 1.5×–2× DSP rate. Same SAPI surface, same install, same settings (minus the now-defunct `VocoderMode` knob).

This release is a drop-in replacement for `0.2.0`. **Everyone should re-run `VibeSuperTonic.exe` once after upgrading** — a one-shot schema migration (v4→v5) cleans the stale `VocoderMode` field out of your `settings.json`.

---

### Highlights (v0.2.1)

- **DSP path replaced: phase vocoder → Sonic (PSOLA)**. Each output pitch period is bit-perfect from the input, so the voice's spectral envelope (formants) is preserved sample-for-sample. No more "voice character changed" feeling at speed-up.
- **SIMD inner loop**. Sonic's AMDF pitch-detection inner loop (the hot path — ~225K abs-diff operations per pitch-period decision) is vectorized via `System.Numerics.Vector<int>` + `Vector.Widen` + `Vector.Abs`. 4–8× speedup on AVX2, 8–16× on AVX-512.
- **Source tree cleanup**. Phase vocoder, FFT, and the 28-mode `VocoderMode` registry knob are gone. `TimeStretch.cs` shrinks from ~500 lines to ~50 (thin wrapper over `Sonic`). Net deletion of ~700 lines.

---

### DSP (time-stretch)

#### Why the phase vocoder lost

Five rounds of user-driven perceptual A/B over the phase-vocoder configuration space:

- **Round 1**: 13 modes covering frame size, peak radius, locking on/off — winner was mode 14 (frame 2048, hop 256, radius 2, hard locking — 87.5% overlap version of Laroche-Dolson).
- **Round 2**: pushed overlap further (hop 128), tried large/small FFTs and soft Gaussian locking — saturation on overlap, distortion on the others.
- **Round 3**: peak-prominence filtering, transient-reset disable, low-magnitude bypass — the *transient reset detector was over-firing on voiced micro-onsets and adding artifacts*. Disabling it (mode 20) won.
- **Round 4**: combined "no transient reset" + "low-mag bypass" → mode 22 became the new top.
- **Round 5**: threshold sweeps below perceptual discrimination, peak tracking broke OLA continuity. Plateau confirmed.

The remaining "voice character changed" artifact was a fundamental phase-vocoder failure mode (formant smearing from bin-quantized FFT representation + harmonic-phase tracking). It is not fixable within the phase vocoder family.

#### Sonic — what changed

Pitch-synchronous overlap-add. For each detected pitch period P (via AMDF over the 65–400 Hz range covering all human speech):

- At speed *s* ≥ 2: emit one crossfaded block of length P/(s−1), consume P + P/(s−1) input samples → ratio 1/s.
- At 1 < *s* < 2: emit one crossfaded block of length P, consume 2P, then copy P·(2−s)/(s−1) input samples verbatim — interleaves compression with passthrough to land on ratio 1/s overall.
- Unvoiced regions (fricatives, breath, silence) produce a best-fit period; crossfading noise with noise is acoustically benign.

Why this beats phase vocoder for speech:

- No bin-quantized frequency representation → no inter-bin phasing.
- Each output pitch period is bit-perfect from the input → formants stay put.
- No phase-tracking math that drifts across frames.
- No analysis frame size to smear transients across.

#### SIMD pitch detection

`Sonic.AmdfDiff` widens int16 samples to int32 (`Vector<short>` arithmetic wraps modulo 65536 — would corrupt diffs above ±32767), then does abs-difference and accumulation in `Vector<int>`. JIT picks the widest SIMD register available (SSE2: 4 lanes, AVX2: 8 lanes, AVX-512: 16 lanes). Period range is 110–678 samples at 44.1 kHz, so the inner loop dwarfs the tail handler.

---

### Schema migration (v4 → v5)

- Removes the `VocoderMode` property from `EngineSettings` (was used to select among phase-vocoder variants; meaningless now). Existing `settings.json` files with `VocoderMode` and `PerVoice[*].VocoderMode` deserialize harmlessly — the field is ignored on read and dropped on next write.
- `EnsureMigrated` triggers one such write on first launch, so the user's `settings.json` sheds the stale field without waiting for a Tune-tab edit.

No other settings change. No re-registration needed. SAPI clients keep seeing the same 10 voices, same CLSID, same registry layout.

---

### Removed (v0.2.1)

- `Synth/TimeStretch.cs`: phase vocoder family (28 modes, transient detection, peak locking, low-magnitude bypass). Replaced with a ~50-line wrapper around `Sonic`.
- `Synth/Fft.cs`: in-house radix-2 Cooley-Tukey FFT. No longer used.
- `EngineSettings.VocoderMode`: the knob that selected among phase-vocoder configurations. Gone from the schema, the registry parser, the CLI `--set` parser, the Tune tab, and the launcher's preserve-on-save logic.
- Tune tab dropdown for vocoder mode (only briefly visible during the round-1 A/B work; dropped before this release).

---

### Added (v0.2.1)

- `Synth/Sonic.cs`: ~200-line port of Bill Cook's Sonic, single-stream mono int16, both speedup and slowdown branches. BSD license preserved (Cook's original C source: [waywardgeek/sonic](https://github.com/waywardgeek/sonic)).
- SIMD AMDF via `System.Numerics.Vector<int>`.

---

### Bug fixes (carried forward from v0.2.0)

See the v0.2.0 section below for the long-standing audio-buffer-cut hardening, telemetry timing, and Restart-Manager lock-probe fixes. v0.2.1 introduces no regressions in those areas.

---

## VibeSuperTonic v0.2.0

First non-spike release. The original CLI Launcher has grown into a full-featured Control Panel; the engine has user-tunable knobs, GPU acceleration, and a phase-vocoder DSP path; and the SAPI handoff is hardened against the audio-buffer-cut bugs that would clip the last word of long passages.

This release replaces the `0.1.0-spike` build entirely. **Everyone should re-run `VibeSuperTonic.exe` once after upgrading** — a one-shot schema migration runs on first launch (clears stale per-voice overrides, enables GPU by default, locks engine speed at 1.0×).

---

### Highlights (v0.2.0)

- **Control Panel GUI** replaces the CLI Launcher. Six tabs: Status, Tune, Benchmark, Monitor, Advanced, About.
- **GPU acceleration** via DirectML — opt-in, falls back to CPU automatically. Typical 2-5× speedup on iGPU, more on dGPU.
- **Phase-locked phase vocoder** for time-stretch — replaces WSOLA. Cleanly handles 0.5×–2× without the robotic / dropped-syllable artifacts the old algorithm had. *(Note: superseded by Sonic in v0.2.1.)*
- **User-tunable engine knobs** (totalStep, DSP rate, volume trim, chunking, ONNX threads…) exposed via the Tune and Advanced tabs and the registry-backed settings system.
- **Live engine telemetry** in the Monitor tab — current voice, RTF, latency, CPU%, RAM, dropped-frame counter, all updated at 5 Hz.
- **Self-fixing install** — Status tab runs 12 integrity checks (registry, model files, .NET runtime, voice tokens) with one-click Repair.

---

### Control Panel

#### New tabs

- **Status** — green/red checklist of registry and file-system integrity, with per-row Repair buttons. Identifies which processes are holding engine DLLs (Restart Manager) so you know what to close before re-installing.
- **Tune** — quality presets (Draft / Balanced / Quality / HiFi), DSP rate (0.5×–2.0×), volume trim, default voice. Per-voice overrides via a scope dropdown.
- **Benchmark** — A/B-compare presets on a passage of text. Shows RTF, CPU %, peak RAM, and a green/yellow/red verdict per preset, with an inline guide explaining what each metric means.
- **Monitor** — live engine telemetry via shared-memory ring buffer.
- **Advanced** — Tier B knobs (chunking, inter-chunk silence, rate-clamp, ONNX threads), GPU toggle, "Danger zone" with Unregister.
- **About** — version, runtime info, project link.

#### Self-fixing install flow

- Manifest-driven model downloader with SHA-256 verification (`models-manifest.json`).
- Lock-aware repairs: refuses to overwrite engine DLLs while a SAPI client is using them; tells you exactly which process to close.
- Schema migration on launch — cleans stale settings from earlier builds without surprising the user.

#### CLI preserved

- `VibeSuperTonic.exe --register` — silent install (UAC if needed)
- `VibeSuperTonic.exe --unregister` — clean uninstall
- `VibeSuperTonic.exe --repair` — runs the full integrity-check pass headless
- `VibeSuperTonic.exe --bench` — JSON-output benchmark
- `VibeSuperTonic.exe --set <key>=<value>` — script any single knob

---

### Engine

#### GPU acceleration (DirectML)

- ONNX Runtime DirectML provider, opt-in via Advanced tab. **GPU is the new default** for fresh installs; existing installs are flipped on by the v1→v2 schema migration.
- Adds ~30 MB to the engine folder (DirectML.dll). Falls back to CPU automatically if DirectML init fails (no DX12 GPU, driver too old, etc.).
- Adapter selection: due to inconsistent device-ID mapping between DXGI's `EnumAdapterByGpuPreference` and ORT's DirectML provider on some systems, the in-app adapter picker has been removed. To choose iGPU vs dGPU on a multi-adapter machine, configure **Windows Settings → System → Display → Graphics → Add VibeSuperTonic.exe → "High performance" / "Power saving"**.

#### CPU performance

- ORT SessionOptions tuned: `GraphOptimizationLevel.ORT_ENABLE_ALL`, sequential execution mode, explicit inter-op = 1.
- **Optimized-graph cache**: ORT writes its post-fusion graph to `models/onnx-optimized/` on first run. Subsequent cold starts skip the optimization pass entirely (saves 1–3 s per session load).
- ONNX intra-op and inter-op thread counts are user-tunable (Advanced tab).
- WSOLA / phase-vocoder correlation hot loops vectorized with `System.Numerics.Vector<float>` (auto-selects AVX2 / AVX-512 / NEON).

#### Settings system

- New registry-backed settings tree at `HKCU\SOFTWARE\VibeSuperTonic\Settings\` with monotonic version counter.
- Engine reads settings from a cached snapshot, invalidated only when the version DWORD bumps. Per-`Speak` cost is microseconds (one registry DWORD read).
- Per-voice override support: any knob can be overridden for a specific voice token. Auto-pruning when overrides match global defaults.
- Schema-aware migrations: each launcher build runs forward-only migrations (v0→v1 prune, v1→v2 GPU default + clear stale per-voice, v2→v3 force `engineSpeed=1.0`).

#### Telemetry (v0.2.0)

- Engine writes a snapshot to `Local\VibeSuperTonic.Telemetry` shared memory every chunk: voice, current text fragment, resolved knob values, first-byte latency, rolling RTF, pipeline depth, inter-chunk gap, CPU %, RSS, ONNX threads, underrun counter, last error.
- Monitor tab reads this at 5 Hz when active; otherwise the timer pauses.

---

### DSP (time-stretch — v0.2.0 implementation)

The DSP path is the most user-visible change in this release.

#### Phase-locked phase vocoder

- Replaces the WSOLA implementation, which dropped trailing syllables at chunk boundaries and produced robotic / metallic artifacts above 1.5× stretch.
- Operates entirely in the frequency domain (STFT → modify magnitudes / phases → ISTFT → overlap-add). No frame-similarity search, no boundary anchor, no robotic / metallic artifacts.
- **Phase locking** (Laroche-Dolson): identifies spectral peaks (typically the fundamental + its harmonics in voiced speech) and locks surrounding bins' phases to the peak's phase + the original input phase offset. Eliminates the "phasy/reverby" distortion that basic phase vocoders produce on speech.
- Frame size 2048 (~46 ms at 44.1 kHz), synth hop 512 (75% overlap, COLA-clean), peak detection radius 3 (chosen after A/B testing on speech).
- A custom radix-2 Cooley-Tukey FFT (`Synth/Fft.cs`) keeps the engine self-contained — no FFT NuGet dependency.

#### Engine speed locked at 1.0×

- The Supertonic model's duration predictor under-renders the trailing phoneme at engine speeds > 1.0×, manifesting as the last word being clipped on every chunk.
- The engine speed slider is now disabled at 1.0× and the v2→v3 migration forces existing installs to 1.0×.
- All speedup goes through the DSP rate, which the new vocoder handles cleanly across the full 0.5×–2.0× range.

#### SAPI audio handoff hardening

A long-standing class of bugs where the last word of long passages would intermittently drop is now fixed. Three changes work together:

- **Real-time write throttle** in `StreamPcm`: writes to SAPI are paced so the buffer never holds more than ~100 ms of audio. Without this, we'd hand SAPI 5+ seconds of PCM in tens of ms; the audio device pulled at real-time and couldn't catch up before SAPI's "done" signal.
- **Tail-flush silence (700 ms)** before signaling end-of-stream — pads the audio so any buffer-cut from the SAPI client lands in silence, not in the last word.
- **Drain wait** at end of `Speak`: don't return until elapsed wall-clock time ≥ total audio duration since the first SAPI Write. Floor at 500 ms.

---

### Settings & defaults

- **`engineSpeed=1.0`** locked (was per-preset varying). All speedup via DSP.
- **Default preset = Balanced** (totalStep = 6, was 4).
- **Draft preset totalStep = 4** (was 2 — too aggressive, caused syllable drops).
- **Balanced preset totalStep = 6** (was 4).
- **`UseDirectML=true`** by default (was false). Falls back to CPU automatically.

---

### Bug fixes (v0.2.0)

- Phase-1 launcher CLI works again from PowerShell — `VibeSuperTonic.exe --register` now writes its log to the parent console (was silent because of the WinExe regression).
- `MonitorTab.Refresh()` no longer overrides `Control.Refresh()`; renamed to `RefreshTelemetry()`.
- `LockProbe` Restart Manager session-key uses `StringBuilder` (correct out-buffer marshaling).
- `BenchmarkTab` always restores prior settings on cancel/error (was leaking the benchmark's preset overrides).
- `Benchmark` no longer relies on `voice.Status.RealtimePosition` (which it polled too eagerly, exiting before audio actually started).
- `MonitorTab` timer paused when the tab isn't visible; Restart Manager probe throttled to 0.2 Hz from 5 Hz.
- `EngineSettings.Save` is auto-pruning: per-voice entries that exactly match global are dropped to prevent the "stale snapshot shadowing the global" class of bugs.
- `Repair` UX: per-row Repair buttons disable the list during operation, preventing concurrent registry mutations.
- `BenchmarkTab` cancellation token sources properly disposed across runs.
- `TimeStretch` tail handling no longer drops the trailing 30–80 ms of every chunk at high stretch factors.
- GUI window default size raised to 980×740 (was 760×600); per-voice scope dropdown widened so full voice names show.
- About-tab Unregister button moved into the Advanced tab's Danger Zone, where destructive actions belong.

---

### Project links

- Repo: [Hananel-Hazan/VibeSuperTonic](https://github.com/Hananel-Hazan/VibeSuperTonic)
- Phase plan: see `lets-jump-to-phase-recursive-newell.md` (kept for historical reference; this release implements all phases through 3)

---

### Migration notes (upgrading from 0.1.0-spike)

1. Close every SAPI client (Lingoes, Balabolka, NVDA, Narrator, etc.) and any old `VibeSuperTonic.exe`.
2. Replace the entire folder with the new ZIP contents (or run `pack-zip.ps1` from this commit).
3. Run `VibeSuperTonic.exe` once. The schema migration runs silently:
   - All per-voice overrides are cleared (legacy snapshots can shadow global with stale values).
   - `UseDirectML` is set to `true` (engine falls back to CPU if DML init fails).
   - `EngineSpeed` is forced to `1.0`.
4. Open the Status tab; click **Repair All** if anything is red.
5. (Optional) Open Windows Settings → System → Display → Graphics → Add `VibeSuperTonic.exe` → set **High performance** if you want the dGPU on a multi-adapter machine.

No breaking changes to SAPI clients — they continue to see 10 voices (M1–M5, F1–F5) registered identically.

---

### Known limitations (v0.2.0 — see v0.2.1 for updates)

- DSP rate >2.0× is clamped — phase vocoder quality degrades sharply past that. *(v0.2.1: same cap, different reason — Sonic's crossfade quality degrades past 2×.)*
- DirectML adapter selection is "the one Windows picks" — multi-adapter laptops use Windows Graphics Settings to choose, not an in-app picker.
- First synthesis after a cold start takes 2–5 s (ONNX model load, ~380 MB) on CPU; less on GPU. Subsequent syntheses are fast.
- The Supertonic model has no pitch-control parameter; pitch follows the model's voice embedding. SSML `<prosody pitch>` is ignored.
- Phoneme overrides (`<phoneme>`) are not supported by Supertonic.

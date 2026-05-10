# VibeSuperTonic v0.2.0

First non-spike release. The original CLI Launcher has grown into a full-featured Control Panel; the engine has user-tunable knobs, GPU acceleration, and a phase-vocoder DSP path; and the SAPI handoff is hardened against the audio-buffer-cut bugs that would clip the last word of long passages.

This release replaces the `0.1.0-spike` build entirely. **Everyone should re-run `VibeSuperTonic.exe` once after upgrading** — a one-shot schema migration runs on first launch (clears stale per-voice overrides, enables GPU by default, locks engine speed at 1.0×).

---

## Highlights

- **Control Panel GUI** replaces the CLI Launcher. Six tabs: Status, Tune, Benchmark, Monitor, Advanced, About.
- **GPU acceleration** via DirectML — opt-in, falls back to CPU automatically. Typical 2-5× speedup on iGPU, more on dGPU.
- **Phase-locked phase vocoder** for time-stretch — replaces WSOLA. Cleanly handles 0.5×–2× without the robotic / dropped-syllable artifacts the old algorithm had.
- **User-tunable engine knobs** (totalStep, DSP rate, volume trim, chunking, ONNX threads…) exposed via the Tune and Advanced tabs and the registry-backed settings system.
- **Live engine telemetry** in the Monitor tab — current voice, RTF, latency, CPU%, RAM, dropped-frame counter, all updated at 5 Hz.
- **Self-fixing install** — Status tab runs 12 integrity checks (registry, model files, .NET runtime, voice tokens) with one-click Repair.

---

## Control Panel

### New tabs
- **Status** — green/red checklist of registry and file-system integrity, with per-row Repair buttons. Identifies which processes are holding engine DLLs (Restart Manager) so you know what to close before re-installing.
- **Tune** — quality presets (Draft / Balanced / Quality / HiFi), DSP rate (0.5×–2.0×), volume trim, default voice. Per-voice overrides via a scope dropdown.
- **Benchmark** — A/B-compare presets on a passage of text. Shows RTF, CPU %, peak RAM, and a green/yellow/red verdict per preset, with an inline guide explaining what each metric means.
- **Monitor** — live engine telemetry via shared-memory ring buffer.
- **Advanced** — Tier B knobs (chunking, inter-chunk silence, rate-clamp, ONNX threads), GPU toggle, "Danger zone" with Unregister.
- **About** — version, runtime info, project link.

### Self-fixing install flow
- Manifest-driven model downloader with SHA-256 verification (`models-manifest.json`).
- Lock-aware repairs: refuses to overwrite engine DLLs while a SAPI client is using them; tells you exactly which process to close.
- Schema migration on launch — cleans stale settings from earlier builds without surprising the user.

### CLI preserved
- `VibeSuperTonic.exe --register` — silent install (UAC if needed)
- `VibeSuperTonic.exe --unregister` — clean uninstall
- `VibeSuperTonic.exe --repair` — runs the full integrity-check pass headless
- `VibeSuperTonic.exe --bench` — JSON-output benchmark
- `VibeSuperTonic.exe --set <key>=<value>` — script any single knob

---

## Engine

### GPU acceleration (DirectML)
- ONNX Runtime DirectML provider, opt-in via Advanced tab. **GPU is the new default** for fresh installs; existing installs are flipped on by the v1→v2 schema migration.
- Adds ~30 MB to the engine folder (DirectML.dll). Falls back to CPU automatically if DirectML init fails (no DX12 GPU, driver too old, etc.).
- Adapter selection: due to inconsistent device-ID mapping between DXGI's `EnumAdapterByGpuPreference` and ORT's DirectML provider on some systems, the in-app adapter picker has been removed. To choose iGPU vs dGPU on a multi-adapter machine, configure **Windows Settings → System → Display → Graphics → Add VibeSuperTonic.exe → "High performance" / "Power saving"**.

### CPU performance
- ORT SessionOptions tuned: `GraphOptimizationLevel.ORT_ENABLE_ALL`, sequential execution mode, explicit inter-op = 1.
- **Optimized-graph cache**: ORT writes its post-fusion graph to `models/onnx-optimized/` on first run. Subsequent cold starts skip the optimization pass entirely (saves 1–3 s per session load).
- ONNX intra-op and inter-op thread counts are user-tunable (Advanced tab).
- WSOLA / phase-vocoder correlation hot loops vectorized with `System.Numerics.Vector<float>` (auto-selects AVX2 / AVX-512 / NEON).

### Settings system
- New registry-backed settings tree at `HKCU\SOFTWARE\VibeSuperTonic\Settings\` with monotonic version counter.
- Engine reads settings from a cached snapshot, invalidated only when the version DWORD bumps. Per-`Speak` cost is microseconds (one registry DWORD read).
- Per-voice override support: any knob can be overridden for a specific voice token. Auto-pruning when overrides match global defaults.
- Schema-aware migrations: each launcher build runs forward-only migrations (v0→v1 prune, v1→v2 GPU default + clear stale per-voice, v2→v3 force `engineSpeed=1.0`).

### Telemetry
- Engine writes a snapshot to `Local\VibeSuperTonic.Telemetry` shared memory every chunk: voice, current text fragment, resolved knob values, first-byte latency, rolling RTF, pipeline depth, inter-chunk gap, CPU %, RSS, ONNX threads, underrun counter, last error.
- Monitor tab reads this at 5 Hz when active; otherwise the timer pauses.

---

## DSP (time-stretch)

The DSP path is the most user-visible change in this release.

### Phase-locked phase vocoder
- Replaces the WSOLA implementation, which dropped trailing syllables at chunk boundaries and produced robotic / metallic artifacts above 1.5× stretch.
- Operates entirely in the frequency domain (STFT → modify magnitudes / phases → ISTFT → overlap-add). No frame-similarity search, no boundary anchor, no robotic / metallic artifacts.
- **Phase locking** (Laroche-Dolson): identifies spectral peaks (typically the fundamental + its harmonics in voiced speech) and locks surrounding bins' phases to the peak's phase + the original input phase offset. Eliminates the "phasy/reverby" distortion that basic phase vocoders produce on speech.
- Frame size 2048 (~46 ms at 44.1 kHz), synth hop 512 (75% overlap, COLA-clean), peak detection radius 3 (chosen after A/B testing on speech).
- A custom radix-2 Cooley-Tukey FFT (`Synth/Fft.cs`) keeps the engine self-contained — no FFT NuGet dependency.

### Engine speed locked at 1.0×
- The Supertonic model's duration predictor under-renders the trailing phoneme at engine speeds > 1.0×, manifesting as the last word being clipped on every chunk.
- The engine speed slider is now disabled at 1.0× and the v2→v3 migration forces existing installs to 1.0×.
- All speedup goes through the DSP rate, which the new vocoder handles cleanly across the full 0.5×–2.0× range.

### SAPI audio handoff hardening
A long-standing class of bugs where the last word of long passages would intermittently drop is now fixed. Three changes work together:
- **Real-time write throttle** in `StreamPcm`: writes to SAPI are paced so the buffer never holds more than ~100 ms of audio. Without this, we'd hand SAPI 5+ seconds of PCM in tens of ms; the audio device pulled at real-time and couldn't catch up before SAPI's "done" signal.
- **Tail-flush silence (700 ms)** before signaling end-of-stream — pads the audio so any buffer-cut from the SAPI client lands in silence, not in the last word.
- **Drain wait** at end of `Speak`: don't return until elapsed wall-clock time ≥ total audio duration since the first SAPI Write. Floor at 500 ms.

---

## Settings & defaults

- **`engineSpeed=1.0`** locked (was per-preset varying). All speedup via DSP.
- **Default preset = Balanced** (totalStep = 6, was 4).
- **Draft preset totalStep = 4** (was 2 — too aggressive, caused syllable drops).
- **Balanced preset totalStep = 6** (was 4).
- **`UseDirectML=true`** by default (was false). Falls back to CPU automatically.

---

## Bug fixes

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

## Project links

- Repo: https://github.com/Hananel-Hazan/VibeSuperTonic
- Phase plan: see `lets-jump-to-phase-recursive-newell.md` (kept for historical reference; this release implements all phases through 3)

---

## Migration notes (upgrading from 0.1.0-spike)

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

## Known limitations

- DSP rate >2.0× is clamped — phase vocoder quality degrades sharply past that.
- DirectML adapter selection is "the one Windows picks" — multi-adapter laptops use Windows Graphics Settings to choose, not an in-app picker.
- First synthesis after a cold start takes 2–5 s (ONNX model load, ~380 MB) on CPU; less on GPU. Subsequent syntheses are fast.
- The Supertonic model has no pitch-control parameter; pitch follows the model's voice embedding. SSML `<prosody pitch>` is ignored.
- Phoneme overrides (`<phoneme>`) are not supported by Supertonic.

# Phase 0 spike — Linux CPU viability

The gate from [docs/LINUX-PORT-PLAN.md](../../docs/LINUX-PORT-PLAN.md). One
question: does ONNX Runtime's CPU provider render Supertonic fast enough on
linux-x64 to build the port on? Plan's exit criterion is **RTF ≤ 0.5 at
totalStep 8**.

Throwaway. It exists to produce one number.

## What it measures

- cold vs warm model load (the optimized-graph cache makes run 1 unrepresentative)
- RTF at totalStep 4 / 8 / 12, median of 3, on one ~200-char chunk
- intra-op thread sweep at totalStep 8
- peak RSS
- that the manifest-driven model download works on Linux

It links the **real** [SupertonicSdk.cs](../../src/VibeSuperTonic.Engine/Synth/SupertonicSdk.cs),
not a copy, and compiles it against the CPU-only ORT package without the
`ORT_DIRECTML` symbol — that combination is itself part of the test.

## Build

```powershell
# from the repo root, on Windows
dotnet publish spike\phase0-linux\Phase0.csproj -c Release -r linux-x64 --self-contained -o spike\phase0-linux\out\linux-x64
cd spike\phase0-linux\out
tar -czf vst-phase0-linux-x64.tar.gz -C linux-x64 .
```

Self-contained multi-file on purpose — no .NET install needed on the target, and
single-file + native ONNX is the exact combination that produced the
`0xc0000005` crash behind `RenderHost` on Windows.

## Run on the Mint box

Copy `vst-phase0-linux-x64.tar.gz` over, then:

```bash
mkdir -p ~/vst-phase0 && tar -xzf vst-phase0-linux-x64.tar.gz -C ~/vst-phase0
cd ~/vst-phase0
chmod +x vst-phase0          # tar from Windows/NTFS carries no exec bit
./vst-phase0                 # run 1 — downloads ~383 MB, pays graph optimization
./vst-phase0                 # run 2 — THIS is the number that counts
paplay phase0-out.wav        # confirm it sounds right, not just fast
```

Both runs print a verdict line. Report run 2.

### Flags

| Flag | Effect |
| --- | --- |
| `--no-download` | skip the model fetch (models already in `./models`) |
| `--no-thread-sweep` | skip the intra-op sweep, ~3 model reloads faster |

### If run 1 fails

- **`cannot execute binary file`** → forgot `chmod +x`.
- **`libonnxruntime.so: cannot open shared object file`** → the publish drop is
  incomplete; re-publish and re-tar.
- **download failures** → the manifest carries three mirrors per file and the
  tool falls through them automatically; a hard failure means no network or all
  four sources are down.
- **`FAIL LoadTextToSpeech threw`** → the interesting case. Paste the exception
  and stack trace; this is what Phase 0 exists to find.

## Result — PASS

Both runs measured on the same 20-CPU machine, CPU provider only.
Windows 2026-08-14; Mint 22.3 Zena / Cinnamon / X11, 2026-08-15.

| totalStep | Windows RTF | **Linux RTF** |
| --- | --- | --- |
| 4 | 0.130 | **0.102** |
| 8 | 0.252 | **0.193** ← gate, ≤ 0.50 |
| 12 | 0.356 | **0.278** |

Load: Windows 3.35 s cold / 0.58 s warm; Linux **1.00 s cold / 0.43 s warm**.
Peak RSS: Windows 1154 / 642 MB; Linux 1182 / **830 MB**.

Intra-op sweep at totalStep 8:

| `OnnxThreads` | Windows | Linux |
| --- | --- | --- |
| auto | 0.248 | **0.189** |
| 10 | 0.240 | 0.393 |
| 20 | 0.484 | 0.433 |

**Let ORT choose.** On Windows only full oversubscription hurt; on Linux any
manual value is ~2× worse than auto.

Running both sides isolated "Linux is slower" from "this machine is slower" —
and the answer turned out to be neither. Linux is ~25% *faster* here at every
step count, with a materially quicker cold start.

Model download: all 16 manifest entries (383 MB) fetched from the first mirror
with no fallbacks. Output WAV: 44100 Hz mono s16le, 12.82 s, peak 0.46 FS, no
clipping — the engine's native format, so Phase 2's PulseAudio sink resamples
nothing.

Spike's job is done. It can be deleted once Phase 1 starts; the numbers live in
[the plan](../../docs/LINUX-PORT-PLAN.md#phase-0--prove-the-model-runs--05-day--gate).

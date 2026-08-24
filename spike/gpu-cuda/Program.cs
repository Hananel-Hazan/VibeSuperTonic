using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Supertonic;

// Phase 8b's GPU gate, measured rather than reasoned about.
//
// Three things have to hold before a CUDA backend is worth building, and the
// plan fails the whole idea if any one of them does not:
//
//   1. at least 30% off FIRST-AUDIO latency,
//   2. no RTF regression,
//   3. a clean fall back to CPU when the driver or libraries are missing.
//
// This measures all three and prints a verdict. It writes no settings, builds no
// backend project and changes no product behaviour — the answer it produces is
// the input to that decision, not the decision.
//
// Run it with the CUDA and cuDNN shared libraries reachable:
//
//   LD_LIBRARY_PATH=$(ls -d ~/.cache/vst-gpu-spike/cuda12/nvidia/*/lib | tr '\n' ':') \
//     dotnet run --project spike/gpu-cuda -c Release -- --models ~/Apps/VibeSuperTonic/models
//
// Running it WITHOUT that variable is not a mistake; it is gate 3, and the
// spike says so rather than crashing.

string modelsRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    "Apps", "VibeSuperTonic", "models");
int totalStep = 6;          // what the live install runs — see plan note 3
string voice = "M4";
string language = "en";
int runs = 5;
int cpuThreads = 4;
string? cudaLibs = null;
int switchRounds = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--models" when i + 1 < args.Length: modelsRoot = args[++i]; break;
        case "--total-step" when i + 1 < args.Length: totalStep = int.Parse(args[++i]); break;
        case "--voice" when i + 1 < args.Length: voice = args[++i]; break;
        case "--lang" when i + 1 < args.Length: language = args[++i]; break;
        case "--runs" when i + 1 < args.Length: runs = int.Parse(args[++i]); break;
        case "--threads" when i + 1 < args.Length: cpuThreads = int.Parse(args[++i]); break;
        case "--cuda-libs" when i + 1 < args.Length: cudaLibs = args[++i]; break;
        case "--switch" when i + 1 < args.Length: switchRounds = int.Parse(args[++i]); break;
        case "--help" or "-h":
            Console.WriteLine("vst-gpu-spike [--models <dir>] [--total-step N] [--voice M4] "
                              + "[--lang en] [--runs 5] [--threads 4]");
            return 0;
    }
}

// Does pre-loading the CUDA libraries by absolute path stand in for
// LD_LIBRARY_PATH? It has to, if the provider pack is going to work in a daemon
// nobody launches from a wrapper script: libonnxruntime_providers_cuda.so has no
// RPATH (checked), so its NEEDED sonames are resolved by the loader, and the
// question is whether an already-loaded object with the right soname satisfies
// them. This flag is how that gets answered rather than assumed.
if (cudaLibs is not null)
{
    // ONLY the provider's direct NEEDED set, in dependency order. Pre-loading
    // the whole directory also works and then aborts at exit with a corrupted
    // heap about two runs in three — measured — because cuDNN's engine libraries
    // are its own to dlopen when it wants them, and loading them out from under
    // it gives their initialisers a different order than the one they were built
    // for. The seven below are the ones ldd names on
    // libonnxruntime_providers_cuda.so; everything else resolves through cuDNN's
    // own $ORIGIN rpath from the same directory.
    string[] needed =
    [
        "libcudart.so.12", "libcublasLt.so.12", "libcublas.so.12",
        "libcufft.so.11", "libcurand.so.10", "libnvrtc.so.12", "libcudnn.so.9",
    ];

    int loaded = 0;
    foreach (string name in needed)
    {
        string lib = Path.Combine(cudaLibs, name);
        if (!File.Exists(lib)) { Console.WriteLine($"  missing {name}"); continue; }
        // dlopen directly rather than NativeLibrary.Load, for the two flags it
        // does not expose. RTLD_GLOBAL puts the object where the loader will find
        // it when it resolves the provider's NEEDED sonames; RTLD_NODELETE says
        // never unload it — and NODELETE is the one that matters, because
        // pre-loading through NativeLibrary.Load aborted at exit with a corrupted
        // heap in three runs out of three, while the identical directory reached
        // through LD_LIBRARY_PATH did not. Something unloads these underneath
        // CUDA's own teardown; NODELETE takes that possibility away.
        IntPtr handle = Dl.Open(lib, Dl.Lazy | Dl.Global | Dl.NoDelete);
        if (handle == IntPtr.Zero) Console.WriteLine($"  could not load {name}: {Dl.Error()}");
        else loaded++;
    }
    Console.WriteLine($"pre-loaded {loaded} of {needed.Length} libraries from {cudaLibs}");
}

string onnxDir = Path.Combine(modelsRoot, "onnx");
string stylePath = Path.Combine(modelsRoot, "voice_styles", voice + ".json");
if (!Directory.Exists(onnxDir)) { Console.Error.WriteLine($"no onnx/ under {modelsRoot}"); return 2; }
if (!File.Exists(stylePath)) { Console.Error.WriteLine($"no voice style at {stylePath}"); return 2; }

// The first chunk of a real utterance is one sentence, and first audio is what
// the user waits for. Deliberately short: a long sample would let throughput
// hide the fixed per-inference cost, which is the entire question here.
const string FirstChunk = "This is the first sentence of a longer reading.";

// The sweep's own sample, so the RTF number is comparable with `vst-ctl benchmark`.
const string Sample =
    "This machine is measuring itself. The middle of three runs is the number that counts.";

Console.WriteLine($"models      {modelsRoot}");
Console.WriteLine($"totalStep   {totalStep}   voice {voice}   lang {language}   runs {runs}");
Console.WriteLine($"LD_LIBRARY_PATH {(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") is { Length: > 0 } p ? p : "(unset)")}");
Console.WriteLine();

var style = Helper.LoadVoiceStyle([stylePath]);

// THE DAEMON'S QUESTION, not the gate's: the battery rule disposes a live CUDA
// session and builds a CPU one in a process that then keeps running for days. If
// tearing down a CUDA session corrupts the heap, that is a daemon that dies on
// the first unplug — so this loop does the switch, repeatedly, and renders after
// each one to prove the survivor works.
if (switchRounds > 0)
{
    for (int round = 1; round <= switchRounds; round++)
    {
        foreach (string provider in new[] { "cuda", "cpu" })
        {
            long t = Stopwatch.GetTimestamp();
            using (var tts = provider == "cuda" ? BuildCuda() : Helper.LoadTextToSpeech(onnxDir, useGpu: false, intraOpThreads: cpuThreads))
            {
                double buildMs = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                long r = Stopwatch.GetTimestamp();
                var (wav, _) = tts.Call(FirstChunk, language, style, totalStep);
                Console.WriteLine($"round {round} {provider}: built in {buildMs:F0} ms, "
                                  + $"rendered {wav.Length / (double)tts.SampleRate:F2} s of audio in "
                                  + $"{Stopwatch.GetElapsedTime(r).TotalMilliseconds:F0} ms");
            }
            Console.WriteLine($"round {round} {provider}: session disposed, process still here");
        }
    }
    Console.WriteLine("switch test survived");
    return 0;
}

var cpu = Measure("cpu", () => Helper.LoadTextToSpeech(onnxDir, useGpu: false, intraOpThreads: cpuThreads));
var gpu = Measure("cuda", BuildCuda);

Console.WriteLine();
Console.WriteLine("provider   load      first-audio   RTF     audio");
Console.WriteLine("-------------------------------------------------------");
foreach (var r in new[] { cpu, gpu })
{
    if (r.Error is not null) { Console.WriteLine($"{r.Name,-10} FAILED — {r.Error}"); continue; }
    Console.WriteLine($"{r.Name,-10} {r.LoadMs,6:F0} ms  {r.FirstAudioMs,8:F0} ms   {r.Rtf,5:F3}   {r.AudioSeconds,4:F1} s");
}

Console.WriteLine();
if (gpu.Error is not null)
{
    // Gate 3, and the only gate this branch can answer. A missing library must
    // read as one sentence a person can act on, not as a stack trace or a
    // SIGSEGV — because in the product this path is a hotkey that goes quiet.
    Console.WriteLine("VERDICT: CPU-only.");
    Console.WriteLine($"  CUDA could not be initialised: {gpu.Error}");
    Console.WriteLine("  Gate 3 (clean fall back to CPU) — PASS: the failure is an exception with a");
    Console.WriteLine("  readable message, caught above, with the CPU path unaffected in the same process.");
    return 0;
}

double firstAudioSaved = 1 - (gpu.FirstAudioMs / cpu.FirstAudioMs);
bool gate1 = firstAudioSaved >= 0.30;
bool gate2 = gpu.Rtf <= cpu.Rtf;

Console.WriteLine($"gate 1 — 30% off first audio : {(gate1 ? "PASS" : "FAIL")} "
                  + $"({firstAudioSaved * 100:F1}% off, {cpu.FirstAudioMs:F0} ms -> {gpu.FirstAudioMs:F0} ms)");
Console.WriteLine($"gate 2 — no RTF regression   : {(gate2 ? "PASS" : "FAIL")} "
                  + $"(cpu {cpu.Rtf:F3} -> cuda {gpu.Rtf:F3})");
Console.WriteLine($"gate 3 — clean CPU fallback  : run again with LD_LIBRARY_PATH unset");
Console.WriteLine();
Console.WriteLine($"VERDICT: {(gate1 && gate2 ? "GPU wins its gate — build the backend." : "CPU-only. Record it and do not revisit until the hardware changes.")}");
return 0;

TextToSpeech BuildCuda()
{
    // A GPU session cannot use the CPU-optimised graph cache — the optimisation
    // passes are provider-specific — so this loads from onnx/ with ORT_ENABLE_ALL
    // and pays that cost every time, which is part of what the load column is
    // measuring. Memory pattern and the CPU arena are off for the same reason
    // LoadTextToSpeech turns them off on its DirectML path.
    var opts = new SessionOptions
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        EnableMemoryPattern = false,
        EnableCpuMemArena = false,
        InterOpNumThreads = 1,
        IntraOpNumThreads = cpuThreads,
    };

    // Throws if the provider cannot load — a missing libcudnn, a driver too old,
    // no device. That throw is gate 3 and is caught by the caller.
    opts.AppendExecutionProvider_CUDA(0);

    var cfgs = Helper.LoadCfgs(onnxDir);
    var (dp, enc, vec, voc) = Helper.LoadOnnxAll(onnxDir, opts);
    var text = Helper.LoadTextProcessor(onnxDir);
    return new TextToSpeech(cfgs, text, dp, enc, vec, voc);
}

Result Measure(string name, Func<TextToSpeech> build)
{
    TextToSpeech? tts = null;
    try
    {
        long t0 = Stopwatch.GetTimestamp();
        tts = build();
        double loadMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        // Discarded, and it has to be: the first inference on a fresh CUDA
        // context pays kernel JIT and workspace allocation that a warm daemon
        // pays once in its life. Timing it would answer a question nobody asked.
        tts.Call(FirstChunk, language, style, totalStep);

        var first = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            long t = Stopwatch.GetTimestamp();
            tts.Call(FirstChunk, language, style, totalStep);
            first[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }

        var wall = new double[runs];
        double audioSeconds = 0;
        for (int i = 0; i < runs; i++)
        {
            long t = Stopwatch.GetTimestamp();
            var (wav, _) = tts.Call(Sample, language, style, totalStep);
            wall[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            audioSeconds = (double)wav.Length / tts.SampleRate;
        }

        double medianWall = Median(wall);
        return new Result(name, loadMs, Median(first), medianWall / 1000.0 / audioSeconds, audioSeconds, null);
    }
    catch (Exception ex)
    {
        return new Result(name, 0, 0, 0, 0, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
    finally
    {
        tts?.Dispose();
    }
}

static double Median(double[] values)
{
    var sorted = (double[])values.Clone();
    Array.Sort(sorted);
    int mid = sorted.Length / 2;
    return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
}

/// <summary>The two dlopen flags .NET does not expose. See the pre-load block.</summary>
internal static class Dl
{
    public const int Lazy = 0x00001;
    public const int Global = 0x00100;
    public const int NoDelete = 0x01000;

    [DllImport("libc", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr DlOpen(string file, int flags);

    [DllImport("libc", EntryPoint = "dlerror")]
    private static extern IntPtr DlError();

    public static IntPtr Open(string path, int flags) => DlOpen(path, flags);

    public static string Error() =>
        Marshal.PtrToStringAnsi(DlError()) ?? "unknown dlopen failure";
}

internal sealed record Result(
    string Name, double LoadMs, double FirstAudioMs, double Rtf, double AudioSeconds, string? Error);

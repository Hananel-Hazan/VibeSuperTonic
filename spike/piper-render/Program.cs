// Phase P0 — run a Piper voice on our own ONNX Runtime.
//
//     dotnet run --project spike/piper-render -- [--cuda] [--length-scale N] [-o out.wav]
//
// The voice is en_US-lessac-medium, fetched by hand into voice/ (see README).
// The phoneme ids are HARDCODED, captured from piper's own phonemizer — see
// reference-ids.json. That is deliberate and it is the point: P0 asks whether
// the GRAPH runs, and nothing else. Producing those ids ourselves is P1, which
// is the go/no-go, and mixing the two would let a phonemiser bug read as a
// graph that does not work.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VibeSuperTonic.Spike.PiperRender;

// bin/Debug/net10.0 back to the project directory. The voice sits beside the
// source rather than beside the binary because it is fetched by hand once and
// a `dotnet clean` should not take 61 MB with it.
var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var voiceDir = Path.Combine(projectDir, "voice");
// The id map is per-voice in principle and identical across lessac's tiers in
// fact — checked, 154 entries both — so one hardcoded id array serves both and
// `--voice` is only a filename.
var voiceName = ArgValue("--voice") ?? "en_US-lessac-medium";
var model = Path.Combine(voiceDir, voiceName + ".onnx");
var configPath = model + ".json";

var useCuda = args.Contains("--cuda");
var outPath = ArgValue("-o") ?? ArgValue("--out") ?? Path.Combine(projectDir, "out.wav");
// The three scales are overridable for one reason beyond curiosity: with the
// noise at zero the graph is deterministic, so our render and piper's own can be
// compared sample for sample. That is the difference between "it made speech"
// and "we are calling it exactly the way piper does".
var lengthScaleOverride = ArgValue("--length-scale") is { } ls ? float.Parse(ls) : (float?)null;
var noiseScaleOverride = ArgValue("--noise-scale") is { } ns ? float.Parse(ns) : (float?)null;
var noiseWOverride = ArgValue("--noise-w") is { } nw ? float.Parse(nw) : (float?)null;
var rawOut = args.Contains("--no-normalize");
// Applied AFTER the model, at the voice's own sample rate. length_scale
// saturates just under 2x (measured), so anything past that has to come from
// here — which is the one case the "speed comes from Piper" decision does not
// cover, because Piper cannot deliver it.
var stretch = ArgValue("--stretch") is { } st ? double.Parse(st) : 1.0;

if (!File.Exists(model))
{
    Console.Error.WriteLine($"""
        The voice is not in {voiceDir}.

        It is fetched by hand and never committed — 61 MB, and the same
        acceptance rule the Supertonic weights live under:

            B=https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/en/en_US/lessac/medium
            curl -sSL -o {model} $B/{voiceName}.onnx
            curl -sSL -o {configPath} $B/{voiceName}.onnx.json
        """);
    return 1;
}

// ---------------------------------------------------------------- the voice
//
// Everything the graph needs beyond the ids is in this file. `inference` holds
// the three scales in the order the model wants them, `audio.sample_rate` is
// what the voice was trained at — 22050 here, 16000 for a `low` voice — and
// `num_speakers` decides whether there is a fourth input at all.
using var configJson = JsonDocument.Parse(File.ReadAllText(configPath));
var cfg = configJson.RootElement;

var sampleRate = cfg.GetProperty("audio").GetProperty("sample_rate").GetInt32();
var numSpeakers = cfg.GetProperty("num_speakers").GetInt32();
var inference = cfg.GetProperty("inference");
var noiseScale = noiseScaleOverride ?? inference.GetProperty("noise_scale").GetSingle();
var lengthScale = lengthScaleOverride ?? inference.GetProperty("length_scale").GetSingle();
var noiseW = noiseWOverride ?? inference.GetProperty("noise_w").GetSingle();

Console.WriteLine($"voice        {voiceName}, {sampleRate} Hz, {numSpeakers} speaker(s), {new FileInfo(model).Length / 1e6:F0} MB");
Console.WriteLine($"scales       noise {noiseScale}, length {lengthScale}, noise_w {noiseW}");

// ------------------------------------------------------------------ the ids
//
// "The quick brown fox jumps over the lazy dog." through espeak-ng en-us, as
// piper assembles it: BOS, PAD, then every phoneme followed by PAD, then EOS.
// The interleave is not optional — a model fed a bare id sequence still renders
// audio, just wrong audio, which is the kind of failure that reads as "the
// route does not work".
long[] phonemeIds =
[
    1, 0, 41, 0, 59, 0, 3, 0, 23, 0, 35, 0, 120, 0, 74, 0, 23, 0, 3, 0, 15, 0,
    88, 0, 120, 0, 14, 0, 100, 0, 26, 0, 3, 0, 19, 0, 120, 0, 51, 0, 122, 0,
    23, 0, 31, 0, 3, 0, 17, 0, 108, 0, 120, 0, 102, 0, 25, 0, 28, 0, 31, 0, 3,
    0, 121, 0, 27, 0, 100, 0, 34, 0, 60, 0, 3, 0, 41, 0, 59, 0, 3, 0, 24, 0,
    120, 0, 18, 0, 74, 0, 38, 0, 21, 0, 3, 0, 17, 0, 120, 0, 51, 0, 122, 0, 66,
    0, 10, 0, 2,
];

// P2's one-row-per-process mode. Printed as TSV so the driver script can
// assemble the table without this program knowing what the table looks like.
if (args.Contains("--measure"))
{
    var row = Measure.Run(model, configPath, phonemeIds, useCuda,
        ArgValue("--repeat") is { } rr ? int.Parse(rr) : 5);
    Console.WriteLine(string.Join("\t",
        row.Voice, row.Provider, row.ModelMb,
        row.SessionBuildMs.ToString("F0"), row.FirstRunMs.ToString("F0"),
        row.WarmRunMs.ToString("F0"), row.AudioSeconds.ToString("F2"),
        row.RssAfterLoadMb, row.RssPeakMb));
    return 0;
}

if (args.Contains("--rate"))
{
    var dir = ArgValue("--rate-dir") ?? Path.Combine(projectDir, "rate");
    Console.WriteLine($"rate comparison -> {dir}");
    Rate.Render(model, configPath, phonemeIds, dir);
    return 0;
}

// --------------------------------------------------------------- the session
using var options = new SessionOptions();
if (useCuda)
{
    try
    {
        options.AppendExecutionProvider_CUDA(0);
        Console.WriteLine("provider     CUDA requested");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"provider     CUDA refused: {ex.Message.Split('\n')[0]}");
        return 2;
    }
}

var sw = Stopwatch.StartNew();
using var session = new InferenceSession(model, options);
var loadMs = sw.Elapsed.TotalMilliseconds;

Console.WriteLine($"inputs       {string.Join(", ", session.InputMetadata.Select(i => $"{i.Key}:{i.Value.ElementType.Name}[{string.Join(',', i.Value.Dimensions)}]"))}");
Console.WriteLine($"outputs      {string.Join(", ", session.OutputMetadata.Keys)}");
Console.WriteLine($"session      built in {loadMs:F0} ms");

// ------------------------------------------------------------------ the run
var inputs = new List<NamedOnnxValue>
{
    NamedOnnxValue.CreateFromTensor("input",
        new DenseTensor<long>(phonemeIds, [1, phonemeIds.Length])),
    NamedOnnxValue.CreateFromTensor("input_lengths",
        new DenseTensor<long>(new long[] { phonemeIds.Length }, [1])),
    NamedOnnxValue.CreateFromTensor("scales",
        new DenseTensor<float>(new[] { noiseScale, lengthScale, noiseW }, new[] { 3 })),
};

// The fourth input exists only on multi-speaker voices. Sending it to a
// single-speaker graph is an error, not a no-op.
if (numSpeakers > 1)
    inputs.Add(NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { 0 }, [1])));

// `--repeat` exists because the first inference on CUDA is not the number
// anyone cares about: it carries kernel selection and context setup that the
// second one does not. Reporting only a cold run would answer "is the GPU
// faster" with a measurement of the GPU starting up.
var repeat = ArgValue("--repeat") is { } r ? int.Parse(r) : 1;
var timings = new List<double>(repeat);
float[] raw = [];

for (var pass = 0; pass < repeat; pass++)
{
    sw.Restart();
    using var results = session.Run(inputs);
    timings.Add(sw.Elapsed.TotalMilliseconds);
    if (pass == repeat - 1) raw = results.First().AsEnumerable<float>().ToArray();
}

var runMs = timings[^1];

// Upstream peak-normalises and then clips. Reproduce it or chase a level
// difference that is not a bug.
var peak = 0f;
foreach (var s in raw) peak = Math.Max(peak, Math.Abs(s));
var gain = rawOut ? 1f : peak < 1e-8f ? 0f : 1f / peak;

var pcm = new short[raw.Length];
for (var i = 0; i < raw.Length; i++)
    pcm[i] = (short)Math.Clamp(raw[i] * gain * 32767f, -32767f, 32767f);

if (Math.Abs(stretch - 1.0) > 0.001)
{
    var sonic = new VibeSuperTonic.Core.Audio.Sonic(sampleRate, (float)stretch);
    sonic.WriteSamples(pcm, 0, pcm.Length);
    sonic.Flush();
    var buf = new short[(int)(pcm.Length / stretch) + 4096];
    pcm = buf[..sonic.ReadSamples(buf, 0, buf.Length)];
    Console.WriteLine($"stretch      x{stretch:F2} at {sampleRate} Hz");
}

WriteWav(outPath, pcm, sampleRate);

var seconds = raw.Length / (double)sampleRate;
Console.WriteLine($"rendered     {raw.Length} samples, {seconds:F2} s of audio, peak {peak:F3}");
Console.WriteLine(repeat > 1
    ? $"inference    cold {timings[0]:F0} ms, warm {timings.Skip(1).Average():F0} ms over {repeat - 1}  →  warm RTF {timings.Skip(1).Average() / 1000.0 / seconds:F3}"
    : $"inference    {runMs:F0} ms  →  RTF {runMs / 1000.0 / seconds:F3}");
Console.WriteLine($"wrote        {outPath}");
return 0;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void WriteWav(string path, short[] pcm, int sampleRate)
{
    using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var w = new BinaryWriter(f);
    var dataBytes = pcm.Length * sizeof(short);

    w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
    w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
    w.Write("data"u8); w.Write(dataBytes);
    foreach (var s in pcm) w.Write(s);
}

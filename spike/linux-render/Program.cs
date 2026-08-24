using System.Diagnostics;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Onnx.Ort;

// Renders text to a WAV through the real seam: SentenceChunker -> ISynthesizer ->
// AudioBuffer/WavWriter. Nothing from the Windows engine is referenced.
//
//   vst-linux-render <models-dir> [out.wav]
//
// <models-dir> must contain onnx/ and voice_styles/.

string modelsRoot = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "models");
string outPath = args.Length > 1 ? args[1] : "linux-render-out.wav";

const string Text =
    "The sea is everything. It covers seven tenths of the terrestrial globe. " +
    "Its breath is pure and healthy. It is an immense desert, where man is never lonely, " +
    "for he feels life stirring on all sides.";

const string VoiceId = "M1";
// Supertonic language code, not a BCP-47 tag — the model speaks "en", and "en-us"
// throws. SupertonicLanguages.FromLcid does this mapping in the engine and is one
// of the files still to move into Core.
const string Language = "en";
const int TotalStep = 8;
const float InterChunkSilenceSeconds = 0.2f;

Console.WriteLine($"models      : {modelsRoot}");
if (!Directory.Exists(Path.Combine(modelsRoot, "onnx")))
{
    Console.Error.WriteLine($"FAIL  no onnx/ under {modelsRoot}");
    return 2;
}

// 1. Chunk in Core — no ONNX involved.
var chunks = SentenceChunker.Chunk(Text);
Console.WriteLine($"chunks      : {chunks.Count} ({string.Join(", ", chunks.Select(c => c.Length))} chars)");

// 2. Render each chunk through the seam.
using ISynthesizer synth = new OrtSynthesizer(modelsRoot);

var loadWatch = Stopwatch.StartNew();
int sampleRate = synth.SampleRate;          // forces the model load
loadWatch.Stop();
Console.WriteLine($"load        : {loadWatch.Elapsed.TotalSeconds:F2} s, {sampleRate} Hz");

var options = new SynthesisOptions(VoiceId, Language, TotalStep);
var pcm = new List<short>();
int silenceSamples = (int)(InterChunkSilenceSeconds * sampleRate);

var renderWatch = Stopwatch.StartNew();
foreach (var chunk in chunks)
{
    if (pcm.Count > 0) pcm.AddRange(new short[silenceSamples]);
    pcm.AddRange(synth.Synthesize(chunk, options));
}
renderWatch.Stop();

var audio = AudioBuffer.Duration(pcm.Count, sampleRate);
double rtf = renderWatch.Elapsed.TotalSeconds / audio.TotalSeconds;

// 3. Write through Core's writer.
WavWriter.WriteMono16(outPath, pcm.ToArray(), sampleRate);

Console.WriteLine($"render      : {renderWatch.Elapsed.TotalSeconds:F2} s for {audio.TotalSeconds:F2} s audio");
Console.WriteLine($"RTF         : {rtf:F3}   {(rtf <= 0.5 ? "OK (<= 0.5 gate)" : "ABOVE GATE")}");
Console.WriteLine($"wrote       : {outPath} ({new FileInfo(outPath).Length / 1024} KB)");

// 4. Prove cancellation actually interrupts inference, which is what stop needs.
Console.Write("cancel test : ");
using var cts = new CancellationTokenSource();
var cancelWatch = Stopwatch.StartNew();
cts.CancelAfter(TimeSpan.FromMilliseconds(150));
try
{
    synth.Synthesize(string.Join(' ', Enumerable.Repeat(Text, 4)), options, cts.Token);
    cancelWatch.Stop();
    Console.WriteLine($"NOT cancelled — ran to completion in {cancelWatch.Elapsed.TotalSeconds:F2} s");
    return 1;
}
catch (OperationCanceledException)
{
    cancelWatch.Stop();
    Console.WriteLine($"cancelled after {cancelWatch.ElapsedMilliseconds} ms");
}

return 0;

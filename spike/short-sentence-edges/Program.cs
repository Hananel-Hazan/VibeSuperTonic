// usage: harness <modelsRoot> <outDir> <voice> <steps> <speed> <stretch> <reps> <textsFile>
// writes <outDir>/<idx>_r<rep>.wav (16-bit mono)
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Onnx.Ort;

var (root, outDir, voice) = (args[0], args[1], args[2]);
int steps = int.Parse(args[3]);
float speed = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
double stretch = double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
int reps = int.Parse(args[6]);
var texts = File.ReadAllLines(args[7]).Where(l => l.Trim().Length > 0).ToArray();
Directory.CreateDirectory(outDir);
using var synth = new OrtSynthesizer(root, ExecutionProviders.Cpu, 4, 1);
var opts = new SupertonicOptions(voice, "en", steps, speed, 0.3f);
int sr = synth.SampleRate;
for (int i = 0; i < texts.Length; i++)
for (int r = 0; r < reps; r++)
{
    var pcm = synth.Synthesize(texts[i], opts);
    if (Math.Abs(stretch - 1.0) > 0.001) pcm = TimeStretch.Stretch(pcm, stretch, sr);
    WriteWav(Path.Combine(outDir, $"{i:D2}_r{r}.wav"), pcm, sr);
}
Console.WriteLine($"done sr={sr}");

static void WriteWav(string path, short[] pcm, int sr)
{
    using var bw = new BinaryWriter(File.Create(path));
    int bytes = pcm.Length * 2;
    bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
    bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write("data"u8); bw.Write(bytes);
    foreach (var s in pcm) bw.Write(s);
}

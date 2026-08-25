using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VibeSuperTonic.Core.Audio;

namespace VibeSuperTonic.Spike.PiperRender;

/// <summary>
/// Phase P2's second half — settle the speed question with audio.
///
/// <para>The <see href="../../docs/PIPER-PLAN.md">decision</see> is that
/// <c>length_scale</c> carries the whole rate on the Piper path and the
/// pitch-preserving time-stretch is off. The reasoning is sound —
/// <c>RateClampCeiling</c> exists because <i>Supertonic</i> degrades past
/// roughly 1.3, which is a fact about Supertonic and not about VITS — but
/// reasoning is not how a thing that is listened to gets decided.</para>
///
/// <para><b>Three ways, not two</b>, because the obvious two-way comparison
/// would be unfair to the stretch and would hide something worth knowing:
/// <see cref="TimeStretch"/> hardcodes 44100 Hz to match Supertonic's output,
/// and a Piper voice renders at 22050. So the stretch is rendered both as the
/// product would apply it today — at the wrong rate — and with Sonic given the
/// voice's real rate. If the second sounds fine and the first does not, the
/// finding is a latent bug in the shared path rather than an argument about
/// <c>length_scale</c>.</para>
/// </summary>
public static class Rate
{
    /// <summary>
    /// Rendered at these speaking rates. 1.0 is the reference; 1.35 is past the
    /// ceiling Supertonic is clamped to; 1.6 is where a fast reader lives and
    /// where Supertonic cannot go at all without the stretch.
    /// </summary>
    public static readonly double[] Rates = [1.0, 1.35, 1.6];

    public static void Render(string model, string configPath, long[] ids, string outDir)
    {
        Directory.CreateDirectory(outDir);

        using var configJson = JsonDocument.Parse(File.ReadAllText(configPath));
        var cfg = configJson.RootElement;
        var sampleRate = cfg.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        var inference = cfg.GetProperty("inference");
        var noiseScale = inference.GetProperty("noise_scale").GetSingle();
        var noiseW = inference.GetProperty("noise_w").GetSingle();

        using var session = new InferenceSession(model);
        var voice = Path.GetFileNameWithoutExtension(model);

        foreach (var rate in Rates)
        {
            // length_scale is a DURATION multiplier: bigger is slower. A rate of
            // 1.35 is a length_scale of 1/1.35. Getting this inverted produces
            // audio that is obviously wrong, which is the good kind of mistake.
            var lengthScale = (float)(1.0 / rate);

            var byModel = Render(session, ids, noiseScale, lengthScale, noiseW);
            Write(Path.Combine(outDir, $"{voice}-{rate:0.00}-length_scale.wav"), byModel, sampleRate);

            if (rate == 1.0) continue;

            // Rendered at 1.0, then stretched — what the Supertonic path does.
            var flat = Render(session, ids, noiseScale, 1.0f, noiseW);

            Write(Path.Combine(outDir, $"{voice}-{rate:0.00}-stretch-as-shipped.wav"),
                TimeStretch.Stretch(flat, rate), sampleRate);

            Write(Path.Combine(outDir, $"{voice}-{rate:0.00}-stretch-correct-rate.wav"),
                StretchAt(flat, rate, sampleRate), sampleRate);
        }
    }

    private static short[] Render(InferenceSession session, long[] ids,
        float noiseScale, float lengthScale, float noiseW)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("input_lengths", new DenseTensor<long>(new long[] { ids.Length }, [1])),
            NamedOnnxValue.CreateFromTensor("scales", new DenseTensor<float>(
                new[] { noiseScale, lengthScale, noiseW }, new[] { 3 })),
        };

        using var results = session.Run(inputs);
        var raw = results.First().AsEnumerable<float>().ToArray();

        var peak = 0f;
        foreach (var s in raw) peak = Math.Max(peak, Math.Abs(s));
        var gain = peak < 1e-8f ? 0f : 1f / peak;

        var pcm = new short[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            pcm[i] = (short)Math.Clamp(raw[i] * gain * 32767f, -32767f, 32767f);
        return pcm;
    }

    /// <summary>
    /// <see cref="TimeStretch.Stretch"/> with the voice's real sample rate
    /// instead of Supertonic's — the same Sonic, told the truth about its input.
    /// </summary>
    private static short[] StretchAt(short[] input, double factor, int sampleRate)
    {
        if (Math.Abs(factor - 1.0) < 0.001 || input.Length < 4096) return input;

        var sonic = new Sonic(sampleRate, (float)factor);
        sonic.WriteSamples(input, 0, input.Length);
        sonic.Flush();

        var output = new short[(int)(input.Length / factor) + 4096];
        var written = sonic.ReadSamples(output, 0, output.Length);
        return output[..written];
    }

    private static void Write(string path, short[] pcm, int sampleRate)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(f);
        var dataBytes = pcm.Length * sizeof(short);

        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);

        var seconds = pcm.Length / (double)sampleRate;
        Console.WriteLine($"  {seconds,5:F2} s  {Path.GetFileName(path)}");
    }
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VibeSuperTonic.Spike.PiperRender;

/// <summary>
/// Phase P2 — the numbers, against the ones Supertonic already has recorded.
///
/// <para><b>Each voice is measured in its own process.</b> Resident set is the
/// reason: once an ORT session has been built and torn down, the allocator does
/// not hand the pages back, so a second voice measured in the same process
/// reports the high-water mark of both. That is the number this phase exists to
/// get right — Piper's whole claim against Supertonic is ~63 MB of model
/// against ~830 MB resident — and measuring it wrong in the cheap direction is
/// how a claim survives that should not.</para>
/// </summary>
public static class Measure
{
    // What Supertonic costs on this class of machine, from
    // docs/LINUX-PORT-PLAN.md. Printed beside every row rather than left for
    // the reader to go and find, because a measurement with no baseline beside
    // it is a number nobody can act on.
    public const double SupertonicRtfLinux = 0.193;
    public const int SupertonicFirstAudioCpuMs = 802;
    public const int SupertonicFirstAudioCudaMs = 77;
    public const int SupertonicResidentMb = 830;

    public sealed record Row(
        string Voice, string Provider, int ModelMb,
        double SessionBuildMs, double FirstRunMs, double WarmRunMs,
        double AudioSeconds, long RssAfterLoadMb, long RssPeakMb);

    /// <summary>
    /// Resident set, read from /proc rather than from GC.GetTotalMemory — the
    /// model lives in native memory the managed heap knows nothing about, and
    /// asking the GC would report a few MB for a session holding hundreds.
    /// </summary>
    public static long RssMb()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]) / 1024;
        return -1;
    }

    public static long PeakRssMb()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]) / 1024;
        return -1;
    }

    /// <summary>
    /// One voice, one provider, measured from a clean process.
    /// <paramref name="ids"/> is a FIRST CHUNK rather than a paragraph: the
    /// number a person actually waits for is the first chunk's render, and the
    /// product already caps a chunk at 200 characters.
    /// </summary>
    public static Row Run(string model, string configPath, long[] ids, bool cuda, int warmRuns)
    {
        using var configJson = JsonDocument.Parse(File.ReadAllText(configPath));
        var cfg = configJson.RootElement;
        var sampleRate = cfg.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        var numSpeakers = cfg.GetProperty("num_speakers").GetInt32();
        var inference = cfg.GetProperty("inference");

        var rssBefore = RssMb();

        using var options = new SessionOptions();
        if (cuda) options.AppendExecutionProvider_CUDA(0);

        var sw = Stopwatch.StartNew();
        using var session = new InferenceSession(model, options);
        var buildMs = sw.Elapsed.TotalMilliseconds;
        var rssAfterLoad = RssMb();

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("input_lengths", new DenseTensor<long>(new long[] { ids.Length }, [1])),
            NamedOnnxValue.CreateFromTensor("scales", new DenseTensor<float>(
                new[]
                {
                    inference.GetProperty("noise_scale").GetSingle(),
                    inference.GetProperty("length_scale").GetSingle(),
                    inference.GetProperty("noise_w").GetSingle(),
                }, new[] { 3 })),
        };
        if (numSpeakers > 1)
            inputs.Add(NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { 0 }, [1])));

        sw.Restart();
        int samples;
        using (var first = session.Run(inputs))
            samples = first.First().AsEnumerable<float>().Count();
        var firstMs = sw.Elapsed.TotalMilliseconds;

        var warm = new List<double>();
        for (var i = 0; i < warmRuns; i++)
        {
            sw.Restart();
            using var r = session.Run(inputs);
            _ = r.First().AsEnumerable<float>().First();
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }

        _ = rssBefore;
        return new Row(
            Path.GetFileNameWithoutExtension(model),
            cuda ? "CUDA" : "CPU",
            (int)(new FileInfo(model).Length / 1_000_000),
            buildMs, firstMs, warm.Count > 0 ? warm.Average() : firstMs,
            samples / (double)sampleRate,
            rssAfterLoad, PeakRssMb());
    }
}

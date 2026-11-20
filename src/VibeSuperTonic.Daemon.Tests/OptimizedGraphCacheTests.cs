using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Supertonic;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Synthesis.Piper;
using VibeSuperTonic.Onnx.Ort;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The persisted optimised graph (review items 10 and 82): a saved graph is used
/// only for the exact model it was made from, and it is never visible half-written.
///
/// <para>The models are real ONNX graphs, hand-encoded below: one node, either
/// <c>Identity</c> or <c>Neg</c>, so which graph a session actually runs is a
/// sign on its output. That is what makes the staleness visible: a cache keyed
/// only on its file existing returns +2 for a model that now says -2.</para>
/// </summary>
public class OptimizedGraphCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"vst-optgraph-{Guid.NewGuid():N}");

    public OptimizedGraphCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly DateTime FixedMtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SessionOptions CpuOptions() => new() { IntraOpNumThreads = 1, InterOpNumThreads = 1 };

    private static float RunOnce(InferenceSession session, float x)
    {
        var input = NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(new[] { x }, new[] { 1 }));
        using var results = session.Run(new[] { input });
        return results.First().AsEnumerable<float>().Single();
    }

    private string[] CacheFiles(string stem) =>
        Directory.GetFiles(_dir, stem + ".optimized-*").Select(Path.GetFileName).ToArray()!;

    [Fact]
    public void A_model_whose_size_changed_is_not_served_from_the_old_graph()
    {
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Identity", padding: 0));
        File.SetLastWriteTimeUtc(model, FixedMtime);

        using (var first = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool hit1))
        {
            Assert.False(hit1);
            Assert.Equal(2f, RunOnce(first, 2f));
        }
        Assert.Single(CacheFiles("m"));

        // Same name, same mtime, different size: only the size can tell them apart.
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 64));
        File.SetLastWriteTimeUtc(model, FixedMtime);

        using (var second = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool hit2))
        {
            Assert.False(hit2);
            Assert.Equal(-2f, RunOnce(second, 2f));
        }

        // The old graph was pruned once the new one was published, and no temp is left.
        Assert.Single(CacheFiles("m"));
    }

    [Fact]
    public void A_model_whose_mtime_changed_is_not_served_from_the_old_graph()
    {
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Identity", padding: 3));
        File.SetLastWriteTimeUtc(model, FixedMtime);
        using (OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out _)) { }

        // "Neg" plus 8 bytes of padding is the same length as "Identity" plus 3.
        byte[] neg = TinyOnnx.Build("Neg", padding: 8);
        Assert.Equal(new FileInfo(model).Length, neg.Length);
        File.WriteAllBytes(model, neg);
        File.SetLastWriteTimeUtc(model, FixedMtime.AddSeconds(1));

        using var session = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool hit);
        Assert.False(hit);
        Assert.Equal(-2f, RunOnce(session, 2f));
    }

    [Fact]
    public void An_unchanged_model_is_served_from_its_saved_graph()
    {
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 0));

        using (OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool cold)) Assert.False(cold);
        using var warm = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool hit);

        Assert.True(hit);
        Assert.Equal(-2f, RunOnce(warm, 2f));
    }

    [Fact]
    public void Another_architectures_graph_is_left_alone()
    {
        // Windows' x64 and x86 engines share one models folder; pruning across
        // them would make each re-optimise every time the other had run.
        string other = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X86 ? "x64" : "x86";
        string foreign = Path.Combine(_dir, $"m.optimized-{other}-0123456789abcdef.onnx");
        File.WriteAllText(foreign, "theirs");
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 0));

        using (OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out _)) { }

        Assert.True(File.Exists(foreign));
        Assert.Equal(2, CacheFiles("m").Length);
    }

    [Fact]
    public void Hosts_starting_together_all_get_a_working_session_and_leave_one_file()
    {
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 0));

        var outputs = Enumerable.Range(0, 8).AsParallel().WithDegreeOfParallelism(8).Select(i =>
        {
            using var s = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out _);
            return RunOnce(s, 2f);
        }).ToArray();

        Assert.All(outputs, y => Assert.Equal(-2f, y));
        Assert.Single(CacheFiles("m"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_cache_directory_that_refuses_writes_costs_the_saving_not_the_session()
    {
        string model = Path.Combine(_dir, "m.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 0));
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Root ignores the mode; the property under test then cannot be observed.
            bool writable;
            try { File.WriteAllText(Path.Combine(_dir, "probe"), ""); writable = true; }
            catch (UnauthorizedAccessException) { writable = false; }
            if (writable) return;

            using var session = OptimizedGraphCache.Load(model, _dir, "m", CpuOptions, out bool hit);
            Assert.False(hit);
            Assert.Equal(-2f, RunOnce(session, 2f));
            Assert.Empty(CacheFiles("m"));
        }
        finally
        {
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_piper_voice_saves_its_graph_beside_the_model()
    {
        string voiceDir = Path.Combine(_dir, "xx_XX-test-low");
        Directory.CreateDirectory(voiceDir);
        string model = Path.Combine(voiceDir, "xx_XX-test-low.onnx");
        File.WriteAllBytes(model, TinyOnnx.Build("Neg", padding: 0));
        File.WriteAllText(model + ".json", """
            {
              "audio": { "sample_rate": 16000, "quality": "low" },
              "espeak": { "voice": "en-us" },
              "inference": { "noise_scale": 0.667, "length_scale": 1, "noise_w": 0.8 },
              "phoneme_id_map": { "_": [0], "^": [1], "$": [2] },
              "num_speakers": 1
            }
            """);

        using (var synth = new PiperSynthesizer(model, new NoPhonemes(), ExecutionProviders.Cpu, 1, 1))
            await synth.PreloadAsync();

        string[] saved = Directory.GetFiles(voiceDir, "xx_XX-test-low.optimized-*.onnx");
        Assert.Single(saved);
        Assert.Equal(OptimizedGraphCache.PathFor(model, voiceDir, "xx_XX-test-low"), saved[0]);
        Assert.Empty(Directory.GetFiles(voiceDir, "*.tmp"));
    }

    private sealed class NoPhonemes : IPhonemizer
    {
        public IReadOnlyList<IReadOnlyList<string>> Phonemize(string voice, string text) => [];
    }

    /// <summary>
    /// A one-node ONNX model, <c>y = op(x)</c> on a float[1], written as raw
    /// protobuf. <paramref name="padding"/> lengthens the model's doc_string so a
    /// test can choose the file size without changing the graph.
    /// </summary>
    private static class TinyOnnx
    {
        public static byte[] Build(string op, int padding)
        {
            byte[] tensorType = Concat(
                Varint(1, 1),                                   // elem_type FLOAT
                Bytes(2, Bytes(1, Varint(1, 1))));              // shape { dim { dim_value 1 } }
            byte[] ValueInfo(string name) => Concat(Str(1, name), Bytes(2, Bytes(1, tensorType)));

            byte[] node = Concat(Str(1, "x"), Str(2, "y"), Str(3, "n"), Str(4, op));
            byte[] graph = Concat(Bytes(1, node), Str(2, "g"), Bytes(11, ValueInfo("x")), Bytes(12, ValueInfo("y")));

            return Concat(
                Varint(1, 8),                                   // ir_version
                Str(2, "vst-test"),                             // producer_name
                Str(6, new string('p', padding)),               // doc_string
                Bytes(7, graph),
                Bytes(8, Varint(2, 13)));                       // opset_import { version 13 }
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Raw(ulong v)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                if (v != 0) b |= 0x80;
                bytes.Add(b);
            } while (v != 0);
            return bytes.ToArray();
        }

        private static byte[] Varint(int field, ulong value) => Concat(Raw((ulong)(field << 3)), Raw(value));

        private static byte[] Bytes(int field, byte[] payload) =>
            Concat(Raw((ulong)((field << 3) | 2)), Raw((ulong)payload.Length), payload);

        private static byte[] Str(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));
    }
}

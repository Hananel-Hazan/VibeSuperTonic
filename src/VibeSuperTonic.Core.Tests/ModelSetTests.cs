using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The model-set fingerprint — a staleness trigger, and one of the two the
/// readiness pass added after the first draft recorded only the machine.
///
/// <para>A benchmark profile is a property of <em>these weights</em>. Swap them
/// and the cost curve moves, and nothing about the machine has changed to say so.
/// The fingerprint has to be stable across the things that do not matter — a
/// folder copy, a different filesystem's enumeration order — and unstable across
/// the one that does.</para>
/// </summary>
public class ModelSetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vst-modelset-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Models(string name)
    {
        string dir = Path.Combine(_root, name, "onnx");
        Directory.CreateDirectory(dir);
        return Path.Combine(_root, name);
    }

    private static void Weight(string modelsRoot, string name, int bytes) =>
        File.WriteAllBytes(Path.Combine(modelsRoot, "onnx", name), new byte[bytes]);

    [Fact]
    public void The_same_weights_fingerprint_the_same()
    {
        string a = Models("a");
        Weight(a, "vocoder.onnx", 1000);
        Weight(a, "text_encoder.onnx", 2000);

        Assert.Equal(ModelSet.Fingerprint(a), ModelSet.Fingerprint(a));
    }

    [Fact]
    public void A_copy_of_the_same_weights_fingerprints_the_same()
    {
        // The portable-folder case. Copying to another machine gives every file a
        // new timestamp and the same contents; treating that as a model change
        // would invalidate every profile the moment it travelled. The machine id
        // is what catches the copy, and it catches it for the right reason.
        string a = Models("a");
        string b = Models("b");
        Weight(a, "vocoder.onnx", 1000);
        Weight(b, "vocoder.onnx", 1000);
        File.SetLastWriteTimeUtc(Path.Combine(b, "onnx", "vocoder.onnx"), new DateTime(2020, 1, 1));

        Assert.Equal(ModelSet.Fingerprint(a), ModelSet.Fingerprint(b));
    }

    [Fact]
    public void Different_weights_fingerprint_differently()
    {
        string a = Models("a");
        string b = Models("b");
        Weight(a, "vocoder.onnx", 1000);
        Weight(b, "vocoder.onnx", 1001);

        Assert.NotEqual(ModelSet.Fingerprint(a), ModelSet.Fingerprint(b));
    }

    [Fact]
    public void An_added_model_fingerprints_differently()
    {
        string a = Models("a");
        Weight(a, "vocoder.onnx", 1000);
        string before = ModelSet.Fingerprint(a);

        Weight(a, "vector_estimator.onnx", 500);

        Assert.NotEqual(before, ModelSet.Fingerprint(a));
    }

    [Fact]
    public void The_order_files_are_created_in_does_not_change_the_answer()
    {
        // Enumeration order differs between filesystems, and therefore between the
        // two platforms reading one USB stick. An order-dependent hash would make
        // every profile stale on arrival, in a way that looks like a model change.
        string a = Models("a");
        string b = Models("b");
        Weight(a, "aaa.onnx", 10); Weight(a, "zzz.onnx", 20);
        Weight(b, "zzz.onnx", 20); Weight(b, "aaa.onnx", 10);

        Assert.Equal(ModelSet.Fingerprint(a), ModelSet.Fingerprint(b));
    }

    [Fact]
    public void Non_model_files_are_ignored()
    {
        // The voice styles and the manifest live beside the weights and change for
        // their own reasons. A profile is not stale because a voice was added.
        string a = Models("a");
        Weight(a, "vocoder.onnx", 1000);
        string before = ModelSet.Fingerprint(a);

        File.WriteAllText(Path.Combine(a, "onnx", "notes.txt"), "hello");

        Assert.Equal(before, ModelSet.Fingerprint(a));
    }

    [Fact]
    public void No_models_is_a_stable_answer_rather_than_a_random_one()
    {
        // A machine with no models must not thrash between stale and fresh — it
        // simply never matches a profile measured against real weights.
        string a = Models("a");

        Assert.Equal("none", ModelSet.Fingerprint(a));
        Assert.Equal("none", ModelSet.Fingerprint(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void A_fingerprint_of_nothing_never_equals_a_fingerprint_of_weights()
    {
        string a = Models("a");
        Weight(a, "vocoder.onnx", 1000);

        Assert.NotEqual(ModelSet.Fingerprint(a), ModelSet.Fingerprint(Models("empty")));
    }
}

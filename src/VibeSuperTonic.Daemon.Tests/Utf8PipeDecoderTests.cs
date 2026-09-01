using System.Text;
using VibeSuperTonic.Daemon.Interop;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The selection arrives from a pipe in blocks whose sizes nobody chooses, so
/// the only honest test is one that tries every block size — the defect this
/// covers passed for years of ASCII and for most non-ASCII, and only bit when a
/// character happened to straddle byte 16384.
/// </summary>
public class Utf8PipeDecoderTests
{
    // Deliberately ordinary prose. Curly quotes and an em dash are 3 bytes each
    // and appear in anything pasted from a browser or a word processor; the
    // accented and non-Latin text is 2-4 bytes. The bug is not exotic.
    private const string Sample =
        "The “additive” prediction — naïve, perhaps — assumes the flow is "
        + "integrated over the entire surface. Équation: 3 ≈ 4. "
        + "עברית مرحبا 日本語 🚀 done.";

    private static string Decode(byte[] bytes, int blockSize)
    {
        var d = new Utf8PipeDecoder();
        for (int i = 0; i < bytes.Length; i += blockSize)
        {
            int n = Math.Min(blockSize, bytes.Length - i);
            var block = new byte[n];
            Array.Copy(bytes, i, block, 0, n);
            d.Feed(block, n);
        }
        return d.Finish();
    }

    [Fact]
    public void Every_block_size_reproduces_the_text_exactly()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Sample);

        for (int size = 1; size <= bytes.Length; size++)
            Assert.Equal(Sample, Decode(bytes, size));
    }

    /// <summary>
    /// The shape actually measured in the field: one 16 KB read, then another.
    /// Reverting Feed to a per-block <c>Encoding.UTF8.GetString</c> fails this.
    /// </summary>
    [Fact]
    public void A_character_straddling_a_16k_boundary_survives()
    {
        // Pad so that a continuation byte lands exactly on the boundary.
        const int Block = 16 * 1024;
        for (int pad = 0; pad < 4; pad++)
        {
            string text = new string('x', pad) + string.Concat(Enumerable.Repeat("wé", 20000));
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= Block) continue;

            string got = Decode(bytes, Block);
            Assert.Equal(text, got);
            Assert.DoesNotContain('�', got);
        }
    }

    [Fact]
    public void A_transfer_cut_mid_character_is_not_silently_dropped()
    {
        // The owner died half way through a 3-byte character. The bytes must not
        // vanish: a truncated selection should look truncated.
        byte[] bytes = Encoding.UTF8.GetBytes("ok “");
        var d = new Utf8PipeDecoder();
        d.Feed(bytes, bytes.Length - 1);

        string got = d.Finish();
        Assert.StartsWith("ok ", got);
        Assert.Contains('�', got);
    }

    [Fact]
    public void Empty_and_zero_length_feeds_are_harmless()
    {
        var d = new Utf8PipeDecoder();
        d.Feed(new byte[16], 0);
        Assert.Equal("", d.Finish());
    }
}

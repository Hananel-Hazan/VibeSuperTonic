using VibeSuperTonic.Core.Models;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Where a manifest entry and a voice id are allowed to land.
///
/// <para><b>The only suite here whose subject is what a hostile file can do.</b>
/// Both files that drive these paths — <c>models-manifest.json</c> and
/// <c>piper-voices.json</c> — are generated and shipped by us, and both sit
/// beside the binaries in a portable install where anything can edit them. A
/// voice catalog is the kind of file somebody copies from a forum post because it
/// lists more voices. A voice id also arrives straight from a command line.</para>
///
/// <para>The hash pin is not a defence: it says the bytes are the ones the
/// manifest named, not that the manifest named a sane place to put them.</para>
/// </summary>
public class StorePathTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "vst-store-root");

    // ------------------------------------------------------------------ Under

    [Theory]
    [InlineData("piper/en_US-ljspeech-high/en_US-ljspeech-high.onnx")]
    [InlineData("onnx/model.onnx")]
    // Normalisation that stays inside is fine, and refusing it would be a rule
    // about text rather than about where the bytes go.
    [InlineData("piper/x/../en_US-ljspeech-high.onnx")]
    public void A_path_that_stays_inside_the_store_resolves(string relative)
    {
        string? resolved = StorePath.Under(Root, relative);

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, resolved);
    }

    [Theory]
    // Traversal, which is what anyone would think to check for.
    [InlineData("../../.bashrc")]
    [InlineData("piper/../../../etc/cron.d/x")]
    [InlineData("..")]
    // AND the one that surprises people: Path.Combine DISCARDS the base when the
    // second argument is rooted, so this is not a traversal being caught — it is
    // an instruction being obeyed, and a check written against ".." never sees it.
    [InlineData("/etc/cron.d/anything")]
    [InlineData("/tmp/elsewhere.onnx")]
    // Nothing to resolve.
    [InlineData("")]
    [InlineData("   ")]
    public void A_path_that_leaves_the_store_is_refused(string relative)
    {
        Assert.Null(StorePath.Under(Root, relative));
    }

    [Fact]
    public void A_sibling_directory_that_merely_starts_with_the_root_is_refused()
    {
        // The trailing separator in the guard is what this covers. Without it
        // "/tmp/vst-store-root" is a prefix of "/tmp/vst-store-root-evil" and the
        // check passes on a string comparison while the file lands outside.
        Assert.Null(StorePath.Under(Root, "../vst-store-root-evil/payload.onnx"));
    }

    [Fact]
    public void A_path_the_platform_cannot_parse_is_refused_rather_than_thrown()
    {
        // This runs inside a loop over a file somebody else wrote, so it must
        // return an answer rather than take the process down.
        Assert.Null(StorePath.Under(Root, "a\0b.onnx"));
    }

    // ----------------------------------------------------------- IsSafeVoiceId

    [Theory]
    [InlineData("en_US-ljspeech-high")]
    [InlineData("de_DE-thorsten-medium")]
    [InlineData("en_GB-vctk-medium")]
    [InlineData("uk_UA-lada-x_low")]
    // A real shipped id. An ASCII-only rule would refuse a voice that is in the
    // catalog right now, which is how a security guard becomes a bug report
    // about Portuguese.
    [InlineData("pt_PT-tugão-medium")]
    public void A_real_voice_id_is_accepted(string id)
    {
        Assert.True(StorePath.IsSafeVoiceId(id), id);
    }

    [Theory]
    // ".." resolves to a path INSIDE the models root — the store's parent — so
    // "does it stay inside the root" is not a strong enough question for an id
    // that ends in Directory.Delete(recursive: true).
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../..")]
    [InlineData("../../etc")]
    [InlineData("piper/en_US-ljspeech-high")]
    [InlineData("a\\b")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData(".hidden")]
    [InlineData("with space")]
    [InlineData("nul\0byte")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_id_that_is_a_path_is_refused(string? id)
    {
        Assert.False(StorePath.IsSafeVoiceId(id), id ?? "(null)");
    }

    [Fact]
    public void An_absurdly_long_id_is_refused()
    {
        Assert.False(StorePath.IsSafeVoiceId(new string('a', 4096)));
    }
}

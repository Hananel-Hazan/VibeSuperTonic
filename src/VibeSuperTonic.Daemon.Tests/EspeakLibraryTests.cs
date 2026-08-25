using VibeSuperTonic.Piper;
using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// Which espeak-ng gets bound, and the check that stops an old one reaching a
/// render.
///
/// <para>These run against whatever this machine has, which is the point: the
/// probe's whole job is to be honest about that.</para>
/// </summary>
public class EspeakLibraryTests
{
    [Fact]
    public void An_explicit_path_wins_and_carries_its_data_directory()
    {
        // VST_ESPEAK_LIB is what both spikes use and what a developer sets. It
        // beats the loader path so that a machine with a distro package still
        // measures the library we built.
        string lib = Path.Combine(Path.GetTempPath(), $"vst-espeak-{Guid.NewGuid():N}", "install", "lib");
        string data = Path.Combine(Path.GetDirectoryName(lib)!, "share", "espeak-ng-data");
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(data);
        string so = Path.Combine(lib, "libespeak-ng.so.1.52.0.1");
        File.WriteAllText(so, "not a library");

        try
        {
            Environment.SetEnvironmentVariable(EspeakLibrary.LibraryVariable, so);
            var resolution = EspeakLibrary.Probe();

            Assert.Equal(so, resolution.Path);
            Assert.Equal(EspeakLibrary.LibraryVariable, resolution.Source);
            // install/lib/libespeak-ng.so -> install/share/espeak-ng-data, which
            // is the layout build-espeak.sh produces.
            Assert.Equal(data, resolution.DataDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EspeakLibrary.LibraryVariable, null);
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(lib)!)!, true); } catch { }
        }
    }

    [Fact]
    public void Beside_the_executable_beats_the_loader_path()
    {
        // Where P5 will ship ours. Checked with an explicit base directory so the
        // test does not depend on where the test host happens to run from.
        string root = Path.Combine(Path.GetTempPath(), $"vst-espeak-{Guid.NewGuid():N}");
        string beside = Path.Combine(root, "espeak");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "libespeak-ng.so.1.52.0.1"), "not a library");
        Directory.CreateDirectory(Path.Combine(beside, "espeak-ng-data"));

        try
        {
            var resolution = EspeakLibrary.Probe(root);

            Assert.Equal("beside the executable", resolution.Source);
            Assert.Equal(Path.Combine(beside, "espeak-ng-data"), resolution.DataDir);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void With_nothing_installed_beside_it_the_loader_path_is_the_answer_and_says_so()
    {
        string empty = Path.Combine(Path.GetTempPath(), $"vst-espeak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);

        try
        {
            var resolution = EspeakLibrary.Probe(empty);

            Assert.Null(resolution.Path);
            Assert.Equal("loader path", resolution.Source);
            // Logged verbatim by the daemon: which library answered is the
            // difference between our build and a distro one, and that difference
            // is inaudible until it is a report about prosody.
            Assert.Contains("loader path", resolution.Describe());
        }
        finally
        {
            try { Directory.Delete(empty, true); } catch { }
        }
    }

    [Fact]
    public void A_library_that_cannot_be_loaded_is_a_sentence_not_an_exception()
    {
        // The check runs before espeak is initialised, so what a user gets is
        // this text rather than an EntryPointNotFoundException thrown out of a
        // P/Invoke on the render thread — which reaches them as a press that
        // produced no sound.
        string fake = Path.Combine(Path.GetTempPath(), $"vst-espeak-{Guid.NewGuid():N}.so");
        File.WriteAllText(fake, "not a library");

        try
        {
            string? why = EspeakLibrary.MissingTerminatorExport(
                new EspeakLibrary.Resolution(fake, "a test", null));

            Assert.NotNull(why);
            Assert.Contains(EspeakLibrary.LibraryVariable, why);
        }
        finally
        {
            File.Delete(fake);
        }
    }
}

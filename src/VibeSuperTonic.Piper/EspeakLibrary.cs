using System.Runtime.InteropServices;

namespace VibeSuperTonic.Piper;

/// <summary>
/// Where <c>libespeak-ng.so</c> and its data come from, decided in one place.
///
/// <para><b>This is the day-one question P3 was told to answer before there was a
/// call site to retrofit.</b> P1 measured parity against a library built from
/// <see href="../../spike/piper-phonemes/build-espeak.sh">our own script</see>,
/// pinned to commit <c>724808c5</c> — and it has to be that build rather than
/// the distro package for a reason that is not licensing:
/// <c>espeak_TextToPhonemesWithTerminator</c> <b>does not exist at the 1.52.0
/// tag</b>. A build from the release compiles, links, phonemises, and cannot
/// tell you whether a clause ended a sentence — so every input collapses to one
/// sentence and the trailing punctuation the model was trained on is missing.
/// Ubuntu ships 1.52.0. Binding "whatever the loader finds" would therefore
/// produce a working-looking product with subtly wrong prosody, and P1's zero
/// divergences would be a statement about a library nobody runs.</para>
///
/// <para>So the probe is ordered by <em>how sure we are what it is</em>, and the
/// loader path comes last:</para>
///
/// <list type="number">
///   <item><c>VST_ESPEAK_LIB</c> — set explicitly, wins. What the spikes use.</item>
///   <item><c>espeak/</c> beside the executable — where <see href="../../docs/PIPER-PLAN.md#p5">P5</see>
///   will ship ours, which is also the GPL obligation: the offer is the exact
///   source we built.</item>
///   <item>The loader's own search path. Development convenience and nothing
///   more; <see cref="Probe"/> reports which one answered so a surprise is
///   visible in the daemon log rather than in the prosody.</item>
/// </list>
/// </summary>
public static class EspeakLibrary
{
    /// <summary>The soname the P/Invoke declares, and what the loader path is asked for.</summary>
    public const string Name = "espeak-ng";

    /// <summary>Set either of these to bind a specific build — both spikes do.</summary>
    public const string LibraryVariable = "VST_ESPEAK_LIB";
    public const string DataVariable = "VST_ESPEAK_DATA";

    /// <summary>
    /// The library this process will bind, and where the answer came from.
    /// <c>Path</c> is null when only the loader path is left to try — which is
    /// not a failure, just the least-known case.
    /// </summary>
    public readonly record struct Resolution(string? Path, string Source, string? DataDir)
    {
        public string Describe() => Path is null
            ? $"espeak-ng: from the loader path ({Source}), data {DataDir ?? "espeak-ng's default"}"
            : $"espeak-ng: {Path} ({Source}), data {DataDir ?? "espeak-ng's default"}";
    }

    /// <summary>
    /// Decide without loading anything. Cheap, and worth logging at startup: the
    /// difference between our build and the distro's is inaudible until it is a
    /// field report about prosody.
    /// </summary>
    public static Resolution Probe(string? baseDirectory = null)
    {
        string root = baseDirectory ?? AppContext.BaseDirectory;

        string? explicitLib = Environment.GetEnvironmentVariable(LibraryVariable);
        if (!string.IsNullOrWhiteSpace(explicitLib) && File.Exists(explicitLib))
            return new Resolution(explicitLib, LibraryVariable, DataFor(explicitLib));

        // Beside the executable, in the layout the packer will compose. The
        // versioned soname is checked first and the bare symlink second, because
        // an install that copied files rather than preserving links has only the
        // versioned one.
        string shipped = Path.Combine(root, "espeak");
        if (Directory.Exists(shipped))
        {
            var candidates = Directory.GetFiles(shipped, "libespeak-ng.so*")
                .OrderByDescending(f => f.Length)
                .ToArray();
            if (candidates.Length > 0)
                return new Resolution(candidates[0], "beside the executable", DataFor(candidates[0]));
        }

        return new Resolution(null, "loader path", DataFor(null));
    }

    /// <summary>
    /// The <c>espeak-ng-data</c> directory that goes with a library, or null to
    /// let espeak-ng use the location it was compiled with.
    /// </summary>
    private static string? DataFor(string? libraryPath)
    {
        string? explicitData = Environment.GetEnvironmentVariable(DataVariable);
        if (!string.IsNullOrWhiteSpace(explicitData) && Directory.Exists(explicitData))
            return explicitData;

        if (libraryPath is null) return null;

        // install/lib/libespeak-ng.so -> install/share/espeak-ng-data, and the
        // flat case where the packer has put both side by side.
        string? libDir = Path.GetDirectoryName(libraryPath);
        if (libDir is null) return null;

        foreach (var relative in new[]
                 {
                     Path.Combine("..", "share", "espeak-ng-data"),
                     "espeak-ng-data",
                 })
        {
            string candidate = Path.GetFullPath(Path.Combine(libDir, relative));
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int _resolverInstalled;

    /// <summary>
    /// Point this assembly's <c>espeak-ng</c> imports at <paramref name="resolution"/>.
    ///
    /// <para>Installed once per process — .NET allows exactly one resolver per
    /// assembly and throws on the second — and a no-op when the resolution is the
    /// loader path, which is what a bare <c>DllImport</c> already does.</para>
    /// </summary>
    public static void Bind(Resolution resolution)
    {
        if (resolution.Path is null) return;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;

        string path = resolution.Path;
        NativeLibrary.SetDllImportResolver(typeof(EspeakLibrary).Assembly,
            (name, _, _) => name == Name ? NativeLibrary.Load(path) : IntPtr.Zero);
    }
}

using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The stress tests, run with nothing beside them.
///
/// <para>They saturate the CPU and the thread pool on purpose (a 2 MB chunk, sixteen
/// threads at once), and xunit runs test classes in parallel, so beside them
/// <see cref="PipelineLatencyTests"/> measured its twenty utterances over the
/// ceiling (seen 2026-10-04, the first full run after they landed) while passing
/// every time on its own. A latency test that fails because a stress test shares
/// its process is a flake, and a flake teaches people to re-run red.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StressCollection
{
    public const string Name = "stress";
}

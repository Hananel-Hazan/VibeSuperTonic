using Xunit;

namespace VibeSuperTonic.Daemon.Tests;

/// <summary>
/// The tests that change process-wide environment variables, run alone.
///
/// <para>xunit runs one class's tests one at a time, but different classes in
/// parallel, in one process. <see cref="SocketModeTests"/> points <c>$TMPDIR</c>
/// at a scratch directory it deletes afterwards, so any class that called
/// <c>Path.GetTempPath()</c> meanwhile built its fixture inside that directory
/// and lost it: <c>SpeechdRatePlanTests</c> failed in CI writing
/// <c>/tmp/vst-sock-…/vst-rate-…/data/settings.json</c>, and
/// <c>EngineRoutingTests</c> and <c>VoiceVisibilityTests</c> failed the same
/// way, about one run in ten, locally. A collection with parallelization
/// disabled runs after every parallel one, with nothing beside it.</para>
///
/// <para>Any test that calls <c>Environment.SetEnvironmentVariable</c> belongs
/// here.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "process environment";
}

using VibeSuperTonic.Core.Synthesis;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// The guard on the unpaced-render switch.
///
/// <para>What is being pinned is not that the switch works — that is one
/// comparison and it is visible in the source. It is that the switch cannot be
/// thrown for a process that did not throw it itself. The engine ships inside
/// other people's programs, and unpaced writes there mean SAPI's buffer runs
/// deep and clients that close their output when Speak returns lose the trailing
/// words: R-14, the defect that shipped in five consecutive releases while every
/// test passed. A machine-wide <c>VIBESUPERTONIC_UNPACED=1</c> would be the way
/// back to it, so "1" failing is the assertion that matters here.</para>
/// </summary>
public class BenchSwitchesTests
{
    [Fact]
    public void A_process_naming_itself_is_authorised()
    {
        Assert.True(BenchSwitches.IsAuthorised(BenchSwitches.TokenFor(4321), 4321));
    }

    /// <summary>
    /// The value a person would set machine-wide, and the one that must do
    /// nothing. Every truthy-looking spelling, because the point is that the
    /// guard is an identity check and not a truthiness check.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("-1")]
    public void A_truthy_value_authorises_nothing(string value)
    {
        Assert.False(BenchSwitches.IsAuthorised(value, 4321));
    }

    [Fact]
    public void Another_processs_id_is_refused()
    {
        Assert.False(BenchSwitches.IsAuthorised(BenchSwitches.TokenFor(4321), 1234));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_or_blank_is_refused(string? value)
    {
        Assert.False(BenchSwitches.IsAuthorised(value, 4321));
    }

    /// <summary>
    /// Surrounding whitespace survives an environment round-trip through some
    /// tooling, so it is trimmed rather than treated as a mismatch. Interior
    /// junk is still a mismatch.
    /// </summary>
    [Theory]
    [InlineData(" 4321", true)]
    [InlineData("4321 ", true)]
    [InlineData("\t4321\r\n", true)]
    [InlineData("+4321", false)]
    [InlineData("4321.0", false)]
    [InlineData("4 321", false)]
    [InlineData("0x10E1", false)]
    [InlineData("4321x", false)]
    public void Only_a_bare_decimal_id_matches(string value, bool expected)
    {
        Assert.Equal(expected, BenchSwitches.IsAuthorised(value, 4321));
    }

    /// <summary>
    /// Keeps the function total. A caller that passed a default-initialised id
    /// must not be authorised by a literal "0" sitting in the environment.
    /// </summary>
    [Fact]
    public void A_non_positive_process_id_is_never_authorised()
    {
        Assert.False(BenchSwitches.IsAuthorised("0", 0));
        Assert.False(BenchSwitches.IsAuthorised("-5", -5));
    }

    /// <summary>
    /// The names are a contract with the render helper, which links this same
    /// file rather than referencing Core — so a rename has to be a deliberate
    /// act, not a refactor that leaves the engine reading a variable nobody
    /// sets. Both failures are silent: the bench simply stays paced, or quietly
    /// measures the profile the last sweep saved.
    /// </summary>
    [Fact]
    public void The_variable_names_are_part_of_the_contract()
    {
        Assert.Equal("VIBESUPERTONIC_UNPACED", BenchSwitches.UnpacedVariable);
        Assert.Equal("VIBESUPERTONIC_NO_PROFILE", BenchSwitches.NoProfileVariable);
    }

    /// <summary>
    /// The two switches are independent, and this test is the tripwire on that.
    ///
    /// <para>They are separate because the preset Benchmark tab may want unpaced
    /// rendering while still applying the profile — that tab answers "what will
    /// my machine do for me", and the profile is part of that answer. Anyone who
    /// later folds them into one variable to save a line fails here, rather than
    /// silently changing what that tab measures.</para>
    /// </summary>
    [Fact]
    public void The_two_switches_are_not_the_same_variable()
    {
        Assert.NotEqual(BenchSwitches.UnpacedVariable, BenchSwitches.NoProfileVariable);
    }

    /// <summary>
    /// One switch being authorised says nothing about the other: they are
    /// separate variables read separately, so a process that set only one gets
    /// only one.
    /// </summary>
    [Fact]
    public void Authorising_one_switch_does_not_authorise_the_other()
    {
        // Same guard, different variables — the isolation is in the caller
        // reading the right name, which is what the constants above pin.
        Assert.True(BenchSwitches.IsAuthorised(BenchSwitches.TokenFor(99), 99));
        Assert.False(BenchSwitches.IsAuthorised(null, 99));
    }
}

using VibeSuperTonic.Core.Export;
using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// vst-ctl's command line (review items 21, 68, 77, 96). Before CtlArgs existed
/// this parser sat in Ctl/Program.cs and could only be tested by starting the
/// binary; `--voice=M4` was silently dropped and the default voice spoke.
/// </summary>
public class CtlArgsTests
{
    private static CtlArgs P(params string[] a) => CtlArgs.Parse(a);

    [Theory]
    [InlineData("read", RequestVerb.Read)]
    [InlineData("toggle", RequestVerb.Toggle)]
    [InlineData("speak", RequestVerb.Speak)]
    [InlineData("stop", RequestVerb.Stop)]
    [InlineData("pause", RequestVerb.Pause)]
    [InlineData("resume", RequestVerb.Resume)]
    [InlineData("status", RequestVerb.Status)]
    [InlineData("subscribe", RequestVerb.Subscribe)]
    [InlineData("reload", RequestVerb.Reload)]
    [InlineData("config", RequestVerb.Config)]
    [InlineData("diagnostics", RequestVerb.Diagnostics)]
    [InlineData("benchmark", RequestVerb.Benchmark)]
    [InlineData("render", RequestVerb.Render)]
    [InlineData("voices", RequestVerb.Voices)]
    [InlineData("shutdown", RequestVerb.Shutdown)]
    [InlineData("SPEAK", RequestVerb.Speak)]
    public void EveryVerbParsesCaseInsensitively(string word, RequestVerb expected)
    {
        var r = P(word);
        Assert.Equal(CtlOutcome.Run, r.Outcome);
        Assert.Equal(expected, r.Verb);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("Speak,Read")]
    [InlineData("-1")]
    [InlineData("nonsense")]
    public void VerbByNumberOrListIsRefused(string word)
    {
        var r = P(word);
        Assert.Equal(CtlOutcome.Error, r.Outcome);
        Assert.Contains("unknown verb", r.Error);
    }

    [Fact]
    public void SeekTakesAnOffset()
    {
        Assert.Equal(42, P("seek", "42").SeekOffset);
        Assert.Equal(CtlOutcome.Error, P("seek").Outcome);
        Assert.Equal(CtlOutcome.Error, P("seek", "x").Outcome);
    }

    [Fact]
    public void SpeakJoinsTheText()
    {
        var r = P("speak", "hello", "world");
        Assert.Equal(new[] { "speak", "hello", "world" }, r.Positional);
    }

    [Fact]
    public void VoiceWithSpaceAndWithEqualsAreTheSame()
    {
        Assert.Equal("M4", P("speak", "--voice", "M4", "hello").Voice);
        var eq = P("speak", "--voice=M4", "hello");
        Assert.Equal("M4", eq.Voice);
        Assert.Equal(new[] { "speak", "hello" }, eq.Positional);
    }

    [Fact]
    public void EveryValueOptionAcceptsEquals()
    {
        var r = P("render", "--language=de", "--rate=-30", "--volume=20", "--out=/tmp/x.mp3", "--format=flac", "hi");
        Assert.Equal("de", r.Language);
        Assert.Equal(-30, r.Rate);
        Assert.Equal(20, r.Volume);
        Assert.Equal("/tmp/x.mp3", r.OutPath);
        Assert.Equal(ExportFormat.Flac, r.ExportFormat);
        Assert.Equal(new[] { "render", "hi" }, r.Positional);
    }

    [Fact]
    public void NegativeRateIsAValueNotText()
    {
        var r = P("render", "--rate", "-30", "hi");
        Assert.Equal(-30, r.Rate);
        Assert.Equal(new[] { "render", "hi" }, r.Positional);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--voices=M4")]
    [InlineData("--Voice")]
    public void UnknownOptionIsRefused(string option)
    {
        var r = P("speak", option, "hello");
        Assert.Equal(CtlOutcome.Error, r.Outcome);
        Assert.Contains("unknown option", r.Error);
    }

    [Fact]
    public void FlagWithAValueIsRefused() =>
        Assert.Equal(CtlOutcome.Error, P("status", "--json=1").Outcome);

    [Theory]
    [InlineData("--voice")]
    [InlineData("--voice=")]
    [InlineData("--language")]
    [InlineData("--out")]
    [InlineData("--rate")]
    [InlineData("--rate=fast")]
    [InlineData("--volume=loud")]
    [InlineData("--format=ogg")]
    public void ValueOptionWithoutAGoodValueIsRefused(string option) =>
        Assert.Equal(CtlOutcome.Error, P("render", option).Outcome);

    [Fact]
    public void ValueOptionDoesNotSwallowTheNextOption() =>
        Assert.Equal(CtlOutcome.Error, P("render", "--voice", "--marks", "x").Outcome);

    [Fact]
    public void DoubleDashEndsOptions()
    {
        var r = P("speak", "--", "--version", "--bogus");
        Assert.Equal(CtlOutcome.Run, r.Outcome);
        Assert.Equal(new[] { "speak", "--version", "--bogus" }, r.Positional);
    }

    [Fact]
    public void SingleDashTextStaysText()
    {
        Assert.Equal(new[] { "speak", "-", "-5" }, P("speak", "-", "-5").Positional);
    }

    [Theory]
    [InlineData("--stdin")]
    [InlineData("--text-stdin")]
    public void StdinFlagForSpeakAndRender(string flag)
    {
        Assert.True(P("speak", flag).TextFromStdin);
        Assert.True(P("render", flag).TextFromStdin);
    }

    [Fact]
    public void StdinIsRefusedForOtherVerbsAndWithCommandLineText()
    {
        Assert.Equal(CtlOutcome.Error, P("status", "--stdin").Outcome);
        Assert.Equal(CtlOutcome.Error, P("speak", "--stdin", "also this").Outcome);
    }

    [Fact]
    public void FlagsAreSet()
    {
        var r = P("diagnostics", "--json", "--text", "--no-start", "--force");
        Assert.True(r.AsJson);
        Assert.True(r.WithText);
        Assert.True(r.NoStart);
        Assert.True(r.Force);
        Assert.True(P("render", "--marks", "x").Marks);
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("-y")]
    [InlineData("--accept-licence")]
    [InlineData("--accept-license")]
    public void LicenceAcceptanceSpellings(string flag)
    {
        var r = P("voice", "install", "en_US-lessac-medium", flag);
        Assert.True(r.AcceptLicence);
        Assert.Equal(RequestVerb.VoiceInstall, r.Verb);
        Assert.Equal("en_US-lessac-medium", r.VoiceSubject);
    }

    [Fact]
    public void VoiceRemoveAndSubjectViaVoiceOption()
    {
        var r = P("voice", "remove", "--voice=piper-x");
        Assert.Equal(RequestVerb.VoiceRemove, r.Verb);
        Assert.Equal("piper-x", r.VoiceSubject);
        Assert.Equal(CtlOutcome.Error, P("voice").Outcome);
        Assert.Equal(CtlOutcome.Error, P("voice", "frob", "x").Outcome);
        Assert.Equal(CtlOutcome.Error, P("voice", "install").Outcome);
    }

    [Fact]
    public void FormatFollowsExtensionAndWavOnlyGoesToStdout()
    {
        Assert.Equal(ExportFormat.Mp3, P("render", "x", "--out", "a.mp3").ExportFormat);
        Assert.Equal(CtlOutcome.Error, P("render", "x", "--format", "mp3").Outcome);
        Assert.Equal(ExportFormat.Wav, P("render", "x", "--format", "wav").ExportFormat);
        Assert.Equal(ExportFormat.Aac, P("render", "x", "--out", "a.m4a").ExportFormat);
        // Not silently WAV under an .aac name.
        Assert.Equal(CtlOutcome.Error, P("render", "x", "--out", "a.AAC").Outcome);
    }

    [Fact]
    public void MetaOutcomes()
    {
        Assert.Equal(CtlOutcome.Usage, P().Outcome);
        Assert.Equal(CtlOutcome.Help, P("--help").Outcome);
        Assert.Equal(CtlOutcome.Help, P("-h").Outcome);
        Assert.Equal(CtlOutcome.Version, P("--version").Outcome);
        Assert.Equal(CtlOutcome.ExportFormats, P("--export-formats").Outcome);
        // Text after `--` is not a question about the binary.
        Assert.Equal(CtlOutcome.Run, P("render", "--", "--version").Outcome);
    }

    [Fact]
    public void OptionsWithNoVerbAreRefusedWithUsage()
    {
        var r = P("--json");
        Assert.Equal(CtlOutcome.Error, r.Outcome);
        Assert.True(r.ShowUsage);
    }
}

using VibeSuperTonic.Core.Ipc;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Bookmarks crossing the render stream: the daemon puts them on the reply that
/// carries the audio after them, and <see cref="RenderWav.Read"/> hands them to
/// whoever asked — in order, before that audio, and to nobody who did not ask.
/// </summary>
public class RenderMarksTests
{
    private const int Rate = 22050;

    private static string Reply(string? pcmOf = null, bool final = false, params RenderMark[] marks)
    {
        string? pcm = pcmOf is null ? null : Convert.ToBase64String(new byte[] { 1, 0, 2, 0 });
        return Protocol.Encode(new Response
        {
            Ok = true,
            Audio = new AudioChunk(Rate, 1, pcm, final, marks.Length == 0 ? null : marks),
        });
    }

    private static (int Code, List<string> Events) Read(bool wantMarks, params string[] lines)
    {
        int at = 0;
        var events = new List<string>();
        var output = new RecordingStream(events);

        int code = RenderWav.Read(
            () => at < lines.Length ? lines[at++] : null,
            output, TextWriter.Null,
            onMark: wantMarks ? m => events.Add($"mark:{m.Name}@{m.Sample}") : null);

        return (code, events);
    }

    /// <summary>Notes each write, so ordering against the marks is observable.</summary>
    private sealed class RecordingStream(List<string> events) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            events.Add($"write:{count}");
            base.Write(buffer, offset, count);
        }
    }

    [Fact]
    public void A_mark_is_reported_before_the_samples_of_the_reply_that_carried_it()
    {
        var (code, events) = Read(true,
            Reply(),
            Reply("a", false, new RenderMark("first", 0)),
            Reply("b", false, new RenderMark("second", 2)),
            Reply(final: true));

        Assert.Equal(0, code);

        int firstMark = events.IndexOf("mark:first@0");
        int secondMark = events.IndexOf("mark:second@2");
        Assert.True(firstMark >= 0 && secondMark > firstMark, string.Join(", ", events));

        // 44 header bytes, then the first chunk's 4, then the second mark, then
        // the second chunk: each mark sits immediately before its own audio.
        Assert.Equal("write:44", events[0]);
        Assert.Equal("mark:first@0", events[1]);
        Assert.Equal("write:4", events[2]);
        Assert.Equal("mark:second@2", events[3]);
        Assert.Equal("write:4", events[4]);
    }

    [Fact]
    public void A_mark_after_the_last_word_rides_on_the_final_reply()
    {
        var (code, events) = Read(true,
            Reply(),
            Reply("a"),
            Reply(final: true, marks: new RenderMark("end", 2)));

        Assert.Equal(0, code);
        Assert.Contains("mark:end@2", events);
    }

    [Fact]
    public void Nobody_who_did_not_ask_is_told_about_marks()
    {
        var (code, events) = Read(false,
            Reply(),
            Reply("a", false, new RenderMark("ignored", 0)),
            Reply(final: true));

        Assert.Equal(0, code);
        Assert.DoesNotContain(events, e => e.StartsWith("mark:", StringComparison.Ordinal));
    }
}

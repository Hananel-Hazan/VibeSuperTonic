using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.SpeechD;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// SSML on Linux, level with Windows. On Windows SAPI parses the document and the
/// engine receives <c>xml:lang</c> as a per-fragment language, <c>prosody rate</c>
/// as a per-fragment rate adjustment, <c>break</c> as silence and <c>mark</c> as a
/// bookmark; <see cref="SsmlDocument"/> produces the same list.
///
/// <para>Two halves matter equally. The positive half is each of those four. The
/// negative half is the contract: <b>nothing that is not a well-formed document
/// parses, and nothing malformed throws</b> — a false return is what makes the
/// daemon strip, which is today's behaviour, instead of failing a screen
/// reader's utterance over an unclosed tag.</para>
/// </summary>
public class SsmlFragmentsTests
{
    private static IReadOnlyList<SsmlFragment> Parse(string ssml)
    {
        Assert.True(SsmlDocument.TryParse(ssml, out var fragments), "expected a well-formed document: " + ssml);
        return fragments;
    }

    private static string Doc(string inner, string attrs = "") =>
        $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\"{attrs}>{inner}</speak>";

    // ----------------------------------------------------------------- text

    [Fact]
    public void Plain_text_inside_speak_is_one_text_fragment()
    {
        var f = Assert.Single(Parse("<speak>hello</speak>"));

        Assert.Equal(SsmlFragmentKind.Text, f.Kind);
        Assert.Equal("hello", f.Text);
        Assert.Null(f.Lang);
        Assert.Equal(0, f.RateAdj);
    }

    /// <summary>
    /// PARSING MUST NOT CHANGE WHAT IS SAID. For markup that only structures the
    /// text, the words are exactly what the strip produces — the property that
    /// lets the daemon switch from stripping to parsing without a single
    /// existing document sounding different.
    /// </summary>
    [Theory]
    [InlineData("<speak>hello</speak>")]
    [InlineData("<speak><s>One.</s><s>Two.</s></speak>")]
    [InlineData("<speak><p><s>Please <emphasis level=\"strong\">stop</emphasis> here.</s></p></speak>")]
    [InlineData("<speak>a &lt; b &amp; c</speak>")]
    [InlineData("<speak>a < b</speak>")]
    [InlineData("<speak>  spaced \n\n out  <say-as interpret-as=\"digits\">123</say-as></speak>")]
    [InlineData("<speak><!-- a note -->visible</speak>")]
    [InlineData("<speak>&#72;i &#x1F600;</speak>")]
    public void The_spoken_words_equal_what_the_strip_would_have_said(string ssml)
    {
        var fragments = Parse(ssml);
        Assert.Equal(Ssml.Strip(ssml), SsmlDocument.SpokenText(fragments));
    }

    [Fact]
    public void An_escaped_tag_is_text_not_markup()
    {
        // &lt;speak&gt; as literal words: decoding before parsing would turn it
        // into markup and delete it. The same ordering rule the strip has.
        var f = Assert.Single(Parse("<speak>say &lt;speak&gt; first</speak>"));
        Assert.Equal("say <speak> first", f.Text);
    }

    [Fact]
    public void A_sub_alias_is_said_and_what_it_replaces_is_not()
    {
        var f = Assert.Single(Parse("<speak>the <sub alias=\"World Health Organization\">WHO</sub> said</speak>"));
        Assert.Equal("the World Health Organization said", f.Text);
    }

    [Fact]
    public void Metadata_is_not_spoken()
    {
        var f = Assert.Single(Parse("<speak><metadata><dc:title>secret</dc:title></metadata>shown</speak>"));
        Assert.Equal("shown", f.Text);
    }

    // ------------------------------------------------------------- language

    [Fact]
    public void Xml_lang_gives_the_fragment_a_supertonic_language()
    {
        var f = Parse(Doc("Hallo Welt", " xml:lang=\"de-DE\"")).Single();
        Assert.Equal("de", f.Lang);
    }

    [Fact]
    public void A_language_change_splits_the_fragments_and_scopes_to_its_element()
    {
        var f = Parse(Doc("Good morning. <voice xml:lang=\"fr-FR\">Bonjour.</voice> Good night.", " xml:lang=\"en-US\""));

        Assert.Equal(
            [("Good morning.", "en"), ("Bonjour.", "fr"), ("Good night.", "en")],
            f.Select(x => (x.Text, x.Lang)).ToArray());
    }

    /// <summary>
    /// Windows resolves the LCID with <c>FromLcid</c>, which returns null for a
    /// language Supertonic lacks — and a null fragment language means "the
    /// configured one", not "whatever was around it". An unsupported tag clears
    /// the inherited language rather than keeping it.
    /// </summary>
    [Fact]
    public void An_unsupported_language_falls_back_to_the_configured_one_not_the_parents()
    {
        var f = Parse(Doc("Hallo <lang xml:lang=\"th-TH\">สวัสดี</lang> danke", " xml:lang=\"de\""));

        Assert.Equal("de", f[0].Lang);
        Assert.Null(f[1].Lang);
        Assert.Equal("de", f[2].Lang);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("DE", "de")]
    [InlineData("pt_BR", "pt")]
    [InlineData("en", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("th-TH", null)]
    [InlineData("zh-CN", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("na", null)]
    public void A_language_tag_maps_the_way_the_lcid_does_on_windows(string? tag, string? expected)
    {
        Assert.Equal(expected, SupertonicLanguages.FromTag(tag));
    }

    [Fact]
    public void Every_language_the_lcid_table_knows_the_tag_table_knows_too()
    {
        // The two vocabularies must agree for every language Supertonic speaks,
        // or the same document would switch language on one platform only.
        foreach (var l in SupertonicLanguages.All)
        {
            Assert.Equal(l.Code, SupertonicLanguages.FromTag(l.Code));
            Assert.Equal(l.Code, SupertonicLanguages.FromLcid(Convert.ToUInt16(l.HexLcid, 16)));
        }
    }

    // ----------------------------------------------------------------- rate

    [Fact]
    public void Prosody_rate_applies_to_its_own_text_only()
    {
        var f = Parse(Doc("one <prosody rate=\"x-fast\">two</prosody> three"));

        Assert.Equal([0, 6, 0], f.Select(x => x.RateAdj).ToArray());
        Assert.Equal(["one", "two", "three"], f.Select(x => x.Text).ToArray());
    }

    [Theory]
    [InlineData("x-slow", -6)]
    [InlineData("slow", -3)]
    [InlineData("medium", 0)]
    [InlineData("default", 0)]
    [InlineData("fast", 3)]
    [InlineData("x-fast", 6)]
    [InlineData("X-FAST", 6)]
    public void The_keywords_map_onto_the_sapi_scale(string value, int expected)
    {
        Assert.Equal(expected, SsmlDocument.RateAdjFrom(value));
    }

    [Theory]
    [InlineData("+0%", 0)]
    [InlineData("100%", 0)]
    [InlineData("+100%", 6)]       // twice as fast: 10 * log3(2) = 6.3
    [InlineData("200%", 6)]
    [InlineData("50%", -6)]
    [InlineData("-50%", -6)]
    [InlineData("2", 6)]           // SSML 1.0: a plain number is a multiplier
    [InlineData("0.5", -6)]
    public void A_relative_rate_goes_through_the_scales_own_definition(string value, int expected)
    {
        Assert.Equal(expected, SsmlDocument.RateAdjFrom(value));
    }

    /// <summary>
    /// THE CLAMP. SAPI's scale ends at ±10 and the engine clamps site plus
    /// fragment to it, so an absurd request lands on the end, not past it.
    /// </summary>
    [Theory]
    [InlineData("100000%", 10)]
    [InlineData("1000", 10)]
    [InlineData("-99%", -10)]
    [InlineData("0.001", -10)]
    public void An_extreme_rate_is_clamped_to_the_scales_ends(string value, int expected)
    {
        Assert.Equal(expected, SsmlDocument.RateAdjFrom(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("quickly")]
    [InlineData("+3")]
    [InlineData("-100%")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("%")]
    public void A_rate_that_says_nothing_usable_changes_nothing(string value)
    {
        Assert.Null(SsmlDocument.RateAdjFrom(value));
    }

    [Fact]
    public void Nested_prosody_adds_up_and_the_sum_is_clamped()
    {
        var f = Parse(Doc(
            "<prosody rate=\"fast\">a <prosody rate=\"x-fast\">b <prosody rate=\"x-fast\">c</prosody></prosody></prosody>"));

        Assert.Equal([3, 9, 10], f.Select(x => x.RateAdj).ToArray());
    }

    [Fact]
    public void An_unusable_rate_leaves_the_text_at_the_rate_around_it()
    {
        var f = Parse(Doc("<prosody rate=\"fast\">a <prosody rate=\"nonsense\">b</prosody></prosody>"));
        Assert.Equal([3], f.Select(x => x.RateAdj).Distinct().ToArray());
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 1.5)]
    [InlineData(-10, 1.0 / 1.5)]
    [InlineData(99, 1.5)]          // clamped, as the engine clamps
    [InlineData(-99, 1.0 / 1.5)]
    public void The_adjustment_becomes_the_engines_own_speed_factor(int adj, double factor)
    {
        Assert.Equal(factor, SpeechRate.RateAdjScale(adj), 6);
    }

    // ---------------------------------------------------------------- break

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("2s", 2000)]
    [InlineData("1.5s", 1500)]
    [InlineData(" 250MS ", 250)]
    [InlineData("300", 300)]
    public void A_break_time_is_milliseconds(string time, int expected)
    {
        var f = Parse(Doc($"a<break time=\"{time}\"/>b"));

        Assert.Equal(SsmlFragmentKind.Silence, f[1].Kind);
        Assert.Equal(expected, f[1].SilenceMs);
    }

    [Fact]
    public void A_break_sits_between_the_text_either_side_of_it()
    {
        var f = Parse(Doc("before<break time=\"1s\"/>after"));

        Assert.Equal(
            [SsmlFragmentKind.Text, SsmlFragmentKind.Silence, SsmlFragmentKind.Text],
            f.Select(x => x.Kind).ToArray());
        Assert.Equal(["before", "after"], f.Where(x => x.Kind == SsmlFragmentKind.Text).Select(x => x.Text).ToArray());
    }

    [Theory]
    [InlineData("x-weak", 100)]
    [InlineData("weak", 200)]
    [InlineData("medium", 400)]
    [InlineData("strong", 700)]
    [InlineData("x-strong", 1000)]
    public void A_break_strength_has_a_duration(string strength, int expected)
    {
        Assert.Equal(expected, SsmlDocument.BreakMs(null, strength));
    }

    [Fact]
    public void A_bare_break_is_a_medium_one_and_time_beats_strength()
    {
        Assert.Equal(400, SsmlDocument.BreakMs(null, null));
        Assert.Equal(250, SsmlDocument.BreakMs("250ms", "x-strong"));
    }

    [Fact]
    public void A_break_with_no_duration_is_not_a_fragment_as_on_windows()
    {
        // The engine adds a silence item only for SilenceMSecs > 0.
        Assert.DoesNotContain(Parse(Doc("a<break time=\"0ms\"/>b")), x => x.Kind == SsmlFragmentKind.Silence);
        Assert.DoesNotContain(Parse(Doc("a<break strength=\"none\"/>b")), x => x.Kind == SsmlFragmentKind.Silence);
    }

    [Theory]
    [InlineData("99999999s")]
    [InlineData("1e12ms")]
    [InlineData("1000000000000000000000s")]
    public void A_break_is_clamped_so_a_client_cannot_ask_for_hours(string time)
    {
        Assert.Equal(SsmlDocument.MaxBreakMs, SsmlDocument.BreakMs(time, null));
    }

    [Theory]
    [InlineData("-5s")]
    [InlineData("soon")]
    [InlineData("NaNs")]
    public void An_unreadable_time_falls_back_to_the_strength_rule(string time)
    {
        Assert.Equal(400, SsmlDocument.BreakMs(time, null));
    }

    // ----------------------------------------------------------------- mark

    [Fact]
    public void A_mark_is_a_fragment_at_its_position()
    {
        var f = Parse(Doc("<mark name=\"start\"/>Hello world. This is a test.<mark name=\"end\"/>"));

        Assert.Equal(
            [SsmlFragmentKind.Mark, SsmlFragmentKind.Text, SsmlFragmentKind.Mark],
            f.Select(x => x.Kind).ToArray());
        Assert.Equal(["start", "end"], f.Where(x => x.Kind == SsmlFragmentKind.Mark).Select(x => x.Mark!).ToArray());
    }

    [Fact]
    public void Marks_split_the_text_at_the_point_they_were_written()
    {
        var f = Parse(Doc("one <mark name=\"a\"/>two <mark name=\"b\"/>three"));

        Assert.Equal(
            ["one", "a", "two", "b", "three"],
            f.Select(x => x.Kind == SsmlFragmentKind.Mark ? x.Mark! : x.Text).ToArray());
    }

    [Fact]
    public void A_mark_name_is_decoded_and_an_empty_one_is_dropped()
    {
        var f = Parse(Doc("a<mark name=\"x&amp;y\"/>b<mark name=\"\"/>c<mark/>d"));

        Assert.Equal(["x&y"], f.Where(x => x.Kind == SsmlFragmentKind.Mark).Select(x => x.Mark!).ToArray());
    }

    /// <summary>
    /// What speech-dispatcher actually sends in SSML mode: its own marks, named
    /// <c>__spd_id_N</c>, wrapped around every sentence so it can resume from one.
    /// </summary>
    [Fact]
    public void The_marks_speechd_inserts_come_through_in_order()
    {
        var f = Parse("<speak><mark name=\"__spd_id_1\"/>First sentence. <mark name=\"__spd_id_2\"/>Second one.</speak>");

        Assert.Equal(["__spd_id_1", "__spd_id_2"],
            f.Where(x => x.Kind == SsmlFragmentKind.Mark).Select(x => x.Mark!).ToArray());
        Assert.Equal(["First sentence.", "Second one."],
            f.Where(x => x.Kind == SsmlFragmentKind.Text).Select(x => x.Text).ToArray());
    }

    // ------------------------------------------------------------ malformed

    /// <summary>
    /// THE CONTRACT. Each of these is something a client really sends, and each
    /// must come back "not parsed" — so the daemon strips, as it always has —
    /// rather than throw or half-parse.
    /// </summary>
    [Theory]
    [InlineData("<speak>unclosed")]
    [InlineData("<speak><s>one</speak>")]
    [InlineData("<speak>one</s></speak>")]
    [InlineData("<speak>one</speak> trailing words")]
    [InlineData("<speak>one</speak><speak>two</speak>")]
    [InlineData("<speak><!-- never ends")]
    [InlineData("<speak><![CDATA[never ends")]
    [InlineData("<speak><br>line</speak>")]
    [InlineData("<speak><s></p><p></s></speak>")]
    [InlineData("<speak><mark name=\"x\"></speak>")]
    public void Malformed_documents_are_refused_so_the_caller_strips(string ssml)
    {
        Assert.False(SsmlDocument.TryParse(ssml, out var fragments));
        Assert.Empty(fragments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("if (a < b && c > d) { }")]
    [InlineData("<speaker>not ours</speaker>")]
    [InlineData("<html><body>hi</body></html>")]
    public void Anything_that_is_not_a_document_is_refused(string? text)
    {
        Assert.False(SsmlDocument.TryParse(text, out _));
    }

    [Fact]
    public void An_unterminated_tag_is_text_not_a_crash()
    {
        // TagEnd refuses a '<' with no '>' after it, so this is words, as the
        // strip treats it — better a stray bracket than a dropped utterance.
        Assert.False(SsmlDocument.TryParse("<speak>one <two", out _));
    }

    [Fact]
    public void A_self_closing_speak_is_an_empty_document()
    {
        Assert.True(SsmlDocument.TryParse("<speak/>", out var f));
        Assert.Empty(f);
    }

    [Fact]
    public void Cdata_is_spoken_literally()
    {
        var f = Assert.Single(Parse("<speak><![CDATA[a < b & c]]></speak>"));
        Assert.Equal("a < b & c", f.Text);
    }

    /// <summary>
    /// The never-throws half, held by volume rather than by example: a thousand
    /// mutations of a rich document — cut anywhere, with a character swapped,
    /// duplicated or deleted — must each return true or false and nothing else.
    /// </summary>
    [Fact]
    public void No_input_makes_the_parser_throw()
    {
        string seed = Doc(
            "Hi <voice xml:lang=\"fr\">salut <prosody rate=\"+20%\">vite<break time=\"1s\"/></prosody></voice>"
            + "<mark name=\"m&amp;\"/><sub alias=\"x\">y</sub><!-- c --><![CDATA[z]]>&#x41;&bogus;");
        var rng = new Random(20261003);
        const string noise = "<>/\"'&;=!?- \n\t\0\uD800";

        for (int i = 0; i < 1000; i++)
        {
            string s = seed;
            switch (rng.Next(4))
            {
                case 0: s = s[..rng.Next(s.Length)]; break;
                case 1: s = s.Remove(rng.Next(s.Length), 1); break;
                case 2: s = s.Insert(rng.Next(s.Length), noise[rng.Next(noise.Length)].ToString()); break;
                default:
                    int at = rng.Next(s.Length);
                    s = s[..at] + s[at] + s[at..];
                    break;
            }

            var ex = Record.Exception(() => SsmlDocument.TryParse(s, out _));
            Assert.Null(ex);
        }
    }

    // --------------------------------------------------- volume and the wire

    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(-100, 0.0f)]
    [InlineData(-50, 0.5f)]
    [InlineData(50, 1.5f)]
    [InlineData(100, 2.0f)]
    [InlineData(-1000, 0.0f)]     // clamped, never refused
    [InlineData(1000, 2.0f)]
    public void Speechd_volume_is_a_linear_gain_with_unity_at_zero(int volume, float expected)
    {
        Assert.Equal(expected, SpeechRate.SpeechdVolumeScale(volume), 4);
    }

    [Fact]
    public void Volume_is_monotonic_across_the_whole_range()
    {
        float last = -1;
        for (int v = -100; v <= 100; v++)
        {
            float g = SpeechRate.SpeechdVolumeScale(v);
            Assert.True(g >= last, $"volume {v} is quieter than {v - 1}");
            last = g;
        }
    }

    [Fact]
    public void A_mark_line_survives_the_round_trip_including_hostile_names()
    {
        foreach (string name in new[] { "simple", "__spd_id_12", "with space", "line\nbreak", "tab\there", "ünï ✓", "%41", "" })
        {
            string line = MarkLine.Format(new RenderMark(name, 4410));

            Assert.DoesNotContain('\n', line);
            Assert.True(MarkLine.TryParse(line, out var back));
            Assert.Equal(name, back.Name);
            Assert.Equal(4410, back.Sample);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("some ordinary diagnostic")]
    [InlineData("@vst-mark")]
    [InlineData("@vst-mark nope name")]
    [InlineData("@vst-mark -5 name")]
    [InlineData("@vst-mark 5")]
    public void Only_a_well_formed_mark_line_is_a_mark(string line)
    {
        Assert.False(MarkLine.TryParse(line, out _));
    }

    [Fact]
    public void A_name_is_made_safe_for_a_protocol_line()
    {
        Assert.Equal("a b c", MarkLine.ForProtocolLine("a\nb\rc"));
        Assert.Equal("__spd_id_3", MarkLine.ForProtocolLine("__spd_id_3"));
    }

    [Fact]
    public void Marks_and_volume_cross_the_protocol_and_are_absent_when_unused()
    {
        var request = new Request { Verb = RequestVerb.Render, Text = "x", Volume = -20, Marks = true };
        var back = Protocol.TryDecode<Request>(Protocol.Encode(request));
        Assert.Equal(-20, back!.Volume);
        Assert.True(back.Marks);

        // A client that never asked sends neither field: an older daemon must
        // not see anything it does not know.
        string plain = Protocol.Encode(new Request { Verb = RequestVerb.Render, Text = "x" });
        Assert.DoesNotContain("olume", plain);
        Assert.DoesNotContain("arks", plain);

        var chunk = new AudioChunk(22050, 1, null, false, [new RenderMark("m", 7)]);
        var chunkBack = Protocol.TryDecode<Response>(Protocol.Encode(new Response { Ok = true, Audio = chunk }));
        Assert.Equal("m", Assert.Single(chunkBack!.Audio!.Marks!).Name);
        Assert.DoesNotContain("marks", Protocol.Encode(new Response { Ok = true, Audio = new AudioChunk(1, 1, null, true) }));
    }
}

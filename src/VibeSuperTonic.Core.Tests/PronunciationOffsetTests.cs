using System.Text.RegularExpressions;
using VibeSuperTonic.Core.Text;
using Xunit;

namespace VibeSuperTonic.Core.Tests;

/// <summary>
/// Regression coverage for R-2 — pronunciation rules silently corrupting word and
/// sentence boundary offsets. This was live on Windows: any length-changing rule
/// shifted every later highlight by the accumulated delta, and the drift started
/// small enough that nobody filed it.
/// </summary>
public class PronunciationOffsetTests
{
    private static PronunciationsConfig Config(params PronunciationRule[] rules) =>
        new() { Enabled = true, Rules = new List<PronunciationRule>(rules) };

    private static PronunciationRule Rule(string match, string replace, bool wholeWord = true) =>
        new() { Enabled = true, Match = match, Replace = replace, WholeWord = wholeWord };

    private static Regex?[] Compile(PronunciationsConfig cfg) =>
        cfg.Rules.Select(PronunciationsConfig.Compile).ToArray();

    // ---------------------------------------------------------------- behaviour

    /// <summary>
    /// The exact algorithm this replaced. Offset tracking must not change one
    /// character of what the engine actually says, so the new implementation is
    /// held against the old one rather than against my expectations of it.
    /// </summary>
    private static string LegacyApply(PronunciationsConfig cfg, string text, IReadOnlyList<Regex?> compiled)
    {
        if (!cfg.Enabled || string.IsNullOrEmpty(text) || cfg.Rules.Count == 0) return text;
        for (int i = 0; i < cfg.Rules.Count; i++)
        {
            var rule = cfg.Rules[i];
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Match)) continue;
            var re = compiled[i];
            if (re is null) continue;
            text = re.Replace(text, rule.Replace ?? "");
        }
        return text;
    }

    public static TheoryData<string, PronunciationsConfig> BehaviourCases() => new()
    {
        { "The load is 5 kg and it moves",  Config(Rule("kg", "kilograms")) },
        { "kg kg kg",                       Config(Rule("kg", "kilograms")) },
        { "NOT a chance",                   Config(Rule("NOT", "not!")) },
        { "nothing matches here",           Config(Rule("kg", "kilograms")) },
        { "chained kg here",                Config(Rule("kg", "kilograms"), Rule("kilograms", "kilos")) },
        { "delete me please",               Config(Rule("me", "")) },
        { "echo kg back",                   Config(Rule("kg", "[$&]")) },   // $& expansion must survive
        { "case KG test",                   Config(new PronunciationRule { Enabled = true, Match = "kg", Replace = "kilograms", CaseSensitive = false }) },
        { "sub-string kgx untouched",       Config(Rule("kg", "kilograms")) },
        { "partial kgx hit",                Config(Rule("kg", "kilograms", wholeWord: false)) },
    };

    [Theory]
    [MemberData(nameof(BehaviourCases))]
    public void Apply_produces_the_same_string_as_the_legacy_implementation(string text, PronunciationsConfig cfg)
    {
        var compiled = Compile(cfg);
        Assert.Equal(LegacyApply(cfg, text, compiled), cfg.Apply(text, compiled, out _));
    }

    // ------------------------------------------------------------------ offsets

    [Fact]
    public void Length_increasing_rule_maps_later_words_back_to_their_true_source()
    {
        //             0123456789...
        const string source = "The load is 5 kg and it moves";
        var cfg = Config(Rule("kg", "kilograms"));

        string spoken = cfg.Apply(source, Compile(cfg), out var map);
        Assert.Equal("The load is 5 kilograms and it moves", spoken);

        // "and" sits at 24 in the rewritten string but at 17 in the source. The
        // old code reported 24, landing seven characters into the wrong word.
        int andInSpoken = spoken.IndexOf("and", StringComparison.Ordinal);
        Assert.Equal(24, andInSpoken);
        Assert.Equal(17, map.ToSource(andInSpoken));
        Assert.Equal("and", source.Substring(map.ToSource(andInSpoken), 3));
    }

    [Fact]
    public void Substituted_span_highlights_what_the_user_actually_wrote()
    {
        const string source = "The load is 5 kg and it moves";
        var cfg = Config(Rule("kg", "kilograms"));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        int start = spoken.IndexOf("kilograms", StringComparison.Ordinal);
        // Nine rewritten characters covering the two the user typed.
        Assert.Equal(14, map.ToSource(start));
        Assert.Equal(2, map.SourceSpanLength(start, "kilograms".Length));
        Assert.Equal("kg", source.Substring(map.ToSource(start), map.SourceSpanLength(start, 9)));
    }

    [Fact]
    public void Every_index_inside_a_substitution_resolves_to_its_start()
    {
        const string source = "a kg b";
        var cfg = Config(Rule("kg", "kilograms"));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        int start = spoken.IndexOf("kilograms", StringComparison.Ordinal);
        for (int i = 0; i < "kilograms".Length; i++)
            Assert.Equal(2, map.ToSource(start + i));
    }

    [Fact]
    public void Length_decreasing_rule_maps_later_words_forward()
    {
        const string source = "The load is 5 kilograms and it moves";
        var cfg = Config(Rule("kilograms", "kg"));

        string spoken = cfg.Apply(source, Compile(cfg), out var map);
        Assert.Equal("The load is 5 kg and it moves", spoken);

        int andInSpoken = spoken.IndexOf("and", StringComparison.Ordinal);
        Assert.Equal(17, andInSpoken);
        Assert.Equal(24, map.ToSource(andInSpoken));
        Assert.Equal("and", source.Substring(map.ToSource(andInSpoken), 3));
    }

    [Fact]
    public void Rules_that_rewrite_each_others_output_still_resolve_to_the_original()
    {
        const string source = "The load is 5 kg and it moves";
        // Rule 2 matches only what rule 1 produced — the case a per-pass delta
        // list has to compose across, and the reason this map is per-character.
        var cfg = Config(Rule("kg", "kilograms"), Rule("kilograms", "kilos"));

        string spoken = cfg.Apply(source, Compile(cfg), out var map);
        Assert.Equal("The load is 5 kilos and it moves", spoken);

        int andInSpoken = spoken.IndexOf("and", StringComparison.Ordinal);
        Assert.Equal(17, map.ToSource(andInSpoken));

        int kilos = spoken.IndexOf("kilos", StringComparison.Ordinal);
        Assert.Equal(14, map.ToSource(kilos));          // back through both passes to "kg"
        Assert.Equal(2, map.SourceSpanLength(kilos, 5));
    }

    [Fact]
    public void Multiple_hits_of_the_same_rule_accumulate_correctly()
    {
        const string source = "kg then kg then kg";
        var cfg = Config(Rule("kg", "kilograms"));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        // Third occurrence: source index 16, and every 'then' in between maps back.
        int third = spoken.LastIndexOf("kilograms", StringComparison.Ordinal);
        Assert.Equal(16, map.ToSource(third));

        int secondThen = spoken.IndexOf("then", spoken.IndexOf("then", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        Assert.Equal(11, map.ToSource(secondThen));
        Assert.Equal("then", source.Substring(map.ToSource(secondThen), 4));
    }

    [Fact]
    public void Deleting_rule_maps_the_following_text_correctly()
    {
        const string source = "please delete me now";
        var cfg = Config(Rule("delete ", "", wholeWord: false));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        Assert.Equal("please me now", spoken);
        int now = spoken.IndexOf("now", StringComparison.Ordinal);
        Assert.Equal("now", source.Substring(map.ToSource(now), 3));
    }

    // ------------------------------------------------------------- fast paths

    [Fact]
    public void No_rule_firing_yields_an_identity_map_and_the_same_instance()
    {
        const string source = "nothing here matches";
        var cfg = Config(Rule("kg", "kilograms"));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        Assert.Same(source, spoken);
        Assert.True(map.IsIdentity);
        Assert.Equal(7, map.ToSource(7));
    }

    [Fact]
    public void Disabled_config_is_identity()
    {
        var cfg = Config(Rule("kg", "kilograms"));
        cfg.Enabled = false;
        string spoken = cfg.Apply("5 kg", Compile(cfg), out var map);

        Assert.Equal("5 kg", spoken);
        Assert.True(map.IsIdentity);
    }

    [Fact]
    public void Disabled_rule_is_skipped()
    {
        var cfg = Config(Rule("kg", "kilograms"));
        cfg.Rules[0].Enabled = false;
        Assert.Equal("5 kg", cfg.Apply("5 kg", Compile(cfg), out var map));
        Assert.True(map.IsIdentity);
    }

    [Fact]
    public void Empty_and_null_text_are_safe()
    {
        var cfg = Config(Rule("kg", "kilograms"));
        Assert.Equal("", cfg.Apply("", Compile(cfg), out var m1));
        Assert.Equal(0, m1.ToSource(0));
        Assert.Null(cfg.Apply(null!, Compile(cfg), out var m2));
        Assert.Equal(0, m2.ToSource(5));
    }

    [Fact]
    public void On_demand_compilation_matches_the_cached_path()
    {
        const string source = "The load is 5 kg and it moves";
        var cfg = Config(Rule("kg", "kilograms"));

        string cached = cfg.Apply(source, Compile(cfg), out var cachedMap);
        string onDemand = cfg.Apply(source, null, out var onDemandMap);

        Assert.Equal(cached, onDemand);
        for (int i = 0; i <= cached.Length; i++)
            Assert.Equal(cachedMap.ToSource(i), onDemandMap.ToSource(i));
    }

    [Fact]
    public void Index_past_the_end_clamps_to_the_source_length()
    {
        const string source = "5 kg";
        var cfg = Config(Rule("kg", "kilograms"));
        string spoken = cfg.Apply(source, Compile(cfg), out var map);

        Assert.Equal(source.Length, map.ToSource(spoken.Length));
        Assert.Equal(source.Length, map.ToSource(spoken.Length + 100));
        Assert.Equal(0, map.ToSource(-5));
    }

    // ---------------------------------------------------- whole-word anchoring

    /// <summary>
    /// A symbol rule with WholeWord set could never fire, and the Pronunciations
    /// tab defaults WholeWord to true — so every symbol rule a user added was
    /// born dead. Found on Linux from a real "=" -> " equal " rule that did
    /// nothing; the defect is in shared Core code and was live on Windows for as
    /// long as the feature has existed.
    /// </summary>
    [Theory]
    [InlineData("3 = 4", "3 equal 4")]           // spaces both sides: the normal case, and the broken one
    [InlineData("3=4", "3 equal 4")]             // jammed: the only case that used to work
    [InlineData("x = y", "x equal y")]
    [InlineData("= 4", "equal 4")]               // start of text, no left context at all
    [InlineData("3 =", "3 equal")]
    public void A_symbol_rule_fires_even_with_WholeWord_set(string input, string expected)
    {
        var config = new PronunciationsConfig();
        config.Rules.Add(new PronunciationRule { Match = "=", Replace = " equal ", WholeWord = true });
        var compiled = config.Rules.Select(PronunciationsConfig.Compile).ToArray();

        // Collapse the doubled spaces the replacement introduces; the sanitizer
        // and chunker do that downstream and it is not what this pins.
        string actual = Regex.Replace(config.Apply(input, compiled), @"\s+", " ").Trim();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Whole_word_still_anchors_a_real_word()
    {
        // The other half: the fix must not turn WholeWord into a no-op. "kg"
        // inside "kgs" must still not match.
        var config = new PronunciationsConfig();
        config.Rules.Add(new PronunciationRule { Match = "kg", Replace = "kilograms", WholeWord = true });
        var compiled = config.Rules.Select(PronunciationsConfig.Compile).ToArray();

        Assert.Equal("5 kilograms today", config.Apply("5 kg today", compiled));
        Assert.Equal("5 kgs today", config.Apply("5 kgs today", compiled));
        Assert.Equal("5 workg today", config.Apply("5 workg today", compiled));
    }

    [Fact]
    public void A_mixed_rule_anchors_only_the_side_that_can_bind()
    {
        // "%s" starts with a symbol and ends with a word character, so only the
        // right side gets an anchor.
        var config = new PronunciationsConfig();
        config.Rules.Add(new PronunciationRule { Match = "%s", Replace = " percent ", WholeWord = true });
        var compiled = config.Rules.Select(PronunciationsConfig.Compile).ToArray();

        Assert.Contains("percent", config.Apply("50%s here", compiled));
        Assert.DoesNotContain("percent", config.Apply("50%sx here", compiled));
    }
}

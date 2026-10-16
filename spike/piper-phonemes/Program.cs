// Phase P1 — the go/no-go. Diff our phoneme ids against piper's, over the
// committed corpus, and report every divergence.
//
//     VST_ESPEAK_LIB=<...>/libespeak-ng.so VST_ESPEAK_DATA=<...>/espeak-ng-data \
//       dotnet run --project spike/piper-phonemes
//
// The corpus and its expected ids come from corpus.json, generated once by
// build-corpus.py from piper itself and committed. Running this needs no
// Python, which is the point: the number has to be re-checkable on a machine
// that has never had piper installed.

using System.Text.Json;
using VibeSuperTonic.Spike.PiperPhonemes;

var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var corpusPath = Path.Combine(projectDir, "corpus.json");

var lib = Environment.GetEnvironmentVariable("VST_ESPEAK_LIB");
var data = Environment.GetEnvironmentVariable("VST_ESPEAK_DATA");
var verbose = args.Contains("-v") || args.Contains("--verbose");

if (lib is null || data is null)
{
    Console.Error.WriteLine("""
        Set VST_ESPEAK_LIB and VST_ESPEAK_DATA.

        They are deliberately explicit rather than discovered: this compares
        against the library WE build, and falling back to whatever the distro
        installed would make a passing run mean nothing. See
        docs/PIPER-PLAN.md#espeak-minimal for the build.
        """);
    return 2;
}

using var doc = JsonDocument.Parse(File.ReadAllText(corpusPath));
var root = doc.RootElement;

var idMaps = new Dictionary<string, IReadOnlyDictionary<string, int[]>>();
foreach (var lang in root.GetProperty("id_maps").EnumerateObject())
{
    var map = new Dictionary<string, int[]>();
    foreach (var entry in lang.Value.EnumerateObject())
        map[entry.Name] = entry.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    idMaps[lang.Name] = map;
}

using var espeak = new Espeak(data, lib);

// NEGATIVE CONTROL. Before believing a pass, prove the harness can fail: run
// the corpus once per deliberate sabotage and require every one of them to
// diverge. A comparison that agrees no matter what it compares is this
// project's most-repeated bug, and the only defence is asking it to fail.
if (args.Contains("--negative-control"))
{
    var sabotages = Enum.GetValues<Espeak.Sabotage>().Where(x => x != Espeak.Sabotage.None);
    var allCaught = true;

    foreach (var sabotage in sabotages)
    {
        espeak.Broken = sabotage;
        var bad = CountDivergences(espeak, root, idMaps);
        var caught = bad > 0;
        allCaught &= caught;
        Console.WriteLine($"  {(caught ? "caught" : "MISSED")}  {sabotage,-18} {bad} sentence(s) diverge");
    }

    espeak.Broken = Espeak.Sabotage.None;
    Console.WriteLine();
    Console.WriteLine(allCaught
        ? "The harness fails when it should. A pass below means something."
        : "A SABOTAGE WENT UNNOTICED — the corpus does not cover that behaviour.");
    Console.WriteLine();
    if (!allCaught) return 3;
}

int texts = 0, sentencesExpected = 0, matched = 0, diverged = 0;
var divergenceByLang = new Dictionary<string, int>();
var shown = 0;

foreach (var entry in root.GetProperty("entries").EnumerateArray())
{
    texts++;
    var lang = entry.GetProperty("lang").GetString()!;
    var voice = entry.GetProperty("espeak_voice").GetString()!;
    var text = entry.GetProperty("text").GetString()!;
    var idMap = idMaps[lang];

    var expected = entry.GetProperty("sentences").EnumerateArray()
        .Select(s => s.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray())
        .ToArray();
    var expectedPhonemes = entry.GetProperty("sentences").EnumerateArray()
        .Select(s => s.GetProperty("phonemes").EnumerateArray().Select(x => x.GetString()!).ToArray())
        .ToArray();
    sentencesExpected += expected.Length;

    var ours = espeak.Phonemize(voice, text).Select(s => Espeak.ToIds(s, idMap).ToArray()).ToArray();
    var oursPhonemes = espeak.Phonemize(voice, text).Select(s => s.ToArray()).ToArray();

    // A different SENTENCE COUNT is a divergence in itself, and the one most
    // likely to be missed: comparing only the sentences both sides produced
    // would score a missing sentence as a pass.
    if (ours.Length != expected.Length)
    {
        diverged += Math.Max(ours.Length, expected.Length);
        divergenceByLang[lang] = divergenceByLang.GetValueOrDefault(lang) + 1;
        Report(lang, text, $"sentence count: ours {ours.Length}, piper {expected.Length}", null, null);
        continue;
    }

    for (var i = 0; i < expected.Length; i++)
    {
        if (ours[i].AsSpan().SequenceEqual(expected[i]))
        {
            matched++;
            continue;
        }

        diverged++;
        divergenceByLang[lang] = divergenceByLang.GetValueOrDefault(lang) + 1;
        Report(lang, text, $"sentence {i + 1} of {expected.Length}",
            (oursPhonemes[i], ours[i]), (expectedPhonemes[i], expected[i]));
    }
}

Console.WriteLine();
Console.WriteLine($"corpus     {texts} texts, {sentencesExpected} sentences, {idMaps.Count} languages");
Console.WriteLine($"identical  {matched}");
Console.WriteLine($"diverged   {diverged}");
if (divergenceByLang.Count > 0)
    Console.WriteLine($"           by language: {string.Join(", ", divergenceByLang.Select(kv => $"{kv.Key} {kv.Value}"))}");
Console.WriteLine();
Console.WriteLine(diverged == 0
    ? "PARITY — every sentence in the corpus, id for id."
    : $"NOT PARITY — {diverged} of {sentencesExpected} sentences differ.");

return diverged == 0 ? 0 : 1;

void Report(string lang, string text, string what, (string[] ph, int[] ids)? ours, (string[] ph, int[] ids)? theirs)
{
    // Truncated by default: a hundred divergences of the same shape is one
    // finding, and scrolling past it is how the shape gets missed.
    if (!verbose && shown >= 12)
    {
        if (shown == 12) Console.WriteLine("  … more, run with -v for all of them");
        shown++;
        return;
    }
    shown++;

    Console.WriteLine($"[{lang}] {what}");
    Console.WriteLine($"  text   {Escape(text)}");
    if (ours is { } o && theirs is { } t)
    {
        Console.WriteLine($"  ours   {Escape(string.Concat(o.ph))}");
        Console.WriteLine($"  piper  {Escape(string.Concat(t.ph))}");
        var at = FirstDifference(o.ids, t.ids);
        Console.WriteLine($"  ids differ at {at}: ours {Window(o.ids, at)}  piper {Window(t.ids, at)}");
    }
    Console.WriteLine();
}

static int FirstDifference(int[] a, int[] b)
{
    var n = Math.Min(a.Length, b.Length);
    for (var i = 0; i < n; i++)
        if (a[i] != b[i]) return i;
    return n;
}

static string Window(int[] ids, int at)
    => "[" + string.Join(",", ids.Skip(Math.Max(0, at - 2)).Take(6)) + "]";

static string Escape(string s)
    => s.Replace("\n", "\\n").Replace("\r", "\\r");

static int CountDivergences(Espeak espeak, JsonElement root,
    Dictionary<string, IReadOnlyDictionary<string, int[]>> idMaps)
{
    var bad = 0;
    foreach (var entry in root.GetProperty("entries").EnumerateArray())
    {
        var lang = entry.GetProperty("lang").GetString()!;
        var voice = entry.GetProperty("espeak_voice").GetString()!;
        var text = entry.GetProperty("text").GetString()!;
        var idMap = idMaps[lang];

        var expected = entry.GetProperty("sentences").EnumerateArray()
            .Select(s => s.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray())
            .ToArray();
        var ours = espeak.Phonemize(voice, text)
            .Select(s => Espeak.ToIds(s, idMap).ToArray()).ToArray();

        if (ours.Length != expected.Length) { bad += Math.Max(ours.Length, expected.Length); continue; }
        for (var i = 0; i < expected.Length; i++)
            if (!ours[i].AsSpan().SequenceEqual(expected[i])) bad++;
    }
    return bad;
}

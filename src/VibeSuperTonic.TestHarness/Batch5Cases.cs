using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.TestHarness;

/// <summary>
/// Step 12: the Windows items of the 2026-11-09 code review (fix plan, batch 5).
///
/// Every case here was written before its fix and run against the unfixed
/// engine first, so each one has been seen failing. Most of them read
/// <c>engine.log</c>, because what they defend is what the engine decided
/// (the language, the text it spoke, the bytes it counted), and the log is the
/// one place the engine says so. The rest measure the audio itself.
///
/// The step rewrites <c>settings.json</c> for several cases and puts the
/// original back afterwards, including when a case throws.
/// </summary>
internal static partial class Program
{
    private const int SPF_ASYNC = 1;
    private const int SPF_IS_XML = 8;
    private const int SPF_NLP_SPEAK_PUNC = 64;

    // Four sentences, each long enough that a 100-character chunk limit gives
    // one chunk per sentence: the rate and offset cases need several chunks.
    private const string Passage =
        "The first sentence of this passage is here to fill one chunk of text. " +
        "A second sentence follows it, and it is about the same length as that. " +
        "Then comes the third sentence, which keeps the reader company a while. " +
        "The fourth and last sentence closes the passage and ends the test run.";

    private static int Step12_Batch5()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 12: Windows engine items of the 2026-11-09 review (batch 5) ===");

        string dataDir = GetDataDirFromRegistry();
        string logPath = Path.Combine(dataDir, "logs", "engine.log");
        string settingsPath = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(settingsPath) || !File.Exists(logPath))
        {
            Console.WriteLine($"  FAIL: settings.json or engine.log missing under {dataDir} — run the Control Panel and one Speak first");
            return 1;
        }

        string dir = Path.Combine(Path.GetTempPath(), "vst-harness-b5");
        Directory.CreateDirectory(dir);
        string original = File.ReadAllText(settingsPath);
        var ctx = new B5(logPath, settingsPath, original, dir);

        int fail = 0;
        try
        {
            fail += B5Case("item 9   plain text speaks the configured language (de)", () => B5_DefaultLanguage(ctx));
            fail += B5Case("item 66  a broken settings.json keeps the last good settings", () => B5_BrokenSettings(ctx));
            fail += B5Case("item 48  SPF_NLP_SPEAK_PUNC names the punctuation", () => B5_SpeakPunctuation(ctx));
            fail += B5Case("item 48  <spell> is spoken letter by letter", () => B5_SpellOut(ctx));
            fail += B5Case("item 50  <volume level=25> is quieter", () => B5_SapiVolume(ctx));
            fail += B5Case("item 50  SSML <prosody volume=x-soft> is quieter", () => B5_SsmlVolume(ctx));
            fail += B5Case("item 50  <pitch middle=10> is higher", () => B5_Pitch(ctx));
            fail += B5Case("item 50  <emph> is louder", () => B5_Emphasis(ctx));
            fail += B5Case("item 51  a one-word utterance's tail (pad + drain)", () => B5_ShortTail(ctx));
            fail += B5Case("item 80  word audio offsets rise through every chunk", () => B5_AudioOffsets(ctx));
            fail += B5Case("item 49  a rate change mid-passage reaches later chunks", () => B5_RateChange(ctx));
            fail += B5Case("item 35  the byte total after an abort is the bytes written", () => B5_AbortByteTotal(ctx));
            fail += B5Case("item 30  bookmark names are not leaked", () => B5_BookmarkLeak(ctx));
            fail += B5Case("item 34  the hang ceiling outlasts watchdog + CPU retry, keeps the latch", () => B5_HangCeiling());
            fail += B5Case("item 33  SynthesisSilenceSec reaches the model", () => B5_SynthesisSilence(ctx));
            fail += B5Case("item 32  Tier B is global unless a voice sets it", () => B5_TierBPerVoice(ctx));
            fail += B5Case("item 32  migration drops Tier B copied into per-voice entries", () => B5_TierBMigration(ctx));
        }
        finally
        {
            ctx.Restore();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "  PASS: all batch 5 cases" : $"  {fail} batch 5 case(s) failed");
        return fail;
    }

    // ------------------------------------------------------------ plumbing

    private sealed class B5(string logPath, string settingsPath, string original, string dir)
    {
        public string LogPath { get; } = logPath;
        public string SettingsPath { get; } = settingsPath;
        public string Original { get; } = original;
        public string Dir { get; } = dir;

        /// <summary>The original settings with <paramref name="mutate"/> applied.</summary>
        public void Settings(Action<JsonObject> mutate)
        {
            var root = JsonNode.Parse(Original)!.AsObject();
            mutate(root);
            Write(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Write(string text)
        {
            File.WriteAllText(SettingsPath, text);
            // The engine keys its cache on the file's mtime; give the next
            // Speak a reading that has visibly moved.
            Thread.Sleep(200);
        }

        public void Restore() { try { Write(Original); } catch { } }

        public long Mark() => ReadLogLength(LogPath);
        public string Since(long mark) => ReadLogSince(LogPath, mark);
        public string Wav(string name) => Path.Combine(Dir, name);
    }

    /// <summary>Set by <c>--batch5 &lt;text&gt;</c>: run only the cases whose label contains it.</summary>
    internal static string? B5Filter;

    private static int B5Case(string label, Func<(bool Ok, string Detail)> run)
    {
        if (B5Filter is not null && !label.Contains(B5Filter, StringComparison.OrdinalIgnoreCase)) return 0;
        (bool ok, string detail) result;
        try { result = run(); }
        catch (Exception ex) { result = (false, $"{ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"  {(result.ok ? "PASS" : "FAIL")} {label} — {result.detail}");
        return result.ok ? 0 : 1;
    }

    private static string? LastLine(string log, string contains) =>
        log.Split('\n').LastOrDefault(l => l.Contains(contains, StringComparison.Ordinal))?.Trim();

    private static IEnumerable<string> Lines(string log, string contains) =>
        log.Split('\n').Where(l => l.Contains(contains, StringComparison.Ordinal)).Select(l => l.Trim());

    /// <summary>What the engine said it will speak, joined across fragments.</summary>
    private static string Spoken(string log) =>
        string.Join(" | ", Lines(log, "BuildSpeakPlan: frag action=")
            .Select(l => Regex.Match(l, "spoken=\"(.*)\"$").Groups[1].Value));

    private static void SystemSpeechToFile(string wav, Action<SpeechSynthesizer> speak,
        Action<SpeechSynthesizer>? setup = null)
    {
        using var synth = new SpeechSynthesizer();
        synth.SelectVoice(TargetVoice);
        setup?.Invoke(synth);
        synth.SetOutputToWaveFile(wav);
        speak(synth);
        synth.SetOutputToNull();
    }

    /// <summary>
    /// Speak through SAPI's own SpVoice, rendered to a file. The way to put
    /// SAPI flags and SAPI XML on the wire, which System.Speech cannot do.
    /// <paramref name="whileSpeaking"/> makes the call asynchronous and runs
    /// against the voice while it speaks.
    /// </summary>
    private static void SapiToFile(string text, int flags, string wav,
        Action<dynamic>? whileSpeaking = null, int? eventInterests = null)
    {
        Type spVoice = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("SAPI.SpVoice unavailable");
        Type spFile = Type.GetTypeFromProgID("SAPI.SpFileStream") ?? throw new InvalidOperationException("SAPI.SpFileStream unavailable");
        dynamic voice = Activator.CreateInstance(spVoice)!;
        dynamic stream = Activator.CreateInstance(spFile)!;
        try
        {
            dynamic tokens = voice.GetVoices(string.Empty, string.Empty);
            for (int i = 0; i < tokens.Count; i++)
            {
                dynamic tok = tokens.Item(i);
                if (((string)tok.Id).IndexOf("VibeSuperTonic_M1", StringComparison.OrdinalIgnoreCase) >= 0)
                { voice.Voice = tok; break; }
            }
            if (eventInterests is int ei) voice.EventInterests = ei;
            stream.Open(wav, 3 /* SSFMCreateForWrite */, false);
            voice.AudioOutputStream = stream;
            if (whileSpeaking is null)
            {
                voice.Speak(text, flags);
            }
            else
            {
                voice.Speak(text, flags | SPF_ASYNC);
                whileSpeaking(voice);
                voice.WaitUntilDone(120_000);
            }
            voice.AudioOutputStream = null;
            stream.Close();
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(stream); } catch { }
            try { Marshal.FinalReleaseComObject(voice); } catch { }
        }
    }

    private sealed record Wave(short[] Samples, int Rate)
    {
        public double Seconds => Rate > 0 ? Samples.Length / (double)Rate : 0;

        public double Rms()
        {
            if (Samples.Length == 0) return 0;
            double e = 0;
            foreach (short s in Samples) e += (double)s * s;
            return Math.Sqrt(e / Samples.Length);
        }
    }

    /// <summary>Reads a 16-bit PCM mono WAV, whatever rate SAPI chose for the file.</summary>
    private static Wave ReadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int rate = 0, channels = 1, bits = 16;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToInt16(b, body + 14);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException($"{path}: {bits}-bit audio, expected 16");
                int n = Math.Min(size, b.Length - body) / 2 / Math.Max(1, channels);
                var s = new short[n];
                for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(b, body + i * 2 * channels);
                return new Wave(s, rate);
            }
            pos = body + size + (size & 1);
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }

    /// <summary>
    /// Median fundamental frequency over the loud frames, by normalised
    /// autocorrelation. Crude, and enough: the cases compare two renders of the
    /// same text by the same voice, so the error is common to both.
    /// </summary>
    private static double MedianF0(Wave w)
    {
        var s = w.Samples;
        int rate = w.Rate;
        int frame = rate / 25;
        int minLag = rate / 400, maxLag = rate / 65;
        if (s.Length < frame + maxLag + 1) return 0;

        var energies = new List<(int Start, double E)>();
        for (int start = 0; start + frame + maxLag < s.Length; start += frame / 2)
        {
            double e = 0;
            for (int i = 0; i < frame; i++) e += (double)s[start + i] * s[start + i];
            energies.Add((start, e));
        }
        double loud = energies.Max(x => x.E) * 0.2;

        var f0 = new List<double>();
        foreach (var (start, e) in energies)
        {
            if (e < loud || e <= 0) continue;
            var norm = new double[maxLag + 1];
            double best = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double c = 0, e2 = 0;
                for (int i = 0; i < frame; i++)
                {
                    double a = s[start + i], d = s[start + i + lag];
                    c += a * d;
                    e2 += d * d;
                }
                norm[lag] = e2 > 0 ? c / Math.Sqrt(e * e2) : 0;
                if (norm[lag] > best) best = norm[lag];
            }
            if (best < 0.6) continue;
            // The first lag close to the best, not the best itself: a period's
            // multiples correlate almost as well, and taking them halves F0.
            for (int lag = minLag; lag <= maxLag; lag++)
                if (norm[lag] >= 0.9 * best) { f0.Add(rate / (double)lag); break; }
        }
        if (f0.Count == 0) return 0;
        f0.Sort();
        return f0[f0.Count / 2];
    }

    // --------------------------------------------------------------- cases

    private const string German = "Guten Tag. Wie geht es Ihnen heute? Das Wetter ist schön.";

    private static (bool, string) B5_DefaultLanguage(B5 c)
    {
        c.Settings(s => s["Language"] = "de");
        long mark = c.Mark();
        SystemSpeechToFile(c.Wav("lang.wav"), s => s.Speak(German));
        string? line = LastLine(c.Since(mark), "BuildSpeakPlan: frag LangID=");
        if (line is null) return (false, "engine logged no language line");
        string shown = line[(line.IndexOf("frag ", StringComparison.Ordinal) + 5)..];
        return (line.Contains("lang=de", StringComparison.Ordinal), shown);
    }

    /// <summary>
    /// Runs straight after <see cref="B5_DefaultLanguage"/>, whose German
    /// settings the engine in this process has just read. A file that then
    /// fails to parse must leave them in force, not drop to the defaults.
    /// </summary>
    private static (bool, string) B5_BrokenSettings(B5 c)
    {
        c.Write("{ \"Language\": \"de\", this is not json ::: ");
        long mark = c.Mark();
        SystemSpeechToFile(c.Wav("broken.wav"), s => s.Speak(German));
        string? line = LastLine(c.Since(mark), "BuildSpeakPlan: frag LangID=");
        c.Restore();
        if (line is null) return (false, "engine logged no language line");
        string shown = line[(line.IndexOf("frag ", StringComparison.Ordinal) + 5)..];
        return (line.Contains("lang=de", StringComparison.Ordinal), $"after a corrupt file: {shown}");
    }

    private static (bool, string) B5_SpeakPunctuation(B5 c)
    {
        const string text = "Hello, world. Is it done?";
        long mark = c.Mark();
        SapiToFile(text, 0, c.Wav("nopunc.wav"));
        string plain = Spoken(c.Since(mark));

        mark = c.Mark();
        SapiToFile(text, SPF_NLP_SPEAK_PUNC, c.Wav("punc.wav"));
        string punc = Spoken(c.Since(mark));

        bool ok = punc.Contains("comma", StringComparison.OrdinalIgnoreCase)
               && punc.Contains("question mark", StringComparison.OrdinalIgnoreCase)
               && !plain.Contains("comma", StringComparison.OrdinalIgnoreCase);
        return (ok, $"with flag: \"{punc}\"; without: \"{plain}\"");
    }

    private static (bool, string) B5_SpellOut(B5 c)
    {
        long mark = c.Mark();
        SapiToFile("<spell>VST 42</spell>", SPF_IS_XML, c.Wav("spell.wav"));
        string spoken = Spoken(c.Since(mark));
        bool ok = spoken.Contains("V, S, T", StringComparison.Ordinal)
               && spoken.Contains("4, 2", StringComparison.Ordinal);
        return (ok, $"\"{spoken}\"");
    }

    private const string Sentence = "This sentence is spoken twice, and only one of them is changed.";

    private static (bool, string) B5_SapiVolume(B5 c)
    {
        SapiToFile(Sentence, SPF_IS_XML, c.Wav("vol-a.wav"));
        SapiToFile($"<volume level=\"25\">{Sentence}</volume>", SPF_IS_XML, c.Wav("vol-b.wav"));
        double a = ReadWav(c.Wav("vol-a.wav")).Rms(), b = ReadWav(c.Wav("vol-b.wav")).Rms();
        double ratio = a > 0 ? b / a : 0;
        return (ratio is > 0.1 and < 0.5, $"RMS {a:F0} → {b:F0} (ratio {ratio:F2}, want 0.1–0.5)");
    }

    private static (bool, string) B5_SsmlVolume(B5 c)
    {
        string Ssml(string inner) =>
            "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">" + inner + "</speak>";
        SystemSpeechToFile(c.Wav("svol-a.wav"), s => s.SpeakSsml(Ssml(Sentence)));
        SystemSpeechToFile(c.Wav("svol-b.wav"), s => s.SpeakSsml(Ssml($"<prosody volume=\"x-soft\">{Sentence}</prosody>")));
        double a = ReadWav(c.Wav("svol-a.wav")).Rms(), b = ReadWav(c.Wav("svol-b.wav")).Rms();
        double ratio = a > 0 ? b / a : 0;
        return (ratio is > 0.05 and < 0.7, $"RMS {a:F0} → {b:F0} (ratio {ratio:F2}, want under 0.7)");
    }

    private static (bool, string) B5_Pitch(B5 c)
    {
        SapiToFile(Sentence, SPF_IS_XML, c.Wav("pitch-a.wav"));
        SapiToFile($"<pitch middle=\"10\">{Sentence}</pitch>", SPF_IS_XML, c.Wav("pitch-b.wav"));
        var wa = ReadWav(c.Wav("pitch-a.wav"));
        var wb = ReadWav(c.Wav("pitch-b.wav"));
        double a = MedianF0(wa), b = MedianF0(wb);
        double ratio = a > 0 ? b / a : 0;
        double dur = wa.Seconds > 0 ? wb.Seconds / wa.Seconds : 0;
        return (ratio > 1.12 && dur is > 0.85 and < 1.15,
            $"median F0 {a:F0} → {b:F0} Hz (ratio {ratio:F2}, want > 1.12); duration ratio {dur:F2}");
    }

    private static (bool, string) B5_Emphasis(B5 c)
    {
        SapiToFile(Sentence, SPF_IS_XML, c.Wav("emph-a.wav"));
        SapiToFile($"<emph>{Sentence}</emph>", SPF_IS_XML, c.Wav("emph-b.wav"));
        double a = ReadWav(c.Wav("emph-a.wav")).Rms(), b = ReadWav(c.Wav("emph-b.wav")).Rms();
        double ratio = a > 0 ? b / a : 0;
        return (ratio > 1.1, $"RMS {a:F0} → {b:F0} (ratio {ratio:F2}, want > 1.1)");
    }

    /// <summary>
    /// What a one-word utterance costs after its audio: the silence the engine
    /// appends plus the drain it waits through. An engine that does not log its
    /// pad is the old one, whose pad was a fixed 700 ms.
    /// </summary>
    private static (bool, string) B5_ShortTail(B5 c)
    {
        long mark = c.Mark();
        var sw = Stopwatch.StartNew();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToDefaultAudioDevice();
            synth.Speak("OK.");
        }
        sw.Stop();
        string log = c.Since(mark);
        string? padLine = LastLine(log, "Speak: tail pad ");
        string? drainLine = LastLine(log, "Speak: drained ");
        int pad = padLine is null ? 700 : int.Parse(Regex.Match(padLine, @"tail pad (\d+)ms").Groups[1].Value);
        int drain = drainLine is null ? 0 : int.Parse(Regex.Match(drainLine, @"drained (\d+)ms").Groups[1].Value);
        return (pad + drain <= 600,
            $"pad {pad} ms{(padLine is null ? " (not logged: the fixed 700)" : "")} + drain {drain} ms = {pad + drain} ms (want ≤ 600); Speak took {sw.ElapsedMilliseconds} ms");
    }

    private static void ShortChunks(B5 c) => c.Settings(s => { s["MaxChunkChars"] = 100; s["MinChunkChars"] = 60; });

    /// <summary>
    /// Item 80: the audio offset of every word event the engine hands SAPI,
    /// across a passage of four chunks. A regression that put every boundary at
    /// offset 0, or restarted each chunk at 0, would leave every character
    /// offset check above passing.
    ///
    /// Read from the engine's own trace, after two other routes were measured
    /// and found blind. System.Speech stamps each SpeakProgress with the audio
    /// it has written so far rather than the engine's offset: on this passage
    /// 49 of 53 events repeated a position and the last sat at 7.8 s of 21.2 s.
    /// SpVoice's ISpEventSource queue returned no events at all from a console
    /// process, with every AddEvents logged as S_OK.
    /// </summary>
    private static (bool, string) B5_AudioOffsets(B5 c)
    {
        ShortChunks(c);
        string wav = c.Wav("offsets.wav");
        long mark = c.Mark();
        SystemSpeechToFile(wav, s => s.Speak(Passage));
        string log = c.Since(mark);
        c.Restore();

        int chunks = Lines(log, "Speak: chunk ").Count();
        var offsets = Lines(log, "Speak: AddEvents(word x")
            .SelectMany(l =>
            {
                var m = Regex.Match(l, @"offsets=([\d,]+)");
                return m.Success ? m.Groups[1].Value.Split(',').Select(ulong.Parse) : Enumerable.Empty<ulong>();
            })
            .ToList();
        // The engine's own output format: 44.1 kHz, 16-bit mono.
        const double bytesPerSec = 44100 * 2;
        double dur = ReadWav(wav).Seconds;
        int backwards = 0, flat = 0;
        for (int i = 1; i < offsets.Count; i++)
        {
            if (offsets[i] < offsets[i - 1]) backwards++;
            else if (offsets[i] == offsets[i - 1]) flat++;
        }
        double last = offsets.Count > 0 ? offsets[^1] / bytesPerSec : 0;
        bool ok = chunks >= 3 && offsets.Count >= 20 && backwards == 0 && flat == 0
                  && last <= dur && last >= 0.6 * dur;
        return (ok, $"{offsets.Count} word offsets over {chunks} chunks, {backwards} backwards, {flat} repeated, " +
                    $"last at {last:F2}s of {dur:F2}s");
    }

    private static (bool, string) B5_RateChange(B5 c)
    {
        ShortChunks(c);
        string a = c.Wav("rate-a.wav"), b = c.Wav("rate-b.wav");
        SapiToFile(Passage, 0, a);

        long mark = c.Mark();
        bool sawFirstChunk = false;
        SapiToFile(Passage, 0, b, whileSpeaking: voice =>
        {
            sawFirstChunk = WaitFor(() => c.Since(mark).Contains("Speak: chunk 1:", StringComparison.Ordinal), 30_000);
            voice.Rate = 10;
        });
        c.Restore();

        double da = ReadWav(a).Seconds, db = ReadWav(b).Seconds;
        double ratio = da > 0 ? db / da : 0;
        return (sawFirstChunk && ratio < 0.9,
            $"rate 0 throughout {da:F2}s; rate 10 from the first chunk on {db:F2}s (ratio {ratio:F2}, want < 0.9)");
    }

    private static (bool, string) B5_AbortByteTotal(B5 c)
    {
        ShortChunks(c);
        long mark = c.Mark();
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToDefaultAudioDevice();
            synth.SpeakAsync(Passage);
            WaitFor(() => c.Since(mark).Contains("Speak: chunk 1:", StringComparison.Ordinal), 30_000);
            Thread.Sleep(800); // into the first chunk's audio
            synth.SpeakAsyncCancelAll();
            WaitFor(() => synth.State == SynthesizerState.Ready, 10_000);
        }
        WaitFor(() => c.Since(mark).Contains("Speak: complete:", StringComparison.Ordinal), 10_000);
        c.Restore();

        string log = c.Since(mark);
        long written = Lines(log, "Speak: wrote ")
            .Sum(l => long.Parse(Regex.Match(l, @"wrote (\d+) of").Groups[1].Value));
        string? done = LastLine(log, "Speak: complete:");
        if (done is null) return (false, "engine logged no completion line");
        long total = long.Parse(Regex.Match(done, @"(\d+) bytes total").Groups[1].Value);
        bool aborted = log.Contains("abort", StringComparison.OrdinalIgnoreCase);
        return (aborted && total == written,
            $"aborted={aborted}; written {written} bytes, reported {total} bytes");
    }

    /// <summary>
    /// The engine allocated every bookmark name and never freed it. Measured as
    /// this process's private bytes over rounds of bookmark-only markup — 2,000
    /// bookmarks a round, once with 400-character names (about 1.6 MB of them)
    /// and once with 4-character names. A leak of the names grows with their
    /// length; what both rounds share (SAPI's own per-Speak cost, the voice and
    /// the file stream) is the same either way and cancels in the difference.
    /// Measured 2026-11-20, x64: with the free removed the difference was
    /// 1.56 MB a round; with it, 0.03 and −0.11 MB, both rounds growing about
    /// 0.9 MB, which is that shared cost.
    /// </summary>
    private static (bool, string) B5_BookmarkLeak(B5 c)
    {
        string Markup(int nameLength)
        {
            string name = new('b', nameLength);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 2000; i++) sb.Append("<bookmark mark=\"").Append(name).Append(i).Append("\"/>");
            return sb.ToString();
        }

        long Private()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var p = Process.GetCurrentProcess();
            return p.PrivateMemorySize64;
        }

        // Load the model first. Activating the engine starts a background
        // preload of several hundred MB, and run on a cold process that lands
        // inside the measurement: measured 63 MB a "round" before this line.
        SapiToFile("Warm up.", 0, c.Wav("bm-warm.wav"));

        const int rounds = 4;
        double GrowthPerRoundMb(string xml)
        {
            SapiToFile(xml, SPF_IS_XML, c.Wav("bm.wav"), eventInterests: 0); // warm-up round
            long before = Private();
            for (int r = 0; r < rounds; r++) SapiToFile(xml, SPF_IS_XML, c.Wav("bm.wav"), eventInterests: 0);
            return (Private() - before) / (double)rounds / (1024 * 1024);
        }

        double shortNames = GrowthPerRoundMb(Markup(4));
        double longNames = GrowthPerRoundMb(Markup(400));
        double leak = longNames - shortNames;
        return (leak < 0.5,
            $"per round of 2,000: {longNames:F2} MB with 400-char names, {shortNames:F2} MB with 4-char names; " +
            $"difference {leak:F2} MB (want < 0.5; leaking the names is ~1.6)");
    }

    private static (bool, string) B5_HangCeiling()
    {
        Assembly? engine = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "VibeSuperTonic.Engine");
        if (engine is null) return (false, "engine assembly not loaded in this process");
        var sapi = engine.GetType("VibeSuperTonic.Engine.SapiEngine");
        var adapter = engine.GetType("VibeSuperTonic.Engine.Synth.SupertonicAdapter");
        if (sapi is null || adapter is null) return (false, "engine types not found");

        const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        int Const(Type t, string n) => t.GetField(n, any)?.GetRawConstantValue() is int v ? v : -1;
        int ceiling = Const(sapi, "SynthHardTimeoutMs");
        int watchdog = Const(adapter, "SynthCallWatchdogMs");
        int reload = Const(adapter, "CpuReloadBudgetMs");
        bool order = ceiling > 0 && watchdog > 0 && reload > 0 && ceiling >= 2 * watchdog + reload;

        var latch = adapter.GetField("_useDmlLatchedOff", any);
        var rebuild = adapter.GetMethod("RequestSessionRebuild", any);
        bool kept = false;
        string latchDetail;
        if (latch is null || rebuild is null)
        {
            latchDetail = "no RequestSessionRebuild on the adapter: the ceiling's reset is RequestReset, which clears the latch";
        }
        else
        {
            latch.SetValue(null, true);
            rebuild.Invoke(null, null);
            kept = latch.GetValue(null) is true;
            latch.SetValue(null, false);
            latchDetail = kept ? "the ceiling's rebuild keeps the DirectML latch" : "the ceiling's rebuild cleared the latch";
        }
        return (order && kept,
            $"ceiling {ceiling} ms vs 2 × watchdog {watchdog} ms + CPU reload {reload} ms; {latchDetail}");
    }

    private static (bool, string) B5_SynthesisSilence(B5 c)
    {
        // Two sentences, 340 characters together: one engine chunk, which the
        // model's own splitter (300 characters) cuts in two — the only place
        // SynthesisSilenceSec is inserted.
        const string text =
            "This is the first of two sentences, and it goes on for a while so that the two of them together run past the three hundred characters at which the model splits its input. " +
            "This is the second sentence, and it is about as long again, so that the split falls between the two of them and the silence lands exactly there.";
        string a = c.Wav("sil-a.wav"), b = c.Wav("sil-b.wav");
        c.Settings(s => { s["MaxChunkChars"] = 400; s["MinChunkChars"] = 300; s["SynthesisSilenceSec"] = 0.1; });
        SystemSpeechToFile(a, s => s.Speak(text));
        c.Settings(s => { s["MaxChunkChars"] = 400; s["MinChunkChars"] = 300; s["SynthesisSilenceSec"] = 1.0; });
        SystemSpeechToFile(b, s => s.Speak(text));
        c.Restore();
        double da = ReadWav(a).Seconds, db = ReadWav(b).Seconds;
        return (db - da >= 0.6, $"0.1 s → {da:F2}s, 1.0 s → {db:F2}s (difference {db - da:F2}s, want ≥ 0.6)");
    }

    /// <summary>
    /// Global chunk limits of 80/40 and a per-voice entry for M1 that sets only
    /// Tune-tab (Tier A) knobs. The engine must chunk at 80: a voice that does
    /// not name a Tier B value has not chosen one.
    /// </summary>
    private static (bool, string) B5_TierBPerVoice(B5 c)
    {
        c.Settings(s =>
        {
            s["MaxChunkChars"] = 80;
            s["MinChunkChars"] = 40;
            s["PerVoice"] = new JsonObject
            {
                ["M1"] = new JsonObject
                {
                    ["Preset"] = 0, ["TotalStep"] = 4, ["EngineSpeed"] = 1.0, ["DspRate"] = 1.0,
                    ["DefaultVoice"] = "M1", ["Language"] = "en", ["VolumeTrimDb"] = 0,
                },
            };
        });
        int items = ExecItems(c);
        c.Restore();
        return (items >= 4, $"{items} chunks for {Passage.Length} characters (want ≥ 4 at 80; 200 gives 2)");
    }

    /// <summary>
    /// A file written by the old Tune tab: M1's entry carries its own preset
    /// (Draft, 4 steps) and copies of every Tier B global, MaxChunkChars 200
    /// among them. The launcher's migration must drop the copies and keep the
    /// entry, after which the engine chunks at the global 80.
    /// </summary>
    private static (bool, string) B5_TierBMigration(B5 c)
    {
        c.Settings(s =>
        {
            s["SchemaVersion"] = 6;
            s["MaxChunkChars"] = 80;
            s["MinChunkChars"] = 40;
            s["PerVoice"] = new JsonObject
            {
                ["M1"] = new JsonObject
                {
                    ["Preset"] = 0, ["TotalStep"] = 4, ["EngineSpeed"] = 1.0, ["DspRate"] = 1.0,
                    ["DefaultVoice"] = "M1", ["Language"] = "en", ["VolumeTrimDb"] = 0,
                    ["MaxChunkChars"] = 200, ["MinChunkChars"] = 100, ["InterChunkSilenceMs"] = 200,
                    ["SynthesisSilenceSec"] = 0.3, ["RateClampCeiling"] = 1.3, ["OnnxThreads"] = 0,
                    ["OnnxInterOpThreads"] = 1, ["UseDirectML"] = true, ["DirectMLDeviceId"] = 0,
                },
            };
        });

        string launcher = Path.Combine(GetBaseDirFromRegistry(), "VibeSuperTonic.exe");
        var psi = new ProcessStartInfo(launcher, "--config")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);
        }
        Thread.Sleep(200);

        var after = JsonNode.Parse(File.ReadAllText(c.SettingsPath))!.AsObject();
        var m1 = after["PerVoice"]?["M1"]?.AsObject();
        string[] tierB = { "MaxChunkChars", "MinChunkChars", "InterChunkSilenceMs", "SynthesisSilenceSec",
                           "RateClampCeiling", "OnnxThreads", "OnnxInterOpThreads" };
        var left = m1 is null ? Array.Empty<string>() : tierB.Where(k => m1[k] is not null).ToArray();
        int schema = after["SchemaVersion"]?.GetValue<int>() ?? -1;

        int items = ExecItems(c);
        c.Restore();
        return (m1 is not null && left.Length == 0 && items >= 4,
            $"schema {schema}; M1 entry {(m1 is null ? "gone" : "kept")}, Tier B left in it: " +
            $"{(left.Length == 0 ? "none" : string.Join(", ", left))}; {items} chunks (want ≥ 4)");
    }

    /// <summary>How many chunks the engine planned for <see cref="Passage"/>.</summary>
    private static int ExecItems(B5 c)
    {
        long mark = c.Mark();
        SystemSpeechToFile(c.Wav("tierb.wav"), s => s.Speak(Passage));
        string? line = LastLine(c.Since(mark), "Speak: flattened to ");
        return line is null ? -1 : int.Parse(Regex.Match(line, @"flattened to (\d+)").Groups[1].Value);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace VibeSuperTonic.TestHarness;

internal static class Program
{
    private static readonly Guid EngineClsid = new("F2A8C7B1-1234-5678-9ABC-DEF012345678");
    private static readonly Guid IID_ISpTTSEngine = new("A74D7C8E-4CC5-4F2F-A6EB-804DEE18500E");
    private static readonly Guid IID_ISpObjectWithToken = new("5B559F40-E952-11D2-BB91-00C04F8EE6C0");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private const string TargetVoice = "VibeSuperTonic M1";

    private static int Main(string[] args)
    {
        int failures = 0;
        failures += Step1_CoCreateAndQI();
        failures += Step2_EnumerateVoices();
        failures += Step3_SpeakSync();
        failures += Step4_SpeakAsyncCancel();
        failures += Step5_SsmlEvents();
        failures += Step6_ProsodyRate();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
        return failures;
    }

    private static int Step6_ProsodyRate()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 6: SSML <prosody rate> per-fragment rate ===");
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToDefaultAudioDevice();
            string ssml = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">"
                + "<prosody rate=\"x-fast\">First sentence reads fast.</prosody> "
                + "<prosody rate=\"x-slow\">Second sentence reads slow.</prosody>"
                + "</speak>";
            int wordEvents = 0;
            synth.SpeakProgress += (s, e) => { wordEvents++; };
            var sw = Stopwatch.StartNew();
            synth.SpeakSsml(ssml);
            sw.Stop();
            Console.WriteLine($"  PASS: SpeakSsml returned in {sw.ElapsedMilliseconds} ms, word events: {wordEvents}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Step1_CoCreateAndQI()
    {
        Console.WriteLine("=== Step 1: CoCreateInstance + QueryInterface probes ===");
        try
        {
            var type = Type.GetTypeFromCLSID(EngineClsid, throwOnError: true)!;
            object? inst = Activator.CreateInstance(type);
            Console.WriteLine($"  CoCreateInstance OK: {inst?.GetType()}");
            IntPtr unkPtr = Marshal.GetIUnknownForObject(inst!);
            int ok = Probe("IUnknown", unkPtr, IID_IUnknown)
                   + Probe("ISpTTSEngine", unkPtr, IID_ISpTTSEngine)
                   + Probe("ISpObjectWithToken", unkPtr, IID_ISpObjectWithToken);
            Marshal.Release(unkPtr);
            Marshal.ReleaseComObject(inst!);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Probe(string label, IntPtr unkPtr, Guid iid)
    {
        int hr = Marshal.QueryInterface(unkPtr, iid, out IntPtr ifacePtr);
        if (hr == 0)
        {
            Console.WriteLine($"  QI {label}: OK");
            Marshal.Release(ifacePtr);
            return 0;
        }
        Console.WriteLine($"  QI {label}: FAIL 0x{hr:X8}");
        return 1;
    }

    private static int Step2_EnumerateVoices()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 2: enumerate voices ===");
        using var synth = new SpeechSynthesizer();
        bool found = false;
        foreach (var v in synth.GetInstalledVoices())
        {
            if (string.Equals(v.VoiceInfo.Name, TargetVoice, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  found: [{(v.Enabled ? "enabled" : "DISABLED")}] {v.VoiceInfo.Name} | {v.VoiceInfo.Gender} | {v.VoiceInfo.Age} | {v.VoiceInfo.Culture}");
                found = true;
            }
        }
        if (!found) { Console.WriteLine($"  FAIL: '{TargetVoice}' not found"); return 1; }
        return 0;
    }

    private static int Step3_SpeakSync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 3: SelectVoice + Speak sync (plain text, listening for events) ===");
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToDefaultAudioDevice();
            int wordEvents = 0;
            synth.SpeakProgress += (s, e) => { wordEvents++; };
            var sw = Stopwatch.StartNew();
            synth.Speak("Hello world this is a plain text test");
            sw.Stop();
            Console.WriteLine($"  PASS: Speak() returned in {sw.ElapsedMilliseconds} ms, word events: {wordEvents}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Step5_SsmlEvents()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 5: SSML smoke test — bookmark + word boundary events ===");
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            // Default audio device — SAPI fires time-tied events as audio plays;
            // SetOutputToNull consumes instantly and may swallow word boundaries.
            synth.SetOutputToDefaultAudioDevice();

            int wordEvents = 0;
            int sentenceEvents = 0;
            var bookmarks = new List<string>();
            var firstWord = ""; var lastWord = "";
            synth.SpeakProgress += (s, e) =>
            {
                wordEvents++;
                string w = e.Text ?? "";
                if (firstWord == "" && w.Length > 0) firstWord = w;
                if (w.Length > 0) lastWord = w;
            };
            synth.BookmarkReached += (s, e) => { bookmarks.Add(e.Bookmark); };

            string ssml = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">"
                + "<mark name=\"start\"/>"
                + "Hello world. This is a test."
                + "<mark name=\"end\"/>"
                + "</speak>";
            var sw = Stopwatch.StartNew();
            synth.SpeakSsml(ssml);
            sw.Stop();

            Console.WriteLine($"  word events: {wordEvents} (first='{firstWord}' last='{lastWord}'), bookmarks fired: {bookmarks.Count} ({string.Join(",", bookmarks)})");
            Console.WriteLine($"  SpeakSsml returned in {sw.ElapsedMilliseconds} ms");

            int fail = 0;
            if (wordEvents < 3) { Console.WriteLine($"  WARN: expected ≥3 word boundary events, got {wordEvents}"); fail++; }
            if (bookmarks.Count != 2 || !bookmarks.Contains("start") || !bookmarks.Contains("end"))
            { Console.WriteLine($"  WARN: expected bookmarks 'start' and 'end', got [{string.Join(",", bookmarks)}]"); fail++; }
            if (fail == 0) Console.WriteLine("  PASS: events fired");
            return fail;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Step4_SpeakAsyncCancel()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 4: SpeakAsync + cancel mid-utterance ===");
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            var prompt = synth.SpeakAsync("a longer phrase that should be aborted");
            // give the engine a moment to enter Speak()
            Thread.Sleep(50);
            var sw = Stopwatch.StartNew();
            synth.SpeakAsyncCancelAll();
            // wait for completion / cancellation
            int waited = 0;
            while (synth.State != SynthesizerState.Ready && waited < 5000)
            {
                Thread.Sleep(20);
                waited += 20;
            }
            sw.Stop();
            if (synth.State != SynthesizerState.Ready)
            {
                Console.WriteLine($"  FAIL: synthesizer still {synth.State} after {sw.ElapsedMilliseconds} ms");
                return 1;
            }
            Console.WriteLine($"  PASS: cancelled and back to Ready in {sw.ElapsedMilliseconds} ms (completed: {prompt.IsCompleted})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}

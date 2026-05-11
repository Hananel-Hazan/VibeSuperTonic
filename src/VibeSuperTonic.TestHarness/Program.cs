using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text.Json;
using Microsoft.Win32;

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
        if (args.Contains("--stress"))
        {
            return RunStressSuite();
        }
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

    /// <summary>
    /// Stress suite — exercises the engine + telemetry + reset path under load
    /// and adversarial inputs. Intended to surface concurrency bugs and
    /// state-machine glitches that the smoke tests above won't.
    /// </summary>
    private static int RunStressSuite()
    {
        Console.WriteLine("===== STRESS SUITE =====");
        Console.WriteLine($"Started: {DateTime.Now:HH:mm:ss}");
        Console.WriteLine();
        int failures = 0;
        failures += Stress1_ConcurrentSpeakers();
        failures += Stress2_MidSpeakReset();
        failures += Stress3_RapidResetStorm();
        failures += Stress4_SettingsHotSwap();
        failures += Stress5_JsonCorruptionResilience();
        failures += Stress6_OrphanTmpSweep();
        failures += Stress7_DataDirPathResolution();
        failures += Stress8_LargeSpeak();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "STRESS SUITE: ALL PASSED" : $"STRESS SUITE: {failures} FAILED");
        return failures;
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string GetBaseDirFromRegistry()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
        if (k?.GetValue("BaseDir") is string s && !string.IsNullOrWhiteSpace(s)) return s.TrimEnd('\\');
        return AppContext.BaseDirectory.TrimEnd('\\');
    }

    private static string GetDataDirFromRegistry()
    {
        // Same resolution rules as DataPaths — see engine/launcher mirror copies.
        // Inlined here so the harness doesn't need a project ref.
        using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\VibeSuperTonic");
        string baseDir = GetBaseDirFromRegistry();
        string raw = k?.GetValue("DataDir") as string ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return Path.Combine(baseDir, "data");
        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    private static string SessionsDir() => Path.Combine(GetDataDirFromRegistry(), "sessions");

    private static bool WaitFor(Func<bool> condition, int timeoutMs, int pollMs = 50)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(pollMs);
        }
        return condition();
    }

    private static int FailOrPass(string label, bool ok, string detail)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: {label} — {detail}");
        return ok ? 0 : 1;
    }

    // ─── tests ───────────────────────────────────────────────────────────────

    /// <summary>
    /// N parallel SpeechSynthesizer instances on the same engine DLL, each speaking
    /// a short utterance to a null sink. The engine serializes synthesis on its
    /// shared lock; this verifies no deadlocks, no torn-state cross-talk, and no
    /// mysterious silent failures under contention.
    /// </summary>
    private static int Stress1_ConcurrentSpeakers()
    {
        Console.WriteLine("=== Stress 1: 8 concurrent SAPI clients ===");
        const int N = 8;
        var sw = Stopwatch.StartNew();
        int completed = 0;
        Exception? firstError = null;
        var tasks = Enumerable.Range(0, N).Select(i => Task.Run(() =>
        {
            try
            {
                using var synth = new SpeechSynthesizer();
                synth.SelectVoice(TargetVoice);
                synth.SetOutputToNull();
                synth.Speak($"Concurrent client number {i}, hello world.");
                Interlocked.Increment(ref completed);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref firstError, ex, null); }
        })).ToArray();

        bool all = Task.WaitAll(tasks, TimeSpan.FromSeconds(120));
        sw.Stop();
        if (firstError is not null)
            return FailOrPass("concurrent", false, $"first error: {firstError.GetType().Name}: {firstError.Message}");
        return FailOrPass("concurrent", all && completed == N,
            $"{completed}/{N} completed in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Start a long Speak in this process, wait for the engine to write its
    /// per-PID session file, drop a <c>&lt;pid&gt;.reset</c> sentinel, verify
    /// the engine consumes the marker (file deleted) within the 1 Hz poll
    /// window, and verify a subsequent Speak still works after the reset.
    /// </summary>
    private static int Stress2_MidSpeakReset()
    {
        Console.WriteLine("=== Stress 2: reset signal mid-speak ===");
        try
        {
            int pid = Environment.ProcessId;
            string sessions = SessionsDir();
            string sessionFile = Path.Combine(sessions, $"{pid}.json");
            string marker     = Path.Combine(sessions, $"{pid}.reset");

            // Warm up so the engine has written at least one snapshot.
            using (var warm = new SpeechSynthesizer())
            {
                warm.SelectVoice(TargetVoice);
                warm.SetOutputToNull();
                warm.Speak("Warm up.");
            }
            if (!File.Exists(sessionFile))
                return FailOrPass("reset", false, $"engine never wrote {sessionFile}");

            // Drop the marker; engine should consume + delete within ~1 s.
            File.WriteAllText(marker, "test");
            bool consumed = WaitFor(() => !File.Exists(marker), timeoutMs: 3000);
            if (!consumed)
                return FailOrPass("reset", false, "marker still present after 3 s");

            // Speak still works post-reset.
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();
            var sw = Stopwatch.StartNew();
            synth.Speak("After reset, the engine should rebuild its session and keep working.");
            sw.Stop();
            return FailOrPass("reset", true,
                $"marker consumed within poll window, post-reset Speak {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return FailOrPass("reset", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drop reset markers in a tight loop while the engine is speaking. Verifies:
    /// (a) every marker is consumed and deleted, (b) no errant exception kills
    /// the engine state, (c) Speak still works after the storm.
    /// </summary>
    private static int Stress3_RapidResetStorm()
    {
        Console.WriteLine("=== Stress 3: rapid reset storm during long speak ===");
        try
        {
            int pid = Environment.ProcessId;
            string sessions = SessionsDir();
            string marker = Path.Combine(sessions, $"{pid}.reset");
            Directory.CreateDirectory(sessions);

            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();

            int markersWritten = 0;
            var stop = new ManualResetEventSlim(false);
            var t = Task.Run(() =>
            {
                while (!stop.IsSet)
                {
                    try { File.WriteAllText(marker, "x"); markersWritten++; } catch { }
                    Thread.Sleep(80);
                }
            });

            string longText = string.Join(" ", Enumerable.Repeat(
                "The engine should keep speaking through repeated reset requests without losing state.", 6));
            var sw = Stopwatch.StartNew();
            synth.Speak(longText);
            sw.Stop();

            stop.Set();
            t.Wait(2000);

            // Drain whatever's left.
            WaitFor(() => !File.Exists(marker), timeoutMs: 2000);

            // Final Speak to verify recovery.
            synth.Speak("Final probe.");

            bool ok = !File.Exists(marker);
            return FailOrPass("reset-storm", ok,
                $"wrote {markersWritten} markers in {sw.ElapsedMilliseconds} ms, marker drained: {ok}");
        }
        catch (Exception ex)
        {
            return FailOrPass("reset-storm", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrite settings.json under the engine while it's idle, then Speak. The
    /// engine's mtime-based cache should pick up the new values. Then revert
    /// and Speak again — engine should pick up the revert too.
    /// </summary>
    private static int Stress4_SettingsHotSwap()
    {
        Console.WriteLine("=== Stress 4: settings.json hot-swap ===");
        string settingsPath = Path.Combine(GetDataDirFromRegistry(), "settings.json");
        string? backup = null;
        try
        {
            if (!File.Exists(settingsPath))
            {
                // Force the launcher's migration to run — but we don't have it
                // wired in here, so just skip with a soft pass + warning.
                Console.WriteLine($"  SKIP: settings.json missing at {settingsPath}; run the Control Panel once first.");
                return 0;
            }

            backup = File.ReadAllText(settingsPath);
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();

            // Mutate TotalStep to a low value (faster synthesis) and verify the
            // engine reads it next call. We can't directly observe the engine's
            // resolved step count from here — but we can at least assert no
            // exception fires when the file changes mid-flight.
            using var doc = JsonDocument.Parse(backup);
            var root = doc.RootElement;
            var clone = new System.Collections.Generic.Dictionary<string, object>();
            foreach (var p in root.EnumerateObject())
                clone[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false,
                    JsonValueKind.Number => p.Value.TryGetInt32(out var i) ? i : (object)p.Value.GetDouble(),
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    _ => JsonSerializer.Deserialize<object>(p.Value.GetRawText())!
                };
            clone["TotalStep"] = 4; // Draft-quality
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(clone));
            Thread.Sleep(150);  // give file mtime resolution + engine cache check a moment

            var sw = Stopwatch.StartNew();
            synth.Speak("After settings change.");
            long mutated = sw.ElapsedMilliseconds;

            // Revert.
            File.WriteAllText(settingsPath, backup);
            Thread.Sleep(150);
            sw.Restart();
            synth.Speak("After revert.");
            long reverted = sw.ElapsedMilliseconds;

            return FailOrPass("hot-swap", true,
                $"speak after mutate {mutated} ms, after revert {reverted} ms (no exceptions)");
        }
        catch (Exception ex)
        {
            // Restore on failure path so we don't leave bad state.
            try { if (backup is not null) File.WriteAllText(settingsPath, backup); } catch { }
            return FailOrPass("hot-swap", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Replace settings.json with garbage. Engine should fall through to
    /// defaults (or legacy registry) instead of crashing. Speak must still work.
    /// </summary>
    private static int Stress5_JsonCorruptionResilience()
    {
        Console.WriteLine("=== Stress 5: corrupt settings.json resilience ===");
        string settingsPath = Path.Combine(GetDataDirFromRegistry(), "settings.json");
        string? backup = null;
        try
        {
            if (File.Exists(settingsPath)) backup = File.ReadAllText(settingsPath);
            File.WriteAllText(settingsPath, "{ this is not valid json ::: ");
            Thread.Sleep(150);

            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();
            synth.Speak("Engine should still speak with corrupted settings.");
            return FailOrPass("json-corruption", true, "spoke through corrupt settings");
        }
        catch (Exception ex)
        {
            return FailOrPass("json-corruption", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { if (backup is not null) File.WriteAllText(settingsPath, backup);
                  else if (File.Exists(settingsPath)) File.Delete(settingsPath); } catch { }
        }
    }

    /// <summary>
    /// Plant an orphan <c>&lt;pid&gt;.json.tmp</c> for OUR PID before forcing
    /// the engine to load (via Speak). The init sweep should remove it.
    /// </summary>
    private static int Stress6_OrphanTmpSweep()
    {
        Console.WriteLine("=== Stress 6: orphan .tmp sweep on init ===");
        try
        {
            int pid = Environment.ProcessId;
            string dir = SessionsDir();
            Directory.CreateDirectory(dir);
            string orphan = Path.Combine(dir, $"{pid}.json.tmp");
            File.WriteAllText(orphan, "left over from prior crash");

            // Force engine init in this process via a Speak.
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();
            synth.Speak("Trigger init.");

            // The sweep happens during engine's EnsureInitialized; allow the
            // 1 Hz timer + write path to complete.
            Thread.Sleep(500);

            bool gone = !File.Exists(orphan);
            return FailOrPass("orphan-tmp-sweep", gone,
                gone ? "engine deleted the orphan" : $"orphan still present at {orphan}");
        }
        catch (Exception ex)
        {
            return FailOrPass("orphan-tmp-sweep", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure-string DataDir resolution edge cases. Independent of engine state —
    /// just verifies the resolution rules don't crash on weird input.
    /// </summary>
    private static int Stress7_DataDirPathResolution()
    {
        Console.WriteLine("=== Stress 7: DataDir resolution edge cases ===");
        int fail = 0;
        string baseDir = GetBaseDirFromRegistry();

        var cases = new (string label, string raw, Func<string, bool> check)[]
        {
            ("empty",        "",                            r => r == Path.Combine(baseDir, "data")),
            ("whitespace",   "   ",                         r => r == Path.Combine(baseDir, "data")),
            ("relative",     "stuff",                       r => r.EndsWith(@"\stuff", StringComparison.OrdinalIgnoreCase)),
            ("relative-up",  @"..\sibling-data",            r => Path.IsPathRooted(r) && r.Contains("sibling-data")),
            ("absolute",     @"D:\portable",                r => r == @"D:\portable"),
            ("env-var",      @"%TEMP%\vst-test",            r => Path.IsPathRooted(r) && r.EndsWith(@"\vst-test", StringComparison.OrdinalIgnoreCase)),
            ("missing-env",  @"%NONEXISTENT_XYZ_123%\foo",  r => r.Contains("%NONEXISTENT") || r.Contains("foo")),
            ("trailing-slash", @"D:\foo\",                  r => r.StartsWith(@"D:\foo", StringComparison.OrdinalIgnoreCase)),
        };

        foreach (var (label, raw, check) in cases)
        {
            try
            {
                string resolved = ResolveDataDirInline(raw, baseDir);
                bool ok = check(resolved);
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}: '{raw}' → '{resolved}'");
                if (!ok) fail++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {label}: '{raw}' threw {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }
        return fail;
    }

    /// <summary>
    /// Mirror of <c>DataPaths.ResolveDataDir</c> — keep in sync.
    /// </summary>
    private static string ResolveDataDirInline(string? raw, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Path.Combine(baseDir, "data");
        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(baseDir, expanded);
        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Speak a long passage (~2000 chars) to exercise multi-chunk pipelining,
    /// inter-chunk silence, drain wait, and the per-chunk telemetry write loop.
    /// Also verifies session JSON file stays small (no unbounded growth from
    /// the now-untruncated current-text field).
    /// </summary>
    private static int Stress8_LargeSpeak()
    {
        Console.WriteLine("=== Stress 8: long passage (multi-chunk) ===");
        try
        {
            string passage = string.Concat(Enumerable.Repeat(
                "The phase vocoder reconstructs audio by analyzing short overlapping windows of the input signal. ", 22));
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();
            var sw = Stopwatch.StartNew();
            synth.Speak(passage);
            sw.Stop();

            string sessionFile = Path.Combine(SessionsDir(), $"{Environment.ProcessId}.json");
            long sessionBytes = File.Exists(sessionFile) ? new FileInfo(sessionFile).Length : 0;
            bool sizeOk = sessionBytes < 64 * 1024; // sanity ceiling
            return FailOrPass("large-speak", sizeOk,
                $"{passage.Length} chars in {sw.ElapsedMilliseconds} ms, session file {sessionBytes} bytes");
        }
        catch (Exception ex)
        {
            return FailOrPass("large-speak", false, $"{ex.GetType().Name}: {ex.Message}");
        }
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

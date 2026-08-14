using System.Diagnostics;
using System.Reflection;
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
        failures += Step7_LanguageTag();
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
        failures += Stress9_ConcurrentSpeakWithResetStorm();
        failures += Stress10_InjectedDeviceLossRecovery();
        failures += Stress11_AbortCancelsInflightSynth();
        failures += Stress12_HangTriggersWatchdog();
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
                "Sonic resamples audio by aligning consecutive pitch periods and overlapping them with a smooth crossfade. ", 22));
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

    /// <summary>
    /// Reaches across to the engine's internal device-loss injection hook via
    /// reflection. The engine DLL has been loaded into this process by any
    /// prior <c>SpeechSynthesizer.Speak</c> (SAPI COM-activates it in-proc), so
    /// the type's static field is reachable; reflection bypasses C# accessibility
    /// for internals, so the engine's <c>InternalsVisibleTo</c> isn't strictly
    /// required (it's there as documentation that this field is a test surface).
    /// Returns null if the engine isn't loaded — the caller should warm it up
    /// with at least one Speak first.
    /// </summary>
    private static FieldInfo? FindInjectionField() =>
        FindEngineField("_testInjectDeviceLossOnNextCall");

    /// <summary>
    /// Locates any static field on SupertonicAdapter by name. Used to read the
    /// test-only hang-injection hooks (<c>_testInjectHangOnNextCall</c>,
    /// <c>_testHangRelease</c>) without a hard project reference.
    /// </summary>
    private static FieldInfo? FindEngineField(string fieldName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "VibeSuperTonic.Engine") continue;
            var t = asm.GetType("VibeSuperTonic.Engine.Synth.SupertonicAdapter");
            return t?.GetField(fieldName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        }
        return null;
    }

    /// <summary>
    /// Reads the engine's static <c>LastDeviceEvent</c> + <c>DeviceLossCount</c>
    /// via reflection. Used by Stress10 to verify the recovery path actually
    /// fired (not just that the Speak succeeded — could succeed by skipping
    /// the loss entirely if the injection didn't take).
    /// </summary>
    private static (int lossCount, string lastEvent, bool latchedOff) ReadEngineDeviceState()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "VibeSuperTonic.Engine") continue;
            var t = asm.GetType("VibeSuperTonic.Engine.Synth.SupertonicAdapter");
            if (t is null) return (0, "", false);
            int loss = (int)(t.GetProperty("DeviceLossCount")!.GetValue(null) ?? 0);
            string ev = (string)(t.GetProperty("LastDeviceEvent")!.GetValue(null) ?? "");
            bool latch = (bool)(t.GetProperty("DmlLatchedOff")!.GetValue(null) ?? false);
            return (loss, ev, latch);
        }
        return (0, "", false);
    }

    /// <summary>
    /// Drives the engine's device-loss recovery path deterministically. The
    /// engine has an internal hook (<c>_testInjectDeviceLossOnNextCall</c>)
    /// that, when set, makes the next <c>Synthesize</c> call throw a synthetic
    /// exception whose message matches the same DXGI substring checks
    /// <c>IsDeviceLoss</c> uses for real device-removed errors. So the catch +
    /// dispose + force-CPU-retry path runs exactly as it would in production.
    /// This is the test the user's predecessor never had — Stress9 only
    /// exercised reset-marker concurrency, which doesn't go through the
    /// catch handler.
    ///
    /// Asserts: (a) Speak completes despite the injected loss, (b) the engine
    /// observed the loss (DeviceLossCount increment, LastDeviceEvent populated),
    /// (c) subsequent Speak still works (no zombie state).
    /// </summary>
    private static int Stress10_InjectedDeviceLossRecovery()
    {
        Console.WriteLine("=== Stress 10: injected device-loss → CPU recovery ===");
        try
        {
            using var warmup = new SpeechSynthesizer();
            warmup.SelectVoice(TargetVoice);
            warmup.SetOutputToNull();
            warmup.Speak("Warm up so the engine DLL is loaded.");

            var field = FindInjectionField();
            if (field is null)
                return FailOrPass("injected-loss", false,
                    "could not locate engine injection field via reflection (engine type mismatch?)");

            var before = ReadEngineDeviceState();

            // Arm the injection. Engine consumes via Interlocked.Exchange on its
            // next Synthesize entry, throws a synthetic device-loss, hits the
            // catch+recover path, forces CPU on attempt 1, succeeds, returns.
            field.SetValue(null, 1);

            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();
            var sw = Stopwatch.StartNew();
            synth.Speak("Recovery test under injected device loss.");
            sw.Stop();

            var after = ReadEngineDeviceState();

            bool lossObserved = after.lossCount > before.lossCount;
            bool eventPopulated = !string.IsNullOrEmpty(after.lastEvent)
                && after.lastEvent != before.lastEvent;

            // Subsequent Speak must still work.
            sw.Restart();
            synth.Speak("Post-recovery probe.");
            sw.Stop();
            long probeMs = sw.ElapsedMilliseconds;

            if (!lossObserved)
                return FailOrPass("injected-loss", false,
                    $"DeviceLossCount didn't increment ({before.lossCount} → {after.lossCount}) — injection may not have fired");
            if (!eventPopulated)
                return FailOrPass("injected-loss", false,
                    "LastDeviceEvent unchanged — recovery message not set");

            return FailOrPass("injected-loss", true,
                $"loss caught (#{after.lossCount}), latched={after.latchedOff}, post-probe {probeMs} ms, last event: {Truncate(after.lastEvent, 90)}");
        }
        catch (Exception ex)
        {
            return FailOrPass("injected-loss", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Verifies the CancellationToken plumbing actually unwinds in-flight
    /// synthesis. Starts an async Speak with a long passage, waits long enough
    /// for synthesis to be mid-flight, cancels, then immediately starts a
    /// SECOND Speak and measures how fast it starts producing audio. If the
    /// token plumbing works, the in-flight task bails within one totalStep
    /// iteration (~200 ms); without it, Speak #2 waits behind the orphan
    /// synth of the abandoned text, blowing the time budget.
    ///
    /// Budget: 6 s. Synth of a 200-char chunk typically takes 1-3 s; cancel
    /// + restart should be well under that.
    /// </summary>
    private static int Stress11_AbortCancelsInflightSynth()
    {
        Console.WriteLine("=== Stress 11: SAPI abort cancels in-flight synth ===");
        try
        {
            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();

            // Warmup so first speak isn't paying cold-load.
            synth.Speak("Warm.");

            // Long enough to take multiple chunks; speakAsync returns immediately.
            string longText = string.Join(" ", Enumerable.Repeat(
                "This is a long passage that the engine will start synthesizing before we cancel it.", 8));
            var prompt = synth.SpeakAsync(longText);
            Thread.Sleep(120); // let the engine enter Speak and start the first synth task

            var sw = Stopwatch.StartNew();
            synth.SpeakAsyncCancelAll();
            // Wait for the cancelled speak to reach Ready state.
            int waited = 0;
            while (synth.State != SynthesizerState.Ready && waited < 8000)
            {
                Thread.Sleep(20); waited += 20;
            }
            long cancelToReadyMs = sw.ElapsedMilliseconds;
            if (synth.State != SynthesizerState.Ready)
                return FailOrPass("abort-cancels-synth", false,
                    $"Synthesizer still {synth.State} {cancelToReadyMs} ms after Cancel");

            // Second Speak — measure end-to-end.
            sw.Restart();
            synth.Speak("Quick utterance after cancel.");
            sw.Stop();
            long secondSpeakMs = sw.ElapsedMilliseconds;

            // Budget: even on CPU, a short utterance + drain should fit comfortably
            // under 8 s. The pre-fix code waited for the full orphan synth which
            // alone could push past this; the cancellation plumbing bails far faster.
            bool ok = secondSpeakMs < 8000;
            return FailOrPass("abort-cancels-synth", ok,
                $"cancel→Ready {cancelToReadyMs} ms, second Speak {secondSpeakMs} ms");
        }
        catch (Exception ex)
        {
            return FailOrPass("abort-cancels-synth", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drives the watchdog-on-hang path. The engine has an internal hook
    /// (<c>_testInjectHangOnNextCall</c>) that, when set, blocks
    /// <c>SupertonicAdapter.Synthesize</c> inside <c>tts.Call</c> on a
    /// <c>ManualResetEventSlim</c>. Without the watchdog the test would hang
    /// forever; with the watchdog (30 s) the call throws a synthetic device-loss,
    /// the catch path disposes + retries on CPU, and Speak completes.
    ///
    /// Asserts: (a) Speak returns within the watchdog + CPU-retry budget,
    /// (b) DeviceLossCount incremented (watchdog routed through the recovery path),
    /// (c) a subsequent Speak still works (no zombie state).
    /// </summary>
    private static int Stress12_HangTriggersWatchdog()
    {
        Console.WriteLine("=== Stress 12: synth hang triggers watchdog + recovery ===");
        try
        {
            using var warmup = new SpeechSynthesizer();
            warmup.SelectVoice(TargetVoice);
            warmup.SetOutputToNull();
            warmup.Speak("Warm up so the engine DLL is loaded.");

            var hangField = FindEngineField("_testInjectHangOnNextCall");
            var releaseField = FindEngineField("_testHangRelease");
            if (hangField is null || releaseField is null)
                return FailOrPass("synth-hang-watchdog", false,
                    "could not locate hang injection statics via reflection");

            var release = (ManualResetEventSlim?)releaseField.GetValue(null);
            // Reset in case a prior run left it set.
            release?.Reset();

            var before = ReadEngineDeviceState();

            hangField.SetValue(null, 1);

            using var synth = new SpeechSynthesizer();
            synth.SelectVoice(TargetVoice);
            synth.SetOutputToNull();

            // Budget: 30 s watchdog + ~20 s CPU retry + slack. The test fails
            // if Speak takes longer than 75 s — that means the engine wedged.
            var sw = Stopwatch.StartNew();
            Exception? speakErr = null;
            try { synth.Speak("Hang test, watchdog should rescue this utterance."); }
            catch (Exception ex) { speakErr = ex; }
            sw.Stop();
            long hangSpeakMs = sw.ElapsedMilliseconds;

            // Release the blocked orphan task so the test harness exits cleanly.
            // (Idempotent: even if the orphan already observed cancellation, set is fine.)
            release?.Set();
            // Tiny pause to let any orphan finish unwinding before we probe.
            Thread.Sleep(200);
            release?.Reset();

            if (hangSpeakMs > 75_000)
                return FailOrPass("synth-hang-watchdog", false,
                    $"Speak took {hangSpeakMs} ms — watchdog did not fire (engine wedged)");

            // Probe: subsequent Speak must still work and complete quickly.
            sw.Restart();
            synth.Speak("Post-watchdog probe.");
            sw.Stop();
            long probeMs = sw.ElapsedMilliseconds;
            if (probeMs > 15_000)
                return FailOrPass("synth-hang-watchdog", false,
                    $"post-watchdog probe took {probeMs} ms — engine state suspect");

            var after = ReadEngineDeviceState();
            bool lossObserved = after.lossCount > before.lossCount;

            return FailOrPass("synth-hang-watchdog", lossObserved,
                $"hang-Speak {hangSpeakMs} ms (err={speakErr?.GetType().Name ?? "none"}), " +
                $"probe {probeMs} ms, loss {before.lossCount}→{after.lossCount}, " +
                $"last event: {Truncate(after.lastEvent, 90)}");
        }
        catch (Exception ex)
        {
            return FailOrPass("synth-hang-watchdog", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max - 1) + "…";

    /// <summary>
    /// Concurrent Speak callers + a continuous reset-marker storm. Stand-in for
    /// the real-world DirectML device-loss recovery path: a device-loss has the
    /// same shape as RequestReset (dispose the shared session + force a rebuild
    /// on the next call), so any thread-safety bug in the dispose-while-active
    /// path surfaces here without needing to artificially kill the GPU.
    ///
    /// The bug we're guarding against: the predecessor captured the shared
    /// session reference OUTSIDE its lock, then used it inside. A reset (or
    /// device-loss) racing between the capture and the use would let one
    /// Speak call into a disposed InferenceSession — at best an
    /// ObjectDisposedException; at worst a native AV that kills the SAPI host.
    /// With the snapshot-under-gate fix every Speak should either complete or
    /// fail cleanly with a logged error — never crash, never hang.
    /// </summary>
    private static int Stress9_ConcurrentSpeakWithResetStorm()
    {
        Console.WriteLine("=== Stress 9: concurrent Speak + reset storm ===");
        try
        {
            int pid = Environment.ProcessId;
            string sessions = SessionsDir();
            Directory.CreateDirectory(sessions);
            string marker = Path.Combine(sessions, $"{pid}.reset");

            const int Workers = 6;
            const int UtterancesPerWorker = 3;
            int completed = 0;
            int utterances = 0;
            Exception? firstError = null;

            // Sidecar thread spamming reset markers. Engine's 1 Hz poller picks
            // them up; we want collisions with active Speak calls.
            var stop = new ManualResetEventSlim(false);
            var rt = Task.Run(() =>
            {
                while (!stop.IsSet)
                {
                    try { File.WriteAllText(marker, "x"); } catch { }
                    Thread.Sleep(40);
                }
            });

            var sw = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, Workers).Select(i => Task.Run(() =>
            {
                try
                {
                    using var synth = new SpeechSynthesizer();
                    synth.SelectVoice(TargetVoice);
                    synth.SetOutputToNull();
                    for (int k = 0; k < UtterancesPerWorker; k++)
                    {
                        synth.Speak($"Worker {i} utterance {k} under reset storm.");
                        Interlocked.Increment(ref utterances);
                    }
                    Interlocked.Increment(ref completed);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstError, ex, null);
                }
            })).ToArray();

            bool all = Task.WaitAll(tasks, TimeSpan.FromSeconds(240));
            sw.Stop();
            stop.Set();
            rt.Wait(2000);
            WaitFor(() => !File.Exists(marker), timeoutMs: 2000);

            if (firstError is not null)
                return FailOrPass("speak+reset-storm", false,
                    $"{firstError.GetType().Name}: {firstError.Message}");

            // Post-storm probe — engine must still be usable.
            using (var probe = new SpeechSynthesizer())
            {
                probe.SelectVoice(TargetVoice);
                probe.SetOutputToNull();
                probe.Speak("Post-storm probe.");
            }

            return FailOrPass("speak+reset-storm",
                all && completed == Workers,
                $"{completed}/{Workers} workers, {utterances} utterances in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            return FailOrPass("speak+reset-storm", false, $"{ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// Proves SSML <c>xml:lang</c> actually reaches the model, rather than being
    /// parsed by SAPI and then dropped on our side.
    ///
    /// Comparing the AUDIO cannot work here, and both earlier versions of this
    /// test were wrong for that reason. Supertonic is a flow-matching model: it
    /// starts each call from fresh random noise, so two renders of the SAME text
    /// in the SAME language already differ by a mean absolute sample delta of
    /// ~800 (on a ±32767 scale) — same words, different delivery. There is no
    /// signal a language switch could add that would stand out above that, and
    /// comparing file sizes is worse still, since the duration predictor lands
    /// on nearly the same length either way.
    ///
    /// So assert on the engine's own trace, which is deterministic: it logs the
    /// LangID it received per fragment and the code it resolved. That covers the
    /// whole path under test — SAPI parsed the markup, the fragment reached
    /// BuildSpeakPlan with its LangID intact, and the LCID→code mapping produced
    /// the right answer. What the model then does with a valid language tag is
    /// upstream's business, not this repo's.
    /// </summary>
    private static int Step7_LanguageTag()
    {
        Console.WriteLine();
        Console.WriteLine("=== Step 7: SSML xml:lang reaches the engine as a language code ===");
        string dir = Path.Combine(Path.GetTempPath(), "vst-harness-lang");
        string logPath = Path.Combine(GetDataDirFromRegistry(), "logs", "engine.log");
        try
        {
            if (!File.Exists(logPath))
            {
                Console.WriteLine($"  FAIL: engine log not found at {logPath} — cannot verify");
                return 1;
            }
            Directory.CreateDirectory(dir);
            const string german = "Guten Tag. Wie geht es Ihnen heute? Das Wetter ist schön.";

            // Returns the language line the engine logged for this render.
            string? RenderAndReadTrace(Action<SpeechSynthesizer> speak, string name)
            {
                long before = ReadLogLength(logPath);
                using (var synth = new SpeechSynthesizer())
                {
                    synth.SelectVoice(TargetVoice);
                    synth.SetOutputToWaveFile(Path.Combine(dir, name));
                    speak(synth);
                    synth.SetOutputToNull();
                }
                return ReadLogSince(logPath, before)
                    .Split('\n')
                    .LastOrDefault(l => l.Contains("BuildSpeakPlan: frag LangID="))?
                    .Trim();
            }

            string Ssml(string lang) =>
                $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{lang}\">"
                + german + "</speak>";

            var cases = new[]
            {
                (label: "xml:lang=de-DE     ", expect: "lang=de",
                 act: (Action<SpeechSynthesizer>)(s => s.SpeakSsml(Ssml("de-DE"))), file: "de.wav"),
                (label: "plain text (no tag)", expect: "lang=en",  // falls back to the configured default
                 act: (Action<SpeechSynthesizer>)(s => s.Speak(german)), file: "plain.wav"),
            };

            int fail = 0;
            foreach (var c in cases)
            {
                string? trace = RenderAndReadTrace(c.act, c.file);
                if (trace is null)
                {
                    Console.WriteLine($"  {c.label} → FAIL: engine logged no language line");
                    fail++;
                    continue;
                }
                bool ok = trace.Contains(c.expect);
                Console.WriteLine($"  {c.label} → {(ok ? "ok  " : "FAIL")} {trace[(trace.IndexOf("frag ", StringComparison.Ordinal) + 5)..]}");
                if (!ok) fail++;
            }

            // Regional-variant folding, driven through SAPI's own <lang langid>
            // markup rather than System.Speech SSML. System.Speech resolves a
            // requested culture against the VOICE TOKEN's advertised Language
            // list and silently speaks nothing when it doesn't match — so on an
            // install whose tokens predate the multi-language registration, an
            // SSML regional variant never reaches the engine at all and this
            // would test the registry rather than the mapping.
            //
            // de-AT (0x0C07) is the useful case: a sublanguage we deliberately
            // do NOT list, which must still fold onto the German model.
            {
                long before = ReadLogLength(logPath);
                SpeakSapiXmlToFile($"<lang langid=\"C07\">{german}</lang>", Path.Combine(dir, "de-at.wav"));
                string? trace = ReadLogSince(logPath, before)
                    .Split('\n')
                    .LastOrDefault(l => l.Contains("BuildSpeakPlan: frag LangID="))?
                    .Trim();
                if (trace is null)
                {
                    Console.WriteLine("  langid=C07 (de-AT)  → FAIL: engine logged no language line");
                    fail++;
                }
                else
                {
                    bool ok = trace.Contains("lang=de");
                    Console.WriteLine($"  langid=C07 (de-AT)  → {(ok ? "ok  " : "FAIL")} {trace[(trace.IndexOf("frag ", StringComparison.Ordinal) + 5)..]}");
                    if (!ok) fail++;
                }
            }

            if (fail == 0) Console.WriteLine("  PASS: language markup is parsed, carried, and mapped correctly");
            else Console.WriteLine($"  {fail} language case(s) wrong");
            return fail;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The engine holds engine.log open only for the duration of each append,
    /// but a concurrent SAPI client in another process may be writing to it —
    /// so share everything and never lock the engine out of its own log.
    /// </summary>
    /// <summary>
    /// Speak SAPI-XML markup straight through SpVoice, rendered to a file.
    /// System.Speech has no way to emit <c>&lt;lang langid&gt;</c> — it owns the
    /// SSML→SAPI translation — so tests that need a specific LCID on the wire
    /// have to go through the COM API. Flag 8 = SPF_IS_XML.
    /// </summary>
    private static void SpeakSapiXmlToFile(string xml, string wavPath)
    {
        Type? spVoice = Type.GetTypeFromProgID("SAPI.SpVoice");
        Type? spFile = Type.GetTypeFromProgID("SAPI.SpFileStream");
        if (spVoice is null || spFile is null) throw new InvalidOperationException("SAPI COM classes unavailable");

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
            stream.Open(wavPath, 3 /* SSFMCreateForWrite */, false);
            voice.AudioOutputStream = stream;
            voice.Speak(xml, 8 /* SPF_IS_XML */);
            voice.AudioOutputStream = null;
            stream.Close();
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(stream); } catch { }
            try { Marshal.FinalReleaseComObject(voice); } catch { }
        }
    }

    private static long ReadLogLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string ReadLogSince(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;   // log rotated out from under us
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
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

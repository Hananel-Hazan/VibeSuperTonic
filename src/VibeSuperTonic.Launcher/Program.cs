using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using VibeSuperTonic.Launcher.Bench;
using VibeSuperTonic.Launcher.Integrity;
using VibeSuperTonic.Launcher.Ui;

namespace VibeSuperTonic.Launcher;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        bool isCli = args.Length > 0;
        if (isCli) AttachConsole(ATTACH_PARENT_PROCESS); // best-effort; works only if launched from a console

        // Last-chance crash capture. These catch MANAGED exceptions that escape
        // the UI message loop or background threads and write a stack trace to
        // launcher.log. Note: native/COM access violations (the prime suspect
        // for the SAPI export crash) are corrupted-state exceptions that the
        // CLR will not deliver here on modern .NET — for those, the per-step
        // DiagLog breadcrumbs in the export path are what pinpoint the failure.
        DiagLog.Write($"=== Launcher start (pid {Environment.ProcessId}, args=[{string.Join(' ', args)}]) ===");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) DiagLog.WriteException("AppDomain.UnhandledException", ex);
            else DiagLog.Write($"AppDomain.UnhandledException (non-CLR): {e.ExceptionObject}");
        };
        if (!isCli)
        {
            // Route UI-thread exceptions to our handler instead of the default
            // WinForms dialog, so they're logged before anything else.
            Application.ThreadException += (_, e) => DiagLog.WriteException("Application.ThreadException", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }

        // Argument validation FIRST, and --version/--help before anything that can
        // fail. Until 2026-08-20 "has arguments" meant "is CLI" and any unrecognised
        // flag fell through to Application.Run — so `--version` opened a window, and
        // so did a typo. That is the exact input that took vst-ctl down with SIGABRT
        // on Linux, answered there in one sentence and exit 2; answering it with a
        // GUI is the same defect wearing better clothes. A diagnostic flag must also
        // not depend on the state it exists to diagnose, which is why these two run
        // ahead of the migration below rather than beside --config.
        if (isCli)
        {
            if (TryFindUnknownArgument(args, out var bad))
            {
                Console.Error.WriteLine($"VibeSuperTonic: unrecognised argument '{bad}'");
                Console.Error.WriteLine();
                Console.Error.Write(UsageText());
                return 2;
            }

            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "-?"))
            {
                Console.Write(UsageText());
                return 0;
            }

            if (HasFlag(args, "--version"))
            {
                Console.WriteLine(VersionInfo.Own());
                return 0;
            }
        }

        // One-shot schema migration — prunes stale per-voice overrides + flips GPU on
        // for installs from before that became the default. Safe to call every launch
        // (it no-ops once SchemaVersion is current).
        try { EngineSettingsRegistry.EnsureMigrated(); } catch { /* registry quirks are non-fatal */ }

        // If a previous run died holding temporary settings — a Benchmark sweep or
        // an Export that took the process down before its finally could run — the
        // user's global quality is still pinned to whatever that run applied, in
        // every SAPI client on the machine, with nothing on screen to say so. Put
        // it back before anything reads settings.
        try
        {
            if (EngineSettingsRegistry.TryReconcilePendingRestore(out var detail))
                DiagLog.Write($"Recovered engine settings from an interrupted run ({detail}).");
        }
        catch (Exception ex) { DiagLog.Write($"Settings recovery failed: {ex.Message}"); }

        try
        {
            // ---- CLI flag routing first
            if (HasFlag(args, "--unregister"))
                return Registration.Unregister(Console.WriteLine, elevatedChild: HasFlag(args, "--elevated"));

            if (HasFlag(args, "--register"))
                return Registration.Register(Console.WriteLine, elevatedChild: HasFlag(args, "--elevated"));

            if (HasFlag(args, "--repair"))
                return RunRepairAsync().GetAwaiter().GetResult();

            if (HasFlag(args, "--bench"))
                return RunBenchAsync(args).GetAwaiter().GetResult();

            if (HasFlag(args, "--sweep"))
                return RunSweepAsync(args).GetAwaiter().GetResult();

            if (HasFlag(args, "--config"))
            {
                Console.Write(InferenceReport.Build().ToPlainText());
                return 0;
            }

            if (TryGetSet(args, out var key, out var value))
            {
                if (!EngineSettingsRegistry.TrySetSingle(key!, value!, out var err))
                {
                    Console.Error.WriteLine($"--set failed: {err}");
                    return 1;
                }
                Console.WriteLine($"set {key}={value}");
                return 0;
            }

            // ---- GUI default
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Flags that stand alone.</summary>
    private static readonly string[] KnownFlags =
    {
        "--register", "--unregister", "--elevated", "--repair",
        "--bench", "--sweep", "--no-gpu", "--force",
        "--config", "--version", "--help", "-h", "-?",
    };

    /// <summary>Flags that consume the token after them.</summary>
    private static readonly string[] ValueFlags = { "--set", "--words", "--voice", "--text" };

    /// <summary>
    /// The first argument this build does not understand, if there is one.
    ///
    /// <para>Bare words are rejected alongside unknown switches: nothing here
    /// takes a positional argument, so <c>VibeSuperTonic.exe config</c> is a typo
    /// for <c>--config</c> rather than a request, and opening the GUI in response
    /// is how the mistake went unnoticed.</para>
    /// </summary>
    private static bool TryFindUnknownArgument(string[] args, out string? unknown)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            if (ValueFlags.Contains(a, StringComparer.OrdinalIgnoreCase))
            {
                i++;   // its value is whatever follows, and is not ours to judge
                continue;
            }

            if (KnownFlags.Contains(a, StringComparer.OrdinalIgnoreCase)) continue;

            unknown = a;
            return true;
        }
        unknown = null;
        return false;
    }

    private static string UsageText() =>
        $"""
        VibeSuperTonic {VersionInfo.Own()} — SAPI5 neural voices

        Run with no arguments to open the Control Panel.

          --version              Print the version and exit.
          --config               Print what inference will use, and why.
          --help, -h, -?         This text.

          --register             Register the voices for this user.
          --unregister           Remove the registration.
          --repair               Run every Status check and fix what it can,
                                 including measuring this machine on first run.

          --sweep                Measure this machine's thread and provider curve
                                 and store the winner in data\benchmark.json.
              --voice <id>       Voice to render the sample with (default M1).
              --no-gpu           Skip the DirectML row.
              --force            Measure even if the machine is busy. The result
                                 is shaped by whatever else is running.

          --bench                Time the quality presets and print JSON.
              --voice <id>       Voice to use (default M1).
              --words <n>        Words of the sample to read (default 500).
              --text <string>    Text to read instead of the bundled sample.

          --set <key>=<value>    Change one setting, e.g. --set onnxthreads=6

        Exit codes: 0 success, 1 failure, 2 bad arguments (and, for --sweep, a
        machine too busy to measure).

        """;

    private static bool TryGetSet(string[] args, out string? key, out string? value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--set", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) break;
            var pair = args[i + 1];
            int eq = pair.IndexOf('=');
            if (eq <= 0) break;
            key = pair[..eq];
            value = pair[(eq + 1)..];
            return true;
        }
        key = value = null;
        return false;
    }

    private static async Task<int> RunRepairAsync()
    {
        var checks = Checks.RunAll();
        int unfixed = 0;
        var progress = new Progress<string>(Console.WriteLine);
        foreach (var c in checks.Where(c => !c.Ok))
        {
            Console.WriteLine($"--- {c.Title}: {c.Detail}");
            if (c.Repair is null) { unfixed++; continue; }
            // Installing a machine-wide runtime is not something a --repair run
            // should do unasked; print the command and let the operator decide.
            if (c.NeedsConsent)
            {
                Console.WriteLine($"    skipped (needs confirmation). {c.FixHint}");
                unfixed++;
                continue;
            }
            try { if (!await c.Repair(progress, CancellationToken.None)) unfixed++; }
            catch (Exception ex) { Console.Error.WriteLine($"  exception: {ex.Message}"); unfixed++; }
        }
        Console.WriteLine(unfixed == 0 ? "All checks passed." : $"{unfixed} unfixable issue(s).");
        return unfixed;
    }

    private static async Task<int> RunBenchAsync(string[] args)
    {
        int wordCap = TryGetIntArg(args, "--words", 500);
        string voiceId = TryGetStringArg(args, "--voice", "M1") ?? "M1";
        string text = TryGetStringArg(args, "--text", null) ?? LoadSampleText();
        var presets = new[] { QualityPreset.Draft, QualityPreset.Balanced, QualityPreset.Quality, QualityPreset.HiFi };

        var log = new Progress<string>(Console.Error.WriteLine);
        var results = new List<BenchmarkResult>();
        using (EngineSettingsRegistry.BeginTemporaryChange(log))
        {
            foreach (var p in presets)
                results.Add(await Benchmark.RunAsync(p, voiceId, text, wordCap, log, CancellationToken.None));
        }

        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// The thread and provider sweep, headless.
    ///
    /// <para>The same code the Benchmark tab runs, reached without a window, for
    /// the same reason Linux made <c>vst-ctl benchmark</c> the primary surface and
    /// the button a caller of it: a measurement that only exists inside a GUI
    /// cannot be run over a remote session, scripted across a fleet, or asked for
    /// in a field report.</para>
    ///
    /// <para>Exit code is 0 when a profile was written, 1 when nothing could be
    /// measured, and 2 when the machine was too busy and <c>--force</c> was not
    /// given — a refusal is not a failure, and a script should be able to tell
    /// them apart.</para>
    /// </summary>
    private static async Task<int> RunSweepAsync(string[] args)
    {
        string voiceId = TryGetStringArg(args, "--voice", "M1") ?? "M1";
        bool includeGpu = !HasFlag(args, "--no-gpu");
        bool force = HasFlag(args, "--force");

        var log = new Progress<string>(Console.Error.WriteLine);
        var rows = new Progress<Core.Synthesis.BenchmarkProgress>(p =>
            Console.Error.WriteLine(p.Row.Failed
                ? $"  [{p.Index}/{p.Total}] {p.Row.Label,-10} failed: {p.Row.Error}"
                : $"  [{p.Index}/{p.Total}] {p.Row.Label,-10} {p.Row.MedianWallMs,8:F0} ms  " +
                  $"rtf {p.Row.Rtf:F3}  cores {p.Row.AvgCores:F1}  spread {p.Row.Spread:P0}"));

        // Same restore contract the tab has: the sweep writes a thread count into
        // settings.json for every SAPI client on the machine while it runs, and a
        // run that dies without putting them back leaves the user synthesizing at
        // whatever the dead run applied, silently, until they next open the app.
        using var restore = EngineSettingsRegistry.BeginTemporaryChange(log);

        var outcome = await ThreadSweep.RunAsync(voiceId, includeGpu, force, log, rows, CancellationToken.None);

        if (outcome.Refusal is not null)
        {
            Console.Error.WriteLine(outcome.Refusal);
            return 2;
        }

        foreach (var note in outcome.Notes) Console.Error.WriteLine(note);

        if (outcome.Profile is null) return 1;

        Console.WriteLine(JsonSerializer.Serialize(outcome.Profile,
            Core.Synthesis.BenchmarkJsonContext.Default.BenchmarkProfile));
        return 0;
    }

    private static int TryGetIntArg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var v))
                return v;
        return fallback;
    }

    private static string? TryGetStringArg(string[] args, string name, string? fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }

    private static string LoadSampleText()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "samples", "twenty-thousand-leagues.txt");
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        catch { }
        return "The deep sea, sir, is unknown to us. Either I will know, or I will not exist.";
    }
}

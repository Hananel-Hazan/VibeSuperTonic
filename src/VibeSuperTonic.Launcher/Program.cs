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

        // One-shot schema migration — prunes stale per-voice overrides + flips GPU on
        // for installs from before that became the default. Safe to call every launch
        // (it no-ops once SchemaVersion is current).
        try { EngineSettingsRegistry.EnsureMigrated(); } catch { /* registry quirks are non-fatal */ }

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

        var saved = EngineSettingsRegistry.Load();
        var results = new List<BenchmarkResult>();
        var log = new Progress<string>(Console.Error.WriteLine);
        foreach (var p in presets)
            results.Add(await Benchmark.RunAsync(p, $"VibeSuperTonic_{voiceId}", text, wordCap, log, CancellationToken.None));
        EngineSettingsRegistry.Save(saved);

        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeSuperTonic.Launcher.Host;

/// <summary>
/// The Control Panel's only sanctioned way to make the neural engine speak.
///
/// VibeSuperTonic.exe is published self-contained single-file, and activating
/// the framework-dependent COM engine inside that host access-violates inside
/// ONNX Runtime — 0xc0000005 with the managed stack ending in
/// <c>InferenceSession.RunImpl</c>. It is not a settings problem and it does not
/// depend on CPU vs DirectML. Ordinary SAPI hosts (Balabolka, NVDA, Lingoes) are
/// framework-dependent processes and synthesize fine, so every path in here that
/// needs audio delegates to <c>render\VibeSuperTonic.RenderHost.exe</c>, which
/// deliberately mirrors that host shape.
///
/// The Export tab has done this since 0.2.x. Tune's "Test voice" and the
/// Benchmark tab did not, and both took the whole Control Panel down the moment
/// the engine reached inference — with the extra sting that the crash skipped
/// their settings-restore <c>finally</c>, leaving the user's global quality
/// pinned to whatever the dead run had applied. If you add a fourth caller,
/// route it through here rather than calling
/// <c>Activator.CreateInstance("SAPI.SpVoice")</c> in-process.
/// </summary>
internal static class RenderHostProcess
{
    public static string ExePath =>
        Path.Combine(AppContext.BaseDirectory, "render", "VibeSuperTonic.RenderHost.exe");

    public static void EnsureAvailable()
    {
        if (!File.Exists(ExePath))
            throw new FileNotFoundException(
                $"Render helper not found at {ExePath}. Re-extract the release ZIP — the 'render' folder ships next to VibeSuperTonic.exe.");
    }

    /// <summary>
    /// Text goes to the helper via a UTF-8 temp file: chapters and whole books
    /// blow past command-line length limits, and the round trip through a
    /// console argument mangles Unicode and newlines.
    /// </summary>
    public static string WriteTextFile(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vst_rendertext_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Speaks to the default audio device (Tune tab's "Test voice").</summary>
    public static void Speak(string voiceId, string text, IProgress<string>? log, CancellationToken ct)
        => RunWithText(new[] { "--mode", "speak", "--voice", voiceId }, text, log, ct);

    /// <summary>
    /// Runs the helper with <paramref name="text"/> written to a temp file that is
    /// deleted afterwards. <paramref name="args"/> must NOT contain <c>--text</c>.
    /// </summary>
    public static void RunWithText(
        IReadOnlyList<string> args,
        string text,
        IProgress<string>? log,
        CancellationToken ct,
        IProgress<int>? progress = null,
        Action<string>? onLine = null)
    {
        string textFile = WriteTextFile(text);
        try
        {
            var full = new List<string>(args) { "--text", textFile };
            Run(full, log, ct, progress, onLine);
        }
        finally { try { File.Delete(textFile); } catch { } }
    }

    /// <summary>
    /// Spawns the helper, streams its stdout into <paramref name="log"/> (parsing
    /// "PROGRESS n" lines into <paramref name="progress"/> and handing every line
    /// to <paramref name="onLine"/>), honors <paramref name="ct"/> by killing the
    /// child, and throws on a non-zero exit with stderr as the detail.
    /// </summary>
    public static void Run(
        IReadOnlyList<string> args,
        IProgress<string>? log,
        CancellationToken ct,
        IProgress<int>? progress = null,
        Action<string>? onLine = null)
    {
        EnsureAvailable();

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
            // Without these the child's UTF-8 output is decoded with the console
            // code page, which silently eats every non-ASCII character — "…" and
            // "→" vanished from the helper's own progress lines, so the log pane
            // read "opening file stream  C:\…" as if a path were missing.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        var notesSeen = new HashSet<string>(StringComparer.Ordinal);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            string line = Clean(e.Data);
            if (line.Length == 0) return;
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(9), out int pct))
            {
                progress?.Report(pct);
                return;
            }
            onLine?.Invoke(line);
            log?.Report(line);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            string line = Clean(e.Data);
            if (line.Length == 0) return;

            // ONNX Runtime writes its startup diagnostics to stderr, and most are
            // warnings about operator placement that fire on every session build —
            // eight lines per run, unchanged, harmless. Prefixing those with
            // "render error" is how a benchmark that completed perfectly came to
            // look like a failure. Severity comes from ORT's own tag; only E is an
            // error, and only real errors are kept for the exit-code message so a
            // genuine failure isn't buried under boilerplate.
            var severity = OrtDiagnostic.Match(line);
            if (severity.Success && severity.Groups[1].Value != "E")
            {
                string note = Undated(line);
                if (notesSeen.Add(note)) log?.Report("engine note: " + note);
                return;
            }

            stderr.AppendLine(line);
            log?.Report("render error: " + line);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        while (!proc.WaitForExit(150))
        {
            if (!ct.IsCancellationRequested) continue;
            log?.Report("Cancel requested — terminating render helper.");
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new OperationCanceledException(ct);
        }
        proc.WaitForExit(); // flush async readers

        if (proc.ExitCode != 0)
        {
            string detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"Render helper failed (exit {proc.ExitCode}){(detail.Length > 0 ? ": " + detail : ".")}");
        }
    }

    /// <summary>
    /// ONNX Runtime colours its console output. The escape sequences survive the
    /// pipe and render as literal "[0;93m" rubbish in a WinForms text box.
    ///
    /// Anchored on the ESC byte rather than on "[": a bare-bracket pattern also
    /// matches the "[W" opening ORT's severity tag, which would strip the very
    /// marker <see cref="OrtDiagnostic"/> reads to tell a warning from an error.
    /// </summary>
    private static readonly Regex AnsiEscape = new("\u001b\\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>Matches ORT's own severity tag, e.g. "[W:onnxruntime:, session_state.cc:1280 …]".</summary>
    private static readonly Regex OrtDiagnostic = new(@"\[([WEIV]):onnxruntime", RegexOptions.Compiled);

    private static string Clean(string line) => AnsiEscape.Replace(line, string.Empty).TrimEnd();

    /// <summary>
    /// Drops the leading timestamp so repeats of the same ORT warning collapse to
    /// one line instead of eight.
    /// </summary>
    private static string Undated(string line)
    {
        int bracket = line.IndexOf('[');
        return bracket > 0 ? line[bracket..] : line;
    }
}

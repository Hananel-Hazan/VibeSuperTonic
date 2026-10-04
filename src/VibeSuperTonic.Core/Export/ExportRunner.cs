using System.Diagnostics;
using VibeSuperTonic.Core.Ipc;

namespace VibeSuperTonic.Core.Export;

public enum ExportStatus
{
    /// <summary>The file exists, complete, at the requested path.</summary>
    Done,

    /// <summary>The caller asked to stop. Nothing was left behind.</summary>
    Cancelled,

    /// <summary>Nothing was left behind; <see cref="ExportOutcome.Message"/> says why.</summary>
    Failed,
}

public sealed record ExportOutcome(ExportStatus Status, string Message, long Samples = 0);

/// <summary>
/// Turns a <see cref="RequestVerb.Render"/> reply stream into a finished file
/// of the requested format. Shared by the window's Export tab and
/// <c>vst-ctl render --out file</c>.
///
/// <para><b>The one rule: the destination is either the complete file or it is
/// untouched.</b> Everything is written to a hidden sibling temp file (same
/// directory, so the final step is a rename on one filesystem) and only a
/// successful, uncancelled, non-empty result is renamed over the target. A
/// cancel, a daemon that died mid-sentence, a full disk and an encoder that
/// crashed all end the same way — the temp file is deleted and an existing file
/// at the destination is still there, unharmed. Partial audio that exits "fine"
/// is the failure this exists to prevent: an exported chapter that stops at
/// minute nine looks exactly like a good one until somebody listens.</para>
///
/// <para>For MP3, AAC and FLAC the render's WAV stream is piped to the system
/// ffmpeg, which writes the temp file. ffmpeg exiting non-zero is a failure even
/// when every byte was accepted.</para>
/// </summary>
public static class ExportRunner
{
    /// <summary>How long ffmpeg gets to finish after the last sample.</summary>
    private static readonly TimeSpan EncodeGrace = TimeSpan.FromMinutes(2);

    public static ExportOutcome Run(
        Func<string?> readLine,
        ExportFormat format,
        string outPath,
        FfmpegTools? ffmpeg,
        Func<bool>? cancelled = null,
        Action<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(readLine);

        string full;
        try { full = Path.GetFullPath(outPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail($"not a usable file name: {outPath}");
        }

        string? dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return Fail($"the folder {dir} does not exist");
        if (Directory.Exists(full)) return Fail($"{full} is a folder");

        string? encoder = null;
        if (ExportFormats.NeedsFfmpeg(format))
        {
            encoder = ffmpeg?.EncoderFor(format);
            if (ffmpeg is null || encoder is null)
                return Fail(FfmpegTools.Unavailable(format, ffmpeg, FfmpegDetector.IsSandboxed())
                            ?? "no encoder for that format");
        }

        // Hidden, unique, beside the target. Unique so two exports to one folder
        // cannot share it; beside the target so File.Move is a rename(2).
        // The name is bounded so a 255-byte target name cannot make its own temp name too long.
        string temp = Path.Combine(dir, $".part-{Guid.NewGuid():N}-{Truncate(Path.GetFileName(full), 100)}");

        bool promoted = false;
        try
        {
            var error = new StringWriter();
            ExportOutcome outcome = encoder is null
                ? WriteWav(readLine, temp, error, cancelled, progress)
                : Encode(readLine, ffmpeg!, format, encoder, temp, error, cancelled, progress);

            if (outcome.Status != ExportStatus.Done) return outcome;

            File.Move(temp, full, overwrite: true);
            promoted = true;
            return outcome;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"cannot write {full}: {ex.Message}");
        }
        finally
        {
            if (!promoted) TryDelete(temp);
        }
    }

    // -------------------------------------------------------------------- WAV

    private static ExportOutcome WriteWav(
        Func<string?> readLine, string temp, StringWriter error,
        Func<bool>? cancelled, Action<long>? progress)
    {
        using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var guarded = new FaultRecordingStream(file);

        long samples = 0;
        int code = RenderWav.Read(readLine, guarded, error, cancelled, n => { samples = n; progress?.Invoke(n); });

        return Judge(code, guarded, samples, error, cancelled, () =>
        {
            guarded.Flush();
            RenderWav.PatchSizes(file);
        });
    }

    // ------------------------------------------------------------------ ffmpeg

    private static ExportOutcome Encode(
        Func<string?> readLine, FfmpegTools ffmpeg, ExportFormat format, string encoder, string temp,
        StringWriter error, Func<bool>? cancelled, Action<long>? progress)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in FfmpegTools.BuildArgs(format, encoder, temp)) psi.ArgumentList.Add(arg);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Fail($"could not start ffmpeg ({ffmpeg.Path}): {ex.Message}");
        }
        if (process is null) return Fail($"could not start ffmpeg ({ffmpeg.Path})");

        using (process)
        {
            // Both read to the end, always: a full stderr pipe stalls ffmpeg, and
            // a stalled ffmpeg stalls our write to its stdin.
            var ffmpegErrors = new List<string>();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } line)
                    lock (ffmpegErrors) { if (ffmpegErrors.Count < 20) ffmpegErrors.Add(line); }
            };
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var guarded = new FaultRecordingStream(process.StandardInput.BaseStream);
            long samples = 0;
            int code;
            try
            {
                code = RenderWav.Read(readLine, guarded, error, cancelled, n => { samples = n; progress?.Invoke(n); });
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { /* ffmpeg went first */ }
            }

            bool stop = cancelled?.Invoke() == true;
            if (stop || code != 0 || guarded.Fault is not null)
            {
                TryKill(process);
            }
            else
            {
                var waited = Stopwatch.StartNew();
                while (!process.WaitForExit(200))
                {
                    if (cancelled?.Invoke() == true || waited.Elapsed > EncodeGrace)
                    {
                        TryKill(process);
                        if (cancelled?.Invoke() != true) return Fail("ffmpeg did not finish encoding");
                        break;
                    }
                }
            }

            process.WaitForExit();                      // also drains the async readers

            if (code == 0 && guarded.Fault is null && cancelled?.Invoke() != true && process.ExitCode != 0)
            {
                string why;
                lock (ffmpegErrors) why = ffmpegErrors.Count > 0 ? ffmpegErrors[^1] : $"exit code {process.ExitCode}";
                return Fail($"ffmpeg failed: {why}");
            }

            return Judge(code, guarded, samples, error, cancelled, () =>
            {
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new IOException("ffmpeg produced an empty file");
            });
        }
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// The verdict, in one place for both paths. RenderWav returns 0 for "the
    /// reader went away" because for a speech module that is a stop; here it is
    /// a truncated file, so a recorded write fault overrides a zero. An empty
    /// render also returns 0 from it — a file with no audio is not an export.
    /// </summary>
    private static ExportOutcome Judge(
        int code, FaultRecordingStream sink, long samples, StringWriter error,
        Func<bool>? cancelled, Action finish)
    {
        if (cancelled?.Invoke() == true)
            return new ExportOutcome(ExportStatus.Cancelled, "cancelled; nothing was written");

        if (sink.Fault is { } fault) return Fail($"writing the audio failed: {fault.Message}");
        if (code != 0) return Fail(FirstLine(error.ToString(), "the render failed"));
        if (samples == 0) return Fail("there was nothing to say (the text was empty)");

        try { finish(); }
        catch (IOException ex) { return Fail(ex.Message); }

        return new ExportOutcome(ExportStatus.Done, "done", samples);
    }

    private static ExportOutcome Fail(string message) => new(ExportStatus.Failed, message);

    private static string FirstLine(string text, string fallback)
    {
        foreach (string line in text.Split('\n'))
            if (line.Trim() is { Length: > 0 } trimmed) return trimmed;
        return fallback;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Remembers the first IOException a write or flush threw, then rethrows it.</summary>
    private sealed class FaultRecordingStream(Stream inner) : Stream
    {
        public IOException? Fault { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => Guard(() => inner.Write(buffer, offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { try { inner.Write(buffer); } catch (IOException ex) { Fault ??= ex; throw; } }
        public override void Flush() => Guard(inner.Flush);

        private void Guard(Action action)
        {
            try { action(); }
            catch (IOException ex) { Fault ??= ex; throw; }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

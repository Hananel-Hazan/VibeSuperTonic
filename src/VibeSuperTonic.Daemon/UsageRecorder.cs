using System.Text.Json;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// Accumulates render timings and writes <c>usage-stats.json</c>.
///
/// <para><b>Why the daemon writes this and not a script.</b> Nothing outside the
/// process can see how long a render took: the IPC event stream carries state
/// changes and word boundaries, never durations, and by the time audio reaches
/// the device the synthesis cost has been hidden by buffering. So the one place
/// that can honestly answer "how fast is this provider for this person" is the
/// method that calls the provider — see
/// <see cref="ProviderSwitchingSynthesizer.Synthesize"/>.</para>
///
/// <para><b>Batched, because renders are frequent.</b> One render is one chunk,
/// so a page of text is a dozen of them and writing the file each time would put
/// a dozen fsync-shaped operations inside the press-to-speech budget. Flushed
/// after <see cref="FlushEvery"/> samples or <see cref="FlushAfter"/>, whichever
/// comes first, so a person who reads one sentence and walks away still has
/// their sample on disk a minute later.</para>
///
/// <para><b>Never throws.</b> A read-only data directory is a supported install
/// (see <see cref="DaemonLog"/>), and losing a measurement is not a reason to
/// lose an utterance.</para>
/// </summary>
internal sealed class UsageRecorder
{
    private const int FlushEvery = 5;
    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private UsageStats? _stats;
    private int _pending;
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private bool _warned;

    public UsageRecorder(string path, Action<string> log)
    {
        _path = path;
        _log = log;
    }

    /// <summary>
    /// Record one render. <paramref name="rtf"/> is wall time over the duration
    /// of the audio produced.
    /// </summary>
    public void Add(string provider, int threads, double rtf)
    {
        // A render that produced no audio, or a clock that went backwards, is not
        // a measurement. Left out rather than clamped: a zero would drag a median
        // toward "infinitely fast" and is exactly the kind of number that makes a
        // routing decision confidently wrong.
        if (rtf <= 0 || double.IsNaN(rtf) || double.IsInfinity(rtf)) return;

        lock (_gate)
        {
            _stats ??= Load();

            string now = DateTime.UtcNow.ToString("O");
            _stats.For(provider).Add(rtf, threads, now);
            _stats.UpdatedUtc = now;
            _pending++;

            if (_pending >= FlushEvery || DateTime.UtcNow - _lastFlushUtc >= FlushAfter)
                FlushLocked();
        }
    }

    /// <summary>Write whatever has not been written. Called on a clean shutdown.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_pending > 0) FlushLocked();
        }
    }

    private UsageStats Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UsageStats();

            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(fs, UsageStatsJsonContext.Default.UsageStats)
                   ?? new UsageStats();
        }
        catch
        {
            // A malformed file is start-again, not stop. These are measurements
            // of a machine, reproducible by using it.
            return new UsageStats();
        }
    }

    private void FlushLocked()
    {
        if (_stats is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write-and-rename: vst-autotune.sh reads this file on a timer and
            // must never see a half-written one, which a plain truncate-and-write
            // would hand it on any sufficiently long list of samples.
            string tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(fs, _stats, UsageStatsJsonContext.Default.UsageStats);

            File.Move(tmp, _path, overwrite: true);

            _pending = 0;
            _lastFlushUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Once. A read-only install would otherwise say this on every render
            // for the life of the process.
            if (!_warned)
            {
                _warned = true;
                _log($"usage: cannot write {_path} ({ex.GetType().Name}: {ex.Message}); " +
                     "provider timings will not be kept");
            }

            _pending = 0;
            _lastFlushUtc = DateTime.UtcNow;
        }
    }
}

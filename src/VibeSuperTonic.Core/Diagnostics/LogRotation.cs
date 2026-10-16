namespace VibeSuperTonic.Core.Diagnostics;

/// <summary>
/// Size cap for the append-only diagnostic logs. Without this they grow for the
/// lifetime of the install — a screen-reader user speaks all day, every day, and
/// the engine traces every utterance; field logs reach multiple MB in a few
/// months and the interesting lines are the ones you can't scroll to.
///
/// Deliberately a single rolled generation (<c>engine.log</c> +
/// <c>engine.log.1</c>) rather than a numbered series: the value of these logs
/// is "what happened just before the thing I'm reporting", which never needs
/// more than the current file plus its predecessor. Two files also means the
/// bound is obvious — at most 2x the cap on disk, forever.
public static class LogRotation
{
    public const long DefaultMaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Roll <paramref name="path"/> aside if it has outgrown the cap. Call
    /// while holding whatever lock guards the append, and before appending.
    ///
    /// Every failure mode here is survivable and ignored on purpose. The engine
    /// loads into EVERY SAPI host on the machine — Balabolka, NVDA, Word and
    /// the Control Panel can all be appending to one engine.log at once — so a
    /// rename can lose a race against another process's open handle. The loser
    /// just keeps appending to a slightly oversized log until someone wins.
    /// A logger must never turn a diagnostic into the user's actual problem.
    /// </summary>
    public static void RollIfNeeded(string path, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < maxBytes) return;

            string rolled = path + ".1";
            try { File.Delete(rolled); } catch { /* previous generation held open */ }
            File.Move(path, rolled);
        }
        catch { /* see summary — logging failures stay invisible */ }
    }
}

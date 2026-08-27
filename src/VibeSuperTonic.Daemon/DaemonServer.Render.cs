using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Text;

namespace VibeSuperTonic.Daemon;

/// <summary>
/// <see cref="RequestVerb.Render"/> — synthesise, and hand the samples back
/// instead of playing them.
///
/// <para><b>Why the daemon must not play this.</b> The first customer is a
/// Speech Dispatcher module, and speech-dispatcher has to be able to stop an
/// utterance the instant the next keystroke arrives — which it can only do to a
/// process it owns. If this daemon played, <c>spd-say -C</c> would kill a pipe
/// that is not the daemon and the speech would carry on. Orca interrupts itself
/// on every keypress, so "carries on" means every keystroke's audio queued
/// behind the last. docs/SPEECHD-PLAN.md, trap 3.</para>
///
/// <para><b>It is not speech and must not disturb any.</b> No sink, no playback
/// clock, no tray state, no <c>ToggleGate</c>. A render while the user is
/// listening to something must leave that something alone — the only thing it
/// shares with the press path is the engine, and that sharing is the one
/// genuinely awkward part (see the concurrency note below).</para>
/// </summary>
public sealed partial class DaemonServer
{
    /// <summary>
    /// Chunks are bounded so one utterance is not one allocation.
    ///
    /// <para><c>StreamReader.ReadLineAsync</c> has no length cap in either
    /// direction, so "one line per utterance" would be unbounded memory chosen
    /// by whoever is sending the text. 32k samples is 0.74 s at 44.1 kHz and
    /// 1.5 s at 22.05 kHz — small enough that a reader starts hearing audio
    /// promptly, large enough that the base64 and JSON overheads are noise.</para>
    /// </summary>
    private const int RenderChunkSamples = 32768;

    /// <summary>
    /// Render <paramref name="request"/>'s text, one reply per chunk.
    ///
    /// <para>Owns the writer for the duration, like <c>BenchmarkAsync</c> and
    /// <c>VoiceInstallAsync</c>. The contract a client reads against: replies
    /// arrive until one has <see cref="AudioChunk.Final"/> set, the first
    /// carries the format and no samples, and a failure arrives as an ordinary
    /// <see cref="Response.Ok"/> <c>false</c> at any point.</para>
    /// </summary>
    private async Task RenderAsync(StreamWriter writer, Request request, CancellationToken token)
    {
        RefreshConfig(force: false);

        string text = request.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            // Not an error. A screen reader sends whatever the focused widget
            // held, and an empty label is a real thing that happens dozens of
            // times a session. It renders to no audio and says so.
            await WriteAsync(writer, new Response
            {
                Ok = true,
                Audio = new AudioChunk(0, 1, null, Final: true),
            });
            return;
        }

        if (_engines is not { } engines)
        {
            await WriteAsync(writer, Response.Fail("this daemon has no engine"));
            return;
        }

        // ROUTE FIRST, AND REFUSE ON ERROR RATHER THAN RENDERING ANYWAY. Select
        // is what discovers "there are no models yet", and since 2026-08-27 it
        // reports that instead of throwing.
        var selection = engines.Select(request.Voice);
        if (selection.Error is { } routingError)
        {
            await WriteAsync(writer, Response.Fail(routingError));
            return;
        }

        // A render must not swap the engine out from under an utterance that is
        // playing. NeedsIdle means exactly that, and the honest answer is to
        // refuse: speechd will ask again, and a screen reader asking again is
        // cheaper than a user's audiobook changing voice mid-sentence.
        if (selection.NeedsIdle)
        {
            await WriteAsync(writer, Response.Fail(
                $"busy ({_session.State}) — the engine cannot change while something is playing"));
            return;
        }

        var plan = _config.Utterance(request.Voice, request.Language);
        int rate = engines.SampleRate;

        // The format, before any audio. A caller writing a WAV header needs the
        // rate and cannot wait for the first samples to guess it.
        await WriteAsync(writer, new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, null, Final: false),
        });

        var chunks = SentenceChunker.Chunk(text,
            _config.SessionOptions.MaxChunkChars,
            _config.SessionOptions.MinChunkChars);

        try
        {
            foreach (string chunk in chunks)
            {
                token.ThrowIfCancellationRequested();

                // Off the socket thread: Synthesize is a blocking inference and
                // this connection is one of a handful the daemon serves.
                short[] pcm = await Task.Run(
                    () => engines.Synthesize(chunk, plan.Synthesis, token), token);

                for (int at = 0; at < pcm.Length; at += RenderChunkSamples)
                {
                    int count = Math.Min(RenderChunkSamples, pcm.Length - at);
                    var bytes = new byte[count * 2];
                    Buffer.BlockCopy(pcm, at * 2, bytes, 0, bytes.Length);

                    await WriteAsync(writer, new Response
                    {
                        Ok = true,
                        Audio = new AudioChunk(rate, 1, Convert.ToBase64String(bytes), Final: false),
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away — for speechd, that IS the stop. Nothing to
            // report to a socket nobody is reading.
            return;
        }
        catch (Exception ex)
        {
            string why = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}";
            Log($"render: failed after {chunks.Count} chunk(s): {why}");
            try { await WriteAsync(writer, Response.Fail($"render failed — {why}")); }
            catch (IOException) { /* the client went away first */ }
            return;
        }

        await WriteAsync(writer, new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, null, Final: true),
        });
    }
}

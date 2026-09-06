using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Ipc;
using VibeSuperTonic.Core.Session;
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

        // TRAP 11, AND IT ARRIVES WHETHER ANYONE ASKED FOR IT. speechd clients
        // can set SSML mode, and the gate probe measured what that means here: a
        // plain `spd-say "hello"` reaches a module as <speak>hello</speak>. Fed
        // to the model, "speak" is a word the user hears. Stripped here rather
        // than in the module so that every future caller of this verb gets it,
        // and narrowly — see Ssml, which leaves text that is not a document
        // alone so that reading source code aloud still reads the brackets.
        //
        // PUNCTUATION VERBOSITY IS DECIDED BY OMISSION, DELIBERATELY. speechd's
        // four levels mean "say the punctuation out loud" and this product has
        // no concept of it. All four map to nothing, INSTALL.txt says so (S4),
        // and that is a better answer than a half-implementation the user has to
        // discover the shape of.
        string text = Ssml.Strip(request.Text ?? "");
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

        // The whole admission policy, decided rather than emergent — trap 12 and
        // trap 14. It lives in Core because it is otherwise reachable only
        // through a fully built DaemonServer, which is to say not testable at
        // all; see RenderAdmission for why the press wins and what that costs.
        if (RenderAdmission.Refuse(_session.State, selection.NeedsIdle, selection.Error) is { } refusal)
        {
            await WriteAsync(writer, Response.Fail(refusal));
            return;
        }

        // THE SCREEN READER'S RATE, and this verb is the only one that takes one
        // — see Request.Rate. It multiplies whatever settings.json asks for, so a
        // client that sends nothing is unchanged.
        var plan = _config.Utterance(
            request.Voice, request.Language,
            request.Rate is { } speechdRate ? SpeechRate.SpeechdRateScale(speechdRate) : 1.0);
        int rate = engines.SampleRate;

        // The format, before any audio. A caller writing a WAV header needs the
        // rate and cannot wait for the first samples to guess it.
        await WriteAsync(writer, new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, null, Final: false),
        });

        // The rendering voice's own chunk sizes, so `render` and the hotkey
        // break text the same way for the same voice — the Speech Dispatcher
        // module goes through this path, and a chunk size that differed from the
        // one the settings scope asks for would change where it pauses.
        var renderOptions = _config.SessionOptionsFor(request.Voice);
        var chunks = SentenceChunker.Chunk(text,
            renderOptions.MaxChunkChars,
            renderOptions.MinChunkChars);

        try
        {
            foreach (string chunk in chunks)
            {
                token.ThrowIfCancellationRequested();

                // Off the socket thread: Synthesize is a blocking inference and
                // this connection is one of a handful the daemon serves.
                short[] pcm = await Task.Run(
                    () => engines.Synthesize(chunk, plan.Synthesis, token), token);

                // THE REST OF THE PLAN, WHICH THIS VERB USED TO THROW AWAY.
                // Reported 2026-09-06: `render` computed an utterance plan and
                // then streamed the raw model output, so the DSP half of the rate
                // and the user's volume trim reached the hotkey and never reached
                // a screen reader. The session applies both per chunk; so does
                // this, in the same order, because the two paths speak the same
                // settings and must not disagree about what they mean.
                //
                // For Piper the stretch is 1.0 until the model saturates, so
                // dropping it was inaudible at ordinary speeds and total past the
                // wall — which is exactly where a screen reader user lives.
                if (Math.Abs(plan.StretchFactor - 1.0) > 0.001)
                    pcm = TimeStretch.Stretch(pcm, plan.StretchFactor, rate);

                SpeechRate.ApplyGain(pcm, renderOptions.VolumeScale);

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
            // The daemon is shutting down. Nothing to report to a socket that is
            // about to close.
            return;
        }
        catch (IOException)
        {
            // THE CLIENT WENT AWAY MID-RENDER, WHICH IS A STOP AND NOT A FAULT.
            // Under route B speechd terminates the module's child process on
            // STOP, and Orca sends STOP on very nearly every keystroke — so this
            // is the single most common way a render ends. It reached the
            // catch-all below until 2026-08-28 and logged "render: failed" every
            // time, which would have made the daemon log unreadable on the one
            // machine where reading it matters most.
            //
            // Returning here also stops the synthesis: the loop is per chunk, so
            // the utterance the user cancelled does not carry on being computed.
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

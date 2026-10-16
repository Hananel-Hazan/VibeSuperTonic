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
    /// A cap on silence across one render, in milliseconds. A guard: nothing a
    /// real document asks for comes near it, and without it a client can name
    /// any number of breaks of any length.
    /// </summary>
    internal const int MaxRenderSilenceMs = 60_000;

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
        // to the model, "speak" is a word the user hears.
        //
        // SINCE 2026-10-03 THE DOCUMENT IS PARSED RATHER THAN ONLY STRIPPED, and
        // that is what brings Linux level with Windows, where SAPI hands the
        // engine the same pieces already parsed: xml:lang switches the language
        // for the text it encloses, <prosody rate> adjusts the pace of its own
        // fragment, <break> is silence and <mark> a bookmark. See SsmlDocument.
        // Anything it cannot parse comes back as "not a document" and is
        // STRIPPED exactly as before — never an error to the client — and text
        // that is not SSML at all reaches this unchanged (Ssml leaves it alone,
        // so reading source code aloud still reads the brackets).
        //
        // PUNCTUATION VERBOSITY IS DECIDED BY OMISSION, DELIBERATELY. speechd's
        // four levels mean "say the punctuation out loud" and this product has
        // no concept of it. All four map to nothing, INSTALL.txt says so (S4),
        // and that is a better answer than a half-implementation the user has to
        // discover the shape of.
        string raw = request.Text ?? "";
        IReadOnlyList<SsmlFragment> fragments = SsmlDocument.TryParse(raw, out var parsedFragments)
            ? parsedFragments
            : new[] { SsmlFragment.Speak(Ssml.Strip(raw), null, 0) };

        // The whole text path, once per text fragment: the same
        // SynthTextPipeline the hotkey and the Windows engine run, so
        // pronunciation rules, hyphen joins and the invisible-character strip
        // apply to a screen reader as they do everywhere else.
        var renderOptions = _config.SessionOptionsFor(request.Voice);
        var steps = PlanRender(_config, fragments, renderOptions, request);
        int chunkCount = steps.Count(x => x.Text is not null);

        if (chunkCount == 0)
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
        //
        // AND HOLD THE ENGINE IT CHOSE UNTIL THE LAST CHUNK. This loop used to
        // route once and then synthesise every chunk through the router, which
        // answers with whatever is selected NOW — and a render does not keep the
        // session busy, so a hotkey press or another render could switch engines
        // halfway through. The rest of the sentence then reached Supertonic with
        // PiperOptions (or the reverse), Require threw, and the screen reader
        // was cut off mid-word. Reported 2026-10-04; see Select(id, out lease)
        // for why this pins instead of making renders count as busy.
        var selection = engines.Select(request.Voice, out var lease);
        using var engine = lease;

        // The whole admission policy, decided rather than emergent — trap 12 and
        // trap 14. It lives in Core because it is otherwise reachable only
        // through a fully built DaemonServer, which is to say not testable at
        // all; see RenderAdmission for why the press wins and what that costs.
        if (RenderAdmission.Refuse(_session.State, selection.NeedsIdle, selection.Error) is { } refusal)
        {
            await WriteAsync(writer, Response.Fail(refusal));
            return;
        }

        // Unreachable — a selection with no error and no NeedsIdle always pins —
        // but a null here would be a NullReferenceException on the socket thread.
        if (engine is null)
        {
            await WriteAsync(writer, Response.Fail("the engine could not be held for this render"));
            return;
        }

        int rate = engine.SampleRate;

        // The format, before any audio. A caller writing a WAV header needs the
        // rate and cannot wait for the first samples to guess it.
        await WriteAsync(writer, new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, null, Final: false),
        });

        // THE SCREEN READER'S VOLUME, beside its rate. Applied after the
        // configured trim, in the same place, so the two compose as a product.
        float gain = renderOptions.VolumeScale
                     * (request.Volume is { } speechdVolume ? SpeechRate.SpeechdVolumeScale(speechdVolume) : 1f);

        bool wantMarks = request.Marks == true;
        var pendingMarks = new List<RenderMark>();
        long emitted = 0;                              // samples written so far, silence included
        int silenceBudgetMs = MaxRenderSilenceMs;

        // Streams samples in bounded replies. A bookmark waiting rides on the
        // FIRST reply written after it was reached, so its position and the audio
        // that follows it arrive together.
        async Task WriteSamplesAsync(short[] pcm)
        {
            for (int at = 0; at < pcm.Length; at += RenderChunkSamples)
            {
                int count = Math.Min(RenderChunkSamples, pcm.Length - at);
                var bytes = new byte[count * 2];
                Buffer.BlockCopy(pcm, at * 2, bytes, 0, bytes.Length);

                IReadOnlyList<RenderMark>? marks = null;
                if (pendingMarks.Count > 0)
                {
                    marks = pendingMarks.ToArray();
                    pendingMarks.Clear();
                }

                await WriteAsync(writer, new Response
                {
                    Ok = true,
                    Audio = new AudioChunk(rate, 1, Convert.ToBase64String(bytes), Final: false, marks),
                });

                emitted += count;
            }
        }

        try
        {
            foreach (var step in steps)
            {
                token.ThrowIfCancellationRequested();

                if (step.Mark is { } markName)
                {
                    if (wantMarks) pendingMarks.Add(new RenderMark(markName, emitted));
                    continue;
                }

                if (step.SilenceMs > 0)
                {
                    int ms = Math.Min(step.SilenceMs, silenceBudgetMs);
                    silenceBudgetMs -= ms;
                    long samples = (long)rate * ms / 1000;
                    if (samples > 0) await WriteSamplesAsync(new short[samples]);
                    continue;
                }

                // Off the socket thread: Synthesize is a blocking inference and
                // this connection is one of a handful the daemon serves.
                string chunk = step.Text!;
                var plan = step.Plan;
                short[] pcm = await Task.Run(
                    () => engine.Synthesize(chunk, plan.Synthesis, token), token);

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

                SpeechRate.ApplyGain(pcm, gain);

                await WriteSamplesAsync(pcm);
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
            Log($"render: failed after {chunkCount} chunk(s): {why}");
            try { await WriteAsync(writer, Response.Fail($"render failed — {why}")); }
            catch (IOException) { /* the client went away first */ }
            return;
        }

        // A bookmark after the last word is still a bookmark. It rides on the
        // final reply, which has no samples of its own.
        await WriteAsync(writer, new Response
        {
            Ok = true,
            Audio = new AudioChunk(rate, 1, null, Final: true,
                pendingMarks.Count > 0 ? pendingMarks.ToArray() : null),
        });
    }

    /// <summary>One thing for the render loop to do: speak a chunk, pause, or note a mark.</summary>
    internal readonly record struct RenderStep(
        string? Text, HostConfig.UtterancePlan Plan, int SilenceMs, string? Mark);

    /// <summary>
    /// Turn parsed fragments into steps, running each text fragment through
    /// <see cref="SynthTextPipeline"/> and the chunker, and resolving the plan —
    /// language and pace — that fragment is spoken with.
    ///
    /// <para>Separated from <see cref="RenderAsync"/>, which is reachable only
    /// through a socket, so that what a fragment turns into is testable.</para>
    /// </summary>
    internal static List<RenderStep> PlanRender(
        HostConfig config,
        IReadOnlyList<SsmlFragment> fragments, SpeechSessionOptions options, Request request)
    {
        var steps = new List<RenderStep>();

        // The speechd rate multiplies whatever the settings ask for, and a
        // fragment's own <prosody rate> adjusts that further — the engine's rule
        // on Windows, where SAPI's RateAdj is added to the site rate and applied
        // as 1.5^(n/10), reproduced here as a factor on the same scale.
        double speechdScale = request.Rate is { } r ? SpeechRate.SpeechdRateScale(r) : 1.0;

        foreach (var fragment in fragments)
        {
            switch (fragment.Kind)
            {
                case SsmlFragmentKind.Silence:
                    steps.Add(new RenderStep(null, default, fragment.SilenceMs, null));
                    break;

                case SsmlFragmentKind.Mark:
                    steps.Add(new RenderStep(null, default, 0, fragment.Mark));
                    break;

                default:
                {
                    string spoken = SynthTextPipeline.Prepare(
                        fragment.Text, options.Pronunciations, options.CompiledPronunciations, out _);
                    if (string.IsNullOrWhiteSpace(spoken)) break;

                    // A fragment's language beats the request's, which beats the
                    // configured one — null falls through each, as on Windows.
                    var plan = config.Utterance(
                        request.Voice,
                        fragment.Lang ?? request.Language,
                        speechdScale * SpeechRate.RateAdjScale(fragment.RateAdj));

                    foreach (string chunk in SentenceChunker.Chunk(
                                 spoken, options.MaxChunkChars, options.MinChunkChars))
                    {
                        if (!string.IsNullOrWhiteSpace(chunk))
                            steps.Add(new RenderStep(chunk, plan, 0, null));
                    }
                    break;
                }
            }
        }

        return steps;
    }
}

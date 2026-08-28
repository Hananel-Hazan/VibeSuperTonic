namespace VibeSuperTonic.Core.Ipc;

/// <summary>
/// Turns a <see cref="RequestVerb.Render"/> reply stream into a WAV.
///
/// <para><b>Why this is in Core and not in vst-ctl, where it runs.</b> It is the
/// half of the Speech Dispatcher feature that decides whether a screen reader
/// hears anything, and every rule it enforces fails silently: a truncated render
/// that exits 0 is a sentence the user never hears and nothing logs. In
/// <c>Program.cs</c> as a static local function it could only be tested by
/// starting a daemon and a client; here it is a loop over a line source, and the
/// suite can hand it a truncated stream, an error reply, a chunk of unreadable
/// base64 and a render that produces no samples. docs/TESTING-PLAN.md, "where a
/// check belongs": it is about logic and needs no model, network or device.</para>
///
/// <para><b>The one rule everything here serves: never report success having
/// produced no audio.</b> That is trap 14, it is the same failure a missing
/// espeak dictionary has, and here the person on the other end of it is
/// navigating by ear.</para>
/// </summary>
public static class RenderWav
{
    /// <summary>A canonical PCM WAV header is 44 bytes.</summary>
    public const int HeaderBytes = 44;

    /// <summary>
    /// Read replies until one is final, writing audio to <paramref name="output"/>.
    /// Returns the process exit code the caller should use.
    /// </summary>
    /// <param name="readLine">One reply line, or null when the peer went away.</param>
    /// <param name="output">Where the WAV goes. Not seeked — see <see cref="WriteHeader"/>.</param>
    /// <param name="error">Diagnostics. Speech Dispatcher logs a module's stderr,
    /// so this is the only place a failure becomes visible to anyone.</param>
    /// <param name="stopped">
    /// True when the caller has been asked to stop — a SIGTERM from speechd, which
    /// under route B is what a <c>STOP</c> command becomes. <b>A stop is not a
    /// failure</b>: Orca sends one on nearly every keystroke, and reporting each as
    /// an error would fill the user's log with the sound of the product working.
    /// </param>
    public static int Read(
        Func<string?> readLine,
        Stream output,
        TextWriter error,
        Func<bool>? stopped = null)
    {
        bool wroteHeader = false;
        int headerRate = 0;
        long samples = 0;

        while (true)
        {
            if (stopped?.Invoke() == true) return StopHere(output, wroteHeader);

            string? line = readLine();
            if (line is null)
            {
                if (stopped?.Invoke() == true) return StopHere(output, wroteHeader);

                // A TRUNCATED RENDER IS A FAILURE EVEN IF AUDIO WAS ALREADY
                // WRITTEN. Half an utterance is the shape of a daemon that died
                // mid-sentence, and a screen reader that hears half a sentence
                // and no error has no way to know it was cut off.
                error.WriteLine("the daemon closed the connection mid-render");
                return 1;
            }

            var reply = Protocol.TryDecode<Response>(line);
            if (reply is null)
            {
                error.WriteLine($"unparseable reply: {line}");
                return 1;
            }

            if (!reply.Ok)
            {
                error.WriteLine(reply.Error ?? "render failed");
                return 1;
            }

            if (reply.Audio is not { } audio)
            {
                error.WriteLine("a render reply carried no audio field");
                return 1;
            }

            if (!wroteHeader)
            {
                if (audio.SampleRate <= 0)
                {
                    // The empty-text case: the daemon rendered nothing and said
                    // so. Not an error, and not a WAV either — a zero-byte
                    // stdout is what a player handles best, and speechd's
                    // server-side audio treats it as an utterance with no
                    // samples rather than as a broken one.
                    return 0;
                }

                // GUARDED FOR THE SAME REASON THE SAMPLE WRITES ARE, and found
                // by the test rather than by reasoning: if the destination has
                // already gone — speechd closed the pipe between spawning us and
                // the daemon's first reply, which is what a STOP arriving that
                // fast looks like — an unguarded write here throws out of this
                // loop and the module dies with a stack trace instead of exiting
                // quietly. The first write is the likeliest one to meet a dead
                // pipe, not the least.
                try
                {
                    WriteHeader(output, audio.SampleRate, audio.Channels);
                }
                catch (IOException)
                {
                    return 0;
                }

                wroteHeader = true;
                headerRate = audio.SampleRate;
            }
            else if (audio.SampleRate != headerRate)
            {
                // THE HEADER IS ALREADY OUT AND CANNOT BE REWRITTEN. AudioChunk
                // carries the rate on every chunk precisely so this is
                // detectable: the sink follows the voice, so an engine that
                // changed mid-stream would hand us samples at a rate the header
                // has already promised. The daemon refuses that switch; if one
                // arrives anyway, saying so beats playing a chipmunk.
                error.WriteLine(
                    $"the render changed sample rate mid-stream ({headerRate} -> {audio.SampleRate})");
                return 1;
            }

            if (audio.Pcm is { Length: > 0 } encoded)
            {
                byte[] pcm;
                try { pcm = Convert.FromBase64String(encoded); }
                catch (FormatException)
                {
                    error.WriteLine("a render reply carried unreadable audio");
                    return 1;
                }

                try
                {
                    output.Write(pcm, 0, pcm.Length);
                    samples += pcm.Length / 2;
                }
                catch (IOException)
                {
                    // The reader went away. For a speechd module this IS the
                    // stop, and it is how most utterances a screen reader starts
                    // actually end.
                    return 0;
                }
            }

            if (audio.Final)
            {
                try { output.Flush(); }
                catch (IOException) { return 0; }

                if (samples == 0)
                {
                    error.WriteLine("the render produced no audio");
                    return 1;
                }

                return 0;
            }
        }
    }

    /// <summary>
    /// A stop, mid-stream. Flush what was written and succeed: the audio so far
    /// is real, and the utterance was cancelled rather than lost.
    /// </summary>
    private static int StopHere(Stream output, bool wroteHeader)
    {
        if (wroteHeader)
        {
            try { output.Flush(); } catch (IOException) { /* the peer went first */ }
        }

        return 0;
    }

    /// <summary>
    /// A 44-byte canonical WAV header for 16-bit PCM.
    ///
    /// <para><b>Both size fields are 0xFFFFFFFF, on purpose.</b> This streams to
    /// a pipe, which cannot be seeked, so the sizes cannot be filled in at the
    /// end — and writing a real length would mean buffering the whole utterance,
    /// which is the latency the render verb exists to avoid. Every player this
    /// meets already handles it, because it is how a WAV arriving over a pipe has
    /// always looked. A real file gets the same header for one reason: a file and
    /// a pipe must not differ in a way that only appears when someone switches
    /// between them while debugging.</para>
    /// </summary>
    public static void WriteHeader(Stream output, int sampleRate, int channels)
    {
        const int BitsPerSample = 16;
        int blockAlign = channels * BitsPerSample / 8;
        int byteRate = sampleRate * blockAlign;

        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header[..4]);
        BitConverter.TryWriteBytes(header[4..8], uint.MaxValue);          // RIFF size: unknown
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BitConverter.TryWriteBytes(header[16..20], 16);                   // fmt chunk size
        BitConverter.TryWriteBytes(header[20..22], (short)1);             // PCM
        BitConverter.TryWriteBytes(header[22..24], (short)channels);
        BitConverter.TryWriteBytes(header[24..28], sampleRate);
        BitConverter.TryWriteBytes(header[28..32], byteRate);
        BitConverter.TryWriteBytes(header[32..34], (short)blockAlign);
        BitConverter.TryWriteBytes(header[34..36], (short)BitsPerSample);
        "data"u8.CopyTo(header[36..40]);
        BitConverter.TryWriteBytes(header[40..44], uint.MaxValue);        // data size: unknown

        output.Write(header);
    }
}

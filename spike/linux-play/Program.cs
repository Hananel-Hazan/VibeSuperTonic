using System.Diagnostics;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Synthesis;
using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Linux.Audio;
using VibeSuperTonic.Onnx.Ort;

// Phase 2 end to end. See LinuxPlay.csproj for what the two modes are for.
//
//   vst-linux-play --calibrate [seconds]     clock accuracy, no model needed
//   vst-linux-play <models-dir> [text-file]  render and speak, printing words as heard
//   vst-linux-play <models-dir> --stop-at <ms>   render, speak, stop mid-sentence

const int Rate = 44100;

// One write per this many ms. Also the clock's poll interval, because
// pa_simple_write blocks until the device has room — so the write loop is
// already paced by playback and a separate 50 Hz timer would only add jitter and
// a second thread's worth of races. The daemon in Phase 3 should keep this
// shape.
const int PollMs = 20;

if (args.Length > 0 && args[0] == "--calibrate")
{
    int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 30;
    return Calibrate(seconds);
}

return Speak(args);

// ------------------------------------------------------------------ calibration

// Plays a click every second and reports how far the playback clock's idea of
// elapsed audio diverges from real time.
//
// The comparison is against the wall clock rather than against a microphone,
// which bounds what this can prove: it catches a latency reading that is
// systematically wrong or that drifts, and it cannot catch a fixed offset
// between what libpulse reports and when sound actually leaves the speakers.
// A fixed offset is the benign case — it moves every highlight by the same
// amount, and it is small compared to the 80 ms the plan allows. Drift is the
// one that would make the readout unusable by the end of a paragraph, and drift
// is exactly what this measures.
//
// The clicks are audible on purpose. The printed line for click N should land
// on the click you hear; if the console runs visibly ahead of or behind the
// sound, that is the defect, and no residual in milliseconds argues with it.
static int Calibrate(int seconds)
{
    Console.WriteLine($"calibrate   : {seconds} s click track, {Rate} Hz mono");

    var pcm = new short[Rate * seconds];
    var clicks = new List<long>();
    for (int sec = 0; sec < seconds; sec++)
    {
        long at = (long)sec * Rate;
        clicks.Add(at);
        // 8 ms of 1 kHz, enveloped so it clicks rather than pops.
        int len = Rate * 8 / 1000;
        for (int i = 0; i < len && at + i < pcm.Length; i++)
        {
            double env = 1.0 - (double)i / len;
            pcm[at + i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / Rate) * 12000 * env);
        }
    }

    var scheduler = new BoundaryScheduler();
    for (int i = 0; i < clicks.Count; i++)
        scheduler.Add(new BoundaryEvent(clicks[i], i, 1, BoundaryKind.Word));

    var clock = new PlaybackClock(Rate);
    using var sink = new PulseAudioSink(Rate, "VibeSuperTonic calibrate");

    var wall = Stopwatch.StartNew();
    double firstSoundWall = -1;
    double worstResidualMs = 0;
    int primedAtMs = -1;

    int block = Rate * PollMs / 1000;
    for (int offset = 0; offset < pcm.Length; offset += block)
    {
        int count = Math.Min(block, pcm.Length - offset);

        long before = sink.WrittenFrames;
        sink.Write(pcm.AsSpan(offset, count));
        clock.AddWritten(sink.WrittenFrames - before);

        if (!clock.Update(sink.LatencyUsec)) continue;

        if (primedAtMs < 0)
        {
            primedAtMs = (int)wall.ElapsedMilliseconds;
            // Wall-clock instant that frame 0 reached the speakers, inferred
            // once from the first good reading. Everything after is measured
            // against it.
            firstSoundWall = wall.Elapsed.TotalSeconds - (double)clock.PlayedFrames / Rate;
        }

        double clockSec = (double)clock.PlayedFrames / Rate;
        double wallSec = wall.Elapsed.TotalSeconds - firstSoundWall;
        double residualMs = (clockSec - wallSec) * 1000.0;
        if (Math.Abs(residualMs) > Math.Abs(worstResidualMs)) worstResidualMs = residualMs;

        foreach (var e in scheduler.Advance(clock.PlayedFrames))
        {
            Console.WriteLine(
                $"  click {e.SourceOffset,2}   clock {clockSec,6:F3} s   " +
                $"wall {wallSec,6:F3} s   residual {(clockSec - wallSec) * 1000.0,+7:F1} ms");
        }
    }

    sink.Drain();
    clock.Drained();
    wall.Stop();

    double audioSec = (double)pcm.Length / Rate;
    double elapsedSec = wall.Elapsed.TotalSeconds - firstSoundWall;

    Console.WriteLine();
    Console.WriteLine($"primed at   : {primedAtMs} ms");
    Console.WriteLine($"worst resid.: {worstResidualMs:F1} ms  " +
                      $"{(Math.Abs(worstResidualMs) <= 80 ? "OK (<= 80 ms gate)" : "ABOVE GATE")}");
    Console.WriteLine($"drain       : {elapsedSec:F3} s wall for {audioSec:F3} s audio " +
                      $"({(elapsedSec - audioSec) * 1000:F0} ms)");
    Console.WriteLine($"unfired     : {scheduler.PendingCount} click(s)");

    return Math.Abs(worstResidualMs) <= 80 && scheduler.PendingCount == 0 ? 0 : 1;
}

// ------------------------------------------------------------------- real speech

static int Speak(string[] args)
{
    string modelsRoot = args.Length > 0
        ? args[0]
        : Path.Combine(AppContext.BaseDirectory, "models");

    int stopAtMs = -1;
    string? textFile = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--stop-at" && i + 1 < args.Length && int.TryParse(args[i + 1], out int ms))
        {
            stopAtMs = ms;
            i++;
        }
        else textFile = args[i];
    }

    const string DefaultText =
        "The sea is everything. It covers seven tenths of the terrestrial globe. " +
        "Its breath is pure and healthy.  It is an immense desert, where man is never lonely,\n\n" +
        "for he feels life stirring on all sides. Two spaces and a paragraph break are in " +
        "this text on purpose: they are what the offsets used to drift on.";

    string text = textFile is not null ? File.ReadAllText(textFile) : DefaultText;

    if (!Directory.Exists(Path.Combine(modelsRoot, "onnx")))
    {
        Console.Error.WriteLine($"FAIL  no onnx/ under {modelsRoot}");
        return 2;
    }

    // Same composition the Windows engine uses: prepare once, chunk, then chain
    // each chunk's map onto the pipeline's. Getting this wrong in either order
    // is R-2 and R-14 respectively, so the spike does it the one right way
    // rather than the shortest way.
    string spoken = SynthTextPipeline.Prepare(text, null, null, out var pipelineMap);
    var chunks = SentenceChunker.ChunkWithOffsets(spoken);
    Console.WriteLine($"chunks      : {chunks.Count}");

    using ISynthesizer synth = new OrtSynthesizer(modelsRoot);
    var loadWatch = Stopwatch.StartNew();
    int rate = synth.SampleRate;
    loadWatch.Stop();
    Console.WriteLine($"load        : {loadWatch.Elapsed.TotalSeconds:F2} s, {rate} Hz");

    var options = new SupertonicOptions("M1", "en");
    var scheduler = new BoundaryScheduler();
    var clock = new PlaybackClock(rate);
    using var sink = new PulseAudioSink(rate, "VibeSuperTonic");

    // Render ahead of playback on another thread. One chunk of lookahead is
    // enough to keep the device fed at RTF ~0.21 and keeps the spike honest
    // about the thing that actually matters here — that boundary events are
    // queued while earlier audio is playing, not all up front.
    var rendered = new System.Collections.Concurrent.BlockingCollection<(int index, short[] pcm)>(1);
    var renderTask = Task.Run(() =>
    {
        try
        {
            for (int i = 0; i < chunks.Count; i++)
                rendered.Add((i, synth.Synthesize(chunks[i].Text, options)));
        }
        finally { rendered.CompleteAdding(); }
    });

    var wall = Stopwatch.StartNew();
    long streamFrame = 0;
    int block = rate * PollMs / 1000;
    double firstSoundWall = -1;
    bool stopped = false;

    var planned = new List<BoundaryEvent>();
    foreach (var (index, pcm) in rendered.GetConsumingEnumerable())
    {
        planned.Clear();
        BoundaryPlanner.PlanChunk(
            planned, chunks[index].Text,
            TextOffsetMap.Chain(chunks[index].Map, pipelineMap),
            sourceBase: 0, chunkFrames: pcm.Length, streamStartFrame: streamFrame);
        scheduler.AddRange(planned);
        streamFrame += pcm.Length;

        for (int offset = 0; offset < pcm.Length && !stopped; offset += block)
        {
            int count = Math.Min(block, pcm.Length - offset);

            long before = sink.WrittenFrames;
            try
            {
                sink.Write(pcm.AsSpan(offset, count));
            }
            catch (OperationCanceledException)
            {
                stopped = true;
                break;
            }
            clock.AddWritten(sink.WrittenFrames - before);

            if (!clock.Update(sink.LatencyUsec)) continue;
            if (firstSoundWall < 0)
                firstSoundWall = wall.Elapsed.TotalSeconds - (double)clock.PlayedFrames / rate;

            // A batch means the clock jumped: the priming fast-forward, or a
            // late poll. Print the whole batch here because this is a readout,
            // but a highlight would paint only the last — see BoundaryScheduler.
            foreach (var e in scheduler.Advance(clock.PlayedFrames))
            {
                if (e.Kind != BoundaryKind.Word) continue;
                string word = text.Substring(e.SourceOffset, e.SourceLength);
                Console.WriteLine($"  {wall.Elapsed.TotalSeconds - firstSoundWall,6:F2} s  {word}");
            }

            if (stopAtMs > 0 && wall.ElapsedMilliseconds >= stopAtMs)
            {
                // What Phase 3's stop will do, minus cancelling the renderer.
                // Timed from the ask so the number is comparable with the
                // 100 ms budget the plan sets for stop.
                var stopWatch = Stopwatch.StartNew();
                sink.RequestFlush();
                sink.Flush();
                clock.Reset();
                scheduler.Reset();
                stopWatch.Stop();
                Console.WriteLine($"  STOP        flushed in {stopWatch.Elapsed.TotalMilliseconds:F1} ms");
                stopped = true;
            }
        }

        if (stopped) break;
    }

    if (!stopped)
    {
        sink.Drain();
        clock.Drained();
        foreach (var e in scheduler.Advance(clock.PlayedFrames))
        {
            if (e.Kind != BoundaryKind.Word) continue;
            Console.WriteLine($"  {wall.Elapsed.TotalSeconds - firstSoundWall,6:F2} s  " +
                              $"{text.Substring(e.SourceOffset, e.SourceLength)}   (drain)");
        }
    }

    try { renderTask.Wait(TimeSpan.FromSeconds(30)); } catch { /* spike */ }
    wall.Stop();

    Console.WriteLine();
    Console.WriteLine($"written     : {streamFrame} frames " +
                      $"({(double)streamFrame / rate:F2} s audio)");
    Console.WriteLine($"unfired     : {scheduler.PendingCount} boundary event(s)");
    Console.WriteLine(stopped ? "stopped early" : "completed");

    return 0;
}

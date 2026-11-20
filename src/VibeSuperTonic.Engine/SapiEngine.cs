using System.Runtime.InteropServices;
using System.Text;
using VibeSuperTonic.Core.Text;
using VibeSuperTonic.Engine.Interop;
using VibeSuperTonic.Engine.Settings;
using VibeSuperTonic.Engine.Synth;
using VibeSuperTonic.Engine.Telemetry;
using VibeSuperTonic.Core.Audio;
using VibeSuperTonic.Core.Diagnostics;
using VibeSuperTonic.Core.Synthesis;

namespace VibeSuperTonic.Engine;

[ComVisible(true)]
[Guid(EngineClsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SapiEngine : ISpTTSEngine, ISpObjectWithToken
{
    public const string EngineClsid = "F2A8C7B1-1234-5678-9ABC-DEF012345678";

    // Must match Supertonic's actual output sample rate (read from tts.json: 44100 for supertonic-3).
    // Mismatch makes SAPI play audio at the wrong speed AND wrong pitch.
    private const int SampleRate = 44100;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BlockAlign = Channels * BitsPerSample / 8;
    private const int BytesPerSecond = SampleRate * BlockAlign;
    private const int ChunkBytes = 4096;

    // Per-chunk synth wait budget: "the driver is wedged, not slow." Past this we
    // force a session rebuild and surface E_FAIL instead of holding the COM
    // thread. Stop stays responsive throughout — the wait polls SPVES_ABORT.
    //
    // It has to outlast everything the adapter's own recovery may legitimately
    // spend on one chunk: a GPU call its watchdog gives up on, a CPU session
    // reload, and the CPU retry under the same watchdog. At 45 s it did not
    // (30 + up to 20 for the reload): the ceiling fired while a working CPU
    // retry was still running, threw it away, and its reset re-enabled the GPU
    // the watchdog had just latched off (review item 34).
    private const int SynthHardTimeoutMs =
        2 * SupertonicAdapter.SynthCallWatchdogMs + SupertonicAdapter.CpuReloadBudgetMs + 5_000;
    // Grace window after we cancel speakCts so cancellation can propagate
    // through the totalStep loop in _Infer. One iteration is ~200 ms at
    // totalStep=8; give it ~3× that before we give up on graceful unwind.
    private const int SynthCancelGraceMs = 750;

    // Test hook: a nonzero value replaces SynthHardTimeoutMs for the next waits,
    // so the harness can drive the hang path in seconds rather than 85 (Step 12,
    // item 34). Zero in production; set only by reflection.
    internal static int _testSynthHardTimeoutMs;

    // Ultimate-fallback voice id when neither the SAPI token nor the Control Panel
    // setting provides one. SetObjectToken normally beats this; the Control Panel's
    // Default\DefaultVoice setting is the second-tier fallback.
    private const string DefaultVoiceId = "M1";

    private static readonly Dictionary<string, SupertonicAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _adaptersLock = new();

    private object? _token;
    private string _voiceId = ResolveInitialVoiceId();

    private static string ResolveInitialVoiceId()
    {
        try
        {
            var s = EngineSettingsCache.Resolve();
            return string.IsNullOrEmpty(s.DefaultVoice) ? DefaultVoiceId : s.DefaultVoice;
        }
        catch { return DefaultVoiceId; }
    }

    static SapiEngine()
    {
        Trace("static-ctor", "engine assembly loaded — kicking off background ONNX preload");
        // The SDK's progress lines ("DirectML provider appended", "Loaded N voice
        // styles") used to go to Console.Out — the HOST's stdout, which a console
        // SAPI client may be streaming audio to (review item 67). They go to
        // engine.log instead, and to stdout only inside a benchmark render,
        // where the launcher's sweep reads the provider line from the helper.
        // Set before the preload below, which is what prints them.
        Supertonic.Helper.Diagnostic = msg =>
        {
            Trace("sdk", msg);
            if (BenchSwitches.IsAuthorised(
                    Environment.GetEnvironmentVariable(BenchSwitches.UnpacedVariable), Environment.ProcessId))
                Console.WriteLine(msg);
        };
        // Eager pre-warm. The DLL is loaded by SAPI well before any Speak; trigger model
        // load right now on a background thread so the first synthesis doesn't pay the
        // 2-5s ONNX load cost on top of its own 3-7s synth time.
        try { _ = SupertonicAdapter.PreloadSharedAsync(); }
        catch (Exception ex) { Log("static-ctor.Preload", ex); }
    }
    public SapiEngine() { Trace("ctor", "instance created"); }

    public int SetObjectToken(object pToken)
    {
        Trace("SetObjectToken", $"pToken={pToken?.GetType().FullName ?? "null"}");
        try
        {
            _token = pToken;
            _voiceId = TryReadVoiceIdFromToken(pToken) ?? ResolveInitialVoiceId();
            Trace("SetObjectToken", $"voiceId={_voiceId}");
            // Kick off ONNX model + voice style preload on a background thread so that the
            // first Speak doesn't pay the cold-start cost (~2-5s for the 380MB ONNX models).
            // Most SAPI clients call SetObjectToken when the user picks a voice from the UI
            // — well before the first Speak — giving us a free head start.
            try { _ = GetAdapter(_voiceId).PreloadAsync(); }
            catch (Exception ex) { Log("SetObjectToken.Preload", ex); /* non-fatal */ }
            return SapiConstants.S_OK;
        }
        catch (Exception ex) { Log("SetObjectToken", ex); return SapiConstants.E_FAIL; }
    }

    private static unsafe string? TryReadVoiceIdFromToken(object? token)
    {
        if (token is null) return null;
        IntPtr unk = IntPtr.Zero;
        IntPtr tokenPtr = IntPtr.Zero;
        IntPtr attrsKey = IntPtr.Zero;
        IntPtr resultStr = IntPtr.Zero;
        try
        {
            unk = Marshal.GetIUnknownForObject(token);
            // ISpObjectToken IID
            Guid iidSpObjectToken = new("14056589-E16C-11D2-BB90-00C04F8EE6C0");
            if (Marshal.QueryInterface(unk, iidSpObjectToken, out tokenPtr) != 0) return null;

            // Vtable layout (ISpObjectToken : ISpDataKey):
            //   IUnknown = 0..2
            //   ISpDataKey: SetData=3, GetData=4, SetStringValue=5, GetStringValue=6,
            //               SetDWORD=7, GetDWORD=8, OpenKey=9, ...
            void** vtbl = *(void***)tokenPtr;
            var openKey = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)vtbl[9];

            fixed (char* pAttrs = "Attributes")
            {
                IntPtr key;
                if (openKey(tokenPtr, pAttrs, &key) != 0) return null;
                attrsKey = key;
            }

            void** akVtbl = *(void***)attrsKey;
            var getStringValue = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)akVtbl[6];
            fixed (char* pName = "SupertonicVoiceId")
            {
                IntPtr str;
                if (getStringValue(attrsKey, pName, &str) != 0) return null;
                resultStr = str;
            }

            string? voiceId = Marshal.PtrToStringUni(resultStr);
            return string.IsNullOrWhiteSpace(voiceId) ? null : voiceId;
        }
        catch (Exception ex) { Log("TryReadVoiceIdFromToken", ex); return null; }
        finally
        {
            if (resultStr != IntPtr.Zero) Marshal.FreeCoTaskMem(resultStr);
            if (attrsKey != IntPtr.Zero) Marshal.Release(attrsKey);
            if (tokenPtr != IntPtr.Zero) Marshal.Release(tokenPtr);
            if (unk != IntPtr.Zero) Marshal.Release(unk);
        }
    }

    public int GetObjectToken(out object ppToken)
    {
        ppToken = _token!;
        return _token is null ? SapiConstants.E_FAIL : SapiConstants.S_OK;
    }

    public int GetOutputFormat(
        IntPtr pTargetFmtId,
        IntPtr pTargetWaveFormatEx,
        out Guid pDesiredFormatId,
        out IntPtr ppCoMemDesiredWaveFormatEx)
    {
        pDesiredFormatId = SapiConstants.SPDFID_WaveFormatEx;
        ppCoMemDesiredWaveFormatEx = IntPtr.Zero;
        try
        {
            var wfx = new WAVEFORMATEX
            {
                wFormatTag = SapiConstants.WAVE_FORMAT_PCM,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = BlockAlign,
                nAvgBytesPerSec = BytesPerSecond,
                cbSize = 0,
            };
            int size = Marshal.SizeOf<WAVEFORMATEX>();
            IntPtr mem = Marshal.AllocCoTaskMem(size);
            Marshal.StructureToPtr(wfx, mem, fDeleteOld: false);
            ppCoMemDesiredWaveFormatEx = mem;
            return SapiConstants.S_OK;
        }
        catch (Exception ex) { Log("GetOutputFormat", ex); return SapiConstants.E_FAIL; }
    }

    public unsafe int Speak(
        uint dwSpeakFlags,
        ref Guid rguidFormatId,
        IntPtr pWaveFormatEx,
        IntPtr pTextFragList,
        object pOutputSite)
    {
        Trace("Speak", $"flags={dwSpeakFlags:X}, fragList={pTextFragList:X}, formatId={rguidFormatId}, pWfx={pWaveFormatEx:X}");
        _firstWriteTickMs = 0; // reset; StreamPcm will set on first successful Write
        _bytesWrittenThisSpeak = 0;
        // Re-read per Speak rather than caching at type load. The value cannot
        // change within a process, but the ORDER in which it is first read can:
        // the benchmark helper sets the variable and then activates the COM
        // engine, and a static initialiser that happened to run earlier — for
        // any reason, in any future refactor — would cache "paced" and silently
        // measure the thing this exists to stop measuring. A dictionary lookup
        // per utterance is not worth defending against that.
        UnpacedActive = BenchSwitches.IsAuthorised(
            Environment.GetEnvironmentVariable(BenchSwitches.UnpacedVariable), Environment.ProcessId);
        if (UnpacedActive) Trace("Speak", "real-time pacing and drain disabled for this process (benchmark)");
        if (pWaveFormatEx != IntPtr.Zero)
        {
            var wfx = Marshal.PtrToStructure<WAVEFORMATEX>(pWaveFormatEx);
            Trace("Speak", $"  WFX: tag={wfx.wFormatTag} channels={wfx.nChannels} rate={wfx.nSamplesPerSec} bits={wfx.wBitsPerSample} blockAlign={wfx.nBlockAlign}");
        }
        IntPtr sitePtr = IntPtr.Zero;
        IntPtr unkPtr = IntPtr.Zero;
        try
        {
            if (pOutputSite is null) return SapiConstants.E_INVALIDARG;
            unkPtr = Marshal.GetIUnknownForObject(pOutputSite);
            int qi = Marshal.QueryInterface(unkPtr, typeof(ISpTTSEngineSite).GUID, out sitePtr);
            if (qi != 0) { Trace("Speak", $"QI ISpTTSEngineSite failed 0x{qi:X8}"); return qi; }

            void** vtbl = *(void***)sitePtr;
            // ISpTTSEngineSite vtable layout (after IUnknown's 3 IUnknown slots):
            //   3=AddEvents, 4=GetEventInterest, 5=GetActions, 6=Write,
            //   7=GetRate, 8=GetVolume, 9=GetSkipInfo, 10=CompleteSkip
            var addEvents = (delegate* unmanaged[Stdcall]<IntPtr, SPEVENT*, uint, int>)vtbl[3];
            var getActions = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[5];
            var write = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, int>)vtbl[6];
            var getRate = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)vtbl[7];
            var getVolume = (delegate* unmanaged[Stdcall]<IntPtr, ushort*, int>)vtbl[8];

            int siteRate = 0;
            if (getRate(sitePtr, &siteRate) < 0) siteRate = 0;
            ushort siteVolumePct = 100;
            if (getVolume(sitePtr, &siteVolumePct) < 0) siteVolumePct = 100;

            // Resolve per-Speak settings snapshot (cached by Settings\Version DWORD).
            // Per-voice override beats global default. Engine keeps reading the same snapshot
            // for the duration of this Speak — knob changes mid-stream pick up on the next call.
            var globalSettings = EngineSettingsCache.Resolve();
            var resolved = globalSettings.ResolveFor(_voiceId);
            float volTrimLinear = (float)Math.Pow(10.0, Math.Clamp(resolved.VolumeTrimDb, -12f, 6f) / 20.0);
            float SiteVolumeScale(ushort pct) => Math.Clamp(pct / 100f * volTrimLinear, 0f, 4f);
            float volumeScale = SiteVolumeScale(siteVolumePct);
            uint interChunkSilenceMs = (uint)Math.Max(0, resolved.InterChunkSilenceMs);

            string defaultLang = SupertonicLanguages.Normalize(resolved.Language);
            bool speakPunctuation = (dwSpeakFlags & SapiConstants.SPF_NLP_SPEAK_PUNC) != 0;
            var planItems = BuildSpeakPlan(pTextFragList, defaultLang, speakPunctuation);
            Trace("Speak", $"siteRate={siteRate}, voiceId={_voiceId}, lang={defaultLang}, volume={siteVolumePct}%×{volTrimLinear:F2}, totalStep={resolved.TotalStep}, engSpeed={resolved.EngineSpeed:F2}, dspRate={resolved.DspRate:F2}, punctuation={(speakPunctuation ? "all" : "default")}, plan: {planItems.Count} item(s)");

            // Telemetry: announce we're starting work. BeginUtterance first, so
            // the figures MarkIdle publishes at the end belong to THIS utterance
            // and cannot be the previous one's left in a latch.
            try { TelemetryWriter.BeginUtterance(); } catch { /* swallow */ }
            try { TelemetryWriter.Update(true, _voiceId, "(starting)", resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate, 0, double.NaN, 1, 0, TelemetryThreads(resolved), 0, ""); }
            catch { /* swallow */ }

            if (planItems.Count == 0) return SapiConstants.S_OK;

            SupertonicAdapter adapter;
            try { adapter = GetAdapter(_voiceId); }
            catch (Exception ex) { Log("Speak.GetAdapter", ex); return SapiConstants.E_FAIL; }

            // Pre-flatten Speak items into per-synthesis chunks. Each chunk inherits
            // its parent SpeakTextItem's RateAdj so SSML <prosody rate=...> tags
            // affect only their own enclosed text.
            var execPlan = new List<object>(); // SpeakChunkExec | SpeakSilenceItem | SpeakBookmarkItem
            foreach (var item in planItems)
            {
                if (item is SpeakTextItem speakItem)
                {
                    // ChunkWithOffsets, not Chunk + IndexOf: the chunker
                    // normalises whitespace, so a chunk built from text with a
                    // newline or a double space between sentences is not a
                    // substring of its input and IndexOf misses every time. The
                    // old fallback then walked forward in rewritten coordinates
                    // and drifted — the same class of defect as R-2.
                    foreach (var chunk in SentenceChunker.ChunkWithOffsets(
                                 speakItem.Text, resolved.MaxChunkChars, resolved.MinChunkChars))
                    {
                        // Two rewrites sit between a chunk index and the client's
                        // text, and BOTH have to be undone:
                        //   chunk index -> rewritten  (chunk.Map: the chunker
                        //                              collapsed whitespace)
                        //   rewritten   -> source     (speakItem.Offsets: rules
                        //                              and the sanitizer, R-2)
                        // Composing them once per chunk means the emit path is a
                        // single lookup and cannot get the order wrong. Carrying
                        // only chunk.Start instead was correct exactly while every
                        // separator was one character long — so single spaces,
                        // newlines and tabs looked right and double spaces and
                        // paragraph breaks drifted one character per separator.
                        execPlan.Add(new SpeakChunkExec(
                            chunk.Text, speakItem.SourceCharOffset,
                            TextOffsetMap.Chain(chunk.Map, speakItem.Offsets),
                            speakItem.RateAdj, speakItem.Lang,
                            speakItem.Gain, speakItem.PitchFactor));
                    }
                }
                else
                {
                    execPlan.Add(item);
                }
            }
            Trace("Speak", $"flattened to {execPlan.Count} exec item(s)");

            // Find indices of synth chunks so we can pipeline ahead.
            var synthIndices = new List<int>();
            for (int i = 0; i < execPlan.Count; i++)
                if (execPlan[i] is SpeakChunkExec) synthIndices.Add(i);
            if (synthIndices.Count == 0)
            {
                // Only bookmarks/silence in plan → emit and return.
                ulong streamOffset = 0;
                foreach (var item in execPlan)
                {
                    if (item is SpeakSilenceItem sil)
                        streamOffset += WriteSilence(sil.Milliseconds, volumeScale, sitePtr, getActions, write);
                    else if (item is SpeakBookmarkItem bm)
                        EmitBookmark(addEvents, sitePtr, bm, streamOffset);
                }
                EmitEndOfStream(addEvents, sitePtr, streamOffset);
                return SapiConstants.S_OK;
            }

            // Vtable function pointers for skip handling (used inside the loop)
            var getSkipInfo = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)vtbl[9];
            var completeSkip = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[10];

            // Pre-flight abort check: a SAPI client that calls SpeakAsync and then
            // SpeakAsyncCancelAll before the engine has spun up will have already
            // signaled ABORT. Launching the first synth task in that window creates
            // an orphan — its result is discarded but it still holds the adapter's
            // _ttsGate for the full synth duration, blocking the next Speak the
            // client is presumably about to start. That's a major contributor to
            // "engine doesn't respond after I clicked play twice in a row."
            if ((getActions(sitePtr) & SapiConstants.SPVES_ABORT) != 0)
            {
                Trace("Speak", "abort before first synth launch");
                return SapiConstants.S_OK;
            }

            // Per-Speak CancellationTokenSource. Cancelled when we detect SAPI
            // ABORT (or when Speak unwinds via exception). Plumbed into every
            // adapter.Synthesize call, which propagates it through tts.Call and
            // into the totalStep loop inside _Infer — so a SAPI Stop cuts the
            // in-flight synth within ~200 ms (one totalStep iteration) instead
            // of letting it run to completion and stall the next Speak behind
            // _ttsGate.
            using var speakCts = new CancellationTokenSource();
            var speakToken = speakCts.Token;

            // Kick off synthesis of the first chunk on a background thread.
            // Synthesis + DSP time-stretch run together on the worker thread so both
            // fall under the same pipeline cover, hiding the stretch cost behind the
            // next chunk's synthesis.
            int nextSynthIdx = 1;
            var firstChunkExec = (SpeakChunkExec)execPlan[synthIndices[0]];
            Task<short[]> currentSynth = StartSynth(adapter, firstChunkExec, siteRate, resolved, speakToken);
            // The site rate the in-flight synthesis was started at. SPVES_RATE can
            // move siteRate while it runs; the result is then re-stretched to the
            // new rate rather than played at the old one (review item 49).
            int currentSynthRate = siteRate;
            var firstSynthStartMs = Environment.TickCount64;
            double rollingRtf = double.NaN;

            ulong totalStreamBytes = 0;
            int chunksDone = 0;
            for (int i = 0; i < execPlan.Count; i++)
            {
                uint actions = getActions(sitePtr);

                // Rate and volume can change while a long passage is spoken — a
                // screen reader's rate keys, a client's volume slider. SAPI raises
                // these bits and expects the engine to re-read; until 0.3.0 both
                // were read once per Speak and a change waited for the next one
                // (review item 49). Read here, they take effect at the next chunk.
                if ((actions & SapiConstants.SPVES_RATE) != 0)
                {
                    int newRate = siteRate;
                    if (getRate(sitePtr, &newRate) >= 0 && newRate != siteRate)
                    {
                        Trace("Speak", $"rate {siteRate} → {newRate} before exec item {i}/{execPlan.Count}");
                        siteRate = newRate;
                    }
                }
                if ((actions & SapiConstants.SPVES_VOLUME) != 0)
                {
                    ushort newVolume = siteVolumePct;
                    if (getVolume(sitePtr, &newVolume) >= 0 && newVolume != siteVolumePct)
                    {
                        Trace("Speak", $"volume {siteVolumePct}% → {newVolume}% before exec item {i}/{execPlan.Count}");
                        siteVolumePct = newVolume;
                        volumeScale = SiteVolumeScale(siteVolumePct);
                    }
                }

                if ((actions & SapiConstants.SPVES_ABORT) != 0)
                {
                    Trace("Speak", $"abort before exec item {i}/{execPlan.Count}");
                    // Cancel any in-flight prefetch so it unwinds within one
                    // totalStep iteration (~200 ms) instead of holding _ttsGate
                    // for the rest of the synth and stalling the next Speak.
                    try { speakCts.Cancel(); } catch { }
                    break;
                }
                if ((actions & SapiConstants.SPVES_SKIP) != 0)
                {
                    int skipType = 0, skipCount = 0;
                    if (getSkipInfo(sitePtr, &skipType, &skipCount) >= 0 && skipCount != 0)
                    {
                        int skipped = ApplySkip(execPlan, ref i, skipCount, ref nextSynthIdx, synthIndices,
                            adapter, siteRate, ref currentSynth, ref currentSynthRate, resolved, speakToken);
                        Trace("Speak", $"skip {skipCount} requested, {skipped} applied (i={i})");
                        completeSkip(sitePtr, skipped);
                        continue; // re-evaluate from new i
                    }
                }

                switch (execPlan[i])
                {
                    case SpeakChunkExec chunk:
                    {
                        short[] pcm;
                        long synthEndMs;
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();

                            // Polling wait on the prefetched synth. We CANNOT use
                            // .Result here: ONNX Run() under a sick DirectML driver
                            // can hang indefinitely, and .Result would wedge this COM
                            // thread — SAPI's Stop signal would be ignored, and the
                            // client's next Speak would block on the marshaller. Poll
                            // SPVES_ABORT every 50 ms (matches StreamPcm/drain pattern)
                            // and bail to a hard ceiling at SynthHardTimeoutMs.
                            int waitedMs = 0;
                            bool completed = false;
                            bool aborted = false;
                            int ceilingMs = _testSynthHardTimeoutMs > 0 ? _testSynthHardTimeoutMs : SynthHardTimeoutMs;
                            while (waitedMs < ceilingMs)
                            {
                                if (currentSynth.Wait(50)) { completed = true; break; }
                                waitedMs += 50;
                                if ((getActions(sitePtr) & SapiConstants.SPVES_ABORT) != 0)
                                {
                                    Trace("Speak", $"abort observed during synth wait ({waitedMs}ms)");
                                    try { speakCts.Cancel(); } catch { }
                                    try { currentSynth.Wait(SynthCancelGraceMs); } catch { /* expected */ }
                                    aborted = true;
                                    break;
                                }
                            }

                            if (aborted) goto AfterSpeakLoop;

                            if (!completed)
                            {
                                // Hard timeout: ONNX is wedged. Cancel the token
                                // (best-effort), queue a session reset so the next
                                // Speak gets a fresh load, log, return E_FAIL so the
                                // COM thread is released. The orphan Task is left to
                                // unwind on its own — the adapter watchdog should
                                // already have fired and disposed the session.
                                try { speakCts.Cancel(); } catch { }
                                string hangPreview = chunk.Text.Length > 60
                                    ? chunk.Text.Substring(0, 60) + "…" : chunk.Text;
                                string hangCodePoints = string.Concat(chunk.Text.Take(60).Select(c =>
                                    c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)
                                        ? $"\\u{(int)c:X4}"
                                        : c.ToString()));
                                Log("Speak.SynthHang", new TimeoutException(
                                    $"Synth wait exceeded {SynthHardTimeoutMs} ms for '{hangPreview}' " +
                                    $"(first 60 with escapes: [{hangCodePoints}]). Forcing session rebuild."));
                                // A rebuild, not RequestReset: that one also clears the
                                // DirectML latch, and a hang is exactly the evidence the
                                // latch records — clearing it here re-enabled the GPU the
                                // watchdog had just turned off (review item 34).
                                try { SupertonicAdapter.RequestSessionRebuild(); } catch { }
                                try
                                {
                                    TelemetryWriter.Update(false, _voiceId, "",
                                        resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate,
                                        0, double.NaN, 0, 0, TelemetryThreads(resolved), 0,
                                        $"Synth hang > {SynthHardTimeoutMs / 1000}s — session reset queued");
                                }
                                catch { }
                                return SapiConstants.E_FAIL;
                            }

                            pcm = currentSynth.Result; // safe: completed == true
                            synthEndMs = Environment.TickCount64;
                            chunksDone++;
                            if (currentSynthRate != siteRate)
                            {
                                pcm = Restretch(pcm, currentSynthRate, siteRate, chunk.RateAdj, resolved);
                                Trace("Speak", $"chunk {chunksDone} was synthesised at rate {currentSynthRate}; re-stretched to {siteRate}");
                            }
                            var (s, st) = ComputeSpeed(siteRate, chunk.RateAdj, resolved);
                            // Rolling RTF over this chunk: synth time / produced audio time.
                            double audioSec = (double)pcm.Length / SampleRate;
                            double synthSec = sw.ElapsedMilliseconds / 1000.0;
                            double chunkRtf = audioSec > 0 ? synthSec / audioSec : double.NaN;
                            rollingRtf = double.IsNaN(rollingRtf) ? chunkRtf : rollingRtf * 0.7 + chunkRtf * 0.3;
                            Trace("Speak", $"chunk {chunksDone}: {pcm.Length} samples ({audioSec:F2}s) [synth wait {sw.ElapsedMilliseconds}ms, synth={s:F2} stretch={st:F2} rtf={chunkRtf:F2}]");
                            try
                            {
                                double firstByte = chunksDone == 1 ? (synthEndMs - firstSynthStartMs) : 0;
                                // Send the full chunk text — the Monitor tab's "Currently
                                // synthesizing" pane is multi-line and scrollable, so there's
                                // no UI reason to truncate. (Earlier MMF telemetry had a 4 KB
                                // segment ceiling that made truncation necessary; the JSON
                                // file backend has no such limit.)
                                TelemetryWriter.Update(true, _voiceId, chunk.Text ?? "",
                                    resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate,
                                    firstByte, rollingRtf, 1, 0, TelemetryThreads(resolved), 0, "");
                            }
                            catch { /* swallow */ }
                        }
                        catch (Exception ex)
                        {
                            // Cancellation is a graceful abort — the SAPI client
                            // already signaled SPVES_ABORT (which is what cancelled
                            // the token). Don't return E_FAIL or log this as a
                            // synth failure; just break to the post-loop drain.
                            if (IsCancellation(ex, speakCts))
                            {
                                Trace("Speak", "synth cancelled by abort — breaking to drain");
                                goto AfterSpeakLoop;
                            }
                            // Log the offending chunk text — invisible characters
                            // (control chars, zero-width marks) appear as escape
                            // sequences so the user can identify the trigger when
                            // the bug recurs. Sanitization in BuildSpeakPlan should
                            // already strip the known classes, but field reports of
                            // non-deterministic stalls justify keeping the forensic.
                            string failPreview = chunk.Text.Length > 200
                                ? chunk.Text.Substring(0, 200) + "…" : chunk.Text;
                            string codePoints = string.Concat(chunk.Text.Take(60).Select(c =>
                                c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)
                                    ? $"\\u{(int)c:X4}"
                                    : c.ToString()));
                            Log("Speak.Synthesize",
                                new Exception($"Failed chunk text (first 60 with escapes): [{codePoints}] " +
                                              $"| preview: \"{failPreview}\"", ex));
                            try { TelemetryWriter.Update(false, _voiceId, "", resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate, 0, double.NaN, 0, 0, TelemetryThreads(resolved), 0, ex.Message); } catch { }
                            return SapiConstants.E_FAIL;
                        }

                        // Prefetch next synth chunk with ITS own per-fragment rate.
                        // Skip the prefetch if the client has already signaled ABORT —
                        // the result would be discarded, but the task would hold the
                        // adapter's _ttsGate for several seconds and stall the next
                        // Speak that the client is presumably about to start.
                        if (nextSynthIdx < synthIndices.Count
                            && (getActions(sitePtr) & SapiConstants.SPVES_ABORT) == 0)
                        {
                            var nextChunkExec = (SpeakChunkExec)execPlan[synthIndices[nextSynthIdx]];
                            currentSynth = StartSynth(adapter, nextChunkExec, siteRate, resolved, speakToken);
                            currentSynthRate = siteRate;
                            nextSynthIdx++;
                        }

                        // Site volume × the fragment's own: SSML <prosody volume>,
                        // SAPI <volume>, and <emph> (review item 50).
                        ApplyVolume(pcm, volumeScale * chunk.Gain);
                        EmitSentenceBoundary(addEvents, sitePtr, chunk, pcm.Length, totalStreamBytes);
                        EmitWordBoundaries(addEvents, sitePtr, chunk, pcm.Length, totalStreamBytes);

                        // Count what was written, not what was meant to be: an
                        // abort mid-chunk used to add the whole chunk (review item 35).
                        int hr = StreamPcm(pcm, sitePtr, getActions, write, out ulong chunkWritten);
                        if (hr < 0) return hr;
                        totalStreamBytes += chunkWritten;

                        if (i + 1 < execPlan.Count && execPlan[i + 1] is SpeakChunkExec)
                            totalStreamBytes += WriteSilence(interChunkSilenceMs, volumeScale, sitePtr, getActions, write);
                        break;
                    }
                    case SpeakSilenceItem sil:
                        totalStreamBytes += WriteSilence(sil.Milliseconds, volumeScale, sitePtr, getActions, write);
                        break;
                    case SpeakBookmarkItem bm:
                        EmitBookmark(addEvents, sitePtr, bm, totalStreamBytes);
                        break;
                }
            }
            AfterSpeakLoop:
            // Cancel the token unconditionally on loop exit — completed prefetches
            // are unaffected (cancelling a finished token is a no-op); in-flight
            // prefetches that we won't consume unwind fast. This is the orphan
            // safety net for any code path that reaches here without going
            // through the abort branch (eg. normal completion where the very
            // last prefetch was launched then unused due to skip-arithmetic).
            try { speakCts.Cancel(); } catch { }

            // Tail flush silence: pad the end of the audio with silence so that any
            // hardware-buffer cut lands in silence, not in the last word. SAPI may
            // signal "done" while the audio device's hardware buffer (100-300 ms
            // typical) hasn't fully physically output. Releasing the voice COM at
            // that point can cut whatever's still in the buffer.
            //
            // Scaled to the utterance since 0.3.0 (review item 51). A fixed 700 ms,
            // plus the 500 ms drain floor below, cost every menu item and every
            // echoed keystroke 1.2 s after its audio — a screen reader's whole
            // working vocabulary. TailSilenceMs on the Advanced tab overrides it.
            if (chunksDone > 0)
            {
                uint padMs = TailPadMs(resolved.TailSilenceMs, totalStreamBytes);
                Trace("Speak", $"tail pad {padMs}ms");
                totalStreamBytes += WriteSilence(padMs, volumeScale, sitePtr, getActions, write);
            }

            EmitEndOfStream(addEvents, sitePtr, totalStreamBytes);

            // Drain wait: do NOT return from Speak until the audio device has had
            // time to actually pull through what's still in SAPI's buffer.
            //
            // Root cause: some SAPI clients (Lingoes is suspect) close their audio
            // output buffer the moment Speak returns S_OK — anything SAPI hadn't
            // pulled into the device by then is discarded. The audio device pulls
            // at real-time (BytesPerSecond), starting from the FIRST byte we wrote
            // to SAPI — NOT from when Speak was called. Synth time before that
            // first write doesn't count toward "time the device had to pull."
            //
            // Math: at the moment Speak finishes its writes, the device has been
            // pulling for (now - firstWriteTickMs) ms and has pulled that many ms
            // worth of audio. We have totalAudioMs of audio to play. So the device
            // still needs (totalAudioMs - elapsedSinceFirstWrite) more ms to drain.
            // Add a 150 ms safety pad for SAPI's prebuffer / driver warm-up. Cap at
            // 60 s. Honor SPVES_ABORT every 50 ms so Stop stays responsive.
            double totalAudioMs = (double)totalStreamBytes / BytesPerSecond * 1000.0;
            long elapsedSinceFirstWriteMs = _firstWriteTickMs > 0
                ? Environment.TickCount64 - _firstWriteTickMs
                : 0;
            double drainMs = totalAudioMs - elapsedSinceFirstWriteMs + 150;
            // Floor the drain so the audio device's hardware buffer has time to
            // physically output samples even when our pacing kept SAPI's buffer
            // thin. Without this floor, drainMs can drop to ~0 or even negative
            // when our pacing is exactly aligned, but the device's own hardware
            // buffer (100-300 ms) hasn't drained yet. 250 ms since 0.3.0, down
            // from 500: the tail pad above is written as audio, so it is already
            // inside this wait's arithmetic, and pad + floor together still cover
            // the 300 ms worst case.
            if (drainMs < DrainFloorMs) drainMs = DrainFloorMs;
            // Cap, don't skip: a pathological drainMs >= 60000 used to skip the drain
            // entirely, letting SAPI cut trailing audio. Clamp to the ceiling instead.
            if (drainMs > 60000) drainMs = 60000;
            // The one legitimate reason to skip it: a benchmark render, where the
            // sink is a file and there is no hardware buffer to wait for. This is
            // the half that dominates. With the write pacing gone the writes
            // finish in compute time, so elapsedSinceFirstWriteMs collapses to
            // almost nothing and drainMs grows to very nearly the full audio
            // duration — gating the pacing alone would just move the same six
            // seconds from one loop to the other and change no measurement at all.
            if (UnpacedActive) drainMs = 0;
            if (drainMs > 0)
            {
                int sleptMs = 0;
                int totalSleepMs = (int)drainMs;
                while (sleptMs < totalSleepMs)
                {
                    if ((getActions(sitePtr) & SapiConstants.SPVES_ABORT) != 0) break;
                    int slice = Math.Min(50, totalSleepMs - sleptMs);
                    System.Threading.Thread.Sleep(slice);
                    sleptMs += slice;
                }
                Trace("Speak", $"drained {sleptMs}ms (audio={totalAudioMs:F0}ms, sinceFirstWrite={elapsedSinceFirstWriteMs}ms)");
            }

            Trace("Speak", $"complete: {chunksDone} chunk(s), {totalStreamBytes} bytes total");
            return SapiConstants.S_OK;
        }
        catch (Exception ex) { Log("Speak", ex); try { TelemetryWriter.Update(false, _voiceId, "", 0, 0, 0, 0, double.NaN, 0, 0, 0, 0, ex.Message); } catch { } return SapiConstants.E_FAIL; }
        finally
        {
            // Always mark the engine idle. Every early return (no plan items,
            // bookmarks-only, abort-before-launch, synth E_FAIL, hang timeout)
            // must clear the Monitor's "CurrentText" so it doesn't strand on
            // the last chunk forever.
            try { TelemetryWriter.MarkIdle(); } catch { /* swallow */ }
            if (sitePtr != IntPtr.Zero) Marshal.Release(sitePtr);
            if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
        }
    }

    /// <summary>
    /// One synthesis chunk, plus everything needed to say where it came from.
    ///
    /// <c>FragmentSourceOffset</c> is the SAPI fragment's offset in the client's
    /// text. <c>Offsets</c> maps an index in THIS CHUNK all the way back to the
    /// fragment's source text, having already composed the chunker's whitespace
    /// normalisation with the pronunciation and sanitizer rewrites.
    ///
    /// The two live in different coordinate spaces and must never be added
    /// together — doing exactly that was R-2. Go through
    /// <see cref="SourceOffsetAt"/>, which crosses the spaces via the map.
    /// </summary>
    private sealed record SpeakChunkExec(
        string Text,
        uint FragmentSourceOffset,
        TextOffsetMap Offsets,
        int RateAdj,
        string Lang,
        float Gain,
        double PitchFactor)
    {
        /// <summary>Absolute offset in the client's text of a position within this chunk.</summary>
        public uint SourceOffsetAt(int indexInChunk) =>
            FragmentSourceOffset + (uint)Offsets.ToSource(indexInChunk);

        /// <summary>
        /// How many source characters a span of this chunk covers — the length a
        /// client should highlight. "kilograms" is nine characters of spoken text
        /// but covers the two the user typed.
        /// </summary>
        public int SourceLengthAt(int indexInChunk, int lengthInChunk)
        {
            int length = Offsets.SourceSpanLength(indexInChunk, lengthInChunk);
            if (length > 0 || lengthInChunk <= 0) return length;

            // The span sits entirely inside one substitution, so it covers no
            // distinct source range of its own ("kg" → "kilo grams" splits into two
            // spoken words over one source token). Report a single character at the
            // right place rather than a zero-width selection that clients render as
            // nothing at all.
            int start = Offsets.ToSource(indexInChunk);
            return start < Offsets.SourceLength ? 1 : 0;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="ex"/> is (or wraps) an
    /// <see cref="OperationCanceledException"/> tied to a cancelled token —
    /// the shape Task.Result throws when the underlying lambda observed
    /// the cancellation. Used by Speak's synth-result catch so a SAPI ABORT
    /// is treated as a graceful exit, not a synth failure → E_FAIL.
    /// </summary>
    private static bool IsCancellation(Exception ex, CancellationTokenSource cts)
    {
        if (!cts.IsCancellationRequested) return false;
        if (ex is OperationCanceledException) return true;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.Flatten().InnerExceptions)
                if (inner is OperationCanceledException) return true;
        }
        for (var e = ex.InnerException; e != null; e = e.InnerException)
            if (e is OperationCanceledException) return true;
        return false;
    }

    /// <summary>
    /// Skip <paramref name="skipCount"/> sentences forward (positive) or backward (negative)
    /// from the current position. Updates <paramref name="i"/> to the new index in the
    /// exec plan and re-prefetches the next synthesis. Skipping forward through bookmarks
    /// still emits them; skipping backward reverses to a previous SpeakChunkExec.
    /// </summary>
    private static int ApplySkip(List<object> execPlan, ref int i, int skipCount,
        ref int nextSynthIdx, List<int> synthIndices, SupertonicAdapter adapter, int siteRate,
        ref Task<short[]> currentSynth, ref int currentSynthRate, EngineSettings resolved, CancellationToken speakToken)
    {
        int actuallySkipped = 0;
        int direction = Math.Sign(skipCount);
        int remaining = Math.Abs(skipCount);

        while (remaining > 0 && i + direction >= 0 && i + direction < execPlan.Count)
        {
            i += direction;
            if (execPlan[i] is SpeakChunkExec) { actuallySkipped++; remaining--; }
        }
        if (direction < 0) actuallySkipped = -actuallySkipped;

        // Re-align nextSynthIdx so the prefetch picks up from the new position
        int searchI = i; // ref-locals can't be captured by lambdas — copy first
        int currentSynthIdx = synthIndices.IndexOf(searchI);
        if (currentSynthIdx < 0)
        {
            // i is between SpeakChunkExec items — point at the next one
            currentSynthIdx = synthIndices.FindIndex(idx => idx > searchI);
            if (currentSynthIdx < 0) currentSynthIdx = synthIndices.Count;
        }
        nextSynthIdx = currentSynthIdx; // currentSynth is stale; replace it with the new chunk's

        if (currentSynthIdx < synthIndices.Count)
        {
            var nextExec = (SpeakChunkExec)execPlan[synthIndices[currentSynthIdx]];
            currentSynth = StartSynth(adapter, nextExec, siteRate, resolved, speakToken);
            currentSynthRate = siteRate;
            nextSynthIdx = currentSynthIdx + 1;
            i = synthIndices[currentSynthIdx] - 1;
        }
        return actuallySkipped;
    }

    /// <summary>
    /// Compute the synthesis speed and DSP stretch for a chunk. Returns
    /// (synthSpeed, stretchFactor).
    /// <para>
    /// The model is driven in its empirically-safe range [0.9, ceiling]. DSP
    /// time-stretch (pitch-preserving, currently <see cref="Sonic"/>) takes
    /// whatever extra speed the user asked for that the model couldn't supply —
    /// that's how the DSP knob extends past the model's 1.3× ceiling.
    /// <c>stretchFactor &gt; 1</c> = play faster; <c>&lt; 1</c> = play slower.
    /// </para>
    /// <para>
    /// Composition: total perceived rate ≈ synthSpeed × (1 / stretchFactor) when stretch
    /// is the playback-time multiplier. <see cref="TimeStretch.Stretch"/> takes a "rate"
    /// parameter — see its doc for direction.
    /// </para>
    /// </summary>
    private static (float synthSpeed, double stretchFactor) ComputeSpeed(int siteRate, int fragRate, EngineSettings s)
    {
        int rateAdjust = Math.Clamp(siteRate + fragRate, -10, 10);
        float baseEngine = s.EngineSpeed > 0 ? s.EngineSpeed : SupertonicAdapter.DefaultSpeed;
        float ratePower = (float)Math.Pow(1.5, rateAdjust / 10.0);
        float ceiling = s.RateClampCeiling > 1.0f ? s.RateClampCeiling : 1.3f;

        // What the user asked for (engineSpeed × dspRate × sapiRate)
        float dsp = s.DspRate > 0 ? s.DspRate : 1.0f;
        float requestedTotal = baseEngine * dsp * ratePower;

        // Clamp the model's speed to its safe zone; DSP stretch absorbs the remainder.
        float synthSpeed = Math.Clamp(baseEngine * ratePower, 0.9f, ceiling);
        // stretchFactor is how much the DSP path needs to compress/expand the
        // rendered PCM. E.g., synth=1.3, requested=2.0 → stretch needs to play
        // 1.54× faster. TimeStretch.Stretch interprets its parameter as the
        // speed multiplier on the rendered audio (>1 = compress, <1 = expand).
        double stretchFactor = synthSpeed > 0 ? requestedTotal / synthSpeed : 1.0;
        // Hard ceiling on DSP — extreme stretch sounds bad.
        stretchFactor = Math.Clamp(stretchFactor, 0.5, 2.0);
        return (synthSpeed, stretchFactor);
    }

    /// <summary>
    /// Synthesise one chunk on a worker thread: the model at the speed it can
    /// take, the DSP stretch for the rest, then the fragment's pitch. One place,
    /// because the first chunk, every prefetch and a skip all start one, and
    /// three copies of this lambda is how one of them missed a parameter.
    /// </summary>
    private static Task<short[]> StartSynth(SupertonicAdapter adapter, SpeakChunkExec chunk, int siteRate,
        EngineSettings resolved, CancellationToken token)
    {
        var (synthSpeed, stretch) = ComputeSpeed(siteRate, chunk.RateAdj, resolved);
        int totalStep = resolved.TotalStep;
        // The Advanced tab's knob, which never reached the model before 0.3.0
        // (review item 33). The model inserts it only where its own splitter cuts
        // a chunk — above 300 characters — so it is heard only when Max chunk
        // chars is raised past that.
        float silenceSec = Math.Clamp(resolved.SynthesisSilenceSec, 0f, 2f);
        return Task.Run(() =>
        {
            var raw = adapter.Synthesize(chunk.Text, totalStep: totalStep, speed: synthSpeed,
                cancellationToken: token, lang: chunk.Lang, silenceSec: silenceSec);
            var pcm = TimeStretch.Stretch(raw, stretch, SampleRate);
            return ShiftPitch(pcm, chunk.PitchFactor);
        }, token);
    }

    /// <summary>
    /// A chunk rendered at <paramref name="fromRate"/>, played at
    /// <paramref name="toRate"/>: the ratio of the two total speeds, applied as
    /// one more pitch-preserving stretch. The model's part of the speed is
    /// already baked in, so this is the only lever left once the audio exists.
    /// </summary>
    private static short[] Restretch(short[] pcm, int fromRate, int toRate, int fragRate, EngineSettings s)
    {
        var (fromSynth, fromStretch) = ComputeSpeed(fromRate, fragRate, s);
        var (toSynth, toStretch) = ComputeSpeed(toRate, fragRate, s);
        double from = fromSynth * fromStretch, to = toSynth * toStretch;
        if (from <= 0 || to <= 0) return pcm;
        return TimeStretch.Stretch(pcm, Math.Clamp(to / from, 0.5, 2.0), SampleRate);
    }

    /// <summary>
    /// SAPI's pitch, <c>&lt;pitch middle&gt;</c> and SSML <c>&lt;prosody
    /// pitch&gt;</c>, as a frequency ratio: resampling raises the pitch and
    /// shortens the audio by the same factor, and the pitch-preserving stretch
    /// then puts the duration back. Review item 50: it used to be dropped.
    /// </summary>
    private static short[] ShiftPitch(short[] pcm, double factor)
    {
        if (pcm.Length == 0 || Math.Abs(factor - 1.0) < 0.01) return pcm;
        int n = (int)(pcm.Length / factor);
        if (n < 2) return pcm;
        var resampled = new short[n];
        for (int i = 0; i < n; i++)
        {
            double src = i * factor;
            int j = (int)src;
            double frac = src - j;
            int a = pcm[Math.Min(j, pcm.Length - 1)];
            int b = pcm[Math.Min(j + 1, pcm.Length - 1)];
            resampled[i] = (short)Math.Clamp(Math.Round(a + (b - a) * frac), short.MinValue, short.MaxValue);
        }
        var restored = TimeStretch.Stretch(resampled, 1.0 / factor, SampleRate);
        // TimeStretch leaves audio too short for its pitch search untouched; such
        // a fragment keeps the shifted pitch and the shorter duration, which for a
        // syllable is the lesser of the two errors.
        return restored;
    }

    // <emph>: the model has no emphasis input, so it is approximated the way a
    // speaker does it — a little louder (+3 dB) and a little slower.
    private const float EmphasisGain = 1.41f;
    private const int EmphasisRateAdj = -1;

    /// <summary>SAPI pitch units (−10…10) to a frequency ratio: ±10 is ±6 semitones.</summary>
    private static double PitchFactorFor(int middleAdj) =>
        Math.Pow(2.0, Math.Clamp(middleAdj, -10, 10) * 0.6 / 12.0);

    /// <summary>
    /// The silence appended after an utterance. A configured
    /// <c>TailSilenceMs</c> of zero or more wins; otherwise a tenth of the
    /// utterance, between 150 and 700 ms — a long passage keeps the old pad,
    /// a one-word item stops paying it.
    /// </summary>
    private static uint TailPadMs(int configuredMs, ulong audioBytes)
    {
        if (configuredMs >= 0) return (uint)Math.Min(configuredMs, 2000);
        double audioMs = audioBytes * 1000.0 / BytesPerSecond;
        return (uint)Math.Clamp(audioMs / 10.0, 150.0, 700.0);
    }

    private const int DrainFloorMs = 250;

    /// <summary>
    /// The thread count telemetry reports: the live session's, or before there
    /// is one the configured value, rather than the -1 that means "no session"
    /// (review item 68: the first snapshot of every process said -1).
    /// </summary>
    private static int TelemetryThreads(EngineSettings resolved) =>
        SupertonicAdapter.EffectiveIntraOpThreads >= 0
            ? SupertonicAdapter.EffectiveIntraOpThreads
            : Math.Max(0, resolved.OnnxThreads);

    private static void ApplyVolume(short[] pcm, float scale)
    {
        if (Math.Abs(scale - 1f) < 0.001f) return;
        for (int i = 0; i < pcm.Length; i++)
        {
            int v = (int)(pcm[i] * scale);
            if (v > short.MaxValue) v = short.MaxValue;
            else if (v < short.MinValue) v = short.MinValue;
            pcm[i] = (short)v;
        }
    }

    private unsafe ulong WriteSilence(uint ms, float volumeScale, IntPtr sitePtr,
        delegate* unmanaged[Stdcall]<IntPtr, uint> getActions,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, int> write)
    {
        int samples = (int)((long)SampleRate * ms / 1000L);
        if (samples <= 0) return 0;
        short[] silence = new short[samples];
        // (volumeScale doesn't matter for silence but kept symmetric with audio path)
        StreamPcm(silence, sitePtr, getActions, write, out ulong written);
        return written;
    }

    private static unsafe void EmitSentenceBoundary(
        delegate* unmanaged[Stdcall]<IntPtr, SPEVENT*, uint, int> addEvents,
        IntPtr sitePtr, SpeakChunkExec chunk, int pcmSamples, ulong streamOffsetBytes)
    {
        var ev = new SPEVENT
        {
            eEventId = SapiConstants.SPEI_SENTENCE_BOUNDARY,
            elParamType = SapiConstants.SPET_LPARAM_IS_UNDEFINED,
            ulStreamNum = 0,
            ullAudioStreamOffset = streamOffsetBytes,
            wParam = (IntPtr)chunk.SourceLengthAt(0, chunk.Text.Length),
            lParam = (IntPtr)(int)chunk.SourceOffsetAt(0),
        };
        int hr = addEvents(sitePtr, &ev, 1);
        if (hr < 0) Trace("Speak", $"AddEvents(sentence) hr=0x{hr:X8}");
    }

    private static unsafe void EmitWordBoundaries(
        delegate* unmanaged[Stdcall]<IntPtr, SPEVENT*, uint, int> addEvents,
        IntPtr sitePtr, SpeakChunkExec chunk, int pcmSamples, ulong streamOffsetBytes)
    {
        // Find word spans in chunk.Text and emit one SPEI_WORD_BOUNDARY per word with
        // proportional audio offset (char_pos / chunk_chars × chunk_audio_bytes).
        if (chunk.Text.Length == 0 || pcmSamples == 0) return;

        ulong chunkAudioBytes = (ulong)(pcmSamples * sizeof(short));
        var events = new List<SPEVENT>();
        int n = chunk.Text.Length;
        int pos = 0;
        while (pos < n)
        {
            while (pos < n && !IsWordChar(chunk.Text[pos])) pos++;
            if (pos >= n) break;
            int start = pos;
            while (pos < n && IsWordChar(chunk.Text[pos])) pos++;
            int wordLen = pos - start;
            if (wordLen == 0) continue;

            ulong wordAudioOffset = streamOffsetBytes
                + (ulong)((long)chunkAudioBytes * start / n);
            wordAudioOffset &= ~1UL; // align to sample boundary (2 bytes)

            events.Add(new SPEVENT
            {
                eEventId = SapiConstants.SPEI_WORD_BOUNDARY,
                elParamType = SapiConstants.SPET_LPARAM_IS_UNDEFINED,
                ulStreamNum = 0,
                ullAudioStreamOffset = wordAudioOffset,
                wParam = (IntPtr)chunk.SourceLengthAt(start, wordLen),
                lParam = (IntPtr)(int)chunk.SourceOffsetAt(start),
            });
        }

        if (events.Count == 0) return;
        var arr = events.ToArray();
        fixed (SPEVENT* p = arr)
        {
            int hr = addEvents(sitePtr, p, (uint)arr.Length);
            // Every offset, not just the first: the harness asserts they rise
            // through the passage (review item 80), and this line is the one
            // place a host-independent reading of them exists.
            Trace("Speak", $"AddEvents(word x{arr.Length}) hr=0x{hr:X8} firstOffset={arr[0].ullAudioStreamOffset} pos={(int)arr[0].lParam} len={(int)arr[0].wParam} offsets={string.Join(',', arr.Select(e => e.ullAudioStreamOffset))}");
        }
    }

    // U+2019 and U+02BC as well as the ASCII apostrophe, so "don’t" is one word
    // event, as it is in Core's BoundaryPlanner (review item 13).
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '\'' || c == '\u2019' || c == '\u02BC' || c == '-';

    private static unsafe void EmitBookmark(
        delegate* unmanaged[Stdcall]<IntPtr, SPEVENT*, uint, int> addEvents,
        IntPtr sitePtr, SpeakBookmarkItem bookmark, ulong streamOffsetBytes)
    {
        // SAPI bookmark: lParam is a string pointer (the bookmark name), wParam is the
        // numeric value (atoi if numeric, otherwise 0).
        IntPtr nameUtf16 = Marshal.StringToCoTaskMemUni(bookmark.Name);
        int wInt = int.TryParse(bookmark.Name, out int parsed) ? parsed : 0;
        var ev = new SPEVENT
        {
            eEventId = SapiConstants.SPEI_TTS_BOOKMARK,
            elParamType = SapiConstants.SPET_LPARAM_IS_STRING,
            ulStreamNum = 0,
            ullAudioStreamOffset = streamOffsetBytes,
            wParam = (IntPtr)wInt,
            lParam = nameUtf16,
        };
        try
        {
            int hr = addEvents(sitePtr, &ev, 1);
            if (hr < 0) Trace("Speak", $"AddEvents(bookmark) hr=0x{hr:X8}");
        }
        finally
        {
            // SAPI copies the string inside AddEvents — the SDK's sample engine
            // passes stack memory — so it is ours to free. Until 0.3.0 this said
            // SAPI took ownership, and every bookmark leaked its name: about
            // 1.6 MB per 2,000 measured, for a reader that bookmarks each
            // sentence all day (review item 30).
            Marshal.FreeCoTaskMem(nameUtf16);
        }
    }

    private static unsafe void EmitEndOfStream(
        delegate* unmanaged[Stdcall]<IntPtr, SPEVENT*, uint, int> addEvents,
        IntPtr sitePtr, ulong streamOffsetBytes)
    {
        var ev = new SPEVENT
        {
            eEventId = SapiConstants.SPEI_END_INPUT_STREAM,
            elParamType = SapiConstants.SPET_LPARAM_IS_UNDEFINED,
            ulStreamNum = 0,
            ullAudioStreamOffset = streamOffsetBytes,
            wParam = IntPtr.Zero,
            lParam = IntPtr.Zero,
        };
        int hr = addEvents(sitePtr, &ev, 1);
        if (hr < 0) Trace("Speak", $"AddEvents(end-of-stream) hr=0x{hr:X8}");
    }

    /// <summary>
    /// Wall-clock time of the first successful SAPI Write call within the current
    /// Speak invocation. Used by the write throttle and the drain wait at end-of-Speak.
    /// Cleared at Speak entry.
    /// </summary>
    private long _firstWriteTickMs;
    /// <summary>
    /// Cumulative bytes written to SAPI during the current Speak invocation. Used
    /// alongside <see cref="_firstWriteTickMs"/> to keep our writes paced to roughly
    /// real-time (so SAPI's buffer never holds more than ~LookaheadMs of audio).
    /// </summary>
    private long _bytesWrittenThisSpeak;

    /// <summary>
    /// Maximum bytes we let our writes get ahead of expected playback before we
    /// throttle. Keeping SAPI's buffer thin makes end-of-Speak drain reliable —
    /// the audio device finishes pulling within tens of ms of our last write.
    /// 100 ms is plenty of margin against driver hiccups without making the
    /// per-chunk pipeline visible to the user.
    /// </summary>
    private const int LookaheadMs = 100;
    private const int LookaheadBytes = (BytesPerSecond * LookaheadMs) / 1000;

    /// <summary>
    /// Whether this process is rendering for a benchmark and has asked for the
    /// real-time pacing and the end-of-Speak drain to be skipped — see
    /// <see cref="BenchSwitches"/> for the switch and for why it is pid-keyed.
    ///
    /// <para>Process-wide rather than per-instance because the thing it reflects
    /// is a property of the process, not of a voice: the environment variable is
    /// read from the process block and every engine instance in the process gets
    /// the same answer. Concurrent Speaks therefore write the same value and
    /// cannot race to a different one.</para>
    ///
    /// <para><b>False for every real SAPI host, always.</b> Nothing sets the
    /// variable except the benchmark helper, and the helper can only set it for
    /// itself. If this is ever observed true in Balabolka, NVDA or Lingoes then
    /// the guard has failed and R-14 is back — which is why
    /// <see cref="Telemetry.TelemetryWriter"/> publishes it rather than leaving
    /// it to be inferred from a log line nobody reads.</para>
    /// </summary>
    internal static volatile bool UnpacedActive;

    /// <param name="bytesWritten">What actually reached SAPI, which after an abort
    /// is less than <paramref name="pcm"/> holds.</param>
    private unsafe int StreamPcm(short[] pcm, IntPtr sitePtr,
        delegate* unmanaged[Stdcall]<IntPtr, uint> getActions,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, int> write,
        out ulong bytesWritten)
    {
        int totalBytes = pcm.Length * sizeof(short);
        int offset = 0;
        var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            while (offset < totalBytes)
            {
                if ((getActions(sitePtr) & SapiConstants.SPVES_ABORT) != 0)
                {
                    Trace("Speak", "abort during Write");
                    break;
                }
                uint toWrite = (uint)Math.Min(ChunkBytes, totalBytes - offset);
                uint written;
                int hr = write(sitePtr, basePtr + offset, toWrite, &written);
                if (hr < 0) { Trace("Speak", $"Write hr=0x{hr:X8}"); return hr; }
                // System.Speech's EngineSiteSapi.Write doesn't update *pcbWritten (parameter
                // reassignment), so trust hr=S_OK and advance by the requested count.
                if (_firstWriteTickMs == 0) _firstWriteTickMs = Environment.TickCount64;
                offset += (int)toWrite;
                _bytesWrittenThisSpeak += toWrite;

                // Real-time pacing: don't let SAPI's buffer accumulate more than
                // LookaheadMs of audio. Without this, we hand SAPI 5+ seconds of PCM
                // in tens of ms; the audio device pulls at 44.1 kHz so it can't drain
                // before our drain wait expires, and SAPI clients close the buffer
                // when Speak returns — losing the trailing word(s). With pacing,
                // SAPI's buffer is at most LookaheadMs full at any time, so the
                // device drains within a similar window of our last write.
                //
                // Skipped entirely for a benchmark render (UnpacedActive): there
                // is no device pulling at 44.1 kHz on the other end, only a file,
                // so every millisecond spent here is measurement time bought for
                // nothing. See the drain below — both halves go together.
                long elapsedMs = Environment.TickCount64 - _firstWriteTickMs;
                long expectedBytes = (BytesPerSecond * elapsedMs) / 1000;
                long aheadBytes = _bytesWrittenThisSpeak - expectedBytes;
                if (!UnpacedActive && aheadBytes > LookaheadBytes)
                {
                    int sleepMs = (int)((aheadBytes - LookaheadBytes) * 1000 / BytesPerSecond);
                    if (sleepMs > 0)
                    {
                        // Sleep in slices so SPVES_ABORT (Stop) is responsive.
                        int slept = 0;
                        while (slept < sleepMs)
                        {
                            if ((getActions(sitePtr) & SapiConstants.SPVES_ABORT) != 0) return SapiConstants.S_OK;
                            int slice = Math.Min(50, sleepMs - slept);
                            System.Threading.Thread.Sleep(slice);
                            slept += slice;
                        }
                    }
                }
            }
            return SapiConstants.S_OK;
        }
        finally
        {
            handle.Free();
            // Every exit, the abort-while-paced return included: the line is what
            // the harness sums to check the byte total (review item 35).
            bytesWritten = (ulong)offset;
            Trace("Speak", $"wrote {offset} of {totalBytes} bytes");
        }
    }

    /// <summary>
    /// One unit in the Speak plan: text-to-synthesize, silence pause, or bookmark.
    /// Each SpeakTextItem carries its own per-fragment RateAdj so SSML
    /// &lt;prosody rate="..."&gt; tags affect only their own enclosed text.
    /// </summary>
    private abstract record SpeakItem;
    /// <param name="SourceCharOffset">Where this fragment starts in the client's text.</param>
    /// <param name="Offsets">Maps an index in <paramref name="Text"/> (rewritten) back into the fragment's source text.</param>
    /// <param name="Gain">The fragment's own volume (SPVSTATE.Volume, emphasis) as a linear factor.</param>
    /// <param name="PitchFactor">The fragment's pitch (SPVSTATE.PitchAdj.MiddleAdj) as a frequency ratio.</param>
    private sealed record SpeakTextItem(string Text, uint SourceCharOffset, int RateAdj, string Lang, TextOffsetMap Offsets,
        float Gain, double PitchFactor) : SpeakItem;
    private sealed record SpeakSilenceItem(uint Milliseconds) : SpeakItem;
    private sealed record SpeakBookmarkItem(string Name, uint SourceCharOffset) : SpeakItem;

    /// <summary>
    /// Flatten SAPI's fragment list into our plan. <paramref name="defaultLang"/>
    /// is the configured language (global, or the per-voice override); a fragment
    /// that carries its own <c>LangID</c> — which is how SSML <c>xml:lang</c>
    /// reaches an engine — wins for that fragment only, so a German paragraph
    /// inside an English document is spoken by the German model without the user
    /// touching a setting.
    /// </summary>
    /// <param name="speakPunctuation">The client passed <c>SPF_NLP_SPEAK_PUNC</c>.</param>
    private static List<SpeakItem> BuildSpeakPlan(IntPtr pTextFragList, string defaultLang, bool speakPunctuation)
    {
        var items = new List<SpeakItem>();
        if (pTextFragList == IntPtr.Zero) return items;

        var (pron, pronCompiled) = PronunciationsCache.Resolve();

        IntPtr cur = pTextFragList;
        int safety = 0;
        while (cur != IntPtr.Zero && safety++ < 10000)
        {
            var frag = Marshal.PtrToStructure<SPVTEXTFRAG>(cur);
            string text = (frag.pTextStart != IntPtr.Zero && frag.ulTextLen > 0)
                ? Marshal.PtrToStringUni(frag.pTextStart, (int)frag.ulTextLen) ?? ""
                : "";

            switch (frag.State.eAction)
            {
                case SPVACTIONS.SPVA_Speak:
                case SPVACTIONS.SPVA_SpellOut:
                case SPVACTIONS.SPVA_Pronounce:
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Pronounce is a SAPI-level override and the rules still
                        // apply to it, since the user wrote the rule expecting it to
                        // fire regardless of how the fragment was tagged. SpellOut
                        // is the exception: what is spelled is the letters the user
                        // wrote, so the rules are skipped and only the line-break
                        // join and the sanitizer run. Spelled after the rules, a
                        // rule "SAPI" → "sappy" made <spell>SAPI</spell> say
                        // "S, A, P, P, Y" (the Fable review of 5A, risk 1).
                        // Strip invisible characters (control chars, zero-width
                        // marks, private-use) that have non-deterministically
                        // wedged the synth in the field. Done before chunking so
                        // the chunker sees clean text and the engine's downstream
                        // IndexOf on the same string still finds chunks.
                        // Prepare returns the text the model will speak plus a map
                        // from that text's indices back to the client's. Both stages
                        // change length — rules substitute, the sanitizer strips —
                        // so without the map every later boundary offset is wrong by
                        // the accumulated delta (R-2).
                        bool spelled = frag.State.eAction == SPVACTIONS.SPVA_SpellOut;
                        string spoken = SynthTextPipeline.Prepare(text,
                            spelled ? null : pron, spelled ? null : pronCompiled, out var offsets);
                        if (!string.IsNullOrEmpty(spoken))
                        {
                            // FromLcid returns null both for "fragment carries no
                            // language" (LangID 0, the common case) and for "a
                            // language Supertonic doesn't speak" — both fall back
                            // to the user's configured language.
                            string fragLang = SupertonicLanguages.FromLcid(frag.State.LangID) ?? defaultLang;
                            Trace("BuildSpeakPlan", $"frag LangID=0x{frag.State.LangID:X4} → lang={fragLang}");

                            // Review item 48: both modes change what is said, so
                            // they rewrite the spoken text, and their maps chain
                            // onto the pipeline's so highlights still land on the
                            // user's characters.
                            if (frag.State.eAction == SPVACTIONS.SPVA_SpellOut)
                            {
                                spoken = SapiTextModes.SpellOut(spoken, fragLang, out var spellMap);
                                offsets = TextOffsetMap.Chain(spellMap, offsets);
                            }
                            else if (speakPunctuation)
                            {
                                spoken = SapiTextModes.SpeakPunctuation(spoken, fragLang, out var puncMap);
                                offsets = TextOffsetMap.Chain(puncMap, offsets);
                            }

                            // Review item 50: the fragment's own volume, pitch and
                            // emphasis, which were never read. Volume 0 is taken as
                            // "not set" rather than as silence: a host that sends
                            // a zeroed state must not mute a screen reader, and
                            // <volume level="0"> is the one markup that loses.
                            uint vol = frag.State.Volume is 0 or > 100 ? 100u : frag.State.Volume;
                            bool emphasis = frag.State.EmphAdj != 0;
                            float gain = vol / 100f * (emphasis ? EmphasisGain : 1f);
                            int rateAdj = frag.State.RateAdj + (emphasis ? EmphasisRateAdj : 0);
                            double pitch = PitchFactorFor(frag.State.PitchAdj.MiddleAdj);
                            Trace("BuildSpeakPlan", $"frag action={frag.State.eAction} volume={vol} pitch={frag.State.PitchAdj.MiddleAdj} emph={frag.State.EmphAdj} spoken=\"{Truncate(spoken, 200)}\"");
                            items.Add(new SpeakTextItem(spoken, frag.ulTextSrcOffset, rateAdj, fragLang, offsets, gain, pitch));
                        }
                    }
                    break;
                case SPVACTIONS.SPVA_Silence:
                    if (frag.State.SilenceMSecs > 0)
                        items.Add(new SpeakSilenceItem(frag.State.SilenceMSecs));
                    break;
                case SPVACTIONS.SPVA_Bookmark:
                    if (!string.IsNullOrEmpty(text))
                        items.Add(new SpeakBookmarkItem(text, frag.ulTextSrcOffset));
                    break;
                // SPVA_Section, SPVA_ParseUnknownTag: ignore for v1
            }
            cur = frag.pNext;
        }
        return items;
    }

    private static SupertonicAdapter GetAdapter(string voiceId)
    {
        lock (_adaptersLock)
        {
            if (!_adapters.TryGetValue(voiceId, out var adapter))
            {
                adapter = new SupertonicAdapter(voiceId);
                _adapters[voiceId] = adapter;
            }
            return adapter;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";

    private static readonly object _logLock = new();
    private static string? _logPath;

    private static string LogPath
    {
        get
        {
            if (_logPath != null) return _logPath;
            // DataPaths resolves the user's portable data root (default
            // <BaseDir>\data, override in HKCU\SOFTWARE\VibeSuperTonic\DataDir).
            // We cache the resolved path; if the user changes DataDir, new logs
            // appear at the new location after the next engine load.
            string dir = Settings.DataPaths.LogsDir;
            try { Directory.CreateDirectory(dir); } catch { }
            _logPath = Path.Combine(dir, "engine.log");
            return _logPath;
        }
    }

    private static void Trace(string where, string msg)
    {
        try
        {
            lock (_logLock)
            {
                LogRotation.RollIfNeeded(LogPath);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {where}: {msg}\n");
            }
        }
        catch { }
    }

    private static void Log(string where, Exception ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] {where} EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            // Walk the inner-exception chain. The synth failure path wraps the
            // real ONNX/preprocessing error as InnerException of a forensic
            // wrapper (see Speak.Synthesize), so without this the actual cause —
            // the only actionable line — never reaches the log.
            int depth = 0;
            for (var inner = ex.InnerException; inner is not null && depth < 8; inner = inner.InnerException, depth++)
                sb.Append($"  ---> Inner [{depth}] {inner.GetType().FullName}: {inner.Message}\n{inner.StackTrace}\n");
            sb.Append('\n');
            lock (_logLock)
            {
                LogRotation.RollIfNeeded(LogPath);
                File.AppendAllText(LogPath, sb.ToString());
            }
        }
        catch { }
    }
}

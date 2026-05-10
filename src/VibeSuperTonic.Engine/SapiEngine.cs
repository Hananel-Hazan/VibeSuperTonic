using System.Runtime.InteropServices;
using System.Text;
using VibeSuperTonic.Engine.Interop;
using VibeSuperTonic.Engine.Settings;
using VibeSuperTonic.Engine.Synth;
using VibeSuperTonic.Engine.Telemetry;

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
            float volumeScale = Math.Clamp(siteVolumePct / 100f * volTrimLinear, 0f, 4f);
            uint interChunkSilenceMs = (uint)Math.Max(0, resolved.InterChunkSilenceMs);

            var planItems = BuildSpeakPlan(pTextFragList);
            Trace("Speak", $"siteRate={siteRate}, voiceId={_voiceId}, volume={siteVolumePct}%×{volTrimLinear:F2}, totalStep={resolved.TotalStep}, engSpeed={resolved.EngineSpeed:F2}, dspRate={resolved.DspRate:F2}, plan: {planItems.Count} item(s)");

            // Telemetry: announce we're starting work.
            try { TelemetryWriter.Update(true, _voiceId, "(starting)", resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate, 0, double.NaN, 1, 0, resolved.OnnxThreads, 0, ""); }
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
                    var chunks = SupertonicAdapter.ChunkText(speakItem.Text);
                    int offsetWithin = 0;
                    foreach (var chunkText in chunks)
                    {
                        int idx = speakItem.Text.IndexOf(chunkText, offsetWithin, StringComparison.Ordinal);
                        if (idx < 0) idx = offsetWithin;
                        execPlan.Add(new SpeakChunkExec(chunkText, speakItem.SourceCharOffset + (uint)idx, speakItem.RateAdj));
                        offsetWithin = idx + chunkText.Length;
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

            // Kick off synthesis of the first chunk on a background thread.
            // Synthesize-then-WSOLA-stretch happens together on the worker thread so the
            // synth-only and the stretch both fall under pipeline cover.
            int nextSynthIdx = 1;
            var firstChunkExec = (SpeakChunkExec)execPlan[synthIndices[0]];
            var (firstSynth, firstStretch) = ComputeSpeed(siteRate, firstChunkExec.RateAdj, resolved);
            int totalStepResolved = resolved.TotalStep;
            Task<short[]> currentSynth = Task.Run(() =>
            {
                var raw = adapter.Synthesize(firstChunkExec.Text, totalStep: totalStepResolved, speed: firstSynth);
                return TimeStretch.Stretch(raw, firstStretch);
            });
            var firstSynthStartMs = Environment.TickCount64;
            double rollingRtf = double.NaN;

            ulong totalStreamBytes = 0;
            int chunksDone = 0;
            for (int i = 0; i < execPlan.Count; i++)
            {
                uint actions = getActions(sitePtr);
                if ((actions & SapiConstants.SPVES_ABORT) != 0)
                {
                    Trace("Speak", $"abort before exec item {i}/{execPlan.Count}");
                    break;
                }
                if ((actions & SapiConstants.SPVES_SKIP) != 0)
                {
                    int skipType = 0, skipCount = 0;
                    if (getSkipInfo(sitePtr, &skipType, &skipCount) >= 0 && skipCount != 0)
                    {
                        int skipped = ApplySkip(execPlan, ref i, skipCount, ref nextSynthIdx, synthIndices,
                            adapter, siteRate, ref currentSynth, resolved);
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
                            pcm = currentSynth.Result;
                            synthEndMs = Environment.TickCount64;
                            chunksDone++;
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
                                TelemetryWriter.Update(true, _voiceId,
                                    chunk.Text.Length > 80 ? chunk.Text.Substring(0, 80) : chunk.Text,
                                    resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate,
                                    firstByte, rollingRtf, 1, 0, resolved.OnnxThreads, 0, "");
                            }
                            catch { /* swallow */ }
                        }
                        catch (Exception ex) { Log("Speak.Synthesize", ex); try { TelemetryWriter.Update(false, _voiceId, "", resolved.TotalStep, resolved.EngineSpeed, resolved.DspRate, 0, double.NaN, 0, 0, resolved.OnnxThreads, 0, ex.Message); } catch { } return SapiConstants.E_FAIL; }

                        // Prefetch next synth chunk with ITS own per-fragment rate.
                        if (nextSynthIdx < synthIndices.Count)
                        {
                            var nextChunkExec = (SpeakChunkExec)execPlan[synthIndices[nextSynthIdx]];
                            var (nSynth, nStretch) = ComputeSpeed(siteRate, nextChunkExec.RateAdj, resolved);
                            int nextTotalStep = resolved.TotalStep;
                            currentSynth = Task.Run(() =>
                            {
                                var raw = adapter.Synthesize(nextChunkExec.Text, totalStep: nextTotalStep, speed: nSynth);
                                return TimeStretch.Stretch(raw, nStretch);
                            });
                            nextSynthIdx++;
                        }

                        ApplyVolume(pcm, volumeScale);
                        EmitSentenceBoundary(addEvents, sitePtr, chunk, pcm.Length, totalStreamBytes);
                        EmitWordBoundaries(addEvents, sitePtr, chunk, pcm.Length, totalStreamBytes);

                        int hr = StreamPcm(pcm, sitePtr, getActions, write);
                        if (hr < 0) return hr;
                        totalStreamBytes += (ulong)(pcm.Length * sizeof(short));

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

            // Tail flush silence: pad the end of the audio with silence so that any
            // hardware-buffer cut lands in silence, not in the last word. SAPI may
            // signal "done" while the audio device's hardware buffer (100-300 ms
            // typical) hasn't fully physically output. Releasing the voice COM at
            // that point can cut whatever's still in the buffer. 700 ms of silence
            // gives even the laziest drivers headroom — any cut lands in the pad.
            if (chunksDone > 0)
                totalStreamBytes += WriteSilence(700, volumeScale, sitePtr, getActions, write);

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
            // Floor the drain at 500 ms so the audio device's hardware buffer has
            // time to physically output samples even when our pacing kept SAPI's
            // buffer thin. Without this floor, drainMs can drop to ~0 or even
            // negative when our pacing is exactly aligned, but the device's own
            // hardware buffer (100-300 ms) hasn't drained yet.
            if (drainMs < 500) drainMs = 500;
            if (drainMs > 0 && drainMs < 60000)
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
            try { TelemetryWriter.MarkIdle(); } catch { /* swallow */ }
            return SapiConstants.S_OK;
        }
        catch (Exception ex) { Log("Speak", ex); try { TelemetryWriter.Update(false, _voiceId, "", 0, 0, 0, 0, double.NaN, 0, 0, 0, 0, ex.Message); } catch { } return SapiConstants.E_FAIL; }
        finally
        {
            if (sitePtr != IntPtr.Zero) Marshal.Release(sitePtr);
            if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
        }
    }

    private sealed record SpeakChunkExec(string Text, uint SourceCharOffset, int RateAdj);

    /// <summary>
    /// Skip <paramref name="skipCount"/> sentences forward (positive) or backward (negative)
    /// from the current position. Updates <paramref name="i"/> to the new index in the
    /// exec plan and re-prefetches the next synthesis. Skipping forward through bookmarks
    /// still emits them; skipping backward reverses to a previous SpeakChunkExec.
    /// </summary>
    private static int ApplySkip(List<object> execPlan, ref int i, int skipCount,
        ref int nextSynthIdx, List<int> synthIndices, SupertonicAdapter adapter, int siteRate,
        ref Task<short[]> currentSynth, EngineSettings resolved)
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
            var (nSynth, nStretch) = ComputeSpeed(siteRate, nextExec.RateAdj, resolved);
            int totalStepLocal = resolved.TotalStep;
            currentSynth = Task.Run(() =>
            {
                var raw = adapter.Synthesize(nextExec.Text, totalStep: totalStepLocal, speed: nSynth);
                return TimeStretch.Stretch(raw, nStretch);
            });
            nextSynthIdx = currentSynthIdx + 1;
            i = synthIndices[currentSynthIdx] - 1;
        }
        return actuallySkipped;
    }

    /// <summary>
    /// Compute the synthesis speed and DSP stretch for a chunk. Returns
    /// (synthSpeed, stretchFactor).
    /// <para>
    /// The model is driven in its empirically-safe range [0.9, ceiling]. WSOLA stretch
    /// (pitch-preserving time-scale) takes whatever extra speed the user asked for that
    /// the model couldn't supply — that's how the DSP knob extends past the model's
    /// 1.3× ceiling. <c>stretchFactor &gt; 1</c> = play faster; <c>&lt; 1</c> = play slower.
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
        // stretchFactor is how much WSOLA needs to compress/expand the rendered PCM.
        // E.g., synth=1.3, requested=2.0 → stretch needs to play 1.54× faster.
        // TimeStretch.Stretch interprets its parameter as the speed multiplier on the
        // rendered audio (>1 = compress in time, <1 = expand). Confirmed below.
        double stretchFactor = synthSpeed > 0 ? requestedTotal / synthSpeed : 1.0;
        // Hard ceiling on DSP — extreme stretch sounds bad.
        stretchFactor = Math.Clamp(stretchFactor, 0.5, 2.0);
        return (synthSpeed, stretchFactor);
    }

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
        StreamPcm(silence, sitePtr, getActions, write);
        return (ulong)(samples * sizeof(short));
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
            wParam = (IntPtr)chunk.Text.Length,
            lParam = (IntPtr)(int)chunk.SourceCharOffset,
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
                wParam = (IntPtr)wordLen,
                lParam = (IntPtr)(int)(chunk.SourceCharOffset + (uint)start),
            });
        }

        if (events.Count == 0) return;
        var arr = events.ToArray();
        fixed (SPEVENT* p = arr)
        {
            int hr = addEvents(sitePtr, p, (uint)arr.Length);
            Trace("Speak", $"AddEvents(word x{arr.Length}) hr=0x{hr:X8} firstOffset={arr[0].ullAudioStreamOffset} pos={(int)arr[0].lParam} len={(int)arr[0].wParam}");
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '\'' || c == '-';

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
        int hr = addEvents(sitePtr, &ev, 1);
        if (hr < 0) Trace("Speak", $"AddEvents(bookmark) hr=0x{hr:X8}");
        // The lParam string is owned by SAPI after AddEvents returns; we don't free it here
        // because SAPI will free it via CoTaskMemFree per the SPET_LPARAM_IS_STRING contract.
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

    private unsafe int StreamPcm(short[] pcm, IntPtr sitePtr,
        delegate* unmanaged[Stdcall]<IntPtr, uint> getActions,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint*, int> write)
    {
        int totalBytes = pcm.Length * sizeof(short);
        var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            int offset = 0;
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
                long elapsedMs = Environment.TickCount64 - _firstWriteTickMs;
                long expectedBytes = (BytesPerSecond * elapsedMs) / 1000;
                long aheadBytes = _bytesWrittenThisSpeak - expectedBytes;
                if (aheadBytes > LookaheadBytes)
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
            Trace("Speak", $"wrote {offset} of {totalBytes} bytes");
            return SapiConstants.S_OK;
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// One unit in the Speak plan: text-to-synthesize, silence pause, or bookmark.
    /// Each SpeakTextItem carries its own per-fragment RateAdj so SSML
    /// &lt;prosody rate="..."&gt; tags affect only their own enclosed text.
    /// </summary>
    private abstract record SpeakItem;
    private sealed record SpeakTextItem(string Text, uint SourceCharOffset, int RateAdj) : SpeakItem;
    private sealed record SpeakSilenceItem(uint Milliseconds) : SpeakItem;
    private sealed record SpeakBookmarkItem(string Name, uint SourceCharOffset) : SpeakItem;

    private static List<SpeakItem> BuildSpeakPlan(IntPtr pTextFragList)
    {
        var items = new List<SpeakItem>();
        if (pTextFragList == IntPtr.Zero) return items;

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
                        items.Add(new SpeakTextItem(text, frag.ulTextSrcOffset, frag.State.RateAdj));
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
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeSuperTonic", "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "engine.log");
            return _logPath;
        }
    }

    private static void Trace(string where, string msg)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {where}: {msg}\n");
        }
        catch { }
    }

    private static void Log(string where, Exception ex)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {where} EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch { }
    }
}

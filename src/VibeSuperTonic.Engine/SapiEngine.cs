using System.Runtime.InteropServices;
using System.Text;
using VibeSuperTonic.Engine.Interop;
using VibeSuperTonic.Engine.Synth;

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

    // For Phase 1 spike, hardcode voice id; will be read from token attributes in Phase 2.
    private const string DefaultVoiceId = "M1";

    private static readonly Dictionary<string, SupertonicAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _adaptersLock = new();

    private object? _token;
    private string _voiceId = DefaultVoiceId;

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
            _voiceId = TryReadVoiceIdFromToken(pToken) ?? DefaultVoiceId;
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
            float volumeScale = Math.Clamp(siteVolumePct / 100f, 0f, 1f);

            var planItems = BuildSpeakPlan(pTextFragList);
            Trace("Speak", $"siteRate={siteRate}, voiceId={_voiceId}, volume={siteVolumePct}%, plan: {planItems.Count} item(s)");

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
            var (firstSynth, firstStretch) = ComputeSpeed(siteRate, firstChunkExec.RateAdj);
            Task<short[]> currentSynth = Task.Run(() =>
            {
                var raw = adapter.Synthesize(firstChunkExec.Text, speed: firstSynth);
                return TimeStretch.Stretch(raw, firstStretch);
            });

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
                            adapter, siteRate, ref currentSynth);
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
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            pcm = currentSynth.Result;
                            chunksDone++;
                            var (s, st) = ComputeSpeed(siteRate, chunk.RateAdj);
                            Trace("Speak", $"chunk {chunksDone}: {pcm.Length} samples ({(double)pcm.Length / SampleRate:F2}s) [synth wait {sw.ElapsedMilliseconds}ms, synth={s:F2} stretch={st:F2}]");
                        }
                        catch (Exception ex) { Log("Speak.Synthesize", ex); return SapiConstants.E_FAIL; }

                        // Prefetch next synth chunk with ITS own per-fragment rate.
                        if (nextSynthIdx < synthIndices.Count)
                        {
                            var nextChunkExec = (SpeakChunkExec)execPlan[synthIndices[nextSynthIdx]];
                            var (nSynth, nStretch) = ComputeSpeed(siteRate, nextChunkExec.RateAdj);
                            currentSynth = Task.Run(() =>
                            {
                                var raw = adapter.Synthesize(nextChunkExec.Text, speed: nSynth);
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
                            totalStreamBytes += WriteSilence(200, volumeScale, sitePtr, getActions, write);
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

            EmitEndOfStream(addEvents, sitePtr, totalStreamBytes);
            Trace("Speak", $"complete: {chunksDone} chunk(s), {totalStreamBytes} bytes total");
            return SapiConstants.S_OK;
        }
        catch (Exception ex) { Log("Speak", ex); return SapiConstants.E_FAIL; }
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
        ref Task<short[]> currentSynth)
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
            var (nSynth, nStretch) = ComputeSpeed(siteRate, nextExec.RateAdj);
            currentSynth = Task.Run(() =>
            {
                var raw = adapter.Synthesize(nextExec.Text, speed: nSynth);
                return TimeStretch.Stretch(raw, nStretch);
            });
            nextSynthIdx = currentSynthIdx + 1;
            i = synthIndices[currentSynthIdx] - 1;
        }
        return actuallySkipped;
    }

    /// <summary>
    /// Compute the synthesis speed for a chunk. Returns (synthSpeed, stretchFactor).
    /// Drives the model directly in its empirically-safe range [0.9, 1.3]. The model's
    /// documented max is 1.5 but in practice 1.34+ drops trailing words; 1.3 stays
    /// comfortably inside the safe zone. Stretch is always 1.0 (no DSP). Going past
    /// 1.3× without quality loss requires a phase vocoder or SoundTouch — deferred.
    /// </summary>
    private static (float synthSpeed, double stretchFactor) ComputeSpeed(int siteRate, int fragRate)
    {
        int rateAdjust = Math.Clamp(siteRate + fragRate, -10, 10);
        float raw = SupertonicAdapter.DefaultSpeed * (float)Math.Pow(1.5, rateAdjust / 10.0);
        float speed = Math.Clamp(raw, 0.9f, 1.3f);
        return (speed, 1.0);
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

    private static unsafe ulong WriteSilence(uint ms, float volumeScale, IntPtr sitePtr,
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

    private static unsafe int StreamPcm(short[] pcm, IntPtr sitePtr,
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
                offset += (int)toWrite;
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

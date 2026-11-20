using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Supertonic
{
    // Available languages for multilingual TTS. "na" is upstream's language-agnostic
    // code (used when the input language is unknown; wrapped as <na>...</na> like any
    // other tag). It must be whitelisted here or PreprocessText rejects lang="na" —
    // matches supertone-inc/supertonic csharp/Helper.cs AVAILABLE_LANGS at v3.0.0 (32 entries).
    public static class Languages
    {
        public static readonly string[] Available = { "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na" };
    }

    // ============================================================================
    // Configuration classes
    // ============================================================================

    public class Config
    {
        public AEConfig AE { get; set; } = null!;
        public TTLConfig TTL { get; set; } = null!;

        public class AEConfig
        {
            public int SampleRate { get; set; }
            public int BaseChunkSize { get; set; }
        }

        public class TTLConfig
        {
            public int ChunkCompressFactor { get; set; }
            public int LatentDim { get; set; }
        }
    }

    // ============================================================================
    // Style class
    // ============================================================================

    public class Style
    {
        public float[] Ttl { get; set; }
        public long[] TtlShape { get; set; }
        public float[] Dp { get; set; }
        public long[] DpShape { get; set; }

        public Style(float[] ttl, long[] ttlShape, float[] dp, long[] dpShape)
        {
            Ttl = ttl;
            TtlShape = ttlShape;
            Dp = dp;
            DpShape = dpShape;
        }
    }

    // ============================================================================
    // Unicode text processor
    // ============================================================================

    public class UnicodeProcessor
    {
        private readonly Dictionary<int, long> _indexer;

        public UnicodeProcessor(string unicodeIndexerPath)
        {
            var json = File.ReadAllText(unicodeIndexerPath);
            var indexerArray = JsonSerializer.Deserialize<long[]>(json) ?? throw new Exception("Failed to load indexer");
            _indexer = new Dictionary<int, long>();
            for (int i = 0; i < indexerArray.Length; i++)
            {
                _indexer[i] = indexerArray[i];
            }
        }

        private static string RemoveEmojis(string text)
        {
            var result = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // Get the full code point from surrogate pair
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++; // Skip the low surrogate
                }
                else
                {
                    codePoint = text[i];
                }

                // Check if code point is in emoji ranges
                bool isEmoji = (codePoint >= 0x1F600 && codePoint <= 0x1F64F) ||
                               (codePoint >= 0x1F300 && codePoint <= 0x1F5FF) ||
                               (codePoint >= 0x1F680 && codePoint <= 0x1F6FF) ||
                               (codePoint >= 0x1F700 && codePoint <= 0x1F77F) ||
                               (codePoint >= 0x1F780 && codePoint <= 0x1F7FF) ||
                               (codePoint >= 0x1F800 && codePoint <= 0x1F8FF) ||
                               (codePoint >= 0x1F900 && codePoint <= 0x1F9FF) ||
                               (codePoint >= 0x1FA00 && codePoint <= 0x1FA6F) ||
                               (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF) ||
                               (codePoint >= 0x2600 && codePoint <= 0x26FF) ||
                               (codePoint >= 0x2700 && codePoint <= 0x27BF) ||
                               (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF);

                if (!isEmoji)
                {
                    if (codePoint > 0xFFFF)
                    {
                        // Add back as surrogate pair
                        result.Append(char.ConvertFromUtf32(codePoint));
                    }
                    else
                    {
                        result.Append((char)codePoint);
                    }
                }
            }
            return result.ToString();
        }

        private string PreprocessText(string text, string lang)
        {
            // TODO: Need advanced normalizer for better performance
            text = text.Normalize(NormalizationForm.FormKD);

            // Remove emojis (wide Unicode range)
            // C# doesn't support \u{...} syntax in regex, so we use character filtering instead
            text = RemoveEmojis(text);

            // Replace various dashes and symbols
            var replacements = new Dictionary<string, string>
            {
                {"–", "-"},      // en dash
                {"‑", "-"},      // non-breaking hyphen
                {"—", "-"},      // em dash
                {"_", " "},      // underscore
                {"\u201C", "\""},     // left double quote
                {"\u201D", "\""},     // right double quote
                {"\u2018", "'"},      // left single quote
                {"\u2019", "'"},      // right single quote
                {"´", "'"},      // acute accent
                {"`", "'"},      // grave accent
                {"[", " "},      // left bracket
                {"]", " "},      // right bracket
                {"|", " "},      // vertical bar
                {"/", " "},      // slash
                {"#", " "},      // hash
                {"→", " "},      // right arrow
                {"←", " "},      // left arrow
            };

            foreach (var kvp in replacements)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }

            // Remove special symbols
            text = Regex.Replace(text, @"[♥☆♡©\\]", "");

            // Replace known expressions
            var exprReplacements = new Dictionary<string, string>
            {
                {"@", " at "},
                {"e.g.,", "for example, "},
                {"i.e.,", "that is, "},
            };

            foreach (var kvp in exprReplacements)
            {
                text = text.Replace(kvp.Key, kvp.Value);
            }

            // Fix spacing around punctuation
            text = Regex.Replace(text, @" ,", ",");
            text = Regex.Replace(text, @" \.", ".");
            text = Regex.Replace(text, @" !", "!");
            text = Regex.Replace(text, @" \?", "?");
            text = Regex.Replace(text, @" ;", ";");
            text = Regex.Replace(text, @" :", ":");
            text = Regex.Replace(text, @" '", "'");

            // Remove duplicate quotes
            while (text.Contains("\"\""))
            {
                text = text.Replace("\"\"", "\"");
            }
            while (text.Contains("''"))
            {
                text = text.Replace("''", "'");
            }
            while (text.Contains("``"))
            {
                text = text.Replace("``", "`");
            }

            // Remove extra spaces
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // If text doesn't end with punctuation, quotes, or closing brackets, add a period
            if (!Regex.IsMatch(text, @"[.!?;:,'\u0022\u201C\u201D\u2018\u2019)\]}…。」』】〉》›»]$"))
            {
                text += ".";
            }

            // Validate language
            if (!Languages.Available.Contains(lang))
            {
                throw new ArgumentException($"Invalid language: {lang}. Available: {string.Join(", ", Languages.Available)}");
            }

            // Wrap text with language tags
            text = $"<{lang}>" + text + $"</{lang}>";

            return text;
        }

        private int[] TextToUnicodeValues(string text)
        {
            return text.Select(c => (int)c).ToArray();
        }

        private float[][][] GetTextMask(long[] textIdsLengths)
        {
            return Helper.LengthToMask(textIdsLengths);
        }

        public (long[][] textIds, float[][][] textMask) Call(List<string> textList, List<string> langList)
        {
            var processedTexts = textList.Select((t, i) => PreprocessText(t, langList[i])).ToList();
            var textIdsLengths = processedTexts.Select(t => (long)t.Length).ToArray();
            long maxLen = textIdsLengths.Max();

            var textIds = new long[textList.Count][];
            for (int i = 0; i < processedTexts.Count; i++)
            {
                textIds[i] = new long[maxLen];
                var unicodeVals = TextToUnicodeValues(processedTexts[i]);
                for (int j = 0; j < unicodeVals.Length; j++)
                {
                    if (_indexer.TryGetValue(unicodeVals[j], out long val))
                    {
                        textIds[i][j] = val;
                    }
                }
            }

            var textMask = GetTextMask(textIdsLengths);
            return (textIds, textMask);
        }
    }

    // ============================================================================
    // TextToSpeech class
    // ============================================================================

    public class TextToSpeech : IDisposable
    {
        private readonly Config _cfgs;
        private readonly UnicodeProcessor _textProcessor;
        private readonly InferenceSession _dpOrt;
        private readonly InferenceSession _textEncOrt;
        private readonly InferenceSession _vectorEstOrt;
        private readonly InferenceSession _vocoderOrt;
        // Cached output names per session. Needed for the RunOptions-aware
        // Run overload, which requires IReadOnlyCollection<string> for output
        // names (OutputMetadata.Keys is IEnumerable<string> via the interface
        // declaration, even though the underlying Dictionary's KeyCollection
        // implements IReadOnlyCollection). Cached once to avoid allocating
        // a List on every Run.
        private readonly IReadOnlyCollection<string> _dpOutputNames;
        private readonly IReadOnlyCollection<string> _textEncOutputNames;
        private readonly IReadOnlyCollection<string> _vectorEstOutputNames;
        private readonly IReadOnlyCollection<string> _vocoderOutputNames;
        public readonly int SampleRate;
        private readonly int _baseChunkSize;
        private readonly int _chunkCompressFactor;
        private readonly int _ldim;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Each session owns native handles for its ONNX graph + DML command queue;
            // they must be released explicitly. Swallow per-session errors so a bad
            // handle doesn't stop us from releasing the others.
            try { _dpOrt?.Dispose(); } catch { }
            try { _textEncOrt?.Dispose(); } catch { }
            try { _vectorEstOrt?.Dispose(); } catch { }
            try { _vocoderOrt?.Dispose(); } catch { }
        }

        public TextToSpeech(
            Config cfgs,
            UnicodeProcessor textProcessor,
            InferenceSession dpOrt,
            InferenceSession textEncOrt,
            InferenceSession vectorEstOrt,
            InferenceSession vocoderOrt)
        {
            _cfgs = cfgs;
            _textProcessor = textProcessor;
            _dpOrt = dpOrt;
            _textEncOrt = textEncOrt;
            _vectorEstOrt = vectorEstOrt;
            _vocoderOrt = vocoderOrt;
            _dpOutputNames = dpOrt.OutputMetadata.Keys.ToList();
            _textEncOutputNames = textEncOrt.OutputMetadata.Keys.ToList();
            _vectorEstOutputNames = vectorEstOrt.OutputMetadata.Keys.ToList();
            _vocoderOutputNames = vocoderOrt.OutputMetadata.Keys.ToList();
            SampleRate = cfgs.AE.SampleRate;
            _baseChunkSize = cfgs.AE.BaseChunkSize;
            _chunkCompressFactor = cfgs.TTL.ChunkCompressFactor;
            _ldim = cfgs.TTL.LatentDim;
        }

        private (float[][][] noisyLatent, float[][][] latentMask) SampleNoisyLatent(float[] duration)
        {
            int bsz = duration.Length;
            float wavLenMax = duration.Max() * SampleRate;
            var wavLengths = duration.Select(d => (long)(d * SampleRate)).ToArray();
            int chunkSize = _baseChunkSize * _chunkCompressFactor;
            int latentLen = (int)((wavLenMax + chunkSize - 1) / chunkSize);
            int latentDim = _ldim * _chunkCompressFactor;

            // Generate random noise
            var random = new Random();
            var noisyLatent = new float[bsz][][];
            for (int b = 0; b < bsz; b++)
            {
                noisyLatent[b] = new float[latentDim][];
                for (int d = 0; d < latentDim; d++)
                {
                    noisyLatent[b][d] = new float[latentLen];
                    for (int t = 0; t < latentLen; t++)
                    {
                        // Box-Muller transform for normal distribution
                        double u1 = 1.0 - random.NextDouble();
                        double u2 = 1.0 - random.NextDouble();
                        noisyLatent[b][d][t] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                    }
                }
            }

            var latentMask = Helper.GetLatentMask(wavLengths, _baseChunkSize, _chunkCompressFactor);

            // Apply mask
            for (int b = 0; b < bsz; b++)
            {
                for (int d = 0; d < latentDim; d++)
                {
                    for (int t = 0; t < latentLen; t++)
                    {
                        noisyLatent[b][d][t] *= latentMask[b][0][t];
                    }
                }
            }

            return (noisyLatent, latentMask);
        }

        private (float[] wav, float[] duration) _Infer(List<string> textList, List<string> langList, Style style, int totalStep, float speed = 1.05f, CancellationToken cancellationToken = default, RunOptions? runOptions = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bsz = textList.Count;
            if (bsz != style.TtlShape[0])
            {
                throw new ArgumentException("Number of texts must match number of style vectors");
            }

            // Process text
            var (textIds, textMask) = _textProcessor.Call(textList, langList);
            var textIdsShape = new long[] { bsz, textIds[0].Length };
            var textMaskShape = new long[] { bsz, 1, textMask[0][0].Length };

            var textIdsTensor = Helper.IntArrayToTensor(textIds, textIdsShape);
            var textMaskTensor = Helper.ArrayToTensor(textMask, textMaskShape);

            var styleTtlTensor = new DenseTensor<float>(style.Ttl, style.TtlShape.Select(x => (int)x).ToArray());
            var styleDpTensor = new DenseTensor<float>(style.Dp, style.DpShape.Select(x => (int)x).ToArray());

            // Run duration predictor
            var dpInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
                NamedOnnxValue.CreateFromTensor("style_dp", styleDpTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
            };
            // RunOptions lets the adapter watchdog call Terminate() on a wedged
            // Run from another thread. When runOptions is null we use the simple
            // overload; when provided, the explicit-outputs overload that accepts
            // RunOptions is used (ORT 1.20 doesn't expose a (inputs, RunOptions)
            // shortcut — we have to pass outputNames too).
            using var dpOutputs = runOptions is null
                ? _dpOrt.Run(dpInputs)
                : _dpOrt.Run(dpInputs, _dpOutputNames, runOptions);
            var durOnnx = dpOutputs.First(o => o.Name == "duration").AsTensor<float>().ToArray();
            
            // Apply speed factor to duration
            for (int i = 0; i < durOnnx.Length; i++)
            {
                durOnnx[i] /= speed;
            }

            // Run text encoder
            var textEncInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
            };
            using var textEncOutputs = runOptions is null
                ? _textEncOrt.Run(textEncInputs)
                : _textEncOrt.Run(textEncInputs, _textEncOutputNames, runOptions);
            var textEmbTensor = textEncOutputs.First(o => o.Name == "text_emb").AsTensor<float>();

            // Sample noisy latent
            var (xt, latentMask) = SampleNoisyLatent(durOnnx);
            var latentShape = new long[] { bsz, xt[0].Length, xt[0][0].Length };
            var latentMaskShape = new long[] { bsz, 1, latentMask[0][0].Length };

            var totalStepArray = Enumerable.Repeat((float)totalStep, bsz).ToArray();

            // Iterative denoising. The cancellation check sits at the loop top:
            // ONNX Run itself isn't interruptible (no RunOptions.Terminate threaded
            // through), but each iteration is short (~200 ms with totalStep=8) so
            // a cancellation here makes a SAPI ABORT abandon the synth within one
            // iteration — i.e. ~200 ms — instead of waiting the full synth (which
            // could be 5-10 s for a long chunk).
            for (int step = 0; step < totalStep; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentStepArray = Enumerable.Repeat((float)step, bsz).ToArray();

                var vectorEstInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("noisy_latent", Helper.ArrayToTensor(xt, latentShape)),
                    NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                    NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                    NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                    NamedOnnxValue.CreateFromTensor("latent_mask", Helper.ArrayToTensor(latentMask, latentMaskShape)),
                    NamedOnnxValue.CreateFromTensor("total_step", new DenseTensor<float>(totalStepArray, new int[] { bsz })),
                    NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(currentStepArray, new int[] { bsz }))
                };

                using var vectorEstOutputs = runOptions is null
                    ? _vectorEstOrt.Run(vectorEstInputs)
                    : _vectorEstOrt.Run(vectorEstInputs, _vectorEstOutputNames, runOptions);
                var denoisedLatent = vectorEstOutputs.First(o => o.Name == "denoised_latent").AsTensor<float>();

                // Update xt
                int idx = 0;
                for (int b = 0; b < bsz; b++)
                {
                    for (int d = 0; d < xt[b].Length; d++)
                    {
                        for (int t = 0; t < xt[b][d].Length; t++)
                        {
                            xt[b][d][t] = denoisedLatent.GetValue(idx++);
                        }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Run vocoder
            var vocoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("latent", Helper.ArrayToTensor(xt, latentShape))
            };
            using var vocoderOutputs = runOptions is null
                ? _vocoderOrt.Run(vocoderInputs)
                : _vocoderOrt.Run(vocoderInputs, _vocoderOutputNames, runOptions);
            var wavTensor = vocoderOutputs.First(o => o.Name == "wav_tts").AsTensor<float>();

            return (wavTensor.ToArray(), durOnnx);
        }

        public (float[] wav, float[] duration) Call(string text, string lang, Style style, int totalStep, float speed = 1.05f, float silenceDuration = 0.3f, CancellationToken cancellationToken = default, RunOptions? runOptions = null)
        {
            if (style.TtlShape[0] != 1)
            {
                throw new ArgumentException("Single speaker text to speech only supports single style");
            }

            int maxLen = (lang == "ko" || lang == "ja") ? 120 : 300;
            var textList = Helper.ChunkText(text, maxLen);
            var wavCat = new List<float>();
            float durCat = 0.0f;

            foreach (var chunk in textList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (wav, duration) = _Infer(new List<string> { chunk }, new List<string> { lang }, style, totalStep, speed, cancellationToken, runOptions);

                if (wavCat.Count == 0)
                {
                    wavCat.AddRange(wav);
                    durCat = duration[0];
                }
                else
                {
                    int silenceLen = (int)(silenceDuration * SampleRate);
                    var silence = new float[silenceLen];
                    wavCat.AddRange(silence);
                    wavCat.AddRange(wav);
                    durCat += duration[0] + silenceDuration;
                }
            }

            return (wavCat.ToArray(), new float[] { durCat });
        }

        public (float[] wav, float[] duration) Batch(List<string> textList, List<string> langList, Style style, int totalStep, float speed = 1.05f, CancellationToken cancellationToken = default)
        {
            return _Infer(textList, langList, style, totalStep, speed, cancellationToken);
        }
    }

    // ============================================================================
    // Persisted optimised graphs
    // ============================================================================

    /// <summary>
    /// ORT's optimised graph, saved beside a model so the next process start skips
    /// the optimisation pass. Shared by the Supertonic loader and the Piper
    /// backend, so there is one rule for when a saved graph may be trusted.
    ///
    /// <para><b>The file name is the key.</b> It folds in the source's file name,
    /// size and last-write time, the ORT version, the process architecture and the
    /// CPU's vector instruction sets (see <see cref="Prefix"/> for the one that is
    /// spelled out rather than hashed).
    /// Until 2026-11-20 the Supertonic cache was trusted whenever its files
    /// existed, so a model replaced by "Repair all" kept running its old graph on
    /// CPU while DirectML ran the new one (review item 10). A changed model now
    /// has a different name, so nothing has to remember to delete the old graph;
    /// the stale one is pruned once the new one is published.</para>
    ///
    /// <para><b>Written under a private temp name, then renamed.</b> ORT writes
    /// the graph during session creation, and two hosts starting together could
    /// otherwise read a half-written file: a protobuf error and a silent
    /// utterance. A rename either shows the whole file or none of it.</para>
    ///
    /// <para>Platform-neutral on purpose: this file is compiled into the Windows
    /// engine as well as the Linux backend.</para>
    /// </summary>
    public static class OptimizedGraphCache
    {
        private static readonly Lazy<string> OrtVersion =
            new(() => OrtEnv.Instance().GetVersionString());

        /// <summary>Temp files older than this belong to a process that died mid-write.</summary>
        private static readonly TimeSpan AbandonedTempAge = TimeSpan.FromHours(1);

        /// <summary>
        /// ORT warns, on every save at ORT_ENABLE_ALL, that the NCHWc layout it
        /// writes "may contain hardware specific optimizations". The instruction
        /// sets MLAS chooses its kernels by are therefore part of the key, so a
        /// portable home carried to another machine re-optimises instead of
        /// running a graph laid out for a different CPU.
        /// </summary>
        private static readonly string CpuFeatures = string.Concat(
            System.Runtime.Intrinsics.X86.Avx.IsSupported ? "avx," : "",
            System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "avx2," : "",
            System.Runtime.Intrinsics.X86.Fma.IsSupported ? "fma," : "",
            System.Runtime.Intrinsics.X86.Avx512F.IsSupported ? "avx512f," : "",
            System.Runtime.Intrinsics.X86.Avx512BW.IsSupported ? "avx512bw," : "",
            System.Runtime.Intrinsics.X86.AvxVnni.IsSupported ? "avxvnni," : "",
            System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported ? "neon," : "");

        /// <summary>16 hex characters naming exactly one (source file, runtime, CPU) triple.</summary>
        public static string Key(string sourcePath)
        {
            var source = new FileInfo(sourcePath);
            string material = string.Join("|",
                source.Name,
                source.Length.ToString(CultureInfo.InvariantCulture),
                source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                OrtVersion.Value,
                CpuFeatures);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>Where the optimised graph for <paramref name="sourcePath"/> lives now.</summary>
        public static string PathFor(string sourcePath, string cacheDir, string stem) =>
            Path.Combine(cacheDir, Prefix(stem) + Key(sourcePath) + ".onnx");

        /// <summary>
        /// The architecture is in the name rather than the hash so that pruning
        /// stays within one: the Windows x64 and x86 engines share one
        /// <c>models</c> folder, and each would otherwise delete the other's graph
        /// on every load and re-optimise on the next.
        /// </summary>
        private static string Prefix(string stem) =>
            stem + ".optimized-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() + "-";

        /// <summary>
        /// A session for <paramref name="sourcePath"/>: from the saved graph when it
        /// matches the source, otherwise optimised at <c>ORT_ENABLE_ALL</c> and
        /// saved for next time. <paramref name="newOptions"/> supplies everything
        /// else (threads, memory settings); it is called once per attempt, and the
        /// optimisation level and output path are set here. A cache directory that
        /// cannot be written costs the saving, never the session.
        /// </summary>
        public static InferenceSession Load(
            string sourcePath, string cacheDir, string stem, Func<SessionOptions> newOptions, out bool cacheHit)
        {
            string final = PathFor(sourcePath, cacheDir, stem);

            if (File.Exists(final))
            {
                var hitOptions = newOptions();
                hitOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
                try
                {
                    var session = new InferenceSession(final, hitOptions);
                    cacheHit = true;
                    return session;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Unreadable despite its key (a disk that filled during an
                    // older release's write). Rebuild it rather than fail forever.
                    hitOptions.Dispose();
                    TryDelete(final);
                }
            }

            cacheHit = false;
            string temp = final + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".tmp";
            var options = newOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.OptimizedModelFilePath = temp;

            InferenceSession built;
            try
            {
                built = new InferenceSession(sourcePath, options);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Most often the directory refused the write. Load without saving;
                // if the model itself is broken this throws the real error.
                // Not narrowed to OnnxRuntimeException: measured with ORT 1.22.1,
                // a refused write carries an error code the managed binding has no
                // name for, and it arrives as a KeyNotFoundException.
                options.Dispose();
                TryDelete(temp);
                var plain = newOptions();
                plain.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                return new InferenceSession(sourcePath, plain);
            }

            Publish(temp, final);
            Prune(cacheDir, stem, keep: final);
            return built;
        }

        private static void Publish(string temp, string final)
        {
            try
            {
                // No overwrite: a process that published the same key first wrote
                // the same graph, and on Windows a file another host has open
                // cannot be replaced anyway.
                File.Move(temp, final, overwrite: false);
            }
            catch (IOException) { TryDelete(temp); }
            catch (UnauthorizedAccessException) { TryDelete(temp); }
        }

        /// <summary>Removes this stem's graphs for other keys, and temp files nobody is still writing.</summary>
        private static void Prune(string cacheDir, string stem, string keep)
        {
            try
            {
                foreach (string old in Directory.GetFiles(cacheDir, Prefix(stem) + "*.onnx"))
                    if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                        TryDelete(old);

                DateTime cutoff = DateTime.UtcNow - AbandonedTempAge;
                foreach (string temp in Directory.GetFiles(cacheDir, Prefix(stem) + "*.tmp"))
                    if (File.GetLastWriteTimeUtc(temp) < cutoff)
                        TryDelete(temp);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ============================================================================
    // Helper class with utility functions
    // ============================================================================

    public static class Helper
    {
        // ============================================================================
        // Utility functions
        // ============================================================================

        public static float[][][] LengthToMask(long[] lengths, long maxLen = -1)
        {
            if (maxLen == -1)
            {
                maxLen = lengths.Max();
            }

            var mask = new float[lengths.Length][][];
            for (int i = 0; i < lengths.Length; i++)
            {
                mask[i] = new float[1][];
                mask[i][0] = new float[maxLen];
                for (int j = 0; j < maxLen; j++)
                {
                    mask[i][0][j] = j < lengths[i] ? 1.0f : 0.0f;
                }
            }
            return mask;
        }

        public static float[][][] GetLatentMask(long[] wavLengths, int baseChunkSize, int chunkCompressFactor)
        {
            int latentSize = baseChunkSize * chunkCompressFactor;
            var latentLengths = wavLengths.Select(len => (len + latentSize - 1) / latentSize).ToArray();
            return LengthToMask(latentLengths);
        }

        // ============================================================================
        // ONNX model loading
        // ============================================================================

        public static InferenceSession LoadOnnx(string onnxPath, SessionOptions opts)
        {
            return new InferenceSession(onnxPath, opts);
        }

        public static (InferenceSession dp, InferenceSession textEnc, InferenceSession vectorEst, InferenceSession vocoder)
            LoadOnnxAll(string onnxDir, SessionOptions opts)
        {
            InferenceSession LoadOne(string name) => LoadOnnx(Path.Combine(onnxDir, name + ".onnx"), opts);
            return (LoadOne(ModelNames[0]), LoadOne(ModelNames[1]), LoadOne(ModelNames[2]), LoadOne(ModelNames[3]));
        }

        private static readonly string[] ModelNames = { "duration_predictor", "text_encoder", "vector_estimator", "vocoder" };

        // ============================================================================
        // Configuration loading
        // ============================================================================

        public static Config LoadCfgs(string onnxDir)
        {
            var cfgPath = Path.Combine(onnxDir, "tts.json");
            var json = File.ReadAllText(cfgPath);
            
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            return new Config
            {
                AE = new Config.AEConfig
                {
                    SampleRate = root.GetProperty("ae").GetProperty("sample_rate").GetInt32(),
                    BaseChunkSize = root.GetProperty("ae").GetProperty("base_chunk_size").GetInt32()
                },
                TTL = new Config.TTLConfig
                {
                    ChunkCompressFactor = root.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32(),
                    LatentDim = root.GetProperty("ttl").GetProperty("latent_dim").GetInt32()
                }
            };
        }

        public static UnicodeProcessor LoadTextProcessor(string onnxDir)
        {
            var unicodeIndexerPath = Path.Combine(onnxDir, "unicode_indexer.json");
            return new UnicodeProcessor(unicodeIndexerPath);
        }

        // ============================================================================
        // Voice style loading
        // ============================================================================

        public static Style LoadVoiceStyle(List<string> voiceStylePaths, bool verbose = false)
        {
            int bsz = voiceStylePaths.Count;
            
            // Read first file to get dimensions
            var firstJson = File.ReadAllText(voiceStylePaths[0]);
            using var firstDoc = JsonDocument.Parse(firstJson);
            var firstRoot = firstDoc.RootElement;
            
            var ttlDims = ParseInt64Array(firstRoot.GetProperty("style_ttl").GetProperty("dims"));
            var dpDims = ParseInt64Array(firstRoot.GetProperty("style_dp").GetProperty("dims"));
            
            long ttlDim1 = ttlDims[1];
            long ttlDim2 = ttlDims[2];
            long dpDim1 = dpDims[1];
            long dpDim2 = dpDims[2];
            
            // Pre-allocate arrays with full batch size
            int ttlSize = (int)(bsz * ttlDim1 * ttlDim2);
            int dpSize = (int)(bsz * dpDim1 * dpDim2);
            var ttlFlat = new float[ttlSize];
            var dpFlat = new float[dpSize];
            
            // Fill in the data
            for (int i = 0; i < bsz; i++)
            {
                var json = File.ReadAllText(voiceStylePaths[i]);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Flatten data
                var ttlData3D = ParseFloat3DArray(root.GetProperty("style_ttl").GetProperty("data"));
                var ttlDataFlat = new List<float>();
                foreach (var batch in ttlData3D)
                {
                    foreach (var row in batch)
                    {
                        ttlDataFlat.AddRange(row);
                    }
                }

                var dpData3D = ParseFloat3DArray(root.GetProperty("style_dp").GetProperty("data"));
                var dpDataFlat = new List<float>();
                foreach (var batch in dpData3D)
                {
                    foreach (var row in batch)
                    {
                        dpDataFlat.AddRange(row);
                    }
                }

                // Copy to pre-allocated array
                int ttlOffset = (int)(i * ttlDim1 * ttlDim2);
                ttlDataFlat.CopyTo(ttlFlat, ttlOffset);
                
                int dpOffset = (int)(i * dpDim1 * dpDim2);
                dpDataFlat.CopyTo(dpFlat, dpOffset);
            }

            var ttlShape = new long[] { bsz, ttlDim1, ttlDim2 };
            var dpShape = new long[] { bsz, dpDim1, dpDim2 };

            if (verbose)
            {
                Console.WriteLine($"Loaded {bsz} voice styles");
            }

            return new Style(ttlFlat, ttlShape, dpFlat, dpShape);
        }

        private static float[][][] ParseFloat3DArray(JsonElement element)
        {
            var result = new List<float[][]>();
            foreach (var batch in element.EnumerateArray())
            {
                var batch2D = new List<float[]>();
                foreach (var row in batch.EnumerateArray())
                {
                    var rowData = new List<float>();
                    foreach (var val in row.EnumerateArray())
                    {
                        rowData.Add(val.GetSingle());
                    }
                    batch2D.Add(rowData.ToArray());
                }
                result.Add(batch2D.ToArray());
            }
            return result.ToArray();
        }

        private static long[] ParseInt64Array(JsonElement element)
        {
            var result = new List<long>();
            foreach (var val in element.EnumerateArray())
            {
                result.Add(val.GetInt64());
            }
            return result.ToArray();
        }

        // ============================================================================
        // TextToSpeech loading
        // ============================================================================

        /// <param name="gpuActive">
        /// True only when the DirectML provider was actually appended.
        ///
        /// <para>Reported rather than assumed, added 2026-08-19 for the thread
        /// sweep. <c>useGpu</c> is a request; this is the answer. The two differ
        /// on any machine without a usable DX12 device, and the difference used to
        /// exist only as a line on the console — so a "DirectML" benchmark row on
        /// such a machine would have been a CPU row wearing the wrong label, which
        /// is the one outcome that makes the GPU question look answered when it
        /// was never asked.</para>
        /// </param>
        public static TextToSpeech LoadTextToSpeech(string onnxDir, out bool gpuActive, bool useGpu = false, int intraOpThreads = 0, int interOpThreads = 1, int directMLDevice = 0, Action<SessionOptions, int>? appendGpuProvider = null)
        {
            gpuActive = false;
            // When useGpu is true, append the DirectML execution provider for the device.
            // DirectML uses optimization passes specific to the GPU graph, so it cannot
            // share the CPU-optimized cache — we bypass the disk cache when on GPU.
            // Memory pattern + CPU mem arena are CPU-side optimizations; with DML they
            // either help less or interact poorly, so we disable them in that path.

            // Default ORT SessionOptions are conservative — basic graph opt, default threads,
            // memory patterns. For our streaming-TTS workload these defaults leave noticeable
            // CPU on the table. ORT_ENABLE_ALL fuses Conv+BN+ReLU and similar nodes; sequential
            // execution skips the inter-op parallel scheduler we don't need (one model at a
            // time). IntraOp / InterOp 0 = ORT auto-pick.
            //
            // First-run cost: graph optimization adds ~1-3s per model. We persist the optimized
            // graph alongside the original (in models\onnx-optimized\) so subsequent process
            // starts skip optimization entirely — load straight from the optimized .onnx.
            // Each saved graph is named for the exact model and runtime it came from
            // (OptimizedGraphCache), so a repaired or updated model is re-optimised
            // rather than served from the old graph.
            string optimizedDir = Path.GetFullPath(Path.Combine(onnxDir, "..", "onnx-optimized"));

            SessionOptions NewOptions() => new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = !useGpu,
                EnableCpuMemArena = !useGpu,
                InterOpNumThreads = Math.Max(0, interOpThreads),
                IntraOpNumThreads = Math.Max(0, intraOpThreads),
            };

            var cfgs = LoadCfgs(onnxDir);
            InferenceSession dpOrt, textEncOrt, vectorEstOrt, vocoderOrt;

            if (useGpu)
            {
                var opts = NewOptions();
#if ORT_DIRECTML
                // AppendExecutionProvider_DML takes priority — ops it can't run fall back to CPU.
                // Throws if the DirectML provider can't initialize (e.g. no DX12 GPU). We let
                // that bubble up so the caller can fall back to CPU + log the failure.
                try
                {
                    opts.AppendExecutionProvider_DML(directMLDevice);
                    gpuActive = true;
                    Console.WriteLine($"DirectML provider appended (device {directMLDevice})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DirectML init failed, falling back to CPU: {ex.Message}");
                    // Continue without DML; opts has only the default CPU provider.
                }
#else
                // Not DirectML: this build is the Linux GPU package, where the GPU is
                // CUDA. directMLDevice carries the device ordinal for whichever provider
                // the build has — the name is DirectML's because the Windows engine calls
                // it by name and renaming a parameter across a shipping platform to
                // improve a comment is not a trade worth making.
                //
                // DELIBERATELY NOT SWALLOWED, unlike the DML path above. A missing
                // provider library (the 330 MB pack that does not ship), a driver too
                // old, or no device at all all throw here, and the caller has to learn
                // that so it can fall back to CPU *and say which of those happened*. A
                // GPU path that dies quietly is worse than no GPU path: the symptom is a
                // hotkey that stopped working, which is the exact shape of the
                // audio-device loss that hid for five hours.
                //
                // appendGpuProvider is how the daemon says WHICH vendor's provider: a
                // delegate rather than a name, because this file is also compiled by
                // the spikes, which reference none of the daemon's types. Null keeps
                // the original behaviour, CUDA. The Windows build never reaches this
                // branch and ignores the parameter.
                if (appendGpuProvider is not null) appendGpuProvider(opts, directMLDevice);
                else opts.AppendExecutionProvider_CUDA(directMLDevice);
                Console.WriteLine($"GPU provider appended (device {directMLDevice})");
#endif
                (dpOrt, textEncOrt, vectorEstOrt, vocoderOrt) = LoadOnnxAll(onnxDir, opts);
            }
            else
            {
                try { Directory.CreateDirectory(optimizedDir); } catch { /* best-effort; Load copes */ }

                InferenceSession LoadCached(string name)
                {
                    var session = OptimizedGraphCache.Load(
                        Path.Combine(onnxDir, name + ".onnx"), optimizedDir, name, NewOptions, out _);
                    // The unkeyed graph earlier releases wrote under the plain name.
                    try { File.Delete(Path.Combine(optimizedDir, name + ".onnx")); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return session;
                }

                (dpOrt, textEncOrt, vectorEstOrt, vocoderOrt) = (
                    LoadCached(ModelNames[0]), LoadCached(ModelNames[1]),
                    LoadCached(ModelNames[2]), LoadCached(ModelNames[3]));
            }

            var textProcessor = LoadTextProcessor(onnxDir);

            return new TextToSpeech(cfgs, textProcessor, dpOrt, textEncOrt, vectorEstOrt, vocoderOrt);
        }

        // ============================================================================
        // WAV file writing
        // ============================================================================

        public static void WriteWavFile(string filename, float[] audioData, int sampleRate)
        {
            using var writer = new BinaryWriter(File.Open(filename, FileMode.Create));

            int numChannels = 1;
            int bitsPerSample = 16;
            int byteRate = sampleRate * numChannels * bitsPerSample / 8;
            short blockAlign = (short)(numChannels * bitsPerSample / 8);
            int dataSize = audioData.Length * bitsPerSample / 8;

            // RIFF header
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // fmt chunk size
            writer.Write((short)1); // audio format (PCM)
            writer.Write((short)numChannels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            // Write audio data
            foreach (var sample in audioData)
            {
                float clamped = Math.Max(-1.0f, Math.Min(1.0f, sample));
                short intSample = (short)(clamped * 32767);
                writer.Write(intSample);
            }
        }

        // ============================================================================
        // Tensor conversion utilities
        // ============================================================================

        public static DenseTensor<float> ArrayToTensor(float[][][] array, long[] dims)
        {
            var flat = new List<float>();
            foreach (var batch in array)
            {
                foreach (var row in batch)
                {
                    flat.AddRange(row);
                }
            }
            return new DenseTensor<float>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
        }

        public static DenseTensor<long> IntArrayToTensor(long[][] array, long[] dims)
        {
            var flat = new List<long>();
            foreach (var row in array)
            {
                flat.AddRange(row);
            }
            return new DenseTensor<long>(flat.ToArray(), dims.Select(x => (int)x).ToArray());
        }

        // ============================================================================
        // Timer utility
        // ============================================================================

        public static T Timer<T>(string name, Func<T> func)
        {
            var start = DateTime.Now;
            Console.WriteLine($"{name}...");
            var result = func();
            var elapsed = (DateTime.Now - start).TotalSeconds;
            Console.WriteLine($"  -> {name} completed in {elapsed:F2} sec");
            return result;
        }

        public static string SanitizeFilename(string text, int maxLen)
        {
            var result = new StringBuilder();
            int count = 0;
            foreach (char c in text)
            {
                if (count >= maxLen) break;
                if (char.IsLetterOrDigit(c))
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('_');
                }
                count++;
            }
            return result.ToString();
        }

        // ============================================================================
        // Chunk text
        // ============================================================================

        public static List<string> ChunkText(string text, int maxLen = 300)
        {
            var chunks = new List<string>();

            // Split by paragraph (two or more newlines)
            var paragraphRegex = new Regex(@"\n\s*\n+");
            var paragraphs = paragraphRegex.Split(text.Trim())
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            // Split by sentence boundaries, excluding abbreviations
            var sentenceRegex = new Regex(@"(?<!Mr\.|Mrs\.|Ms\.|Dr\.|Prof\.|Sr\.|Jr\.|Ph\.D\.|etc\.|e\.g\.|i\.e\.|vs\.|Inc\.|Ltd\.|Co\.|Corp\.|St\.|Ave\.|Blvd\.)(?<!\b[A-Z]\.)(?<=[.!?])\s+");

            foreach (var paragraph in paragraphs)
            {
                var sentences = sentenceRegex.Split(paragraph);
                string currentChunk = "";

                foreach (var sentence in sentences)
                {
                    if (string.IsNullOrEmpty(sentence)) continue;

                    if (currentChunk.Length + sentence.Length + 1 <= maxLen)
                    {
                        if (!string.IsNullOrEmpty(currentChunk))
                        {
                            currentChunk += " ";
                        }
                        currentChunk += sentence;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(currentChunk))
                        {
                            chunks.Add(currentChunk.Trim());
                        }
                        currentChunk = sentence;
                    }
                }

                if (!string.IsNullOrEmpty(currentChunk))
                {
                    chunks.Add(currentChunk.Trim());
                }
            }

            // If no chunks were created, return the original text
            if (chunks.Count == 0)
            {
                chunks.Add(text.Trim());
            }

            return chunks;
        }
    }
}

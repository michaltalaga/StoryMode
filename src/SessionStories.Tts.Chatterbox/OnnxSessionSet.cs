using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SessionStories.Tts.Chatterbox;

/// <summary>
/// Owns the four ONNX inference sessions of the Chatterbox pipeline. For the language model it
/// additionally exposes the OrtValue plumbing the autoregressive loop needs to keep the KV cache
/// resident on the execution device (CUDA when available, CPU otherwise) instead of round-tripping
/// it through managed tensors on every step.
/// </summary>
public sealed class OnnxSessionSet : IDisposable
{
    public const int LmLayerCount = 30;
    public const int KvHeadCount = 16;
    public const int HeadDim = 64;

    public InferenceSession SpeechEncoder { get; } = null!;
    public InferenceSession EmbedTokens { get; } = null!;
    public InferenceSession LanguageModel { get; } = null!;
    public InferenceSession ConditionalDecoder { get; } = null!;

    // The fp16 export has a MIXED interface: inputs_embeds and logits stay fp32 while the
    // KV-cache tensors are fp16 — so each tensor class carries its own flag.
    public bool LmEmbedsUseFloat16 { get; }
    public bool LmKvUsesFloat16 { get; }
    public bool LmLogitsUseFloat16 { get; }

    /// <summary>Whether the language model actually runs on the CUDA execution provider.</summary>
    public bool LmUsesCuda { get; }

    /// <summary>
    /// Memory location for LM KV-cache outputs: CUDA device memory when the LM runs on CUDA,
    /// CPU otherwise. Binding present.* outputs here keeps the cache off the PCIe bus.
    /// </summary>
    public OrtMemoryInfo LmKvMemoryInfo { get; }

    // Dispose only the mem-info we created; OrtMemoryInfo.DefaultInstance is a process singleton.
    private readonly OrtMemoryInfo? _ownedKvMemoryInfo;

    /// <summary>The LM's past_key_values.* input names in model declaration order.</summary>
    public IReadOnlyList<string> LmPastInputNames { get; }

    public OnnxSessionSet(ChatterboxOptions options, Action<string>? logWarning = null)
    {
        logWarning ??= message => Console.Error.WriteLine(message);
        var modelDir = options.ModelDir;

        string[] required = ["speech_encoder.onnx", "embed_tokens.onnx", options.LanguageModelFile, "conditional_decoder.onnx"];
        var missing = required.Where(f => !File.Exists(Path.Combine(modelDir, f))).ToArray();
        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                $"Chatterbox model files missing from '{modelDir}': {string.Join(", ", missing)}. " +
                "If the model download is still in progress, wait for it to complete and retry.");
        }

        try
        {
            if (!options.ForceCpu)
                PrependCudaDllDirToPath(options);

            // Speech encoder runs once per voice; CPU is deliberate and sufficient.
            SpeechEncoder = new InferenceSession(Path.Combine(modelDir, "speech_encoder.onnx"));
            EmbedTokens = CreateAccelerated(Path.Combine(modelDir, "embed_tokens.onnx"), options.ForceCpu, logWarning, out _);
            LanguageModel = CreateAccelerated(Path.Combine(modelDir, options.LanguageModelFile), options.ForceCpu, logWarning, out var lmUsesCuda);
            LmUsesCuda = lmUsesCuda;
            ConditionalDecoder = CreateAccelerated(Path.Combine(modelDir, "conditional_decoder.onnx"), options.ForceCpu, logWarning, out _);

            var lmInputs = LanguageModel.InputMetadata;
            if (!lmInputs.TryGetValue("inputs_embeds", out var embedsMeta))
                throw new InvalidOperationException("Language model has no 'inputs_embeds' input; wrong model file?");
            // ElementDataType is the version-stable way to detect fp16; the managed Type
            // mapping for float16 has changed across OnnxRuntime releases.
            LmEmbedsUseFloat16 = embedsMeta.ElementDataType == TensorElementType.Float16;
            LmPastInputNames = lmInputs.Keys
                .Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal))
                .ToArray();
            if (LmPastInputNames.Count != LmLayerCount * 2)
            {
                throw new InvalidOperationException(
                    $"Language model declares {LmPastInputNames.Count} past_key_values inputs, expected {LmLayerCount * 2}.");
            }
            LmKvUsesFloat16 = lmInputs[LmPastInputNames[0]].ElementDataType == TensorElementType.Float16;
            LmLogitsUseFloat16 = LanguageModel.OutputMetadata.TryGetValue("logits", out var logitsMeta)
                && logitsMeta.ElementDataType == TensorElementType.Float16;

            if (LmUsesCuda)
            {
                _ownedKvMemoryInfo = new OrtMemoryInfo(
                    OrtMemoryInfo.allocatorCUDA, OrtAllocatorType.DeviceAllocator, deviceId: 0, OrtMemType.Default);
                LmKvMemoryInfo = _ownedKvMemoryInfo;
            }
            else
            {
                LmKvMemoryInfo = OrtMemoryInfo.DefaultInstance;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes the CUDA/cuDNN runtime DLLs discoverable without requiring callers to prepare PATH.
    /// Uses <see cref="ChatterboxOptions.CudaDllDir"/> when set, otherwise the "cuda" directory
    /// next to the model directory (e.g. &lt;repo&gt;\models\cuda for &lt;repo&gt;\models\chatterbox).
    /// </summary>
    private static void PrependCudaDllDirToPath(ChatterboxOptions options)
    {
        var dir = options.CudaDllDir;
        if (string.IsNullOrEmpty(dir))
        {
            var modelsParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ModelDir)));
            if (modelsParent is null)
                return;
            dir = Path.Combine(modelsParent, "cuda");
        }
        if (!Directory.Exists(dir))
            return;

        var full = Path.GetFullPath(dir);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (path.Split(Path.PathSeparator).Contains(full, StringComparer.OrdinalIgnoreCase))
            return;
        Environment.SetEnvironmentVariable("PATH", full + Path.PathSeparator + path);
    }

    private static InferenceSession CreateAccelerated(string modelPath, bool forceCpu, Action<string> logWarning, out bool usedCuda)
    {
        if (!forceCpu)
        {
            try
            {
                using var sessionOptions = new SessionOptions();
                sessionOptions.AppendExecutionProvider_CUDA();
                var session = new InferenceSession(modelPath, sessionOptions);
                usedCuda = true;
                return session;
            }
            catch (Exception ex)
            {
                logWarning($"CUDA execution provider unavailable for '{Path.GetFileName(modelPath)}', falling back to CPU: {ex.Message}");
            }
        }

        usedCuda = false;
        return new InferenceSession(modelPath);
    }

    /// <summary>
    /// Wraps float data as the LM inputs_embeds value, converting to Float16 when the model requires it.
    /// The returned OrtValue pins the backing array: keep it alive until the run that consumed it completes.
    /// </summary>
    public OrtValue CreateLmEmbedsValue(float[] data, long[] shape)
    {
        if (!LmEmbedsUseFloat16)
            return OrtValue.CreateTensorValueFromMemory(data, shape);

        var half = new Float16[data.Length];
        for (int i = 0; i < data.Length; i++)
            half[i] = (Float16)data[i];
        return OrtValue.CreateTensorValueFromMemory(half, shape);
    }

    /// <summary>An empty [1, 16, 0, 64] KV value for the prefill step (zero bytes, so CPU placement is fine).</summary>
    public OrtValue CreateLmEmptyPastValue()
        => OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance,
            LmKvUsesFloat16 ? TensorElementType.Float16 : TensorElementType.Float,
            [1, KvHeadCount, 0, HeadDim]);

    /// <summary>Copies the last vocab row of a CPU-resident LM logits value (fp32 or fp16) into a float[].</summary>
    public float[] ExtractLastLogits(OrtValue logits)
    {
        var shape = logits.GetTensorTypeAndShape().Shape;
        int vocab = checked((int)shape[^1]);
        var row = new float[vocab];
        if (LmLogitsUseFloat16)
        {
            var span = logits.GetTensorDataAsSpan<Float16>();
            var src = span[^vocab..];
            for (int i = 0; i < vocab; i++)
                row[i] = (float)src[i];
        }
        else
        {
            logits.GetTensorDataAsSpan<float>()[^vocab..].CopyTo(row);
        }
        return row;
    }

    public static float[] ExtractFloats(NamedOnnxValue value, out int[] dims)
    {
        var tensor = value.AsTensor<float>();
        dims = tensor.Dimensions.ToArray();
        var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
        return dense.Buffer.ToArray();
    }

    public static long[] ExtractInt64(NamedOnnxValue value, out int[] dims)
    {
        var tensor = value.AsTensor<long>();
        dims = tensor.Dimensions.ToArray();
        var dense = tensor as DenseTensor<long> ?? tensor.ToDenseTensor();
        return dense.Buffer.ToArray();
    }

    public void Dispose()
    {
        SpeechEncoder?.Dispose();
        EmbedTokens?.Dispose();
        LanguageModel?.Dispose();
        ConditionalDecoder?.Dispose();
        _ownedKvMemoryInfo?.Dispose();
    }
}

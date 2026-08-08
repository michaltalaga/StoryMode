using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SessionStories.Tts.Chatterbox;

/// <summary>
/// Owns the four ONNX inference sessions of the Chatterbox pipeline and hides the
/// fp16-vs-fp32 storage type of the language model: all values crossing this boundary
/// are float[] regardless of the on-disk model precision.
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

    /// <summary>True when the LM expects Float16 for inputs_embeds and past_key_values.</summary>
    public bool LmUsesFloat16 { get; }

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
            // Speech encoder runs once per voice; CPU is deliberate and sufficient.
            SpeechEncoder = new InferenceSession(Path.Combine(modelDir, "speech_encoder.onnx"));
            EmbedTokens = CreateAccelerated(Path.Combine(modelDir, "embed_tokens.onnx"), options.ForceCpu, logWarning);
            LanguageModel = CreateAccelerated(Path.Combine(modelDir, options.LanguageModelFile), options.ForceCpu, logWarning);
            ConditionalDecoder = CreateAccelerated(Path.Combine(modelDir, "conditional_decoder.onnx"), options.ForceCpu, logWarning);

            var lmInputs = LanguageModel.InputMetadata;
            if (!lmInputs.TryGetValue("inputs_embeds", out var embedsMeta))
                throw new InvalidOperationException("Language model has no 'inputs_embeds' input; wrong model file?");
            LmUsesFloat16 = embedsMeta.ElementType == typeof(Float16);
            LmPastInputNames = lmInputs.Keys
                .Where(k => k.StartsWith("past_key_values.", StringComparison.Ordinal))
                .ToArray();
            if (LmPastInputNames.Count != LmLayerCount * 2)
            {
                throw new InvalidOperationException(
                    $"Language model declares {LmPastInputNames.Count} past_key_values inputs, expected {LmLayerCount * 2}.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static InferenceSession CreateAccelerated(string modelPath, bool forceCpu, Action<string> logWarning)
    {
        if (!forceCpu)
        {
            try
            {
                using var sessionOptions = new SessionOptions();
                sessionOptions.AppendExecutionProvider_CUDA();
                return new InferenceSession(modelPath, sessionOptions);
            }
            catch (Exception ex)
            {
                logWarning($"CUDA execution provider unavailable for '{Path.GetFileName(modelPath)}', falling back to CPU: {ex.Message}");
            }
        }

        return new InferenceSession(modelPath);
    }

    /// <summary>Wraps float data as an LM input tensor, converting to Float16 when the model requires it.</summary>
    public NamedOnnxValue CreateLmFloatInput(string name, float[] data, int[] dims)
    {
        if (!LmUsesFloat16)
            return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dims));

        var half = new Float16[data.Length];
        for (int i = 0; i < data.Length; i++)
            half[i] = (Float16)data[i];
        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(half, dims));
    }

    /// <summary>An empty [1, 16, 0, 64] KV tensor for the prefill step.</summary>
    public NamedOnnxValue CreateLmEmptyPast(string name)
    {
        int[] dims = [1, KvHeadCount, 0, HeadDim];
        return LmUsesFloat16
            ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<Float16>(dims))
            : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(dims));
    }

    /// <summary>
    /// Re-labels a present KV output as the matching past input without copying or converting.
    /// The wrapped value stays valid only until the run result it came from is disposed.
    /// </summary>
    public NamedOnnxValue PresentAsPast(string pastName, NamedOnnxValue present)
        => LmUsesFloat16
            ? NamedOnnxValue.CreateFromTensor(pastName, present.AsTensor<Float16>())
            : NamedOnnxValue.CreateFromTensor(pastName, present.AsTensor<float>());

    /// <summary>Copies an LM output (Float16 or float) into a float[] plus its dimensions.</summary>
    public float[] ExtractLmFloats(NamedOnnxValue value, out int[] dims)
    {
        if (!LmUsesFloat16)
            return ExtractFloats(value, out dims);

        var tensor = value.AsTensor<Float16>();
        dims = tensor.Dimensions.ToArray();
        var dense = tensor as DenseTensor<Float16> ?? tensor.ToDenseTensor();
        var span = dense.Buffer.Span;
        var result = new float[span.Length];
        for (int i = 0; i < span.Length; i++)
            result[i] = (float)span[i];
        return result;
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
    }
}

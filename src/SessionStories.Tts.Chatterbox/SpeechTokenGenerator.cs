using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SessionStories.Tts.Chatterbox;

/// <summary>Generated speech token ids without the START seed or STOP token, plus whether STOP was reached.</summary>
public sealed record SpeechTokenResult(long[] Tokens, bool SawStop);

/// <summary>
/// Autoregressive speech-token generation over the Chatterbox LM with KV caching,
/// mirroring the Python reference loop, plus optional classifier-free guidance.
/// </summary>
public static class SpeechTokenGenerator
{
    public const long StartSpeechToken = 6561;
    public const long StopSpeechToken = 6562;

    public static long[] Generate(
        OnnxSessionSet sessions,
        VoiceConditionals conditionals,
        long[] textInputIds,
        float exaggeration,
        float cfgWeight,
        SpeechTokenSampler sampler,
        int maxTokens,
        CancellationToken ct = default,
        Action<int>? onProgress = null)
        => GenerateDetailed(sessions, conditionals, textInputIds, exaggeration, cfgWeight, sampler, maxTokens, ct, onProgress).Tokens;

    public static SpeechTokenResult GenerateDetailed(
        OnnxSessionSet sessions,
        VoiceConditionals conditionals,
        long[] textInputIds,
        float exaggeration,
        float cfgWeight,
        SpeechTokenSampler sampler,
        int maxTokens,
        CancellationToken ct = default,
        Action<int>? onProgress = null)
    {
        // Reference formula: speech-token ids get position 0, text tokens get index-1 (first is -1).
        var positionIds = new long[textInputIds.Length];
        for (int i = 0; i < textInputIds.Length; i++)
            positionIds[i] = textInputIds[i] >= StartSpeechToken ? 0 : i - 1;

        var textEmbeds = RunEmbedTokens(sessions, textInputIds, positionIds, exaggeration, out var textDims);
        int hidden = textDims[2];
        int condLen = checked((int)conditionals.CondEmbDims[1]);
        int prefillLen = condLen + textDims[1];

        bool useCfg = cfgWeight > 0f;
        var condBranch = new Branch();
        var uncondBranch = useCfg ? new Branch() : null;

        var generated = new List<long>(maxTokens + 2) { StartSpeechToken };
        bool sawStop = false;

        try
        {
            var prefillMask = OnesMask(prefillLen);
            var condPrefill = sessions.CreateLmFloatInput(
                "inputs_embeds", Concat(conditionals.CondEmb, textEmbeds), [1, prefillLen, hidden]);
            RunLm(sessions, condBranch, condPrefill, prefillMask, EmptyPasts(sessions));

            if (uncondBranch is not null)
            {
                // Unconditional branch: same voice conditioning, text embeddings replaced with zeros.
                var uncondPrefill = sessions.CreateLmFloatInput(
                    "inputs_embeds", Concat(conditionals.CondEmb, new float[textEmbeds.Length]), [1, prefillLen, hidden]);
                RunLm(sessions, uncondBranch, uncondPrefill, prefillMask, EmptyPasts(sessions));
            }

            int maskLen = prefillLen;
            for (int step = 0; step < maxTokens; step++)
            {
                ct.ThrowIfCancellationRequested();

                var logits = condBranch.LastLogits;
                if (uncondBranch is not null)
                {
                    var uncond = uncondBranch.LastLogits;
                    for (int v = 0; v < logits.Length; v++)
                        logits[v] += cfgWeight * (logits[v] - uncond[v]);
                }

                long token = sampler.Sample(logits, generated);
                generated.Add(token);
                onProgress?.Invoke(step + 1);

                if (token == StopSpeechToken)
                {
                    sawStop = true;
                    break;
                }
                if (step == maxTokens - 1)
                    break;

                var stepEmbeds = RunEmbedTokens(sessions, [token], [step + 1], exaggeration, out _);
                var embeds = sessions.CreateLmFloatInput("inputs_embeds", stepEmbeds, [1, 1, hidden]);
                maskLen++;
                var mask = OnesMask(maskLen);

                // TODO: fold the two CFG branches into a single batch=2 LM run instead of two sequential runs.
                RunLm(sessions, condBranch, embeds, mask, PastsFromPresents(sessions, condBranch));
                if (uncondBranch is not null)
                    RunLm(sessions, uncondBranch, embeds, mask, PastsFromPresents(sessions, uncondBranch));
            }
        }
        finally
        {
            condBranch.Dispose();
            uncondBranch?.Dispose();
        }

        int count = generated.Count - 1 - (sawStop ? 1 : 0);
        var tokens = new long[count];
        generated.CopyTo(1, tokens, 0, count);
        return new SpeechTokenResult(tokens, sawStop);
    }

    private sealed class Branch : IDisposable
    {
        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? Results;
        public float[] LastLogits = [];

        public void Dispose()
        {
            Results?.Dispose();
            Results = null;
        }
    }

    private static void RunLm(
        OnnxSessionSet sessions,
        Branch branch,
        NamedOnnxValue embeds,
        NamedOnnxValue mask,
        List<NamedOnnxValue> pasts)
    {
        var inputs = new List<NamedOnnxValue>(pasts.Count + 2) { embeds, mask };
        inputs.AddRange(pasts);

        var results = sessions.LanguageModel.Run(inputs);
        var previous = branch.Results;
        branch.Results = results;

        DisposableNamedOnnxValue? logitsValue = null;
        foreach (var r in results)
        {
            if (r.Name == "logits")
            {
                logitsValue = r;
                break;
            }
        }
        if (logitsValue is null)
            throw new InvalidOperationException("Language model produced no 'logits' output.");

        var all = sessions.ExtractLmFloats(logitsValue, out var dims);
        int vocab = dims[^1];
        var last = new float[vocab];
        Array.Copy(all, all.Length - vocab, last, 0, vocab);
        branch.LastLogits = last;

        // The previous run's presents were consumed as this run's pasts; safe to release now.
        previous?.Dispose();
    }

    private static List<NamedOnnxValue> EmptyPasts(OnnxSessionSet sessions)
    {
        var pasts = new List<NamedOnnxValue>(sessions.LmPastInputNames.Count);
        foreach (var name in sessions.LmPastInputNames)
            pasts.Add(sessions.CreateLmEmptyPast(name));
        return pasts;
    }

    private static List<NamedOnnxValue> PastsFromPresents(OnnxSessionSet sessions, Branch branch)
    {
        var results = branch.Results
            ?? throw new InvalidOperationException("No language-model results to take the KV cache from.");

        var pasts = new List<NamedOnnxValue>(sessions.LmPastInputNames.Count);
        foreach (var r in results)
        {
            if (!r.Name.StartsWith("present.", StringComparison.Ordinal))
                continue;
            var pastName = "past_key_values." + r.Name["present.".Length..];
            pasts.Add(sessions.PresentAsPast(pastName, r));
        }

        if (pasts.Count != sessions.LmPastInputNames.Count)
        {
            throw new InvalidOperationException(
                $"Expected {sessions.LmPastInputNames.Count} present KV outputs, got {pasts.Count}.");
        }
        return pasts;
    }

    private static NamedOnnxValue OnesMask(int length)
    {
        var ones = new long[length];
        Array.Fill(ones, 1L);
        return NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(ones, new[] { 1, length }));
    }

    private static float[] RunEmbedTokens(
        OnnxSessionSet sessions,
        long[] inputIds,
        long[] positionIds,
        float exaggeration,
        out int[] dims)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, new[] { 1, inputIds.Length })),
            NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<long>(positionIds, new[] { 1, positionIds.Length })),
            NamedOnnxValue.CreateFromTensor("exaggeration", new DenseTensor<float>(new[] { exaggeration }, new[] { 1 })),
        };
        using var results = sessions.EmbedTokens.Run(inputs);
        return OnnxSessionSet.ExtractFloats(results.First(), out dims);
    }

    private static float[] Concat(float[] a, float[] b)
    {
        var result = new float[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

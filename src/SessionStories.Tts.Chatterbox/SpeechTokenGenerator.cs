using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SessionStories.Tts.Chatterbox;

/// <summary>Generated speech token ids without the START seed or STOP token, plus whether STOP was reached.</summary>
public sealed record SpeechTokenResult(long[] Tokens, bool SawStop);

/// <summary>
/// Autoregressive speech-token generation over the Chatterbox LM with KV caching,
/// mirroring the Python reference loop, plus optional classifier-free guidance.
/// Each CFG branch runs through an I/O binding that keeps its KV cache resident on the
/// LM's device — only the per-step logits row crosses back to the CPU.
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

        var generated = new List<long>(maxTokens + 2) { StartSpeechToken };
        bool sawStop = false;

        using var runOptions = new RunOptions();
        using var condBranch = new Branch(sessions);
        using var uncondBranch = useCfg ? new Branch(sessions) : null;

        var emptyPasts = CreateEmptyPasts(sessions);
        try
        {
            using var prefillMask = CreateMaskValue(prefillLen);
            using (var condPrefill = sessions.CreateLmEmbedsValue(
                Concat(conditionals.CondEmb, textEmbeds), [1, prefillLen, hidden]))
            {
                RunLm(sessions, runOptions, condBranch, condPrefill, prefillMask, emptyPasts);
            }

            if (uncondBranch is not null)
            {
                // Unconditional branch: same voice conditioning, text embeddings replaced with zeros.
                using var uncondPrefill = sessions.CreateLmEmbedsValue(
                    Concat(conditionals.CondEmb, new float[textEmbeds.Length]), [1, prefillLen, hidden]);
                RunLm(sessions, runOptions, uncondBranch, uncondPrefill, prefillMask, emptyPasts);
            }
        }
        finally
        {
            foreach (var (_, value) in emptyPasts)
                value.Dispose();
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
            maskLen++;
            // These OrtValues pin managed buffers; the using scope ends only after every run
            // that consumed them (both branches share the same embeds and mask).
            using var embeds = sessions.CreateLmEmbedsValue(stepEmbeds, [1, 1, hidden]);
            using var mask = CreateMaskValue(maskLen);

            // TODO: fold the two CFG branches into a single batch=2 LM run instead of two sequential runs.
            RunLm(sessions, runOptions, condBranch, embeds, mask, prefillPasts: null);
            if (uncondBranch is not null)
                RunLm(sessions, runOptions, uncondBranch, embeds, mask, prefillPasts: null);
        }

        int count = generated.Count - 1 - (sawStop ? 1 : 0);
        var tokens = new long[count];
        generated.CopyTo(1, tokens, 0, count);
        return new SpeechTokenResult(tokens, sawStop);
    }

    /// <summary>
    /// One CFG branch: an I/O binding whose present.* outputs are bound to the LM's device so the
    /// KV cache never crosses to the CPU, plus the previous run's presents (owned here; a present
    /// may only be released after the run that consumed it as a past has completed).
    /// </summary>
    private sealed class Branch : IDisposable
    {
        public readonly OrtIoBinding Binding;
        public OrtValue[]? Presents;
        public string[]? PastNames;
        public float[] LastLogits = [];

        public Branch(OnnxSessionSet sessions)
        {
            Binding = sessions.LanguageModel.CreateIoBinding();
        }

        /// <summary>
        /// Must run before every RunWithBinding: a completed run replaces each device-bound output
        /// entry with the concrete value it allocated, and the next run would otherwise treat that
        /// value as a pre-allocated output whose (KV seq) shape no longer matches.
        /// </summary>
        public void BindOutputsToDevice(OnnxSessionSet sessions)
        {
            foreach (var name in sessions.LanguageModel.OutputMetadata.Keys)
            {
                Binding.BindOutputToDevice(name,
                    name.StartsWith("present.", StringComparison.Ordinal)
                        ? sessions.LmKvMemoryInfo
                        : OrtMemoryInfo.DefaultInstance);
            }
        }

        public void Dispose()
        {
            if (Presents is not null)
            {
                foreach (var p in Presents)
                    p.Dispose();
                Presents = null;
            }
            Binding.Dispose();
        }
    }

    private static void RunLm(
        OnnxSessionSet sessions,
        RunOptions runOptions,
        Branch branch,
        OrtValue embeds,
        OrtValue mask,
        IReadOnlyList<(string Name, OrtValue Value)>? prefillPasts)
    {
        var binding = branch.Binding;
        branch.BindOutputsToDevice(sessions);
        binding.BindInput("inputs_embeds", embeds);
        binding.BindInput("attention_mask", mask);

        var previous = branch.Presents;
        if (previous is not null)
        {
            var pastNames = branch.PastNames!;
            for (int i = 0; i < previous.Length; i++)
                binding.BindInput(pastNames[i], previous[i]);
        }
        else if (prefillPasts is not null)
        {
            foreach (var (name, value) in prefillPasts)
                binding.BindInput(name, value);
        }
        else
        {
            throw new InvalidOperationException("Branch has no KV cache and no prefill pasts were supplied.");
        }

        sessions.LanguageModel.RunWithBinding(runOptions, binding);
        binding.SynchronizeBoundOutputs();

        // Take ownership of each output value individually. The wrapper collection is deliberately
        // not disposed: disposing it would also dispose the device-resident presents we keep across steps.
        var names = binding.GetOutputNames();
        var outputs = new OrtValue?[names.Length];
        int taken = 0;
        foreach (var value in binding.GetOutputValues())
        {
            if (taken == outputs.Length)
            {
                value.Dispose();
                continue;
            }
            outputs[taken++] = value;
        }

        int kvExpected = sessions.LmPastInputNames.Count;
        var presents = new OrtValue[kvExpected];
        // Output binding order is fixed at Branch construction, so past names are computed once.
        bool namesKnown = branch.PastNames is not null;
        var newPastNames = branch.PastNames ?? new string[kvExpected];
        int kv = 0;
        float[]? lastLogits = null;
        try
        {
            if (taken != names.Length)
                throw new InvalidOperationException($"Bound output count {taken} does not match name count {names.Length}.");

            for (int i = 0; i < names.Length; i++)
            {
                var name = names[i];
                if (name == "logits")
                {
                    using var logitsValue = outputs[i]!;
                    outputs[i] = null;
                    lastLogits = sessions.ExtractLastLogits(logitsValue);
                }
                else if (name.StartsWith("present.", StringComparison.Ordinal))
                {
                    if (kv == kvExpected)
                        throw new InvalidOperationException($"More than {kvExpected} present KV outputs.");
                    if (!namesKnown)
                        newPastNames[kv] = "past_key_values." + name["present.".Length..];
                    presents[kv++] = outputs[i]!;
                    outputs[i] = null;
                }
            }

            if (lastLogits is null)
                throw new InvalidOperationException("Language model produced no 'logits' output.");
            if (kv != kvExpected)
                throw new InvalidOperationException($"Expected {kvExpected} present KV outputs, got {kv}.");

            // Any output that is neither logits nor a present is unused.
            foreach (var leftover in outputs)
                leftover?.Dispose();
        }
        catch
        {
            foreach (var leftover in outputs)
                leftover?.Dispose();
            for (int i = 0; i < kv; i++)
                presents[i].Dispose();
            throw;
        }

        branch.Presents = presents;
        branch.PastNames = newPastNames;
        branch.LastLogits = lastLogits;

        // The previous run's presents were consumed as this run's pasts; safe to release only now.
        if (previous is not null)
        {
            foreach (var p in previous)
                p.Dispose();
        }
    }

    private static List<(string Name, OrtValue Value)> CreateEmptyPasts(OnnxSessionSet sessions)
    {
        var pasts = new List<(string, OrtValue)>(sessions.LmPastInputNames.Count);
        foreach (var name in sessions.LmPastInputNames)
            pasts.Add((name, sessions.CreateLmEmptyPastValue()));
        return pasts;
    }

    private static OrtValue CreateMaskValue(int length)
    {
        var ones = new long[length];
        Array.Fill(ones, 1L);
        return OrtValue.CreateTensorValueFromMemory(ones, [1L, length]);
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

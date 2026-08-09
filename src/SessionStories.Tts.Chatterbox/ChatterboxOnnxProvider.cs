using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SessionStories.Core.Tts;

namespace SessionStories.Tts.Chatterbox;

public sealed class ChatterboxOnnxProvider : ITtsProvider, IDisposable
{
    // zh/ja/he/ko are excluded: they require text normalizers that are out of scope for M1.
    public static readonly IReadOnlyList<string> SupportedLanguages =
    [
        "ar", "da", "de", "el", "en", "es", "fi", "fr", "hi", "it",
        "ms", "nl", "no", "pl", "pt", "ru", "sv", "sw", "tr",
    ];

    private readonly ChatterboxOptions _options;
    private readonly VoiceConditionalsCache _voiceCache;
    private readonly object _gate = new();
    private OnnxSessionSet? _sessions;
    private ChatterboxTokenizer? _tokenizer;
    private bool _disposed;

    public ChatterboxOnnxProvider(ChatterboxOptions options)
    {
        _options = options;
        _voiceCache = new VoiceConditionalsCache(options.VoiceCacheDir);
        Capabilities = DescribeCapabilities(options);
    }

    /// <summary>
    /// Capabilities without an instance — callers that only need the metadata (e.g. GET /api/voices)
    /// must not construct a provider, because that would eventually load ONNX sessions into VRAM.
    /// </summary>
    public static TtsCapabilities DescribeCapabilities(ChatterboxOptions options) => new(
        SupportedLanguages,
        [
            new TtsKnob("exaggeration", 0, 1, options.Exaggeration),
            new TtsKnob("cfg", 0, 1, options.CfgWeight),
            new TtsKnob("temperature", 0, 2, options.Temperature),
        ],
        OutputSampleRate: 44100,
        AppliesWatermark: false,
        SupportsVoiceCloning: true,
        StylePresets: StylePresets);

    /// <summary>
    /// Delivery settings as knob pairs. Higher exaggeration pushes emphasis and pace; lower cfg lets
    /// the clone drift further from a flat read, so the two move in opposite directions.
    /// </summary>
    private static readonly IReadOnlyList<TtsStylePreset> StylePresets =
    [
        new(VoiceStyle.Calm, new Dictionary<string, double> { ["exaggeration"] = 0.35, ["cfg"] = 0.5 }),
        new(VoiceStyle.Natural, new Dictionary<string, double> { ["exaggeration"] = 0.65, ["cfg"] = 0.3 }),
        new(VoiceStyle.Lively, new Dictionary<string, double> { ["exaggeration"] = 0.9, ["cfg"] = 0.2 }),
    ];

    public string Id => "chatterbox-onnx";

    public TtsCapabilities Capabilities { get; }

    /// <summary>When set, the synthesized audio is additionally written as a wav next to the mp3.</summary>
    public string? AlsoWriteWavPath { get; set; }

    public Task PrepareVoiceAsync(string referenceWavPath, string voiceId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var sessions = GetSessions();
            _voiceCache.Create(voiceId, referenceWavPath, mono24k => ComputeConditionals(sessions, mono24k));
        }, ct);

    public Task SynthesizeAsync(TtsRequest request, IProgress<TtsProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => SynthesizeCore(request, progress, ct), ct);

    private void SynthesizeCore(TtsRequest request, IProgress<TtsProgress>? progress, CancellationToken ct)
    {
        var language = request.Language.ToLowerInvariant();
        if (!SupportedLanguages.Contains(language))
        {
            throw new NotSupportedException(
                $"Language '{request.Language}' is not supported by provider '{Id}'. " +
                $"Supported: {string.Join(", ", SupportedLanguages)}.");
        }

        var sessions = GetSessions();
        var conditionals = ResolveConditionals(request.VoiceId, sessions);
        var tokenizer = GetTokenizer();

        double exaggeration = Knob(request, "exaggeration", _options.Exaggeration);
        double cfg = Knob(request, "cfg", _options.CfgWeight);
        double temperature = Knob(request, "temperature", _options.Temperature);

        var chunks = SentenceChunker.Chunk(request.Text);
        var audio = new List<ChunkAudio>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TtsProgress(i, chunks.Count, $"Generating chunk {i + 1}/{chunks.Count}"));

            var inputIds = tokenizer.Encode($"[{language}]{chunks[i].Text}");
            int seed = request.Seed.HasValue ? request.Seed.Value + i : Random.Shared.Next();
            var sampler = new SpeechTokenSampler(temperature, _options.MinP, _options.TopP, _options.RepetitionPenalty, seed);

            var tokens = SpeechTokenGenerator.Generate(
                sessions, conditionals, inputIds, (float)exaggeration, (float)cfg, sampler,
                _options.MaxTokensPerChunk, ct);

            var samples = DecodeToWaveform(sessions, conditionals, tokens);
            audio.Add(new ChunkAudio(samples, chunks[i].StartsParagraph));
        }

        AudioPostProcessor.EncodeMp3(audio, request.OutputMp3Path);
        if (AlsoWriteWavPath is { Length: > 0 } wavPath)
            AudioPostProcessor.WriteWav(audio, wavPath);

        progress?.Report(new TtsProgress(chunks.Count, chunks.Count, "Encoding complete"));
    }

    /// <summary>Runs the speech encoder once over a mono 24 kHz reference waveform.</summary>
    public static VoiceConditionals ComputeConditionals(OnnxSessionSet sessions, float[] mono24k)
    {
        var input = NamedOnnxValue.CreateFromTensor(
            "audio_values", new DenseTensor<float>(mono24k, new[] { 1, mono24k.Length }));
        using var results = sessions.SpeechEncoder.Run(new[] { input });

        // Output order per the reference: cond_emb, prompt_token, ref_x_vector, prompt_feat.
        var outputs = results.ToList();
        var condEmb = OnnxSessionSet.ExtractFloats(outputs[0], out var condDims);
        var promptToken = OnnxSessionSet.ExtractInt64(outputs[1], out var promptTokenDims);
        var refXVector = OnnxSessionSet.ExtractFloats(outputs[2], out var refXDims);
        var promptFeat = OnnxSessionSet.ExtractFloats(outputs[3], out var promptFeatDims);

        return new VoiceConditionals(
            condEmb, ToLongDims(condDims),
            promptToken, ToLongDims(promptTokenDims),
            refXVector, ToLongDims(refXDims),
            promptFeat, ToLongDims(promptFeatDims));
    }

    /// <summary>Decodes prompt tokens + generated speech tokens to a mono 24 kHz waveform.</summary>
    public static float[] DecodeToWaveform(OnnxSessionSet sessions, VoiceConditionals conditionals, long[] speechTokens)
    {
        var all = new long[conditionals.PromptToken.Length + speechTokens.Length];
        conditionals.PromptToken.CopyTo(all, 0);
        speechTokens.CopyTo(all, conditionals.PromptToken.Length);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("speech_tokens", new DenseTensor<long>(all, new[] { 1, all.Length })),
            NamedOnnxValue.CreateFromTensor("speaker_embeddings",
                new DenseTensor<float>(conditionals.RefXVector, ToIntDims(conditionals.RefXVectorDims))),
            NamedOnnxValue.CreateFromTensor("speaker_features",
                new DenseTensor<float>(conditionals.PromptFeat, ToIntDims(conditionals.PromptFeatDims))),
        };
        using var results = sessions.ConditionalDecoder.Run(inputs);
        return OnnxSessionSet.ExtractFloats(results.First(), out _);
    }

    private VoiceConditionals ResolveConditionals(string voiceId, OnnxSessionSet sessions)
    {
        if (_voiceCache.TryLoad(voiceId, out var cached))
            return cached;

        if (File.Exists(voiceId) && Path.GetExtension(voiceId).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            var pathHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(voiceId).ToLowerInvariant())));
            var derivedId = $"adhoc-{pathHash[..16].ToLowerInvariant()}";
            return _voiceCache.Create(derivedId, voiceId, mono24k => ComputeConditionals(sessions, mono24k));
        }

        throw new InvalidOperationException(
            $"Voice '{voiceId}' is not in the voice cache and is not a path to an existing .wav file. " +
            "Prepare it first with PrepareVoiceAsync or pass a reference wav path.");
    }

    private OnnxSessionSet GetSessions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessions ??= new OnnxSessionSet(_options);
        }
    }

    private ChatterboxTokenizer GetTokenizer()
    {
        lock (_gate)
        {
            return _tokenizer ??= ChatterboxTokenizer.Load(Path.Combine(_options.ModelDir, "tokenizer.json"));
        }
    }

    private static double Knob(TtsRequest request, string name, double fallback)
        => request.Knobs is not null && request.Knobs.TryGetValue(name, out var value) ? value : fallback;

    private static long[] ToLongDims(int[] dims) => Array.ConvertAll(dims, d => (long)d);

    private static int[] ToIntDims(long[] dims) => Array.ConvertAll(dims, d => checked((int)d));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _sessions?.Dispose();
            _sessions = null;
        }
    }
}

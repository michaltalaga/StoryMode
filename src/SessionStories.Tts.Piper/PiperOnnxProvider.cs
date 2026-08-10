using System.Text.RegularExpressions;
using SessionStories.Core.Tts;
using SessionStories.Tts.Chatterbox;
using SherpaOnnx;

namespace SessionStories.Tts.Piper;

/// <summary>
/// Piper VITS voices via sherpa-onnx: fixed trained voices with native espeak-ng
/// phonemization (no cloning, no reference wav). CPU inference — fast enough that the
/// model is loaded per synthesis call and disposed after, mirroring the GPU-residency
/// lifecycle of the other providers.
/// </summary>
public sealed class PiperOnnxProvider(PiperOptions options) : ITtsProvider
{
    // Bundle folder names carry a locale segment, e.g. "vits-piper-pl_PL-gosia-medium" → "pl".
    private static readonly Regex BundleLocale = new(@"\b([a-z]{2,3})_[A-Z]{2}\b", RegexOptions.Compiled);

    // Coqui bundles name the language on its own instead: "vits-coqui-pl-mai_female" → "pl".
    private static readonly Regex CoquiLocale = new(@"^vits-coqui-([a-z]{2,3})-", RegexOptions.Compiled);

    public string Id => "piper-onnx";

    public TtsCapabilities Capabilities { get; } = DescribeCapabilities(options);

    /// <summary>
    /// Capabilities without an instance — callers that only need the metadata (e.g. GET /api/voices)
    /// have no reason to build a provider just to read a constant.
    /// </summary>
    public static TtsCapabilities DescribeCapabilities(PiperOptions options) => new(
        // What piper can speak here is exactly what has been installed: one bundle per trained voice.
        Languages: [.. InstalledBundles(options)
            .Select(LanguageFromBundle)
            .OfType<string>()
            .Distinct()
            .Order()],
        Knobs: [new TtsKnob("speed", 0.5, 2.0, 1.0)],
        OutputSampleRate: 44100,
        AppliesWatermark: false,
        // Fixed trained VITS models — PrepareVoiceAsync is a no-op and a reference wav is ignored.
        SupportsVoiceCloning: false,
        StylePresets: StylePresets);

    /// <summary>
    /// Piper has no expressiveness control at all — only speed. The same three names therefore mean
    /// something much narrower here than for a cloning engine, which is precisely why the numbers
    /// stay behind the preset: the reader picks a delivery, not an engine parameter.
    /// </summary>
    private static readonly IReadOnlyList<TtsStylePreset> StylePresets =
    [
        new(VoiceStyle.Calm, new Dictionary<string, double> { ["speed"] = 0.92 }),
        new(VoiceStyle.Natural, new Dictionary<string, double> { ["speed"] = 1.0 }),
        new(VoiceStyle.Lively, new Dictionary<string, double> { ["speed"] = 1.08 }),
    ];

    private static IEnumerable<string> InstalledBundles(PiperOptions options)
        => Directory.Exists(options.ModelsRoot)
            ? Directory.EnumerateDirectories(options.ModelsRoot).Select(Path.GetFileName).OfType<string>()
            : [];

    /// <summary>Piper voices are fixed trained models — there is nothing to prepare or cache.</summary>
    public Task PrepareVoiceAsync(string referenceWavPath, string voiceId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SynthesizeAsync(TtsRequest request, IProgress<TtsProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => SynthesizeCore(request, progress, ct), ct);

    private void SynthesizeCore(TtsRequest request, IProgress<TtsProgress>? progress, CancellationToken ct)
    {
        var bundleDir = ResolveBundleDir(request, out var bundleName);

        var language = request.Language.ToLowerInvariant();
        if (LanguageFromBundle(bundleName) is { } bundleLanguage && bundleLanguage != language)
        {
            throw new NotSupportedException(
                $"Language '{request.Language}' is not supported by voice '{request.VoiceId}' " +
                $"(bundle '{bundleName}' is '{bundleLanguage}').");
        }

        double speed = Knob(request, "speed", 1.0);

        var chunks = SentenceChunker.Chunk(request.Text);
        if (chunks.Count == 0)
            throw new InvalidOperationException("No text to synthesize.");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = SingleOnnxFile(bundleDir, bundleName);
        config.Model.Vits.Tokens = RequiredFile(bundleDir, "tokens.txt", bundleName);
        // Phonemiser data is optional: piper bundles carry espeak-ng-data and pronounce via
        // phonemes, while coqui bundles are character-based and ship none. Requiring it would
        // reject a whole family of voices for having a different (not worse) text frontend.
        var espeak = Path.Combine(bundleDir, "espeak-ng-data");
        if (Directory.Exists(espeak))
            config.Model.Vits.DataDir = espeak;
        // NoiseScale/NoiseScaleW/LengthScale keep the sherpa defaults (0.667/0.8/1.0), which
        // match the values piper trained with; "speed" is passed per Generate call instead.
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8);

        // Created per call, disposed after — same lifecycle as the GPU providers.
        using var tts = new OfflineTts(config);
        int nativeSampleRate = tts.SampleRate;

        var audio = new List<ChunkAudio>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new TtsProgress(i, chunks.Count, $"Generating chunk {i + 1}/{chunks.Count}"));

            // OfflineTtsGeneratedAudio has Dispose() but does not implement IDisposable.
            var generated = tts.Generate(chunks[i].Text, (float)speed, speakerId: 0);
            try
            {
                nativeSampleRate = generated.SampleRate; // 22050 for medium bundles — read, never hardcoded
                audio.Add(new ChunkAudio(generated.Samples, chunks[i].StartsParagraph));
            }
            finally
            {
                generated.Dispose();
            }
        }

        AudioPostProcessor.EncodeMp3(audio, request.OutputMp3Path,
            new AudioPostOptions { InputSampleRate = nativeSampleRate });

        progress?.Report(new TtsProgress(chunks.Count, chunks.Count, "Encoding complete"));
    }

    private string ResolveBundleDir(TtsRequest request, out string bundleName)
    {
        // Written by PiperVoiceInstaller at install time and carried on the voice itself.
        if (request.EngineData?.GetValueOrDefault("bundle") is not { Length: > 0 } name)
        {
            throw new InvalidOperationException(
                $"Voice '{request.VoiceId}' has no piper bundle recorded. It predates installable " +
                "voices — remove it in Settings and add it again from the voice list.");
        }
        // A bundle name is a folder under the models root; it may not point anywhere else.
        if (Path.GetFileName(name) != name)
            throw new InvalidOperationException($"Invalid piper bundle name '{name}' on voice '{request.VoiceId}'.");

        bundleName = name;
        var dir = Path.Combine(options.ModelsRoot, name);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(
                $"Piper bundle '{name}' for voice '{request.VoiceId}' is not on disk at '{dir}'. " +
                "Remove the voice in Settings and add it again to re-download it.");
        }
        return dir;
    }

    private static string SingleOnnxFile(string bundleDir, string bundleName)
    {
        var candidates = Directory.EnumerateFiles(bundleDir, "*.onnx").ToList();
        return candidates.Count == 1
            ? candidates[0]
            : throw new InvalidOperationException(
                $"Expected exactly one .onnx model in piper bundle '{bundleName}', found {candidates.Count}.");
    }

    private static string RequiredFile(string bundleDir, string name, string bundleName)
    {
        var path = Path.Combine(bundleDir, name);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Piper bundle '{bundleName}' is missing '{name}'.", path);
    }

    private static string? LanguageFromBundle(string bundleName)
        => BundleLocale.Match(bundleName) is { Success: true } locale ? locale.Groups[1].Value
            : CoquiLocale.Match(bundleName) is { Success: true } coqui ? coqui.Groups[1].Value
            : null;

    private static double Knob(TtsRequest request, string name, double fallback)
        => request.Knobs is not null && request.Knobs.TryGetValue(name, out var value) ? value : fallback;
}

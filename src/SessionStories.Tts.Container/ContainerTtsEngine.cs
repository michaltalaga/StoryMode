using SessionStories.Core.Tts;

namespace SessionStories.Tts.Container;

/// <summary>
/// A speech engine that runs in a container and answers three endpoints: <c>/health</c>,
/// <c>/prepare</c> and <c>/synthesize</c>. One contract, many images — which is what lets a model
/// with no usable ONNX export join the app without another hand-written port.
/// </summary>
/// <param name="LicenceNote">
/// Shown instead of starting the container when <paramref name="Accepted"/> is false. Every model
/// worth having here so far ships under terms someone has to agree to, and agreeing is the
/// operator's decision, not this code's.
/// </param>
/// <param name="Clones">
/// False for engines whose voices are fixed and built in. Not every container engine clones —
/// Qwen's Polish fine-tune ships one trained speaker and discards its speaker encoder entirely —
/// and claiming otherwise would offer a reference recording that is silently ignored.
/// </param>
/// <param name="ModelsRoot">
/// Host directory holding this engine's weights, mounted at <c>/models</c>. One per engine: they
/// are several gigabytes each, download on first use, and pointing two engines at one directory
/// makes each look at the other's half-finished cache.
/// </param>
public sealed record ContainerTtsEngine(
    string Id,
    string Image,
    string ContainerName,
    int Port,
    string ModelsRoot,
    IReadOnlyList<string> Languages,
    IReadOnlyList<TtsStylePreset> StylePresets,
    string LicenceNote,
    bool Accepted,
    bool Clones = true)
{
    /// <summary>Extra `docker run -e` values, for images that need one (licence acknowledgements).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How long to wait for the container to answer /health. Big models load slowly.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much text goes into one generation. The defaults suit engines that lose the thread over
    /// a long passage, which is most of them. VibeVoice is the opposite case — it is built to hold
    /// ninety minutes in a single pass — and feeding it a sentence at a time would throw away the
    /// one thing it does better than everything else here.
    /// </summary>
    public int MinChunkCharacters { get; init; } = 150;

    public int MaxChunkCharacters { get; init; } = 350;
}

/// <summary>The library directories every container sees, whichever engine it runs.</summary>
/// <param name="VoicesRoot">Reference recordings, mounted read-only — a container never writes to the library.</param>
public sealed record ContainerTtsMounts(string VoicesRoot);

/// <summary>The delivery presets every container engine offers, over knobs they all understand.</summary>
public static class ContainerStylePresets
{
    public static readonly IReadOnlyList<TtsStylePreset> Default =
    [
        new(VoiceStyle.Calm, new Dictionary<string, double> { ["temperature"] = 0.5, ["speed"] = 0.95 }),
        new(VoiceStyle.Natural, new Dictionary<string, double> { ["temperature"] = 0.65, ["speed"] = 1.0 }),
        new(VoiceStyle.Lively, new Dictionary<string, double> { ["temperature"] = 0.85, ["speed"] = 1.05 }),
    ];
}

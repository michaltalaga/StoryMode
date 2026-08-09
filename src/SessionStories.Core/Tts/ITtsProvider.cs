namespace SessionStories.Core.Tts;

/// <summary>
/// A pluggable text-to-speech engine. Capability metadata drives voice/language resolution;
/// nothing outside a provider may assume a specific language or engine.
/// </summary>
public interface ITtsProvider
{
    string Id { get; }

    TtsCapabilities Capabilities { get; }

    /// <summary>
    /// Computes and caches voice conditioning from a reference wav so that every future
    /// render of <paramref name="voiceId"/> uses byte-identical conditioning.
    /// </summary>
    Task PrepareVoiceAsync(string referenceWavPath, string voiceId, CancellationToken ct = default);

    /// <summary>Renders <see cref="TtsRequest.Text"/> to an mp3 at <see cref="TtsRequest.OutputMp3Path"/>.</summary>
    Task SynthesizeAsync(TtsRequest request, IProgress<TtsProgress>? progress = null, CancellationToken ct = default);
}

/// <param name="SupportsVoiceCloning">
/// True when the provider can synthesise an arbitrary voice from a reference wav via
/// <see cref="ITtsProvider.PrepareVoiceAsync"/>. False for engines whose voices are fixed
/// trained models — for those, <see cref="ITtsProvider.PrepareVoiceAsync"/> is a no-op and
/// a reference wav is ignored entirely.
/// </param>
/// <param name="StylePresets">
/// The delivery settings this engine offers, in the order they should be shown. Every engine
/// declares the same preset ids (see <see cref="VoiceStyle"/>) over whatever knobs it actually
/// has, so a reader picks "calm" without learning what an engine means by it.
/// </param>
public sealed record TtsCapabilities(
    IReadOnlyList<string> Languages,
    IReadOnlyList<TtsKnob> Knobs,
    int OutputSampleRate,
    bool AppliesWatermark,
    bool SupportsVoiceCloning,
    IReadOnlyList<TtsStylePreset> StylePresets);

/// <summary>A tunable named parameter, e.g. exaggeration or cfg.</summary>
public sealed record TtsKnob(string Name, double Min, double Max, double Default);

/// <summary>
/// A named delivery setting: the knob values one engine uses for a given <see cref="VoiceStyle"/>.
/// The numbers never leave the backend — the UI renders the id and nothing else, which is what
/// lets two engines with unrelated knobs ("exaggeration"+"cfg" vs "speed") share one control.
/// </summary>
public sealed record TtsStylePreset(string Id, IReadOnlyDictionary<string, double> Knobs);

/// <summary>The preset ids every engine must declare. A voice stores one of these, not raw knobs.</summary>
public static class VoiceStyle
{
    public const string Calm = "calm";
    public const string Natural = "natural";
    public const string Lively = "lively";

    public const string Default = Natural;

    public static readonly IReadOnlyList<string> All = [Calm, Natural, Lively];

    /// <summary>Falls back to <see cref="Default"/> for anything unrecognised (hand-edited catalogs).</summary>
    public static string Normalize(string? style)
        => style is not null && All.Contains(style, StringComparer.OrdinalIgnoreCase)
            ? style.ToLowerInvariant()
            : Default;
}

/// <param name="EngineData">
/// The voice's engine-private settings, straight from its catalog entry — e.g. the piper bundle
/// folder, or a cloning engine's reference wav. Opaque everywhere except inside the engine that
/// wrote it; this is what lets a voice be installed from the UI instead of configured by hand.
/// </param>
public sealed record TtsRequest(
    string Text,
    string Language,
    string VoiceId,
    string OutputMp3Path,
    IReadOnlyDictionary<string, double>? Knobs = null,
    int? Seed = null,
    IReadOnlyDictionary<string, string>? EngineData = null);

public sealed record TtsProgress(int ChunkIndex, int ChunkCount, string Message);

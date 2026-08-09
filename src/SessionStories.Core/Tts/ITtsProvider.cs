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
public sealed record TtsCapabilities(
    IReadOnlyList<string> Languages,
    IReadOnlyList<TtsKnob> Knobs,
    int OutputSampleRate,
    bool AppliesWatermark,
    bool SupportsVoiceCloning);

/// <summary>A tunable named parameter, e.g. exaggeration or cfg.</summary>
public sealed record TtsKnob(string Name, double Min, double Max, double Default);

public sealed record TtsRequest(
    string Text,
    string Language,
    string VoiceId,
    string OutputMp3Path,
    IReadOnlyDictionary<string, double>? Knobs = null,
    int? Seed = null);

public sealed record TtsProgress(int ChunkIndex, int ChunkCount, string Message);

namespace SessionStories.Core.Stt;

/// <summary>
/// A pluggable speech-to-text engine. Output is the raw transcript — implementations must
/// never clean, reorder, or otherwise normalize what was said; messiness is the input.
/// </summary>
public interface ISttProvider
{
    string Id { get; }

    SttCapabilities Capabilities { get; }

    /// <summary>Transcribes an audio file and returns the raw transcript text.</summary>
    Task<string> TranscribeAsync(string audioPath, string? languageHint = null, CancellationToken ct = default);
}

public sealed record SttCapabilities(IReadOnlyList<string> Languages);

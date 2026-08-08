namespace SessionStories.Core.Universes;

using SessionStories.Core.Stories;

/// <summary>File-system access to universe packs. Same atomicity and ETag rules as <see cref="IStoryStore"/>.</summary>
public interface IUniverseStore
{
    string UniversesRoot { get; }

    IReadOnlyList<string> ListUniverses();

    /// <summary>Allowed names: constraints.md, bible.md, characters.md, voices.json, tones/&lt;tone&gt;.md.</summary>
    FileContent? ReadFile(string universeId, string relativePath);
    void WriteFile(string universeId, string relativePath, string text, string? expectedETag = null);

    /// <summary>Appends approved facts to bible.md under a dated heading, atomically.</summary>
    void AppendBibleFacts(string universeId, string heading, IReadOnlyList<string> factLines);

    /// <summary>Parsed voices.json catalog; null when absent.</summary>
    VoiceCatalog? ReadVoices(string universeId);
}

public sealed record VoiceCatalog(IReadOnlyDictionary<string, VoiceEntry> Voices, string? Default);

public sealed record VoiceEntry(
    string Provider,
    IReadOnlyList<string> Languages,
    string ReferenceWav,
    double? Exaggeration,
    double? Cfg);

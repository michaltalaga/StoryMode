namespace SessionStories.Core.Voices;

using SessionStories.Core.Stories;

/// <summary>
/// The voice catalog is a machine/engine concern, not a story-world one: one global
/// <c>library/voices.json</c> plus its reference wavs, shared by every universe. Same
/// atomicity and ETag rules as <see cref="IStoryStore"/>.
/// </summary>
public interface IVoiceStore
{
    /// <summary>Directory holding the reference wavs (library/voices).</summary>
    string VoicesRoot { get; }

    /// <summary>Parsed catalog; null when library/voices.json is absent or unparsable.</summary>
    VoiceCatalog? ReadCatalog();

    /// <summary>Raw voices.json for the editor round-trip; null when absent.</summary>
    FileContent? ReadCatalogFile();

    /// <summary>Atomic write. Non-null <paramref name="expectedETag"/> enforces optimistic concurrency.</summary>
    void WriteCatalogFile(string text, string? expectedETag = null);

    /// <summary>Absolute path of the entry's referenceWav under <see cref="VoicesRoot"/>; null when unset or missing on disk.</summary>
    string? ResolveReferenceWav(string voiceId);

    /// <summary>library/voice-previews/&lt;voiceId&gt;.mp3 — a path, which may not exist yet.</summary>
    string PreviewPath(string voiceId);

    /// <summary>library/voice-cache/&lt;voiceId&gt; — conditionals derived from the reference wav; may not exist.</summary>
    string VoiceCachePath(string voiceId);

    /// <summary>
    /// Creates the entry or overwrites its known fields (a null knob removes it). Returns true when
    /// the voice did not exist before. <c>referenceWav</c> and any unknown JSON on the entry survive.
    /// </summary>
    bool WriteVoice(string voiceId, VoiceEntryEdit edit);

    /// <summary>Applies only the non-null fields of <paramref name="edit"/>; false when the entry is absent.</summary>
    bool PatchVoice(string voiceId, VoiceEntryEdit edit);

    /// <summary>
    /// Removes the entry, its rendered preview and its conditionals cache, clearing the catalog
    /// default when it pointed here. The shared reference wav stays unless
    /// <paramref name="deleteReferenceWav"/> says otherwise. False when the entry is absent.
    /// </summary>
    bool DeleteVoice(string voiceId, bool deleteReferenceWav = false);

    /// <summary>Points the catalog default at the voice; false when it is not in the catalog.</summary>
    bool SetDefaultVoice(string voiceId);

    /// <summary>
    /// Writes library/voices/&lt;voiceId&gt;.wav from the stream, points the entry at it and drops the
    /// derived artifacts (preview + conditionals cache), which would otherwise keep the old voice
    /// alive. False when the entry is absent.
    /// </summary>
    bool SaveReferenceWav(string voiceId, Stream wav);
}

public sealed record VoiceCatalog(IReadOnlyDictionary<string, VoiceEntry> Voices, string? Default);

/// <summary>ReferenceWav is a bare file name relative to <see cref="IVoiceStore.VoicesRoot"/>.</summary>
public sealed record VoiceEntry(
    string Provider,
    IReadOnlyList<string> Languages,
    string ReferenceWav,
    double? Exaggeration,
    double? Cfg);

/// <summary>
/// Fields to write onto a catalog entry. The meaning of null differs per operation:
/// <see cref="IVoiceStore.WriteVoice"/> writes all four (null knob = remove the property),
/// <see cref="IVoiceStore.PatchVoice"/> writes only the ones that are not null.
/// </summary>
public sealed record VoiceEntryEdit(
    string? Provider = null,
    IReadOnlyList<string>? Languages = null,
    double? Exaggeration = null,
    double? Cfg = null);

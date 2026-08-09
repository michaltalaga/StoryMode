namespace SessionStories.Core.Voices;

using SessionStories.Core.Stories;

/// <summary>
/// The catalog of voices this machine has <em>installed</em>. A voice is an installed artifact —
/// a human name, a locale, a delivery style and whatever private assets its engine needed — not a
/// hand-written config row: which engine backs it is an implementation detail the reader never sees.
/// Global (an engine concern, not a story-world one): one <c>library/voices.json</c> plus its
/// reference wavs, shared by every universe. Same atomicity and ETag rules as <see cref="IStoryStore"/>.
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

    /// <summary>Absolute path of the voice's reference wav; null when it has none or it is missing on disk.</summary>
    string? ResolveReferenceWav(string voiceId);

    /// <summary>library/voice-previews/&lt;voiceId&gt;.mp3 — the sample the play button streams.</summary>
    string PreviewPath(string voiceId);

    /// <summary>library/voice-cache/&lt;voiceId&gt; — conditionals derived from the reference wav; may not exist.</summary>
    string VoiceCachePath(string voiceId);

    /// <summary>
    /// Creates or replaces the whole entry (an installer owns every field). Returns true when the
    /// voice did not exist before. Hand-written JSON on the entry that we know nothing about survives.
    /// </summary>
    bool UpsertVoice(InstalledVoice voice);

    /// <summary>Applies only the non-null fields of <paramref name="edit"/>; false when the entry is absent.</summary>
    bool PatchVoice(string voiceId, VoiceEdit edit);

    /// <summary>
    /// Removes the entry, its rendered sample and its conditionals cache, clearing the catalog
    /// default when it pointed here. The reference wav may back several entries, so it stays unless
    /// <paramref name="deleteReferenceWav"/> says otherwise. False when the entry is absent.
    /// </summary>
    bool DeleteVoice(string voiceId, bool deleteReferenceWav = false);

    /// <summary>Points the catalog default at the voice; false when it is not in the catalog.</summary>
    bool SetDefaultVoice(string voiceId);

    /// <summary>
    /// Writes library/voices/&lt;voiceId&gt;.wav from the stream and returns its bare file name, for an
    /// installer to record in <see cref="InstalledVoice.EngineData"/>. Deliberately does not touch the
    /// catalog: the wav is captured before the entry exists (upload and recording both land here first).
    /// </summary>
    string SaveReferenceWav(string voiceId, Stream wav);

    /// <summary>
    /// Drops the rendered sample and the conditionals cache. Both are derived from the reference wav
    /// and the conditionals cache never re-reads its source, so replacing that wav without this would
    /// leave the "new" voice sounding exactly like the old one.
    /// </summary>
    void InvalidateDerived(string voiceId);
}

public sealed record VoiceCatalog(IReadOnlyDictionary<string, InstalledVoice> Voices, string? Default);

/// <summary>
/// One installed voice. <paramref name="Id"/> is the stable key that <c>session.&lt;variant&gt;.json</c>
/// points at and is never shown to a reader; <paramref name="Name"/> is what the UI displays, so a
/// voice can be renamed without breaking a single story.
/// </summary>
/// <param name="Locale">BCP-47, e.g. "en-US" or "pl-PL" — drives the flag and the language name.</param>
/// <param name="EngineData">
/// Engine-private settings, written by the installer that produced this voice and read only by that
/// engine (piper: <c>bundle</c>; cloning engines: <c>referenceWav</c>). Nothing else may interpret it.
/// </param>
/// <remarks>
/// Deliberately carries no delivery. How a narrator reads is a property of the story being read —
/// see <see cref="Stories.SessionInfo.Delivery"/> — not of the person reading, and hanging it here
/// forced one delivery across every story a voice narrated while quietly staling its sample.
/// </remarks>
public sealed record InstalledVoice(
    string Id,
    string Name,
    string Description,
    string Locale,
    string EngineId,
    IReadOnlyDictionary<string, string> EngineData,
    VoiceProvenance? Source = null);

/// <summary>Where a voice came from and on what terms — the licensing record travels with the voice.</summary>
public sealed record VoiceProvenance(string Shelf, string License, string Attribution);

/// <summary>The only fields a reader may change after install. Null means "leave alone".</summary>
public sealed record VoiceEdit(string? Name = null, string? Description = null);

/// <summary>Locale helpers. Engines speak a bare language code; the catalog and UI speak BCP-47.</summary>
public static class VoiceLocale
{
    /// <summary>"en-US" → "en". Anything already bare is returned lowercased.</summary>
    public static string LanguageOf(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "";
        var dash = locale.IndexOfAny(['-', '_']);
        return (dash < 0 ? locale : locale[..dash]).ToLowerInvariant();
    }

    /// <summary>"en-US" → "US" for flag lookup; empty when the locale carries no region.</summary>
    public static string RegionOf(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return "";
        var dash = locale.IndexOfAny(['-', '_']);
        return dash < 0 || dash + 1 >= locale.Length ? "" : locale[(dash + 1)..].ToUpperInvariant();
    }

    /// <summary>Bare language codes are widened to the locale we actually ship (schema 1 migration).</summary>
    public static string Normalize(string? locale) => (locale ?? "").Trim() switch
    {
        "" => "en-US",
        "en" => "en-US",
        "pl" => "pl-PL",
        var value when value.Contains('_') => value.Replace('_', '-'),
        var value => value,
    };
}

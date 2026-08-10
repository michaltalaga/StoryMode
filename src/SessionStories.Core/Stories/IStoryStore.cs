namespace SessionStories.Core.Stories;

/// <summary>
/// File-system-first access to story folders. No caching, no locks: every read hits disk so
/// hand-edits are visible immediately; writes are atomic (tmp + move) and optionally guarded
/// by an ETag (derived from mtime + length). A stale ETag throws <see cref="ETagMismatchException"/>
/// carrying the current content so callers can surface a 409 with the live file.
/// </summary>
public interface IStoryStore
{
    string StoriesRoot { get; }

    IReadOnlyList<StorySummary> ListStories();
    StorySummary? GetStory(string storyId);

    /// <summary>Creates the folder, recollections/, and a session.&lt;variant&gt;.json skeleton. Returns the story id.</summary>
    string CreateStory(string slug, string universe, string variant, string title, string language);

    /// <summary>Reads a file relative to the story folder; null when absent.</summary>
    FileContent? ReadFile(string storyId, string relativePath);

    /// <summary>Atomic write. Non-null <paramref name="expectedETag"/> enforces optimistic concurrency.</summary>
    void WriteFile(string storyId, string relativePath, string text, string? expectedETag = null);

    /// <summary>Saves an uploaded recollection stream; recollections are immutable once written.</summary>
    string SaveRecollection(string storyId, string fileName, Stream content);
    IReadOnlyList<string> ListRecollections(string storyId);

    /// <summary>Parses draft.&lt;variant&gt;.md scene markers and merges verify.&lt;variant&gt;.md flags.</summary>
    IReadOnlyList<SceneDto> ReadDraftScenes(string storyId, string variant);

    /// <summary>Replaces one scene's block in the draft (ETag-guarded when given).</summary>
    void WriteScene(string storyId, string variant, string sceneId, string text, string? expectedETag = null);

    /// <summary>
    /// Splices scene.&lt;sceneId&gt;.out.md into draft.&lt;variant&gt;.md: validates marker integrity,
    /// saves the replaced block to draft.&lt;variant&gt;.&lt;sceneId&gt;.prev.md, writes atomically with a
    /// splice-time re-read, deletes the scratch file. Appends the scene when it is new.
    /// </summary>
    void SpliceSceneFromScratch(string storyId, string variant, string sceneId);

    /// <summary>
    /// Removes the '## &lt;sceneId&gt;' section from verify.&lt;variant&gt;.md — a rewritten scene
    /// invalidates its findings; verification is re-run explicitly. Other sections (incl.
    /// '## global') keep their exact bytes; the file is deleted when no sections remain.
    /// No-op when the file is absent or has no section for this scene.
    /// </summary>
    void ClearVerifyFindings(string storyId, string variant, string sceneId);

    /// <summary>Parses outline.&lt;variant&gt;.md scene headings in document order.</summary>
    IReadOnlyList<OutlineScene> ReadOutlineScenes(string storyId, string variant);

    /// <summary>Typed view of the fields the pipeline needs from session.&lt;variant&gt;.json.</summary>
    SessionInfo? ReadSessionInfo(string storyId, string variant);

    /// <summary>
    /// Records the voice and delivery a render was asked for, so the next one can offer the same
    /// again. Both are render-time choices, not part of what the story is — this is remembering
    /// what you picked, not configuring the story. Nulls leave the existing value alone, and any
    /// hand-written JSON on the file survives. No-op when the file is absent.
    /// </summary>
    void RememberRenderChoice(string storyId, string variant, string? voiceId, string? delivery);

    IReadOnlyList<PendingFact> ReadPendingFacts(string storyId, string variant);

    /// <summary>Rewrites the pending file without the decided fact ids; deletes it when empty.</summary>
    void RemovePendingFacts(string storyId, string variant, IReadOnlyCollection<string> factIds);

    /// <summary>Appends one invocation record to gen.&lt;variant&gt;.json (creating it as needed).</summary>
    void AppendGenerationLog(string storyId, string variant, GenerationLogEntry entry);
    GenerationLog? ReadGenerationLog(string storyId, string variant);
}

public sealed record StorySummary(string Id, string Title, string Universe, IReadOnlyList<VariantSummary> Variants);

/// <summary>Stage is the furthest artifact present: spec | outline | draft | audio.</summary>
public sealed record VariantSummary(string Variant, string Pov, string Language, string Stage);

public sealed record FileContent(string Text, string ETag);

public sealed record SceneDto(string SceneId, string Title, string Text, IReadOnlyList<VerifyFlag> Flags);

public sealed record VerifyFlag(string Rule, string Detail);

public sealed record OutlineScene(string SceneId, string Title, int? TargetWords);

/// <param name="Delivery">
/// How this variant should be read — a <see cref="Tts.VoiceStyle"/> id. It belongs to the story
/// rather than to the voice: the same narrator reads a bedtime story and a battle report
/// differently, and a voice is who is speaking, not how. Null means the default.
/// </param>
public sealed record SessionInfo(
    string Universe,
    string Language,
    string Pov,
    string? Voice,
    IReadOnlyDictionary<string, double> VoiceOverrides,
    int? TargetMinutes,
    string? Delivery = null);

public sealed record PendingFact(string FactId, string Text, string? SceneId);

public sealed record GenerationLogEntry(string Stage, string? SessionId, decimal? CostUsd, DateTimeOffset At, bool Success);

public sealed record GenerationLog(string? DraftSessionId, IReadOnlyList<GenerationLogEntry> Invocations);

/// <summary>The write was rejected because the file changed since it was read.</summary>
public sealed class ETagMismatchException(string currentText, string currentETag)
    : Exception("The file changed on disk since it was read.")
{
    public string CurrentText { get; } = currentText;
    public string CurrentETag { get; } = currentETag;
}

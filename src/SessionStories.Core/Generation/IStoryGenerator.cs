namespace SessionStories.Core.Generation;

/// <summary>
/// Runs one generation stage by invoking the on-disk skill headlessly (claude -p).
/// The generator never edits draft files directly — scene prose lands in scratch files
/// the caller splices via the story store.
/// </summary>
public interface IStoryGenerator
{
    Task<GenerationResult> RunStageAsync(
        GenerationStage stage,
        StageContext context,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

public enum GenerationStage { Extract, Outline, Scene, Regen, Verify, Bible }

/// <summary>
/// ResumeSessionId is set only for Scene stages (sequential drafting); Regen always runs fresh.
/// </summary>
public sealed record StageContext(
    string StoryId,
    string Variant,
    string? SceneId = null,
    string? FeedbackNote = null,
    string? ResumeSessionId = null);

public sealed record GenerationResult(bool Success, string? SessionId, decimal? CostUsd, string? Error);

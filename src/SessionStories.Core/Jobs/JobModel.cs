using SessionStories.Core.Voices;

namespace SessionStories.Core.Jobs;

public enum JobType { Transcribe, Generate, RegenScene, Verify, RenderTts, PreviewVoice, InstallVoice }

public enum JobState { Queued, Running, Succeeded, Failed, Cancelled }

/// <summary>
/// In-memory job record. Jobs are not persisted — the durable trace is gen.&lt;variant&gt;.json
/// and the artifacts on disk; a restart loses only this bookkeeping.
/// </summary>
public sealed class JobRecord
{
    private readonly Queue<string> _log = new();
    private readonly Lock _gate = new();

    public required string Id { get; init; }
    public required JobType Type { get; init; }
    public required string StoryId { get; init; }
    public required string Variant { get; init; }
    public string? SceneId { get; init; }
    public string? FeedbackNote { get; init; }
    public string? File { get; init; }

    /// <summary>
    /// PreviewVoice and InstallVoice jobs: the voice they are about. RenderTts: the voice to read
    /// with, chosen when the render is asked for. Null falls back to the story's remembered choice.
    /// </summary>
    public string? VoiceId { get; init; }

    /// <summary>RenderTts only — how to read it. Null falls back to the story's remembered choice.</summary>
    public string? Delivery { get; init; }

    /// <summary>InstallVoice jobs only — what to install and under which name.</summary>
    public VoiceInstallRequest? Install { get; init; }

    public JobState State { get; set; } = JobState.Queued;
    public string Stage { get; set; } = "";

    /// <summary>
    /// 0–100 for the part of the work that can honestly report one (currently downloads).
    /// Null means "running, but no meaningful fraction" — better than a bar that invents progress.
    /// </summary>
    public int? Percent { get; set; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public decimal? CostUsd { get; set; }
    public string? Error { get; set; }

    public const int LogTailCapacity = 100;

    /// <summary>
    /// Mirrors every log line somewhere else as it is written — the console, in practice. The tail
    /// below only keeps the last hundred lines and only exists to be polled by the panel, so
    /// without this a long render's early progress is gone by the time anyone looks, and nothing
    /// survives a restart at all. Set by <c>JobRegistry</c>; failures here are swallowed, because
    /// a broken log sink must never take a render down with it.
    /// </summary>
    public Action<JobRecord, string>? Sink { get; set; }

    public void AppendLog(string line)
    {
        lock (_gate)
        {
            _log.Enqueue(line);
            while (_log.Count > LogTailCapacity)
                _log.Dequeue();
        }

        try
        {
            Sink?.Invoke(this, line);
        }
        catch
        {
            // The job is the point; its logging is not.
        }
    }

    public IReadOnlyList<string> LogTail
    {
        get { lock (_gate) return [.. _log]; }
    }
}

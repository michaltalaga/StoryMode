using System.Collections.Concurrent;
using System.Threading.Channels;
using SessionStories.Core.Jobs;

namespace SessionStories.Api.Jobs;

/// <summary>
/// In-memory job book-keeping plus the serial work queue the runner drains. Jobs are never
/// persisted — the durable trace is gen.&lt;variant&gt;.json and the artifacts on disk.
/// </summary>
public sealed class JobRegistry(ILogger<JobRegistry> logger)
{
    private const int MaxRetained = 200;

    private sealed record Entry(JobRecord Record, CancellationTokenSource Cts);

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    public ChannelReader<string> Reader => _queue.Reader;

    public JobRecord Enqueue(JobRecord record)
    {
        // Everything the panel shows in a job's log goes to the console too, tagged with what it
        // belongs to. The panel's tail is capped and dies with the process; the console is where
        // you look when a render has been going for an hour, or when it failed overnight.
        record.Sink = (job, line) => logger.LogInformation(
            "[{JobType} {JobId}{Subject}] {Line}", job.Type, job.Id, Subject(job), line);

        _jobs[record.Id] = new Entry(record, new CancellationTokenSource());
        TrimRetained();
        _queue.Writer.TryWrite(record.Id);
        return record;
    }

    /// <summary>Story/variant for pipeline jobs, voice for the ones that belong to a voice.</summary>
    private static string Subject(JobRecord job)
    {
        if (!string.IsNullOrEmpty(job.StoryId))
            return $" {job.StoryId}/{job.Variant}";
        return string.IsNullOrEmpty(job.VoiceId) ? "" : $" {job.VoiceId}";
    }

    public JobRecord? Get(string id) => _jobs.TryGetValue(id, out var entry) ? entry.Record : null;

    internal CancellationTokenSource? GetCts(string id) => _jobs.TryGetValue(id, out var entry) ? entry.Cts : null;

    public IReadOnlyList<JobRecord> List()
        => [.. _jobs.Values.Select(e => e.Record).OrderByDescending(r => r.CreatedUtc)];

    /// <summary>Cancels a job: a queued job is finished immediately, a running one has its token cancelled.</summary>
    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var entry))
            return false;

        var record = entry.Record;
        if (record.State == JobState.Queued)
        {
            record.State = JobState.Cancelled;
            record.Error = "cancelled before start";
            record.FinishedUtc = DateTimeOffset.UtcNow;
        }
        entry.Cts.Cancel();
        return true;
    }

    private void TrimRetained()
    {
        if (_jobs.Count <= MaxRetained)
            return;

        static bool IsTerminal(JobState s) => s is JobState.Succeeded or JobState.Failed or JobState.Cancelled;

        foreach (var stale in _jobs.Values
                     .Where(e => IsTerminal(e.Record.State))
                     .OrderBy(e => e.Record.CreatedUtc)
                     .Take(_jobs.Count - MaxRetained))
        {
            if (_jobs.TryRemove(stale.Record.Id, out var removed))
                removed.Cts.Dispose();
        }
    }
}

using SessionStories.Core.Jobs;

namespace SessionStories.Tests;

public sealed class JobRecordTests
{
    private static JobRecord NewJob() => new()
    {
        Id = "abc123",
        Type = JobType.RenderTts,
        StoryId = "2026-08-08-marrowfield",
        Variant = "michal",
    };

    [Fact]
    public void AppendLog_KeepsOnlyTheTail()
    {
        var job = NewJob();

        for (var i = 0; i < JobRecord.LogTailCapacity + 50; i++)
            job.AppendLog($"line {i}");

        Assert.Equal(JobRecord.LogTailCapacity, job.LogTail.Count);
        // The oldest lines are the ones dropped, so a long render's tail is its most recent work.
        Assert.Equal($"line {JobRecord.LogTailCapacity + 49}", job.LogTail[^1]);
    }

    [Fact]
    public void AppendLog_MirrorsEveryLineToTheSink()
    {
        var job = NewJob();
        var seen = new List<string>();
        job.Sink = (_, line) => seen.Add(line);

        // More than the tail holds: the sink is how early progress survives, so it must see
        // everything rather than only what the panel still has.
        for (var i = 0; i < JobRecord.LogTailCapacity + 10; i++)
            job.AppendLog($"line {i}");

        Assert.Equal(JobRecord.LogTailCapacity + 10, seen.Count);
        Assert.Equal("line 0", seen[0]);
    }

    [Fact]
    public void AppendLog_SurvivesASinkThatThrows()
    {
        var job = NewJob();
        job.Sink = (_, _) => throw new InvalidOperationException("console is gone");

        // A broken log sink must never take a render down with it.
        job.AppendLog("still recorded");

        Assert.Equal(["still recorded"], job.LogTail);
    }
}

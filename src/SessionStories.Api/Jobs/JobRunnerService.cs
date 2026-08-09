using SessionStories.Core.Generation;
using SessionStories.Core.Jobs;
using SessionStories.Core.Stories;
using SessionStories.Core.Stt;
using SessionStories.Core.Tts;
using SessionStories.Core.Universes;

namespace SessionStories.Api.Jobs;

/// <summary>
/// Drains the job queue strictly serially — one GPU, one job. ML providers come from
/// factories so each job loads its model lazily and disposes it at job end: Whisper (~3 GB)
/// and Chatterbox (~2.5–3 GB) must never coexist in VRAM. TTS factories are keyed by
/// provider id; voices.json picks one per voice.
/// </summary>
public sealed class JobRunnerService(
    JobRegistry registry,
    IStoryStore stories,
    IUniverseStore universes,
    IStoryGenerator generator,
    Func<ISttProvider> sttFactory,
    IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    SessionStoriesOptions options,
    ILogger<JobRunnerService> logger) : BackgroundService
{
    private const string DefaultTtsProviderId = "chatterbox-onnx";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in registry.Reader.ReadAllAsync(stoppingToken))
        {
            var job = registry.Get(jobId);
            var cts = registry.GetCts(jobId);
            if (job is null || cts is null || job.State != JobState.Queued)
                continue;

            var timeout = options.TimeoutFor(job.Type);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);
            linked.CancelAfter(timeout);

            job.State = JobState.Running;
            job.StartedUtc = DateTimeOffset.UtcNow;
            job.AppendLog($"job {job.Type} started (timeout {timeout.TotalMinutes:0}m)");
            try
            {
                await RunJobAsync(job, linked.Token);
                job.State = JobState.Succeeded;
                job.Stage = "done";
                job.AppendLog("job succeeded");
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
                job.Error = cts.IsCancellationRequested ? "cancelled"
                    : stoppingToken.IsCancellationRequested ? "host shutting down"
                    : $"timed out after {timeout.TotalMinutes:0} minutes";
                job.AppendLog(job.Error);
            }
            catch (Exception ex)
            {
                job.State = JobState.Failed;
                job.Error = ex.Message;
                job.AppendLog($"failed: {ex.Message}");
                logger.LogError(ex, "Job {JobId} ({JobType}) failed", job.Id, job.Type);
            }
            finally
            {
                job.FinishedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private Task RunJobAsync(JobRecord job, CancellationToken ct) => job.Type switch
    {
        JobType.Transcribe => RunTranscribeAsync(job, ct),
        JobType.Generate => RunGenerateAsync(job, ct),
        JobType.RegenScene => RunRegenSceneAsync(job, ct),
        JobType.Verify => RunVerifyAsync(job, ct),
        JobType.RenderTts => RunRenderTtsAsync(job, ct),
        _ => throw new InvalidOperationException($"Unknown job type {job.Type}."),
    };

    private async Task RunTranscribeAsync(JobRecord job, CancellationToken ct)
    {
        var fileName = job.File
            ?? throw new InvalidOperationException("Transcribe job has no file.");
        var audioPath = Path.Combine(stories.StoriesRoot, job.StoryId, "recollections", fileName);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"Recollection '{fileName}' not found.", audioPath);

        job.Stage = "transcribe";
        job.AppendLog($"transcribing {fileName}");
        var stt = sttFactory();
        try
        {
            var transcript = await stt.TranscribeAsync(audioPath, languageHint: null, ct);
            var person = Path.GetFileNameWithoutExtension(fileName);
            stories.WriteFile(job.StoryId, $"recollections/{person}.txt", transcript);
            job.AppendLog($"wrote recollections/{person}.txt ({transcript.Length} chars)");
        }
        finally
        {
            (stt as IDisposable)?.Dispose();
        }
    }

    private async Task RunGenerateAsync(JobRecord job, CancellationToken ct)
    {
        job.Stage = "extract";
        var extract = await RunLoggedStageAsync(job, GenerationStage.Extract,
            new StageContext(job.StoryId, job.Variant), "extract", ct);
        ThrowIfStageFailed("extract", extract);

        job.Stage = "outline";
        var outline = await RunLoggedStageAsync(job, GenerationStage.Outline,
            new StageContext(job.StoryId, job.Variant), "outline", ct);
        ThrowIfStageFailed("outline", outline);
        var draftSessionId = outline.SessionId;

        var scenes = stories.ReadOutlineScenes(job.StoryId, job.Variant);
        if (scenes.Count == 0)
            throw new InvalidOperationException("Outline stage produced no scenes.");

        foreach (var scene in scenes)
        {
            job.Stage = $"scene {scene.SceneId}/{scenes.Count}";
            var context = new StageContext(job.StoryId, job.Variant, scene.SceneId, ResumeSessionId: draftSessionId);
            var result = await RunLoggedStageAsync(job, GenerationStage.Scene, context, $"scene {scene.SceneId}", ct);
            if (!result.Success && draftSessionId is not null)
            {
                // Resume can rot (expired/corrupt session); one fresh attempt before giving up.
                job.AppendLog($"scene {scene.SceneId} failed with resume ({result.Error}); retrying fresh");
                result = await RunLoggedStageAsync(job, GenerationStage.Scene,
                    context with { ResumeSessionId = null }, $"scene {scene.SceneId}", ct);
            }
            ThrowIfStageFailed($"scene {scene.SceneId}", result);
            stories.SpliceSceneFromScratch(job.StoryId, job.Variant, scene.SceneId);
            job.AppendLog($"spliced {scene.SceneId} into draft.{job.Variant}.md");
        }

        job.Stage = "verify";
        var verify = await RunLoggedStageAsync(job, GenerationStage.Verify,
            new StageContext(job.StoryId, job.Variant), "verify", ct);
        ThrowIfStageFailed("verify", verify);

        job.Stage = "bible";
        var bible = await RunLoggedStageAsync(job, GenerationStage.Bible,
            new StageContext(job.StoryId, job.Variant), "bible", ct);
        ThrowIfStageFailed("bible", bible);
    }

    private async Task RunRegenSceneAsync(JobRecord job, CancellationToken ct)
    {
        var sceneId = job.SceneId
            ?? throw new InvalidOperationException("RegenScene job has no sceneId.");

        job.Stage = $"regen {sceneId}";
        // Regen is always a fresh session — never resume.
        var context = new StageContext(job.StoryId, job.Variant, sceneId, job.FeedbackNote);
        var result = await RunLoggedStageAsync(job, GenerationStage.Regen, context, $"regen {sceneId}", ct);
        ThrowIfStageFailed($"regen {sceneId}", result);
        stories.SpliceSceneFromScratch(job.StoryId, job.Variant, sceneId);
        // docs/api.md: the rewritten scene invalidates its verify findings; the user re-runs
        // verification explicitly. (The full Generate pipeline skips this — its verify stage
        // rewrites the whole report right after the scenes anyway.)
        stories.ClearVerifyFindings(job.StoryId, job.Variant, sceneId);
        job.AppendLog($"spliced {sceneId} into draft.{job.Variant}.md; cleared its verify findings");
    }

    private async Task RunVerifyAsync(JobRecord job, CancellationToken ct)
    {
        job.Stage = "verify";
        var result = await RunLoggedStageAsync(job, GenerationStage.Verify,
            new StageContext(job.StoryId, job.Variant), "verify", ct);
        ThrowIfStageFailed("verify", result);
    }

    private async Task RunRenderTtsAsync(JobRecord job, CancellationToken ct)
    {
        job.Stage = "tts";
        var session = stories.ReadSessionInfo(job.StoryId, job.Variant)
            ?? throw new InvalidOperationException($"session.{job.Variant}.json not found or unreadable.");

        var catalog = universes.ReadVoices(session.Universe);
        var voiceId = session.Voice ?? catalog?.Default
            ?? throw new InvalidOperationException(
                $"No voice: session.{job.Variant}.json has no voice and universe '{session.Universe}' has no default.");

        VoiceEntry? entry = null;
        catalog?.Voices.TryGetValue(voiceId, out entry);

        // Knobs: catalog defaults, overridden by the session's voiceOverrides.
        var knobs = new Dictionary<string, double>();
        if (entry?.Exaggeration is { } exaggeration)
            knobs["exaggeration"] = exaggeration;
        if (entry?.Cfg is { } cfg)
            knobs["cfg"] = cfg;
        foreach (var (name, value) in session.VoiceOverrides)
            knobs[name] = value;

        var scenes = stories.ReadDraftScenes(job.StoryId, job.Variant);
        if (scenes.Count == 0)
            throw new InvalidOperationException($"draft.{job.Variant}.md has no scenes to render.");
        var text = string.Join("\n\n", scenes.Select(s => s.Text));

        var outputPath = Path.Combine(stories.StoriesRoot, job.StoryId, "audio", $"{job.Variant}.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var request = new TtsRequest(text, session.Language, voiceId, outputPath, knobs);
        var progress = new DelegateProgress<TtsProgress>(
            p => job.AppendLog($"tts {p.ChunkIndex}/{p.ChunkCount}: {p.Message}"));

        var providerId = string.IsNullOrWhiteSpace(entry?.Provider) ? DefaultTtsProviderId : entry!.Provider;
        if (!ttsFactories.TryGetValue(providerId, out var ttsFactory))
        {
            throw new InvalidOperationException(
                $"Voice '{voiceId}' wants TTS provider '{providerId}', which is not registered. " +
                $"Registered: {string.Join(", ", ttsFactories.Keys)}.");
        }
        job.AppendLog($"voice '{voiceId}' via provider '{providerId}'");

        var tts = ttsFactory();
        try
        {
            try
            {
                await tts.SynthesizeAsync(request, progress, ct);
            }
            catch (InvalidOperationException) when (entry?.ReferenceWav is { Length: > 0 })
            {
                // Voice conditionals missing from the cache: prepare from the catalog reference wav,
                // retry once. For providers with fixed voices PrepareVoiceAsync is a no-op, so the
                // retry just re-raises the original error.
                var referenceWav = Path.GetFullPath(
                    Path.Combine(universes.UniversesRoot, session.Universe, entry.ReferenceWav));
                job.AppendLog($"voice '{voiceId}' not prepared; preparing from {entry.ReferenceWav}");
                await tts.PrepareVoiceAsync(referenceWav, voiceId, ct);
                await tts.SynthesizeAsync(request, progress, ct);
            }
            job.AppendLog($"wrote audio/{job.Variant}.mp3");
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
        }
    }

    private async Task<GenerationResult> RunLoggedStageAsync(
        JobRecord job, GenerationStage stage, StageContext context, string logStage, CancellationToken ct)
    {
        var progress = new DelegateProgress<string>(job.AppendLog);
        var result = await generator.RunStageAsync(stage, context, progress, ct);
        stories.AppendGenerationLog(job.StoryId, job.Variant,
            new GenerationLogEntry(logStage, result.SessionId, result.CostUsd, DateTimeOffset.UtcNow, result.Success));
        if (result.CostUsd is { } cost)
            job.CostUsd = (job.CostUsd ?? 0m) + cost;
        return result;
    }

    private static void ThrowIfStageFailed(string stage, GenerationResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException($"Stage {stage} failed: {result.Error ?? "unknown error"}");
    }

    private sealed class DelegateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

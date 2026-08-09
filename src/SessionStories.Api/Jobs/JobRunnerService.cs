using SessionStories.Core.Generation;
using SessionStories.Core.Jobs;
using SessionStories.Core.Stories;
using SessionStories.Core.Stt;
using SessionStories.Core.Tts;
using SessionStories.Core.Voices;

namespace SessionStories.Api.Jobs;

/// <summary>
/// Drains the job queue strictly serially — one GPU, one job. ML providers come from
/// factories so each job loads its model lazily and disposes it at job end: Whisper (~3 GB)
/// and Chatterbox (~2.5–3 GB) must never coexist in VRAM. TTS factories are keyed by
/// provider id; the global voice catalog picks one per voice.
/// </summary>
public sealed class JobRunnerService(
    JobRegistry registry,
    IStoryStore stories,
    IVoiceStore voices,
    IStoryGenerator generator,
    Func<ISttProvider> sttFactory,
    IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    SessionStoriesOptions options,
    ILogger<JobRunnerService> logger) : BackgroundService
{
    private const string DefaultTtsProviderId = "chatterbox-onnx";

    // Fixed preview copy, one per supported narration language (other languages get the English one).
    private const string PreviewSampleEn =
        "The elder of Marrowfield did not offer them chairs. He spoke about the gem the way a man speaks about a debt he has decided not to pay.";
    private const string PreviewSamplePl =
        "Klejnot jest w jaskini. Jaskini pilnuje klucz, a klucz ma wiedźma, która mieszka gdzie indziej.";

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
        JobType.PreviewVoice => RunPreviewVoiceAsync(job, ct),
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

        var catalog = voices.ReadCatalog();
        var voiceId = session.Voice ?? catalog?.Default
            ?? throw new InvalidOperationException(
                $"No voice: session.{job.Variant}.json has no voice and library/voices.json has no default.");

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

        var referenceWav = voices.ResolveReferenceWav(voiceId);
        var tts = ttsFactory();
        try
        {
            await SynthesizePreparingIfNeededAsync(job, tts, request, progress, voiceId, referenceWav, ct);
            job.AppendLog($"wrote audio/{job.Variant}.mp3");
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
        }
    }

    private async Task RunPreviewVoiceAsync(JobRecord job, CancellationToken ct)
    {
        job.Stage = "preview";
        var voiceId = job.VoiceId
            ?? throw new InvalidOperationException("PreviewVoice job has no voiceId.");

        var entry = voices.ReadCatalog()?.Voices.GetValueOrDefault(voiceId)
            ?? throw new InvalidOperationException($"Voice '{voiceId}' is not in library/voices.json.");

        var language = entry.Languages.FirstOrDefault() ?? "en";
        var text = language.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? PreviewSamplePl : PreviewSampleEn;

        var knobs = new Dictionary<string, double>();
        if (entry.Exaggeration is { } exaggeration)
            knobs["exaggeration"] = exaggeration;
        if (entry.Cfg is { } cfg)
            knobs["cfg"] = cfg;

        var outputPath = voices.PreviewPath(voiceId);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // Atomic: render to a scratch file so a failed or cancelled render never half-replaces a good preview.
        var tempPath = outputPath + ".tmp";

        var request = new TtsRequest(text, language, voiceId, tempPath, knobs);
        var progress = new DelegateProgress<TtsProgress>(
            p => job.AppendLog($"preview {p.ChunkIndex}/{p.ChunkCount}: {p.Message}"));

        var providerId = string.IsNullOrWhiteSpace(entry.Provider) ? DefaultTtsProviderId : entry.Provider;
        if (!ttsFactories.TryGetValue(providerId, out var ttsFactory))
        {
            throw new InvalidOperationException(
                $"Voice '{voiceId}' wants TTS provider '{providerId}', which is not registered. " +
                $"Registered: {string.Join(", ", ttsFactories.Keys)}.");
        }
        job.AppendLog($"preview of voice '{voiceId}' ({language}) via provider '{providerId}'");

        var referenceWav = voices.ResolveReferenceWav(voiceId);
        var tts = ttsFactory();
        try
        {
            await SynthesizePreparingIfNeededAsync(job, tts, request, progress, voiceId, referenceWav, ct);
            File.Move(tempPath, outputPath, overwrite: true);
            job.AppendLog($"wrote voice-previews/{voiceId}.mp3");
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task SynthesizePreparingIfNeededAsync(
        JobRecord job, ITtsProvider tts, TtsRequest request, IProgress<TtsProgress> progress,
        string voiceId, string? referenceWav, CancellationToken ct)
    {
        try
        {
            await tts.SynthesizeAsync(request, progress, ct);
        }
        catch (InvalidOperationException) when (referenceWav is not null)
        {
            // Voice conditionals missing from the cache: prepare from the catalog reference wav,
            // retry once. For providers with fixed voices PrepareVoiceAsync is a no-op, so the
            // retry just re-raises the original error.
            job.AppendLog($"voice '{voiceId}' not prepared; preparing from {Path.GetFileName(referenceWav)}");
            await tts.PrepareVoiceAsync(referenceWav, voiceId, ct);
            await tts.SynthesizeAsync(request, progress, ct);
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

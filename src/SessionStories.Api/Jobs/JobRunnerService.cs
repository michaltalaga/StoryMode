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
    IVoiceGallery gallery,
    IReadOnlyDictionary<string, IVoiceInstaller> installers,
    IStoryGenerator generator,
    Func<ISttProvider> sttFactory,
    IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities,
    SessionStoriesOptions options,
    ILogger<JobRunnerService> logger) : BackgroundService
{
    private const string DefaultTtsProviderId = "chatterbox-onnx";

    // Fixed sample copy, one per supported narration language (other languages get the English one).
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
        JobType.InstallVoice => RunInstallVoiceAsync(job, ct),
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
        var voice = RequireVoice(catalog, voiceId, job.Variant);

        // Knobs: this story's delivery resolved against the voice's engine, then the session's
        // voiceOverrides on top (files-on-disk wins).
        var knobs = new Dictionary<string, double>(StyleKnobs(voice.EngineId, session.Delivery));
        foreach (var (name, value) in session.VoiceOverrides)
            knobs[name] = value;

        var scenes = stories.ReadDraftScenes(job.StoryId, job.Variant);
        if (scenes.Count == 0)
            throw new InvalidOperationException($"draft.{job.Variant}.md has no scenes to render.");
        var text = string.Join("\n\n", scenes.Select(s => s.Text));

        var outputPath = Path.Combine(stories.StoriesRoot, job.StoryId, "audio", $"{job.Variant}.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var request = new TtsRequest(text, session.Language, voiceId, outputPath, knobs,
            EngineData: voice.EngineData);
        var progress = new DelegateProgress<TtsProgress>(
            p => job.AppendLog($"tts {p.ChunkIndex}/{p.ChunkCount}: {p.Message}"));

        job.AppendLog($"voice '{voice.Name}' ({voice.Locale}), delivery {VoiceStyle.Normalize(session.Delivery)}");
        await SynthesizeWithEngineAsync(job, voice, request, progress, ct);
        job.AppendLog($"wrote audio/{job.Variant}.mp3");
    }

    /// <summary>
    /// A story naming a voice that is not installed fails loudly. Substituting a different narrator
    /// silently would be a worse outcome than the error — you would only find out by listening.
    /// </summary>
    private static InstalledVoice RequireVoice(VoiceCatalog? catalog, string voiceId, string variant)
    {
        if (catalog?.Voices.GetValueOrDefault(voiceId) is { } voice)
            return voice;
        var installed = catalog is null || catalog.Voices.Count == 0
            ? "none are installed"
            : "installed: " + string.Join(", ", catalog.Voices.Values.Select(v => $"{v.Name} ({v.Id})").Order());
        throw new InvalidOperationException(
            $"session.{variant}.json uses the voice '{voiceId}', which is not installed — {installed}. " +
            "Add it under Settings → Voices, or point this story at a voice you have.");
    }

    /// <summary>Resolves a named delivery to whatever knobs the given engine actually has.</summary>
    private IReadOnlyDictionary<string, double> StyleKnobs(string engineId, string? delivery)
    {
        var presets = ttsCapabilities.GetValueOrDefault(engineId)?.StylePresets ?? [];
        var wanted = VoiceStyle.Normalize(delivery);
        var preset = presets.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? presets.FirstOrDefault(p => p.Id == VoiceStyle.Default);
        return preset?.Knobs ?? new Dictionary<string, double>();
    }

    /// <summary>Constructs the voice's engine, renders, and always disposes it (VRAM residency).</summary>
    private async Task SynthesizeWithEngineAsync(
        JobRecord job, InstalledVoice voice, TtsRequest request,
        IProgress<TtsProgress> progress, CancellationToken ct)
    {
        var engineId = string.IsNullOrWhiteSpace(voice.EngineId) ? DefaultTtsProviderId : voice.EngineId;
        if (!ttsFactories.TryGetValue(engineId, out var ttsFactory))
        {
            throw new InvalidOperationException(
                $"Voice '{voice.Name}' needs the '{engineId}' engine, which this build does not have. " +
                $"Available: {string.Join(", ", ttsFactories.Keys)}.");
        }

        var referenceWav = voices.ResolveReferenceWav(voice.Id);
        var tts = ttsFactory();
        try
        {
            await SynthesizePreparingIfNeededAsync(job, tts, request, progress, voice.Id, referenceWav, ct);
        }
        finally
        {
            (tts as IDisposable)?.Dispose();
        }
    }

    /// <summary>Re-records an installed voice's sample. Not what the play button does — see EnsureSampleAsync.</summary>
    private async Task RunPreviewVoiceAsync(JobRecord job, CancellationToken ct)
    {
        job.Stage = "sample";
        var voiceId = job.VoiceId
            ?? throw new InvalidOperationException("PreviewVoice job has no voiceId.");

        var voice = voices.ReadCatalog()?.Voices.GetValueOrDefault(voiceId)
            ?? throw new InvalidOperationException($"Voice '{voiceId}' is not installed.");

        await RenderSampleAsync(job, voice, ct);
    }

    private async Task RunInstallVoiceAsync(JobRecord job, CancellationToken ct)
    {
        var request = job.Install
            ?? throw new InvalidOperationException("InstallVoice job has no install request.");

        if (!installers.TryGetValue(request.Plan.EngineId, out var installer))
        {
            throw new InvalidOperationException(
                $"'{request.Name}' needs the '{request.Plan.EngineId}' engine, which this build cannot install. " +
                $"Available: {string.Join(", ", installers.Keys)}.");
        }

        job.Stage = VoiceInstallSteps.Download;
        job.AppendLog($"installing '{request.Name}' ({request.Locale})");

        // The step drives what the panel says; the detail goes to the log for anyone who opens it.
        var steps = new DelegateProgress<VoiceInstallStep>(step =>
        {
            job.Stage = step.Step;
            job.Percent = step.Percent;
            job.AppendLog(step.Detail);
        });

        var voice = await installer.InstallAsync(request, steps, ct);
        voices.UpsertVoice(voice);

        // The invariant that makes play instant: an install is not finished until the voice has a
        // sample on disk. Doing it here, inside the progress bar, is why the play button never waits.
        job.Stage = VoiceInstallSteps.Sample;
        job.Percent = null;
        await EnsureSampleAsync(job, voice, request, ct);

        // First voice on a fresh machine becomes the default, so a story can render before anyone
        // has visited Settings.
        if (string.IsNullOrEmpty(voices.ReadCatalog()?.Default))
            voices.SetDefaultVoice(voice.Id);

        job.AppendLog($"'{voice.Name}' is ready");
    }

    /// <summary>
    /// Shelf voices ship with a sample rendered by the real engine, so installing one is a copy.
    /// Only a voice made from the reader's own recording has no sample yet, and that renders here.
    /// </summary>
    private async Task EnsureSampleAsync(
        JobRecord job, InstalledVoice voice, VoiceInstallRequest request, CancellationToken ct)
    {
        if (request.Source?.Shelf is { Length: > 0 } shelfKey && gallery.SamplePath(shelfKey) is { } shipped)
        {
            var destination = voices.PreviewPath(voice.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(shipped, destination, overwrite: true);
            job.AppendLog("sample ready");
            return;
        }

        job.AppendLog("recording a sample of the new voice");
        await RenderSampleAsync(job, voice, ct);
    }

    private async Task RenderSampleAsync(JobRecord job, InstalledVoice voice, CancellationToken ct)
    {
        var language = VoiceLocale.LanguageOf(voice.Locale);
        var text = language.StartsWith("pl", StringComparison.OrdinalIgnoreCase) ? PreviewSamplePl : PreviewSampleEn;

        var outputPath = voices.PreviewPath(voice.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // Atomic: render to a scratch file so a failed or cancelled render never half-replaces a good sample.
        var tempPath = outputPath + ".tmp";

        // A sample answers "what does this person sound like", so it is always the neutral
        // delivery — how a given story is read is that story's business.
        var request = new TtsRequest(text, language, voice.Id, tempPath,
            StyleKnobs(voice.EngineId, VoiceStyle.Default), EngineData: voice.EngineData);
        var progress = new DelegateProgress<TtsProgress>(
            p => job.AppendLog($"sample {p.ChunkIndex}/{p.ChunkCount}: {p.Message}"));

        try
        {
            await SynthesizeWithEngineAsync(job, voice, request, progress, ct);
            File.Move(tempPath, outputPath, overwrite: true);
            job.AppendLog($"wrote voice-previews/{voice.Id}.mp3");
        }
        finally
        {
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

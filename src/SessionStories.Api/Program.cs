using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using SessionStories.Api;
using SessionStories.Api.Jobs;
using SessionStories.Core.Generation;
using SessionStories.Core.Jobs;
using SessionStories.Core.Stories;
using SessionStories.Core.Stt;
using SessionStories.Core.Tts;
using SessionStories.Core.Universes;
using SessionStories.Core.Voices;
using SessionStories.Providers.Claude;
using SessionStories.Providers.Store;
using SessionStories.Providers.Whisper;
using SessionStories.Tts.Chatterbox;
using SessionStories.Tts.Piper;

var builder = WebApplication.CreateBuilder(args);

// docs/api.md: LAN listener on 0.0.0.0:5211 unless config overrides (appsettings "Urls" / ASPNETCORE_URLS).
if (builder.Configuration["Urls"] is null)
    builder.WebHost.UseUrls("http://0.0.0.0:5211");

var options = builder.Configuration.GetSection("SessionStories").Get<SessionStoriesOptions>() ?? new SessionStoriesOptions();
options.ResolveDefaults(builder.Environment.ContentRootPath);

builder.Services.AddSingleton(options);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<IStoryStore>(_ => new FileStoryStore(Path.Combine(options.LibraryRoot, "stories")));
builder.Services.AddSingleton<IUniverseStore>(_ => new FileUniverseStore(Path.Combine(options.LibraryRoot, "universes")));
// Voices are global — an engine concern, shared by every universe (library/voices.json).
builder.Services.AddSingleton<IVoiceStore>(_ => new FileVoiceStore(options.LibraryRoot, options.Tts.VoiceCacheDir));
builder.Services.AddSingleton<IStoryGenerator>(_ => new ClaudeCliGenerator(new ClaudeCliOptions
{
    ExecutablePath = options.Claude.ExecutablePath,
    RepoRoot = options.RepoRoot,
    MaxBudgetUsd = options.Claude.MaxBudgetUsd,
}));

// GPU residency: ML providers are factories, constructed per job and disposed at job end —
// Whisper and Chatterbox sessions must never coexist in VRAM (JobRunnerService owns the lifecycle).
builder.Services.AddSingleton<Func<ISttProvider>>(_ => () => new WhisperNetSttProvider(new WhisperOptions
{
    ModelPath = options.Whisper.ModelPath,
}));
// TTS providers, keyed by id; the catalog's "provider" picks one per voice (JobRunnerService).
var chatterboxOptions = new ChatterboxOptions
{
    ModelDir = options.Tts.ModelDir,
    VoiceCacheDir = options.Tts.VoiceCacheDir,
};
var piperOptions = new PiperOptions
{
    ModelsRoot = options.Piper.ModelsRoot,
    VoiceModels = options.Piper.VoiceModels,
};
builder.Services.AddSingleton<IReadOnlyDictionary<string, Func<ITtsProvider>>>(_ =>
    new Dictionary<string, Func<ITtsProvider>>
    {
        ["chatterbox-onnx"] = () => new ChatterboxOnnxProvider(chatterboxOptions),
        ["piper-onnx"] = () => new PiperOnnxProvider(piperOptions),
    });
// Capability metadata, keyed the same way but resolved from statics: a read-only endpoint such
// as GET /api/voices must never construct a provider (that path ends in ONNX sessions in VRAM).
builder.Services.AddSingleton<IReadOnlyDictionary<string, TtsCapabilities>>(_ =>
    new Dictionary<string, TtsCapabilities>
    {
        ["chatterbox-onnx"] = ChatterboxOnnxProvider.DescribeCapabilities(chatterboxOptions),
        ["piper-onnx"] = PiperOnnxProvider.DescribeCapabilities(piperOptions),
    });

builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddHostedService<JobRunnerService>();

if (builder.Environment.IsDevelopment())
{
    // Vite dev server may hit the API cross-origin; ETag must be readable by the SPA.
    builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("ETag")));
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
    app.UseCors();
app.UseStaticFiles();

// Probed once at startup; /api/status serves the cached results.
var claudeStatus = ProbeClaude(options.Claude.ExecutablePath);
var gpuName = ProbeGpu();

var api = app.MapGroup("/api");

// ---------------------------------------------------------------------------
// Universes
// ---------------------------------------------------------------------------

api.MapGet("/universes", (IUniverseStore universes) =>
    Results.Ok(universes.ListUniverses().Select(id => new
    {
        id,
        title = ReadUniverseTitle(universes, id) ?? id,
    })));

api.MapGet("/universes/{uid}/files/{*name}", (string uid, string name, IUniverseStore universes, HttpResponse response) =>
{
    if (!IsSafeRelativePath(name))
        return Problem(400, "Invalid file name");
    FileContent? file;
    try
    {
        file = universes.ReadFile(uid, name);
    }
    catch (ArgumentException ex)
    {
        // Outside the universe whitelist — e.g. voices.json, which is global now (see /api/voices).
        return Problem(400, ex.Message);
    }
    if (file is null)
        return Problem(404, $"File '{name}' not found in universe '{uid}'");
    response.Headers.ETag = QuoteETag(file.ETag);
    return Results.Text(file.Text, ContentTypeForText(name));
});

api.MapPut("/universes/{uid}/files/{*name}", async (string uid, string name, HttpRequest request, HttpResponse response, IUniverseStore universes) =>
{
    if (!IsSafeRelativePath(name))
        return Problem(400, "Invalid file name");
    var etag = ReadIfMatch(request);
    if (etag is null)
        return Problem(428, "If-Match header is required for PUT");
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        universes.WriteFile(uid, name, text, etag);
    }
    catch (ETagMismatchException ex)
    {
        return ETagConflict(response, ex, ContentTypeForText(name));
    }
    catch (ArgumentException ex)
    {
        return Problem(400, ex.Message);
    }
    if (universes.ReadFile(uid, name) is { } current)
        response.Headers.ETag = QuoteETag(current.ETag);
    return Results.NoContent();
});

api.MapGet("/universes/{uid}/pending-facts", (string uid, IStoryStore stories) =>
{
    var facts = new List<object>();
    foreach (var story in stories.ListStories().Where(s => s.Universe == uid))
        foreach (var variant in story.Variants)
            foreach (var fact in stories.ReadPendingFacts(story.Id, variant.Variant))
                facts.Add(new
                {
                    storyId = story.Id,
                    variant = variant.Variant,
                    lineId = fact.FactId,
                    text = fact.Text,
                    sceneId = fact.SceneId,
                });
    return Results.Ok(facts);
});

// ---------------------------------------------------------------------------
// Voices — global (machine/engine concern), not scoped to a universe
// ---------------------------------------------------------------------------

api.MapGet("/voices", (IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    var catalog = voices.ReadCatalog();
    if (catalog is null)
        return Results.Ok(Array.Empty<object>());
    return Results.Ok(catalog.Voices.Select(entry =>
        VoiceView(voices, ttsCapabilities, catalog, entry.Key, entry.Value)));
});

// The "engine" select in the Add-voice dialog: whatever this build has registered. Cloning
// support rides along so the dialog can hide the reference-wav field for fixed-voice engines.
api.MapGet("/voices/providers", (IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
    Results.Ok(ttsFactories.Keys.OrderBy(key => key, StringComparer.Ordinal).Select(key => new
    {
        id = key,
        supportsCloning = ttsCapabilities.GetValueOrDefault(key)?.SupportsVoiceCloning ?? false,
    })));

api.MapPost("/voices", (CreateVoiceRequest request, IVoiceStore voices,
    IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    if (!IsValidVoiceId(request.Id))
        return Problem(400, "Invalid voice id");
    if (!ttsFactories.ContainsKey(request.Provider ?? ""))
        return Problem(400, $"Unknown provider '{request.Provider}'. Registered: {string.Join(", ", ttsFactories.Keys)}");
    var languages = CleanLanguages(request.Languages);
    if (languages.Count == 0)
        return Problem(400, "At least one language is required");

    var created = voices.WriteVoice(request.Id!,
        new VoiceEntryEdit(request.Provider, languages, request.Exaggeration, request.Cfg));
    var view = ReadVoiceView(voices, ttsCapabilities, request.Id!);
    return created ? Results.Created($"/api/voices/{request.Id}", view) : Results.Ok(view);
});

api.MapPatch("/voices/{id}", (string id, PatchVoiceRequest request, IVoiceStore voices,
    IReadOnlyDictionary<string, Func<ITtsProvider>> ttsFactories,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    if (!IsValidVoiceId(id))
        return Problem(400, "Invalid voice id");
    if (request.Provider is { } provider && !ttsFactories.ContainsKey(provider))
        return Problem(400, $"Unknown provider '{provider}'. Registered: {string.Join(", ", ttsFactories.Keys)}");
    List<string>? languages = null;
    if (request.Languages is not null)
    {
        languages = CleanLanguages(request.Languages);
        if (languages.Count == 0)
            return Problem(400, "At least one language is required");
    }

    return voices.PatchVoice(id, new VoiceEntryEdit(request.Provider, languages, request.Exaggeration, request.Cfg))
        ? Results.Ok(ReadVoiceView(voices, ttsCapabilities, id))
        : Problem(404, $"Voice '{id}' is not in the catalog");
});

// The reference wav may be shared by several entries, so it survives unless ?deleteWav=true.
api.MapDelete("/voices/{id}", (string id, bool? deleteWav, IVoiceStore voices) =>
{
    if (!IsValidVoiceId(id))
        return Problem(400, "Invalid voice id");
    return voices.DeleteVoice(id, deleteWav == true)
        ? Results.NoContent()
        : Problem(404, $"Voice '{id}' is not in the catalog");
});

api.MapPut("/voices/default", (SetDefaultVoiceRequest request, IVoiceStore voices) =>
{
    if (!IsValidVoiceId(request.Id))
        return Problem(400, "Invalid voice id");
    return voices.SetDefaultVoice(request.Id!)
        ? Results.NoContent()
        : Problem(404, $"Voice '{request.Id}' is not in the catalog");
});

// New reference recording ⇒ the preview and the conditionals cache are stale (the cache
// deliberately never re-reads its source wav); IVoiceStore drops both.
api.MapPost("/voices/{id}/reference", async (string id, IFormFile file, IVoiceStore voices,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    if (!IsValidVoiceId(id))
        return Problem(400, "Invalid voice id");
    if (!Path.GetExtension(file.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        return Problem(400, "Reference recordings must be .wav files");
    await using var stream = file.OpenReadStream();
    return voices.SaveReferenceWav(id, stream)
        ? Results.Ok(ReadVoiceView(voices, ttsCapabilities, id))
        : Problem(404, $"Voice '{id}' is not in the catalog");
}).DisableAntiforgery();

api.MapGet("/voices/catalog", (IVoiceStore voices, HttpResponse response) =>
{
    var file = voices.ReadCatalogFile();
    if (file is null)
        return Problem(404, "voices.json not found in the library root");
    response.Headers.ETag = QuoteETag(file.ETag);
    return Results.Text(file.Text, "application/json");
});

api.MapPut("/voices/catalog", async (HttpRequest request, HttpResponse response, IVoiceStore voices) =>
{
    var etag = ReadIfMatch(request);
    if (etag is null)
        return Problem(428, "If-Match header is required for PUT");
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        voices.WriteCatalogFile(text, etag);
    }
    catch (ETagMismatchException ex)
    {
        return ETagConflict(response, ex, "application/json");
    }
    if (voices.ReadCatalogFile() is { } current)
        response.Headers.ETag = QuoteETag(current.ETag);
    return Results.NoContent();
});

api.MapPost("/voices/{id}/preview", (string id, IVoiceStore voices, JobRegistry registry) =>
{
    if (!IsSafeFileName(id))
        return Problem(400, "Invalid voice id");
    if (voices.ReadCatalog()?.Voices.ContainsKey(id) != true)
        return Problem(404, $"Voice '{id}' is not in the catalog");
    var job = registry.Enqueue(new JobRecord
    {
        Id = NewJobId(),
        Type = JobType.PreviewVoice,
        StoryId = "",
        Variant = "",
        VoiceId = id,
    });
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
});

api.MapGet("/voices/{id}/preview", (string id, IVoiceStore voices) =>
{
    if (!IsSafeFileName(id))
        return Problem(400, "Invalid voice id");
    var path = voices.PreviewPath(id);
    if (!File.Exists(path))
        return Problem(404, $"No rendered preview for voice '{id}'");
    // enableRangeProcessing: phone seek depends on it.
    return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
});

// ---------------------------------------------------------------------------
// Stories
// ---------------------------------------------------------------------------

api.MapGet("/stories", (IStoryStore stories) => Results.Ok(stories.ListStories()));

api.MapPost("/stories", (CreateStoryRequest request, IStoryStore stories) =>
{
    if (string.IsNullOrWhiteSpace(request.Slug) || string.IsNullOrWhiteSpace(request.Universe)
        || string.IsNullOrWhiteSpace(request.Variant) || string.IsNullOrWhiteSpace(request.Title))
        return Problem(400, "slug, universe, variant, and title are required");
    try
    {
        var id = stories.CreateStory(request.Slug, request.Universe, request.Variant, request.Title,
            string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language);
        return Results.Created($"/api/stories/{id}", new { id });
    }
    catch (ArgumentException ex)
    {
        return Problem(400, ex.Message);
    }
    catch (InvalidOperationException ex)
    {
        return Problem(409, ex.Message);
    }
});

api.MapGet("/stories/{sid}", (string sid, IStoryStore stories) =>
{
    var story = stories.GetStory(sid);
    if (story is null)
        return Problem(404, $"Story '{sid}' not found");
    var storyDir = Path.Combine(stories.StoriesRoot, sid);
    return Results.Ok(new
    {
        id = story.Id,
        title = story.Title,
        universe = story.Universe,
        recollections = stories.ListRecollections(sid),
        variants = story.Variants.Select(v => new
        {
            variant = v.Variant,
            pov = v.Pov,
            language = v.Language,
            stage = v.Stage,
            artifacts = new
            {
                session = File.Exists(Path.Combine(storyDir, $"session.{v.Variant}.json")),
                outline = File.Exists(Path.Combine(storyDir, $"outline.{v.Variant}.md")),
                draft = File.Exists(Path.Combine(storyDir, $"draft.{v.Variant}.md")),
                verify = File.Exists(Path.Combine(storyDir, $"verify.{v.Variant}.md")),
                pendingFacts = File.Exists(Path.Combine(storyDir, $"bible.pending.{v.Variant}.md")),
                audio = File.Exists(Path.Combine(storyDir, "audio", $"{v.Variant}.mp3")),
            },
        }),
    });
});

api.MapGet("/stories/{sid}/session/{variant}", (string sid, string variant, IStoryStore stories, HttpResponse response) =>
{
    var file = stories.ReadFile(sid, $"session.{variant}.json");
    if (file is null)
        return Problem(404, $"session.{variant}.json not found for story '{sid}'");
    response.Headers.ETag = QuoteETag(file.ETag);
    return Results.Text(file.Text, "application/json");
});

api.MapPut("/stories/{sid}/session/{variant}", async (string sid, string variant, HttpRequest request, HttpResponse response, IStoryStore stories) =>
{
    var etag = ReadIfMatch(request);
    if (etag is null)
        return Problem(428, "If-Match header is required for PUT");
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        stories.WriteFile(sid, $"session.{variant}.json", text, etag);
    }
    catch (ETagMismatchException ex)
    {
        return ETagConflict(response, ex, "application/json");
    }
    if (stories.ReadFile(sid, $"session.{variant}.json") is { } current)
        response.Headers.ETag = QuoteETag(current.ETag);
    return Results.NoContent();
});

// ---------------------------------------------------------------------------
// Recollections
// ---------------------------------------------------------------------------

api.MapPost("/stories/{sid}/recollections", async (string sid, string? person, IFormFile file, IStoryStore stories, JobRegistry registry) =>
{
    if (stories.GetStory(sid) is null)
        return Problem(404, $"Story '{sid}' not found");
    if (string.IsNullOrWhiteSpace(person) || !IsSafeFileName(person))
        return Problem(400, "Query parameter 'person' is required and must be a plain name");

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    var name = person + extension;
    if (stories.ListRecollections(sid).Contains(name, StringComparer.OrdinalIgnoreCase))
        return Problem(409, $"Recollection '{name}' already exists; recollections are immutable after capture");

    string saved;
    await using (var stream = file.OpenReadStream())
        saved = stories.SaveRecollection(sid, name, stream);

    string? jobId = null;
    if (AudioExtensions.Contains(extension))
    {
        var job = registry.Enqueue(new JobRecord
        {
            Id = NewJobId(),
            Type = JobType.Transcribe,
            StoryId = sid,
            Variant = "",
            File = saved,
        });
        jobId = job.Id;
    }
    return Results.Ok(new { file = saved, jobId });
}).DisableAntiforgery();

api.MapGet("/stories/{sid}/recollections", (string sid, IStoryStore stories) =>
    stories.GetStory(sid) is null
        ? Problem(404, $"Story '{sid}' not found")
        : Results.Ok(stories.ListRecollections(sid)));

api.MapGet("/stories/{sid}/recollections/{file}", (string sid, string file, IStoryStore stories) =>
{
    if (!IsSafeFileName(Path.GetFileNameWithoutExtension(file)))
        return Problem(400, "Invalid file name");
    var path = Path.Combine(stories.StoriesRoot, sid, "recollections", file);
    if (!File.Exists(path))
        return Problem(404, $"Recollection '{file}' not found");
    return Results.File(path, ContentTypeForFile(file), enableRangeProcessing: true);
});

// Transcripts only — the no-cleaning rule binds the model, not the human. Recordings stay immutable.
api.MapPut("/stories/{sid}/recollections/{file}", async (string sid, string file, HttpRequest request, HttpResponse response, IStoryStore stories) =>
{
    if (!IsSafeFileName(Path.GetFileNameWithoutExtension(file)))
        return Problem(400, "Invalid file name");
    if (Path.GetExtension(file).ToLowerInvariant() is not (".txt" or ".md"))
        return Problem(403, "Only transcripts (.txt/.md) can be edited; recordings are immutable after capture");
    var etag = ReadIfMatch(request);
    if (etag is null)
        return Problem(428, "If-Match header is required for PUT");
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        stories.WriteFile(sid, $"recollections/{file}", text, etag);
    }
    catch (ETagMismatchException ex)
    {
        return ETagConflict(response, ex, "text/plain; charset=utf-8");
    }
    return Results.NoContent();
});

// The one-level undo left behind by scene regeneration (draft.<v>.<sceneId>.prev.md).
api.MapGet("/stories/{sid}/prev/{variant}/{sceneId}", (string sid, string variant, string sceneId, IStoryStore stories) =>
{
    if (!IsSafeFileName(variant) || !IsSafeFileName(sceneId))
        return Problem(400, "Invalid name");
    var rel = $"draft.{variant}.{sceneId}.prev.md";
    var prev = stories.ReadFile(sid, rel);
    return prev is null ? Problem(404, $"{rel} not found") : Results.Text(prev.Text, "text/markdown; charset=utf-8");
});

api.MapDelete("/stories/{sid}/prev/{variant}/{sceneId}", (string sid, string variant, string sceneId, IStoryStore stories) =>
{
    if (!IsSafeFileName(variant) || !IsSafeFileName(sceneId))
        return Problem(400, "Invalid name");
    var path = Path.Combine(stories.StoriesRoot, sid, $"draft.{variant}.{sceneId}.prev.md");
    if (!File.Exists(path))
        return Problem(404, "Nothing to discard");
    File.Delete(path);
    return Results.NoContent();
});

// ---------------------------------------------------------------------------
// Draft / verify / bible
// ---------------------------------------------------------------------------

api.MapGet("/stories/{sid}/draft/{variant}", (string sid, string variant, IStoryStore stories, HttpResponse response) =>
{
    var draft = stories.ReadFile(sid, $"draft.{variant}.md");
    if (draft is null)
        return Problem(404, $"draft.{variant}.md not found for story '{sid}'");
    response.Headers.ETag = QuoteETag(draft.ETag);
    return Results.Ok(stories.ReadDraftScenes(sid, variant));
});

api.MapPut("/stories/{sid}/draft/{variant}/scenes/{sceneId}", async (string sid, string variant, string sceneId, HttpRequest request, HttpResponse response, IStoryStore stories) =>
{
    var etag = ReadIfMatch(request);
    if (etag is null)
        return Problem(428, "If-Match header is required for PUT");
    var text = await new StreamReader(request.Body).ReadToEndAsync();
    try
    {
        stories.WriteScene(sid, variant, sceneId, text, etag);
    }
    catch (ETagMismatchException ex)
    {
        return ETagConflict(response, ex, "text/markdown; charset=utf-8");
    }
    // docs/api.md: a rewritten scene invalidates its verify findings; the verify job refreshes them.
    stories.ClearVerifyFindings(sid, variant, sceneId);
    if (stories.ReadFile(sid, $"draft.{variant}.md") is { } current)
        response.Headers.ETag = QuoteETag(current.ETag);
    return Results.NoContent();
});

api.MapGet("/stories/{sid}/verify/{variant}", (string sid, string variant, IStoryStore stories, HttpResponse response) =>
{
    var file = stories.ReadFile(sid, $"verify.{variant}.md");
    if (file is null)
        return Problem(404, $"verify.{variant}.md not found for story '{sid}'");
    response.Headers.ETag = QuoteETag(file.ETag);
    return Results.Ok(ParseVerify(file.Text));
});

api.MapPost("/stories/{sid}/bible/{variant}/approve", (string sid, string variant, ApproveFactsRequest request, IStoryStore stories, IUniverseStore universes) =>
{
    var session = stories.ReadSessionInfo(sid, variant);
    if (session is null)
        return Problem(404, $"session.{variant}.json not found for story '{sid}'");

    var accepted = stories.ReadPendingFacts(sid, variant)
        .Where(f => request.AcceptedLineIds.Contains(f.FactId))
        .ToList();
    if (accepted.Count == 0)
        return Results.Ok(new { appended = 0 });

    var heading = $"## {DateTime.UtcNow:yyyy-MM-dd} {sid} ({variant})";
    universes.AppendBibleFacts(session.Universe, heading, [.. accepted.Select(f => f.Text)]);
    stories.RemovePendingFacts(sid, variant, [.. accepted.Select(f => f.FactId)]);
    return Results.Ok(new { appended = accepted.Count });
});

api.MapGet("/stories/{sid}/audio/{variant}", (string sid, string variant, IStoryStore stories) =>
{
    var path = Path.Combine(stories.StoriesRoot, sid, "audio", $"{variant}.mp3");
    if (!File.Exists(path))
        return Problem(404, $"No rendered audio for '{sid}' variant '{variant}'");
    // enableRangeProcessing: phone seek depends on it.
    return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
});

// TEMP: audio auditioning shelf (library/samples) for TTS engine/voice comparisons.
// Remove together with app/src/routes/Samples.tsx when the Polish TTS quest concludes.
var samplesDir = Path.Combine(options.LibraryRoot, "samples");

api.MapGet("/samples", () =>
    Results.Ok(Directory.Exists(samplesDir)
        ? Directory.GetFiles(samplesDir)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp3" or ".wav" or ".m4a")
            .OrderBy(f => f)
            .Select(f => new { file = Path.GetFileName(f), sizeKb = new FileInfo(f).Length / 1024 })
        : []));

api.MapGet("/samples/{file}", (string file) =>
{
    if (!IsSafeFileName(Path.GetFileNameWithoutExtension(file)))
        return Problem(400, "Invalid file name");
    var path = Path.Combine(samplesDir, file);
    if (!File.Exists(path))
        return Problem(404, $"Sample '{file}' not found");
    return Results.File(path, ContentTypeForFile(file), enableRangeProcessing: true);
});

// ---------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------

api.MapPost("/stories/{sid}/jobs", (string sid, CreateJobRequest request, IStoryStore stories, JobRegistry registry) =>
{
    if (stories.GetStory(sid) is null)
        return Problem(404, $"Story '{sid}' not found");

    switch (request.Type)
    {
        case JobType.Transcribe:
            if (string.IsNullOrWhiteSpace(request.File))
                return Problem(400, "Transcribe jobs require 'file'");
            if (!stories.ListRecollections(sid).Contains(request.File, StringComparer.OrdinalIgnoreCase))
                return Problem(400, $"Recollection '{request.File}' does not exist");
            break;
        case JobType.RegenScene when string.IsNullOrWhiteSpace(request.SceneId):
            return Problem(400, "RegenScene jobs require 'sceneId'");
        case JobType.PreviewVoice:
            // Previews belong to a voice, not a story — POST /api/voices/{id}/preview.
            return Problem(400, "PreviewVoice jobs are enqueued at POST /api/voices/{id}/preview");
    }
    if (request.Type != JobType.Transcribe && string.IsNullOrWhiteSpace(request.Variant))
        return Problem(400, $"{request.Type} jobs require 'variant'");

    var job = registry.Enqueue(new JobRecord
    {
        Id = NewJobId(),
        Type = request.Type,
        StoryId = sid,
        Variant = request.Variant ?? "",
        SceneId = request.SceneId,
        FeedbackNote = request.FeedbackNote,
        File = request.File,
        VoiceId = request.VoiceId,
    });
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
});

api.MapGet("/jobs", (JobRegistry registry) => Results.Ok(registry.List().Select(JobSummary)));

api.MapGet("/jobs/{id}", (string id, JobRegistry registry) =>
{
    var job = registry.Get(id);
    if (job is null)
        return Problem(404, $"Job '{id}' not found");
    var elapsed = job.StartedUtc is { } started
        ? ((job.FinishedUtc ?? DateTimeOffset.UtcNow) - started).TotalSeconds
        : (double?)null;
    return Results.Ok(new
    {
        job.Id,
        job.Type,
        job.State,
        job.Stage,
        job.StoryId,
        job.Variant,
        job.SceneId,
        job.File,
        job.VoiceId,
        job.CreatedUtc,
        job.StartedUtc,
        job.FinishedUtc,
        job.CostUsd,
        job.Error,
        elapsedSeconds = elapsed,
        log = job.LogTail,
    });
});

api.MapPost("/jobs/{id}/cancel", (string id, JobRegistry registry) =>
    registry.Cancel(id)
        ? Results.Ok(new { id, state = registry.Get(id)!.State })
        : Problem(404, $"Job '{id}' not found"));

// ---------------------------------------------------------------------------
// Status
// ---------------------------------------------------------------------------

api.MapGet("/status", () => Results.Ok(new
{
    claude = new { found = claudeStatus.Found, version = claudeStatus.Version },
    gpu = gpuName,
    models = new
    {
        whisper = File.Exists(options.Whisper.ModelPath),
        chatterbox = Directory.Exists(options.Tts.ModelDir)
            && Directory.EnumerateFiles(options.Tts.ModelDir, "*.onnx").Any(),
    },
    libraryRoot = options.LibraryRoot,
}));

// SPA fallback — wwwroot may be empty in dev, so only map when the build output exists.
var indexHtml = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
if (File.Exists(indexHtml))
    app.MapFallbackToFile("index.html");

app.Run();

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static IResult Problem(int statusCode, string detail) => Results.Problem(statusCode: statusCode, detail: detail);

static string QuoteETag(string etag) => $"\"{etag}\"";

static string? ReadIfMatch(HttpRequest request)
{
    var value = request.Headers.IfMatch.FirstOrDefault();
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
}

// docs/api.md: ETag mismatch → 409 carrying the live file so the client can re-merge.
static IResult ETagConflict(HttpResponse response, ETagMismatchException ex, string contentType)
{
    response.Headers.ETag = QuoteETag(ex.CurrentETag);
    return Results.Text(ex.CurrentText, contentType, statusCode: StatusCodes.Status409Conflict);
}

static bool IsSafeRelativePath(string name)
    => !string.IsNullOrWhiteSpace(name)
       && !name.Contains("..")
       && !Path.IsPathRooted(name)
       && !name.Contains('\\')
       && name.Count(c => c == '/') <= 1;

static bool IsSafeFileName(string name)
    => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
       && !name.Contains("..") && name != ".";

/// <summary>Safe file name, and not one of the literal segments that live beside /api/voices/{id}.</summary>
static bool IsValidVoiceId(string? id)
    => id is not null && IsSafeFileName(id) && !Program.ReservedVoiceIds.Contains(id);

static List<string> CleanLanguages(IEnumerable<string>? languages)
    => [.. (languages ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

/// <summary>Re-reads one catalog entry in the GET /api/voices shape; null when the id is gone.</summary>
static object? ReadVoiceView(IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> capabilities, string id)
{
    var catalog = voices.ReadCatalog();
    var entry = catalog?.Voices.GetValueOrDefault(id);
    return entry is null ? null : VoiceView(voices, capabilities, catalog!, id, entry);
}

static object VoiceView(IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> capabilities,
    VoiceCatalog catalog, string id, VoiceEntry entry) => new
{
    id,
    provider = entry.Provider,
    languages = entry.Languages,
    referenceWav = entry.ReferenceWav,
    knobs = new { exaggeration = entry.Exaggeration, cfg = entry.Cfg },
    hasReferenceWav = voices.ResolveReferenceWav(id) is not null,
    hasPreview = File.Exists(voices.PreviewPath(id)),
    isDefault = string.Equals(id, catalog.Default, StringComparison.Ordinal),
    // A property of the engine, not of whether a wav happens to be listed; unknown engine ⇒ false.
    supportsCloning = capabilities.GetValueOrDefault(entry.Provider)?.SupportsVoiceCloning ?? false,
};

static string ContentTypeForText(string name)
    => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? "application/json"
        : "text/markdown; charset=utf-8";

static string ContentTypeForFile(string name)
    => new FileExtensionContentTypeProvider().TryGetContentType(name, out var contentType)
        ? contentType
        : name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? "audio/mp4" : "application/octet-stream";

static string NewJobId() => Guid.NewGuid().ToString("N")[..12];

static string? ReadUniverseTitle(IUniverseStore universes, string universeId)
{
    var constraints = universes.ReadFile(universeId, "constraints.md");
    var firstLine = constraints?.Text.Split('\n').FirstOrDefault(l => l.StartsWith("# "));
    return firstLine?[2..].Trim();
}

// verify.<v>.md: '## sN' / '## global' headings with '[rule-slug] detail' bullets.
static List<VerifyItem> ParseVerify(string text)
{
    var items = new List<VerifyItem>();
    var scope = "global";
    foreach (var raw in text.Split('\n'))
    {
        var line = raw.TrimEnd('\r').Trim();
        if (line.StartsWith("## "))
        {
            scope = line[3..].Trim();
            continue;
        }
        if (line.StartsWith("- ["))
        {
            var close = line.IndexOf(']');
            if (close > 3)
                items.Add(new VerifyItem(scope, line[3..close], line[(close + 1)..].Trim()));
        }
    }
    return items;
}

static (bool Found, string? Version) ProbeClaude(string executablePath)
{
    string[][] attempts = OperatingSystem.IsWindows() && !Path.HasExtension(executablePath)
        ? [[executablePath, "--version"], ["cmd.exe", "/c", executablePath, "--version"]]
        : [[executablePath, "--version"]];
    foreach (var attempt in attempts)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = attempt[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in attempt.Skip(1))
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                continue;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                continue;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode == 0 && output.Length > 0)
                return (true, output);
        }
        catch
        {
            // try next shape
        }
    }
    return (false, null);
}

static string? ProbeGpu()
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--query-gpu=name");
        psi.ArgumentList.Add("--format=csv,noheader");
        using var process = Process.Start(psi);
        if (process is null || !process.WaitForExit(5_000))
            return null;
        var name = process.StandardOutput.ReadLine()?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }
    catch
    {
        return null;
    }
}

sealed record CreateStoryRequest(string Slug, string Universe, string Variant, string Title, string? Language);

sealed record CreateJobRequest(JobType Type, string? Variant, string? SceneId, string? FeedbackNote, string? File, string? VoiceId);

sealed record ApproveFactsRequest(string[] AcceptedLineIds);

sealed record CreateVoiceRequest(string? Id, string? Provider, string[]? Languages, double? Exaggeration, double? Cfg);

sealed record PatchVoiceRequest(string? Provider, string[]? Languages, double? Exaggeration, double? Cfg);

sealed record SetDefaultVoiceRequest(string? Id);

sealed record VerifyItem(string Scope, string Rule, string Detail);

partial class Program
{
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav", ".ogg", ".opus", ".webm", ".aac", ".flac", ".wma",
    };

    /// <summary>Literal segments under /api/voices — a voice may not be named after one of them.</summary>
    internal static readonly HashSet<string> ReservedVoiceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "catalog", "providers", "default",
    };

    private static object JobSummary(JobRecord job) => new
    {
        job.Id,
        job.Type,
        job.State,
        job.Stage,
        job.StoryId,
        job.Variant,
        job.SceneId,
        job.VoiceId,
        job.CreatedUtc,
        job.StartedUtc,
        job.FinishedUtc,
        job.CostUsd,
        job.Error,
    };
}

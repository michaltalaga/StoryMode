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
using SessionStories.Providers.Voices;
using SessionStories.Providers.Whisper;
using SessionStories.Tts.Chatterbox;
using SessionStories.Tts.Piper;
using SessionStories.Tts.Container;

var builder = WebApplication.CreateBuilder(args);

// docs/api.md: LAN listeners on 0.0.0.0 unless config overrides (appsettings "Urls" / ASPNETCORE_URLS).
// https exists for one reason: browsers only expose the microphone on a secure origin, so recording
// a voice from the phone is impossible over plain http. Kestrel picks up the ASP.NET dev certificate.
if (builder.Configuration["Urls"] is null)
    builder.WebHost.UseUrls("http://0.0.0.0:5211", "https://0.0.0.0:5212");

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
};
// The container engines. These exist because the best multilingual models have no ONNX export
// worth trusting, and hand-porting each one is weeks of work — the host talks HTTP and never
// sees the Python inside. Each is registered only when its image is built and its licence
// accepted: an engine that cannot load is worse than an absent one, because it would appear as
// an option and then fail at render time.
var containerMounts = new ContainerTtsMounts(
    VoicesRoot: Path.Combine(options.LibraryRoot, "voices"));
// Long timeout: a chunk is seconds, but the first call also waits for the model to load.
var containerHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

var containerEngines = new List<ContainerTtsEngine>();
if (options.Xtts.AcceptCoquiLicense)
{
    containerEngines.Add(new ContainerTtsEngine(
        Id: "xtts-docker",
        Image: options.Xtts.Image,
        ContainerName: options.Xtts.ContainerName,
        Port: options.Xtts.Port,
        ModelsRoot: options.Xtts.ModelsRoot,
        Languages: ["en", "es", "fr", "de", "it", "pt", "pl", "tr", "ru", "nl", "cs", "ar", "zh-cn", "ja", "hu", "ko", "hi"],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote:
            "XTTS-v2 is published under the Coqui Public Model License (non-commercial use only) " +
            "and will not load until that is accepted. Set SessionStories:Xtts:AcceptCoquiLicense " +
            "to true in appsettings.json if those terms are acceptable to you.",
        Accepted: true)
    {
        Environment = new Dictionary<string, string> { ["COQUI_TOS_AGREED"] = "1" },
    });
}

// Apache-2.0 engines need no acceptance, so the only gate is whether their image exists —
// enabling one you have not built would make it appear as an option and then fail.
if (options.Moss.Enabled)
{
    containerEngines.Add(new ContainerTtsEngine(
        Id: "moss-container",
        Image: options.Moss.Image,
        ContainerName: options.Moss.ContainerName,
        Port: options.Moss.Port,
        ModelsRoot: options.Moss.ModelsRoot,
        Languages: ["zh", "en", "de", "es", "fr", "ja", "it", "hu", "ko", "ru", "fa", "ar", "pl", "pt", "cs", "da", "sv", "el", "tr"],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote: "MOSS-TTS is Apache 2.0; build its image with scripts/build-tts-images.ps1 moss.",
        Accepted: true));
}
if (options.Qwen.Enabled)
{
    containerEngines.Add(new ContainerTtsEngine(
        Id: "qwen-container",
        Image: options.Qwen.Image,
        ContainerName: options.Qwen.ContainerName,
        Port: options.Qwen.Port,
        ModelsRoot: options.Qwen.ModelsRoot,
        // Polish only in practice: the checkpoint is a Polish fine-tune, but the library's
        // language whitelist has no Polish entry, so the image passes "auto" and the model does
        // the rest. Claiming the base model's ten languages here would be claiming ten failures.
        Languages: ["pl"],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote: "Qwen3-TTS is Apache 2.0; build its image with scripts/build-tts-images.ps1 qwen.",
        Accepted: true,
        // A CustomVoice checkpoint: one built-in speaker, speaker encoder discarded at load.
        Clones: false));
}
if (options.Higgs.Enabled)
{
    containerEngines.Add(new ContainerTtsEngine(
        Id: "higgs-container",
        Image: options.Higgs.Image,
        ContainerName: options.Higgs.ContainerName,
        Port: options.Higgs.Port,
        ModelsRoot: options.Higgs.ModelsRoot,
        // The model card claims single-digit WER/CER on 102 languages, Polish in the polished
        // tier. Listed here are the ones a story in this house might plausibly be read in; the
        // rest are left off because an unlisted language fails loudly rather than quietly
        // sounding wrong.
        Languages:
        [
            "en", "pl", "de", "fr", "es", "it", "pt", "nl", "sv", "da", "fi",
            "cs", "sk", "uk", "ru", "hu", "ro", "bg", "hr", "el", "tr", "zh-cn", "ja", "ko",
        ],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote:
            "Higgs TTS 3 is published under the Boson Higgs TTS 3 Research and Non-Commercial " +
            "Licence, whose Creator Use Grant covers monetised podcasts and videos provided " +
            "Boson AI's Higgs Audio is credited. Set SessionStories:Higgs:AcceptLicense to true " +
            "in appsettings.json if those terms are acceptable to you.",
        Accepted: options.Higgs.AcceptLicense)
    {
        // Unlike the others, the model is loaded by a separate inference server inside the image
        // rather than lazily on the first request, so the container answers /health only once
        // that server is serving — and a 4B backbone takes a while to get there.
        StartupTimeout = TimeSpan.FromMinutes(25),
    });
}
if (options.VibeVoice.Enabled)
{
    // Two engines, one image. VibeVoice's long-form model clones and speaks English; Polish
    // exists only as fine-tuned voices for the streaming model, which has no speaker encoder to
    // clone with. Different models with different abilities, so: different entries, rather than
    // one entry that sometimes ignores the recording you gave it.
    containerEngines.Add(new ContainerTtsEngine(
        Id: "vibevoice-container",
        Image: options.VibeVoice.Image,
        ContainerName: options.VibeVoice.ContainerName,
        Port: options.VibeVoice.Port,
        ModelsRoot: options.VibeVoice.ModelsRoot,
        Languages: ["en", "zh-cn"],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote: "VibeVoice is MIT; build its image with scripts/build-tts-images.ps1 vibevoice.",
        Accepted: true)
    {
        // Paragraphs rather than sentences: this model holds up over long passages, and every
        // chunk boundary is a place where the reading can restart on a different footing.
        MinChunkCharacters = 600,
        MaxChunkCharacters = 1600,
    });
    containerEngines.Add(new ContainerTtsEngine(
        Id: "vibevoice-pl-container",
        Image: options.VibeVoice.Image,
        ContainerName: options.VibeVoice.ContainerName + "-pl",
        Port: options.VibeVoice.Port + 1,
        ModelsRoot: options.VibeVoice.ModelsRoot,
        Languages: ["pl"],
        StylePresets: ContainerStylePresets.Default,
        LicenceNote: "VibeVoice is MIT; build its image with scripts/build-tts-images.ps1 vibevoice.",
        Accepted: true,
        Clones: false)
    {
        Environment = new Dictionary<string, string> { ["VIBEVOICE_MODE"] = "polish" },
    });
}

var ttsFactories = new Dictionary<string, Func<ITtsProvider>>
{
    ["chatterbox-onnx"] = () => new ChatterboxOnnxProvider(chatterboxOptions),
    ["piper-onnx"] = () => new PiperOnnxProvider(piperOptions),
};
foreach (var containerEngine in containerEngines)
{
    var captured = containerEngine;
    ttsFactories[captured.Id] = () => new ContainerTtsProvider(captured, containerMounts, containerHttp);
}
builder.Services.AddSingleton<IReadOnlyDictionary<string, Func<ITtsProvider>>>(_ => ttsFactories);

// The shelf of installable voices, and the per-engine plumbing that turns a shelf entry (or a
// reader's own recording) into one. Adding an engine means registering an installer here — no
// other layer, and no configuration file, learns about it.
builder.Services.AddSingleton<IVoiceGallery>(_ => new ShelfVoiceGallery(options.GalleryRoot));
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
builder.Services.AddSingleton<IReadOnlyDictionary<string, IVoiceInstaller>>(services =>
{
    var gallery = (ShelfVoiceGallery)services.GetRequiredService<IVoiceGallery>();
    var store = services.GetRequiredService<IVoiceStore>();
    var http = services.GetRequiredService<HttpClient>();
    var installers = new Dictionary<string, IVoiceInstaller>
    {
        ["chatterbox-onnx"] = new CloningVoiceInstaller("chatterbox-onnx", store,
            () => new ChatterboxOnnxProvider(chatterboxOptions), http, gallery.WavsRoot),
        ["piper-onnx"] = new PiperVoiceInstaller("piper-onnx", piperOptions.ModelsRoot, http),
    };
    // A cloning engine learns a recording; a fixed-voice one already holds its voices. Picking
    // the wrong installer would spend minutes preparing a reference the model then discards.
    foreach (var containerEngine in containerEngines)
    {
        var captured = containerEngine;
        installers[captured.Id] = captured.Clones
            ? new CloningVoiceInstaller(captured.Id, store,
                () => new ContainerTtsProvider(captured, containerMounts, containerHttp), http, gallery.WavsRoot)
            : new FixedVoiceInstaller(captured.Id);
    }
    return installers;
});
// Capability metadata, keyed the same way but resolved from statics: a read-only endpoint such
// as GET /api/voices must never construct a provider (that path ends in ONNX sessions in VRAM).
var ttsCapabilities = new Dictionary<string, TtsCapabilities>
{
    ["chatterbox-onnx"] = ChatterboxOnnxProvider.DescribeCapabilities(chatterboxOptions),
    ["piper-onnx"] = PiperOnnxProvider.DescribeCapabilities(piperOptions),
};
foreach (var containerEngine in containerEngines)
    ttsCapabilities[containerEngine.Id] = ContainerTtsProvider.Describe(containerEngine);
builder.Services.AddSingleton<IReadOnlyDictionary<string, TtsCapabilities>>(_ => ttsCapabilities);

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
// Voices — global (an engine concern, not a story-world one). Installed artifacts:
// which engine backs a voice never crosses this boundary.
// ---------------------------------------------------------------------------

api.MapGet("/voices", (IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    var catalog = voices.ReadCatalog();
    if (catalog is null)
        return Results.Ok(Array.Empty<object>());
    return Results.Ok(catalog.Voices.Values
        .OrderBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(voice => VoiceView(voices, ttsCapabilities, catalog, voice)));
});

// Rename, re-describe, restyle. Everything else about a voice was decided when it was installed.
api.MapPatch("/voices/{id}", (string id, PatchVoiceRequest request, IVoiceStore voices,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
{
    if (!IsValidVoiceId(id))
        return Problem(400, "Invalid voice id");
    if (request.Name is { } name && string.IsNullOrWhiteSpace(name))
        return Problem(400, "A voice needs a name");

    return voices.PatchVoice(id, new VoiceEdit(request.Name, request.Description))
        ? Results.Ok(ReadVoiceView(voices, ttsCapabilities, id))
        : Problem(404, $"Voice '{id}' is not installed");
});

// The reference recording may be shared by several voices, so it survives unless ?deleteWav=true.
api.MapDelete("/voices/{id}", (string id, bool? deleteWav, IVoiceStore voices) =>
{
    if (!IsValidVoiceId(id))
        return Problem(400, "Invalid voice id");
    return voices.DeleteVoice(id, deleteWav == true)
        ? Results.NoContent()
        : Problem(404, $"Voice '{id}' is not installed");
});

api.MapPut("/voices/default", (SetDefaultVoiceRequest request, IVoiceStore voices) =>
{
    if (!IsValidVoiceId(request.Id))
        return Problem(400, "Invalid voice id");
    return voices.SetDefaultVoice(request.Id!)
        ? Results.NoContent()
        : Problem(404, $"Voice '{request.Id}' is not installed");
});

// Every speech engine this build knows about, including the ones that are switched off — so
// "what can this machine do, and what would it take to do more" is answerable from the panel
// instead of by reading appsettings and a docs page.
api.MapGet("/voices/engines", (IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
    Results.Ok(Program.KnownEngines.Select(known =>
    {
        var live = ttsCapabilities.GetValueOrDefault(known.Id);
        return new
        {
            id = known.Id,
            name = known.Name,
            kind = known.Kind,
            licence = known.Licence,
            // Capabilities from the running engine when it is registered; from the catalogue
            // otherwise, so a switched-off engine can still say what it would offer.
            clones = live?.SupportsVoiceCloning ?? known.Clones,
            languages = live?.Languages ?? known.Languages,
            speaksPolish = (live?.Languages ?? known.Languages)
                .Any(language => language.StartsWith("pl", StringComparison.OrdinalIgnoreCase)),
            state = live is not null ? "ready" : known.OffState,
            note = live is not null ? "" : known.EnableHint,
        };
    })));

// ---- the shelf: what you could add ---------------------------------------------------

// Step one of "add a voice". canUpload says whether any installed engine can copy a recording
// in this language — the flow must not offer Upload where it cannot possibly work.
api.MapGet("/voice-gallery/languages", (IVoiceGallery gallery,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities) =>
    Results.Ok(gallery.ListLanguages().Select(language => new
    {
        locale = language.Locale,
        offerCount = language.OfferCount,
        canUpload = ttsCapabilities.Values.Any(capability =>
            capability.SupportsVoiceCloning &&
            capability.Languages.Contains(VoiceLocale.LanguageOf(language.Locale), StringComparer.OrdinalIgnoreCase)),
    })));

api.MapGet("/voice-gallery", (string? locale, IVoiceGallery gallery) =>
{
    if (string.IsNullOrWhiteSpace(locale))
        return Problem(400, "locale is required");
    return Results.Ok(gallery.ListOffers(locale).Select(offer => new
    {
        key = offer.Key,
        name = offer.Name,
        description = offer.Description,
        locale = offer.Locale,
        downloadBytes = offer.DownloadBytes,
        license = offer.License,
        attribution = offer.Attribution,
    }));
});

// Pre-rendered and shipped, so auditioning before installing costs one static file read.
// Catch-all: an offer key carries a slash ("piper/pl_PL-gosia-medium").
api.MapGet("/voice-gallery/sample/{**key}", (string key, IVoiceGallery gallery) =>
{
    var path = gallery.SamplePath(key);
    if (path is null)
        return Problem(404, $"No sample for '{key}'");
    return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
});

api.MapPost("/voices/install", (InstallVoiceRequest request, IVoiceGallery gallery, IVoiceStore voices,
    JobRegistry registry) =>
{
    if (string.IsNullOrWhiteSpace(request.Key))
        return Problem(400, "key is required");
    if (gallery.FindOffer(request.Key) is not { } offer)
        return Problem(404, $"'{request.Key}' is not on the voice list");

    var name = string.IsNullOrWhiteSpace(request.Name) ? offer.Name : request.Name!.Trim();
    var voiceId = UniqueVoiceId(name, voices);
    var job = registry.Enqueue(new JobRecord
    {
        Id = NewJobId(),
        Type = JobType.InstallVoice,
        StoryId = "",
        Variant = "",
        VoiceId = voiceId,
        Install = new VoiceInstallRequest(
            VoiceId: voiceId,
            Name: name,
            Description: offer.Description,
            Locale: offer.Locale,
            Plan: offer.Plan,
            Source: new VoiceProvenance(offer.Key, offer.License, offer.Attribution)),
    });
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, voiceId });
});

// Upload (and, once the mic is reachable, recording) — the same install path with the reader's
// own audio instead of a shelf asset.
api.MapPost("/voices/from-recording", async (HttpRequest http, IVoiceStore voices, JobRegistry registry,
    IReadOnlyDictionary<string, TtsCapabilities> ttsCapabilities, SessionStoriesOptions appOptions) =>
{
    if (!http.HasFormContentType)
        return Problem(400, "Expected a multipart form with the recording");
    var form = await http.ReadFormAsync();
    if (form.Files.Count == 0)
        return Problem(400, "No recording was attached");

    var file = form.Files[0];
    var name = form["name"].ToString().Trim();
    if (name.Length == 0)
        return Problem(400, "A voice needs a name");
    var locale = VoiceLocale.Normalize(form["locale"].ToString());
    var language = VoiceLocale.LanguageOf(locale);

    // Which engine can copy a voice in this language decides the install; the reader never picks
    // one. Preference is explicit rather than whichever the dictionary happens to yield first —
    // XTTS is trained properly on seventeen languages, chatterbox on English with the rest thin.
    var engineId = Program.CloningEnginePreference
        .Concat(ttsCapabilities.Keys)
        .Distinct(StringComparer.Ordinal)
        .FirstOrDefault(id => ttsCapabilities.GetValueOrDefault(id) is { SupportsVoiceCloning: true } capability &&
                              capability.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));
    if (engineId is null)
        return Problem(400, $"No installed engine can copy a voice in {locale}");

    var voiceId = UniqueVoiceId(name, voices);
    var uploadsRoot = Path.Combine(appOptions.LibraryRoot, "voice-uploads");
    Directory.CreateDirectory(uploadsRoot);
    var extension = Path.GetExtension(file.FileName);
    if (extension.Length > 8 || extension.Any(c => !char.IsLetterOrDigit(c) && c != '.'))
        extension = "";
    var uploadPath = Path.Combine(uploadsRoot, voiceId + extension);
    await using (var stream = File.Create(uploadPath))
        await file.CopyToAsync(stream);

    // Reject a too-short recording now rather than after the reader has waited for a job.
    if (ReferenceAudio.TryReadDuration(uploadPath) is { } duration && duration < ReferenceAudio.MinimumDuration)
    {
        File.Delete(uploadPath);
        return Problem(400, string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "That recording is only {0:0.#} seconds long. " +
            "A voice needs at least 10 seconds of clear speech; 20–40 seconds works best.",
            duration.TotalSeconds));
    }

    var job = registry.Enqueue(new JobRecord
    {
        Id = NewJobId(),
        Type = JobType.InstallVoice,
        StoryId = "",
        Variant = "",
        VoiceId = voiceId,
        Install = new VoiceInstallRequest(
            VoiceId: voiceId,
            Name: name,
            Description: form["description"].ToString().Trim(),
            Locale: locale,
            Plan: new VoiceInstallPlan(engineId),
            Source: null,
            SuppliedAudioPath: uploadPath),
    });
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, voiceId });
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
            // Samples belong to a voice, not a story — POST /api/voices/{id}/preview.
            return Problem(400, "PreviewVoice jobs are enqueued at POST /api/voices/{id}/preview");
        case JobType.InstallVoice:
            return Problem(400, "InstallVoice jobs are enqueued at POST /api/voices/install");
    }
    if (request.Type != JobType.Transcribe && string.IsNullOrWhiteSpace(request.Variant))
        return Problem(400, $"{request.Type} jobs require 'variant'");
    if (request.Delivery is { } delivery && !VoiceStyle.All.Contains(delivery, StringComparer.OrdinalIgnoreCase))
        return Problem(400, $"Unknown delivery '{delivery}'. Known: {string.Join(", ", VoiceStyle.All)}");

    // Remember what this render was asked for, so the next one can offer the same again.
    if (request.Type == JobType.RenderTts && (request.VoiceId is not null || request.Delivery is not null))
        stories.RememberRenderChoice(sid, request.Variant!, request.VoiceId, request.Delivery);

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
        Delivery = request.Delivery,
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
        job.Percent,
        // Named so an install still has a row to show before the voice is in the catalog.
        voiceName = job.Install?.Name,
        voiceLocale = job.Install?.Locale,
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

/// <summary>
/// A stable, file-safe id derived from the display name, uniquified against what is installed.
/// The id is plumbing — it keys the catalog and the story files and is never shown; the reader
/// names the voice, and renaming it later must not break a single story.
/// </summary>
static string UniqueVoiceId(string name, IVoiceStore voices)
{
    var slug = new string([.. name.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-')])
        .Trim('-');
    while (slug.Contains("--"))
        slug = slug.Replace("--", "-");
    if (slug.Length == 0)
        slug = "voice";
    if (slug.Length > 40)
        slug = slug[..40].Trim('-');

    var taken = voices.ReadCatalog()?.Voices.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    if (!taken.Contains(slug) && !Program.ReservedVoiceIds.Contains(slug))
        return slug;
    for (var suffix = 2; ; suffix++)
    {
        var candidate = $"{slug}-{suffix}";
        if (!taken.Contains(candidate) && !Program.ReservedVoiceIds.Contains(candidate))
            return candidate;
    }
}

/// <summary>Re-reads one voice in the GET /api/voices shape; null when the id is gone.</summary>
static object? ReadVoiceView(IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> capabilities, string id)
{
    var catalog = voices.ReadCatalog();
    var voice = catalog?.Voices.GetValueOrDefault(id);
    return voice is null ? null : VoiceView(voices, capabilities, catalog!, voice);
}

/// <summary>
/// The reader's view of an installed voice. Deliberately carries no engine id, no knob values and
/// no file names: everything here is something a person can act on. The raw catalog stays reachable
/// at /api/voices/catalog for when you want the machine truth.
/// </summary>
static object VoiceView(IVoiceStore voices, IReadOnlyDictionary<string, TtsCapabilities> capabilities,
    VoiceCatalog catalog, InstalledVoice voice) => new
{
    id = voice.Id,
    name = voice.Name,
    description = voice.Description,
    locale = voice.Locale,
    // The deliveries this voice's engine can do. Options, not a setting: which one a story uses is
    // that story's `delivery`, because how a narrator reads belongs to what is being read.
    styles = capabilities.GetValueOrDefault(voice.EngineId)?.StylePresets.Select(preset => preset.Id).ToArray()
        ?? [.. VoiceStyle.All],
    isDefault = string.Equals(voice.Id, catalog.Default, StringComparison.Ordinal),
    // Install guarantees this; false means something went wrong and the card should say so.
    hasSample = File.Exists(voices.PreviewPath(voice.Id)),
    attribution = voice.Source?.Attribution ?? "",
    license = voice.Source?.License ?? "",
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

/// <summary>VoiceId and Delivery are renderTts choices, made when the render is asked for.</summary>
sealed record CreateJobRequest(JobType Type, string? Variant, string? SceneId, string? FeedbackNote,
    string? File, string? VoiceId, string? Delivery);

sealed record ApproveFactsRequest(string[] AcceptedLineIds);

/// <summary>Everything a reader may change about a voice after it is installed.</summary>
sealed record PatchVoiceRequest(string? Name, string? Description);

/// <summary>Install a voice off the shelf. Name is optional — the offer's own name is the default.</summary>
sealed record InstallVoiceRequest(string? Key, string? Name);

sealed record SetDefaultVoiceRequest(string? Id);

sealed record VerifyItem(string Scope, string Rule, string Detail);

partial class Program
{
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav", ".ogg", ".opus", ".webm", ".aac", ".flac", ".wma",
    };

    /// <summary>
    /// Which cloning engine gets a new recording, best first. Explicit because "whichever the
    /// dictionary yields" is not a decision — XTTS is trained across seventeen languages,
    /// chatterbox is excellent in English and thin elsewhere, which is exactly the Polish problem.
    /// Ids not registered in this build are skipped.
    /// </summary>
    internal static readonly string[] CloningEnginePreference = ["xtts-docker", "chatterbox-onnx"];

    /// <param name="OffState">Why it is unavailable when unregistered: "disabled" or "licence".</param>
    internal sealed record KnownEngine(
        string Id, string Name, string Kind, string Licence, bool Clones,
        IReadOnlyList<string> Languages, string OffState, string EnableHint);

    /// <summary>
    /// The catalogue behind GET /api/voices/engines. Deliberately lists engines that are switched
    /// off too: the question "what could this machine do" was previously answerable only by
    /// reading source, which made every engine addition invisible until someone wired a shelf
    /// entry by hand.
    /// </summary>
    internal static readonly IReadOnlyList<KnownEngine> KnownEngines =
    [
        new("chatterbox-onnx", "Chatterbox", "builtin", "MIT", true,
            ["en", "pl", "de", "fr", "es", "it"], "disabled", ""),
        new("piper-onnx", "Piper / Coqui", "builtin", "MIT · CC0", false,
            ["en", "pl"], "disabled", ""),
        new("xtts-docker", "XTTS-v2", "container", "Non-commercial", true,
            ["en", "pl", "de", "fr", "es", "it", "pt", "ru", "nl", "cs", "tr", "ar", "zh-cn", "ja", "hu", "ko", "hi"],
            "licence",
            "Non-commercial licence. Build its image, then set Xtts:AcceptCoquiLicense."),
        new("moss-container", "MOSS-TTS", "container", "Apache 2.0", true,
            ["pl", "en", "de", "es", "fr", "it", "ja", "ko", "ru", "zh", "pt", "cs", "da", "sv", "el", "tr", "ar", "fa", "hu"],
            "disabled",
            "Build its image with scripts/build-tts-images.ps1 moss, then set Moss:Enabled."),
        new("qwen-container", "Qwen3-TTS (Polish)", "container", "Apache 2.0", false,
            ["pl"],
            "disabled",
            "Build its image with scripts/build-tts-images.ps1 qwen, then set Qwen:Enabled."),
        new("higgs-container", "Higgs TTS 3", "container", "Non-commercial", true,
            ["en", "pl", "de", "fr", "es", "it", "pt", "nl", "sv", "da", "fi", "cs", "sk", "uk",
             "ru", "hu", "ro", "bg", "hr", "el", "tr", "zh-cn", "ja", "ko"],
            "licence",
            "Research and non-commercial licence, free for credited creator use. Build its image " +
            "with scripts/build-tts-images.ps1 higgs, then set Higgs:Enabled and Higgs:AcceptLicense."),
        new("vibevoice-container", "VibeVoice", "container", "MIT", true,
            ["en", "zh-cn"],
            "disabled",
            "Build its image with scripts/build-tts-images.ps1 vibevoice, then set VibeVoice:Enabled."),
        new("vibevoice-pl-container", "VibeVoice (Polish)", "container", "MIT", false,
            ["pl"],
            "disabled",
            "Same image as VibeVoice — building it and setting VibeVoice:Enabled turns on both."),
    ];

    /// <summary>Literal segments under /api/voices — a voice may not be named after one of them.</summary>
    internal static readonly HashSet<string> ReservedVoiceIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "catalog", "providers", "default", "install", "from-recording",
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
        job.Percent,
        // A voice being installed has no catalog entry yet, so its name has to travel on the job.
        voiceName = job.Install?.Name,
        voiceLocale = job.Install?.Locale,
        job.CreatedUtc,
        job.StartedUtc,
        job.FinishedUtc,
        job.CostUsd,
        job.Error,
    };
}

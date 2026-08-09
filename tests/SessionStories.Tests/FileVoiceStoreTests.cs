using SessionStories.Core.Stories;
using SessionStories.Core.Tts;
using SessionStories.Core.Voices;
using SessionStories.Providers.Store;

namespace SessionStories.Tests;

public sealed class FileVoiceStoreTests : IDisposable
{
    private readonly string _library;
    private readonly FileVoiceStore _store;

    public FileVoiceStoreTests()
    {
        _library = Path.Combine(Path.GetTempPath(), "ss-voice-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_library);
        _store = new FileVoiceStore(_library);
    }

    public void Dispose()
    {
        try { Directory.Delete(_library, recursive: true); } catch { /* best effort */ }
    }

    private void WriteCatalogRaw(string content) => File.WriteAllText(Path.Combine(_library, "voices.json"), content);

    private string WriteReferenceWav(string fileName)
    {
        var dir = Path.Combine(_library, "voices");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
        return path;
    }

    private static InstalledVoice Voice(string id, string name, string engine = "chatterbox-onnx",
        string locale = "en-US", Dictionary<string, string>? engineData = null, VoiceProvenance? source = null)
        => new(id, name, "", locale, engine, engineData ?? [], source);

    // ---- catalog parsing (schema 2) ------------------------------------------------------

    [Fact]
    public void ReadCatalog_ParsesInstalledVoices_IgnoringUnknownFields()
    {
        WriteCatalogRaw("""
            {
              "schema": 2,
              "voices": {
                "narrator": {
                  "name": "Narrator",
                  "description": "Even and unhurried.",
                  "locale": "en-US",
                  "style": "calm",
                  "engine": "chatterbox-onnx",
                  "engineData": { "referenceWav": "narrator.wav" },
                  "source": { "shelf": "builtin/default", "license": "MIT", "attribution": "Resemble AI" },
                  "someFutureField": true
                },
                "gosia": {
                  "name": "Gosia",
                  "locale": "pl-PL",
                  "engine": "piper-onnx",
                  "engineData": { "bundle": "vits-piper-pl_PL-gosia-medium" }
                }
              },
              "default": "narrator",
              "_notes": "hand-written"
            }
            """);

        var catalog = _store.ReadCatalog()!;

        Assert.Equal("narrator", catalog.Default);
        Assert.Equal(2, catalog.Voices.Count);

        var narrator = catalog.Voices["narrator"];
        Assert.Equal("Narrator", narrator.Name);
        Assert.Equal("Even and unhurried.", narrator.Description);
        Assert.Equal("en-US", narrator.Locale);
        Assert.Equal("chatterbox-onnx", narrator.EngineId);
        Assert.Equal("narrator.wav", narrator.EngineData["referenceWav"]);
        Assert.Equal("MIT", narrator.Source!.License);

        var gosia = catalog.Voices["gosia"];
        Assert.Equal("piper-onnx", gosia.EngineId);
        Assert.Equal("vits-piper-pl_PL-gosia-medium", gosia.EngineData["bundle"]);
        Assert.Null(gosia.Source);
    }

    [Fact]
    public void ReadCatalog_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.ReadCatalog());
        Assert.Null(_store.ReadCatalogFile());
    }

    // ---- schema 1 migration --------------------------------------------------------------

    [Fact]
    public void ReadCatalog_MigratesSchema1Entries_WithoutRewritingTheFile()
    {
        // The shape shipped before voices became installable artifacts.
        var before = """
            {
              "voices": {
                "narrator-en-dry": {
                  "provider": "chatterbox-onnx",
                  "languages": ["en"],
                  "referenceWav": "narrator-en-dry.wav",
                  "exaggeration": 0.65,
                  "cfg": 0.3
                },
                "narrator-pl-gosia": { "provider": "piper-onnx", "languages": ["pl"] }
              },
              "default": "narrator-en-dry"
            }
            """;
        WriteCatalogRaw(before);

        var catalog = _store.ReadCatalog()!;

        var en = catalog.Voices["narrator-en-dry"];
        Assert.Equal("chatterbox-onnx", en.EngineId);
        // Bare language codes widen to the locale we actually ship, so a flag can be drawn.
        Assert.Equal("en-US", en.Locale);
        // The reference wav was a first-class field; it is engine-private now.
        Assert.Equal("narrator-en-dry.wav", en.EngineData["referenceWav"]);
        // Nothing to show a reader existed, so the id becomes a starting name.
        Assert.Equal("Narrator En Dry", en.Name);

        Assert.Equal("pl-PL", catalog.Voices["narrator-pl-gosia"].Locale);

        // A read must never rewrite: hand-editing the file and reloading has to be safe.
        Assert.Equal(before, _store.ReadCatalogFile()!.Text);
    }

    [Fact]
    public void PatchVoice_ConvergesASchema1EntryToSchema2()
    {
        WriteCatalogRaw("""
            {
              "voices": {
                "v": {
                  "provider": "piper-onnx",
                  "languages": ["pl"],
                  "exaggeration": 0.9,
                  "handWritten": "keep me"
                }
              }
            }
            """);

        Assert.True(_store.PatchVoice("v", new VoiceEdit(Name: "Gosia")));

        var voice = _store.ReadCatalog()!.Voices["v"];
        Assert.Equal("Gosia", voice.Name);
        Assert.Equal("piper-onnx", voice.EngineId);
        Assert.Equal("pl-PL", voice.Locale);

        var raw = _store.ReadCatalogFile()!.Text;
        // The old fields would otherwise be a second, contradicting source of truth.
        Assert.DoesNotContain("\"provider\"", raw);
        Assert.DoesNotContain("\"languages\"", raw);
        Assert.DoesNotContain("\"exaggeration\"", raw);
        Assert.Contains("\"schema\": 2", raw);
        // Anything we do not understand is still not ours to delete.
        Assert.Contains("handWritten", raw);
    }

    [Fact]
    public void ReadCatalog_ToleratesEntriesWithNothingInThem()
    {
        WriteCatalogRaw("{ \"voices\": { \"bare\": {} } }");

        var bare = _store.ReadCatalog()!.Voices["bare"];

        Assert.Equal("Bare", bare.Name);
        Assert.Equal("chatterbox-onnx", bare.EngineId);
        Assert.Equal("en-US", bare.Locale);
        Assert.Empty(bare.EngineData);
    }

    // ---- raw round-trip + ETag ----------------------------------------------------------

    [Fact]
    public void WriteCatalogFile_RoundTripsRawTextAndCreatesTheFile()
    {
        _store.WriteCatalogFile("{ \"voices\": {}, \"default\": null }");

        Assert.Equal("{ \"voices\": {}, \"default\": null }", _store.ReadCatalogFile()!.Text);
    }

    [Fact]
    public void WriteCatalogFile_StaleETag_ThrowsWithCurrentContent()
    {
        _store.WriteCatalogFile("{ \"voices\": {} }");
        var etag = _store.ReadCatalogFile()!.ETag;

        WriteCatalogRaw("{ \"voices\": { \"handEdited\": {} } }");

        var ex = Assert.Throws<ETagMismatchException>(
            () => _store.WriteCatalogFile("{ \"voices\": { \"stale\": {} } }", etag));
        Assert.Equal("{ \"voices\": { \"handEdited\": {} } }", ex.CurrentText);
        Assert.NotEqual(etag, ex.CurrentETag);
        Assert.Equal("{ \"voices\": { \"handEdited\": {} } }", _store.ReadCatalogFile()!.Text);
    }

    // ---- reference wav + sample paths ----------------------------------------------------

    [Fact]
    public void ResolveReferenceWav_ReturnsAbsolutePathUnderVoicesRoot()
    {
        var expected = WriteReferenceWav("narrator.wav");
        _store.UpsertVoice(Voice("narrator", "Narrator",
            engineData: new Dictionary<string, string> { ["referenceWav"] = "narrator.wav" }));

        Assert.Equal(expected, _store.ResolveReferenceWav("narrator"));
    }

    [Fact]
    public void ResolveReferenceWav_NullWhenUnsetUnknownOrMissingOnDisk()
    {
        _store.UpsertVoice(Voice("no-wav", "No Wav"));
        _store.UpsertVoice(Voice("ghost", "Ghost",
            engineData: new Dictionary<string, string> { ["referenceWav"] = "not-on-disk.wav" }));

        Assert.Null(_store.ResolveReferenceWav("no-wav"));
        Assert.Null(_store.ResolveReferenceWav("ghost"));
        Assert.Null(_store.ResolveReferenceWav("not-in-catalog"));
    }

    [Fact]
    public void ResolveReferenceWav_StripsAnyPathFromTheCatalogValue()
    {
        // A hand-edited entry may not reach outside library/voices.
        var expected = WriteReferenceWav("gosia.wav");
        WriteCatalogRaw("""
            { "schema": 2, "voices": { "v": { "engine": "chatterbox-onnx",
              "engineData": { "referenceWav": "../../voices/gosia.wav" } } } }
            """);

        Assert.Equal(expected, _store.ResolveReferenceWav("v"));
    }

    [Fact]
    public void PreviewPath_IsLibraryVoicePreviewsMp3()
    {
        Assert.Equal(Path.Combine(_library, "voice-previews", "narrator.mp3"), _store.PreviewPath("narrator"));
        Assert.Equal(Path.Combine(_library, "voices"), _store.VoicesRoot);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("sub/voice")]
    [InlineData("")]
    public void PreviewPath_RejectsIdsThatAreNotPlainFileNames(string voiceId)
    {
        Assert.Throws<ArgumentException>(() => _store.PreviewPath(voiceId));
    }

    // ---- structured edits ----------------------------------------------------------------

    [Fact]
    public void UpsertVoice_CreatesThenReplaces_KeepingHandWrittenFields()
    {
        WriteCatalogRaw("""
            {
              "schema": 2,
              "voices": {
                "narrator": {
                  "name": "Narrator", "locale": "en-US", "style": "natural",
                  "engine": "chatterbox-onnx", "engineData": { "referenceWav": "narrator.wav" },
                  "someFutureField": true
                }
              },
              "default": "narrator",
              "_notes": "hand-written"
            }
            """);

        Assert.True(_store.UpsertVoice(Voice("gosia", "Gosia", "piper-onnx", "pl-PL",
            engineData: new Dictionary<string, string> { ["bundle"] = "vits-piper-pl_PL-gosia-medium" })));
        Assert.False(_store.UpsertVoice(Voice("narrator", "The Narrator",
            engineData: new Dictionary<string, string> { ["referenceWav"] = "narrator.wav" })));

        var catalog = _store.ReadCatalog()!;
        Assert.Equal("pl-PL", catalog.Voices["gosia"].Locale);
        Assert.Equal("The Narrator", catalog.Voices["narrator"].Name);
        Assert.Equal("narrator", catalog.Default);

        var raw = _store.ReadCatalogFile()!.Text;
        Assert.Contains("_notes", raw);
        Assert.Contains("hand-written", raw);
        Assert.Contains("someFutureField", raw);
    }

    [Fact]
    public void PatchVoice_AppliesOnlyTheFieldsGiven_AndFailsOnUnknownId()
    {
        _store.UpsertVoice(Voice("v", "Gosia", "piper-onnx", "pl-PL"));

        Assert.True(_store.PatchVoice("v", new VoiceEdit(Description: "Measured and even.")));
        Assert.False(_store.PatchVoice("nope", new VoiceEdit(Name: "Ghost")));

        var voice = _store.ReadCatalog()!.Voices["v"];
        Assert.Equal("Measured and even.", voice.Description);
        // Untouched by the patch — only the named fields move.
        Assert.Equal("Gosia", voice.Name);
        Assert.Equal("piper-onnx", voice.EngineId);
        Assert.Equal("pl-PL", voice.Locale);
    }

    [Fact]
    public void DeleteVoice_ClearsDefaultSampleAndCache_ButKeepsTheSharedWav()
    {
        var wav = WriteReferenceWav("shared.wav");
        var shared = new Dictionary<string, string> { ["referenceWav"] = "shared.wav" };
        _store.UpsertVoice(Voice("gone", "Gone", engineData: shared));
        _store.UpsertVoice(Voice("stays", "Stays", engineData: shared));
        _store.SetDefaultVoice("gone");
        var preview = WritePreview("gone");
        var cache = WriteCache("gone");

        Assert.True(_store.DeleteVoice("gone"));
        Assert.False(_store.DeleteVoice("gone"));

        var catalog = _store.ReadCatalog()!;
        Assert.DoesNotContain("gone", catalog.Voices.Keys);
        Assert.Null(catalog.Default);
        Assert.False(File.Exists(preview));
        Assert.False(Directory.Exists(cache));
        // The wav may back other voices, so it survives unless the caller insists.
        Assert.True(File.Exists(wav));

        Assert.True(_store.DeleteVoice("stays", deleteReferenceWav: true));
        Assert.False(File.Exists(wav));
    }

    [Fact]
    public void SaveReferenceWav_WritesTheWavAndDropsDerivedArtifacts()
    {
        // Deliberately before the voice exists: upload captures audio, then the installer records it.
        var preview = WritePreview("clone");
        var cache = WriteCache("clone");

        using var upload = new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x01]);
        Assert.Equal("clone.wav", _store.SaveReferenceWav("clone", upload));

        Assert.Equal(5, new FileInfo(Path.Combine(_library, "voices", "clone.wav")).Length);
        // Stale conditionals are the classic bug: the cache never re-reads its source wav.
        Assert.False(Directory.Exists(cache));
        Assert.False(File.Exists(preview));
    }

    [Fact]
    public void SetDefaultVoice_RejectsAnIdThatIsNotInstalled()
    {
        _store.UpsertVoice(Voice("known", "Known"));
        _store.SetDefaultVoice("known");

        Assert.False(_store.SetDefaultVoice("ghost"));
        Assert.Equal("known", _store.ReadCatalog()!.Default);

        Assert.True(_store.SetDefaultVoice("known"));
        Assert.Equal("known", _store.ReadCatalog()!.Default);
    }

    private string WritePreview(string voiceId)
    {
        var path = _store.PreviewPath(voiceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x49, 0x44, 0x33]);
        return path;
    }

    private string WriteCache(string voiceId)
    {
        var dir = _store.VoiceCachePath(voiceId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        return dir;
    }
}

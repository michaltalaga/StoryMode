using SessionStories.Core.Stories;
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

    // ---- catalog parsing ----------------------------------------------------------------

    [Fact]
    public void ReadCatalog_ParsesRealCatalogShape_IgnoringUnknownFields()
    {
        // mirrors library/voices.json, including the "_notes" field
        WriteCatalogRaw("""
            {
              "voices": {
                "narrator-en-dry": {
                  "provider": "chatterbox-onnx",
                  "languages": ["en"],
                  "referenceWav": "narrator-en-dry.wav",
                  "exaggeration": 0.65,
                  "cfg": 0.3,
                  "someFutureKnob": true
                },
                "narrator-pl-dom": {
                  "provider": "piper-onnx",
                  "languages": ["pl"],
                  "referenceWav": "narrator-pl-dom.wav"
                }
              },
              "default": "narrator-en-dry",
              "_notes": "Voices are global; referenceWav names resolve against library/voices."
            }
            """);

        var catalog = _store.ReadCatalog()!;

        Assert.Equal("narrator-en-dry", catalog.Default);
        Assert.Equal(2, catalog.Voices.Count);
        var en = catalog.Voices["narrator-en-dry"];
        Assert.Equal("chatterbox-onnx", en.Provider);
        Assert.Equal(["en"], en.Languages);
        Assert.Equal("narrator-en-dry.wav", en.ReferenceWav);
        Assert.Equal(0.65, en.Exaggeration);
        Assert.Equal(0.3, en.Cfg);
        var pl = catalog.Voices["narrator-pl-dom"];
        Assert.Equal("piper-onnx", pl.Provider);
        Assert.Equal(["pl"], pl.Languages);
        Assert.Null(pl.Exaggeration);
    }

    [Fact]
    public void ReadCatalog_ToleratesMissingEntryFields()
    {
        WriteCatalogRaw("{ \"voices\": { \"bare\": {} } }");

        var catalog = _store.ReadCatalog()!;

        Assert.Null(catalog.Default);
        var bare = catalog.Voices["bare"];
        Assert.Equal("", bare.Provider);
        Assert.Empty(bare.Languages);
        Assert.Equal("", bare.ReferenceWav);
        Assert.Null(bare.Exaggeration);
        Assert.Null(bare.Cfg);
    }

    [Fact]
    public void ReadCatalog_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.ReadCatalog());
        Assert.Null(_store.ReadCatalogFile());
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

    // ---- reference wav + preview paths ---------------------------------------------------

    [Fact]
    public void ResolveReferenceWav_ReturnsAbsolutePathUnderVoicesRoot()
    {
        var expected = WriteReferenceWav("narrator-en-dry.wav");
        WriteCatalogRaw("{ \"voices\": { \"narrator-en-dry\": { \"referenceWav\": \"narrator-en-dry.wav\" } } }");

        Assert.Equal(expected, _store.ResolveReferenceWav("narrator-en-dry"));
    }

    [Fact]
    public void ResolveReferenceWav_NullWhenUnsetUnknownOrMissingOnDisk()
    {
        WriteCatalogRaw("""
            {
              "voices": {
                "no-wav": {},
                "ghost": { "referenceWav": "not-on-disk.wav" }
              }
            }
            """);

        Assert.Null(_store.ResolveReferenceWav("no-wav"));
        Assert.Null(_store.ResolveReferenceWav("ghost"));
        Assert.Null(_store.ResolveReferenceWav("not-in-catalog"));
    }

    [Fact]
    public void ResolveReferenceWav_StripsAnyPathFromTheCatalogValue()
    {
        // Legacy "voices/<name>.wav" entries (and any traversal attempt) resolve to the bare name.
        var expected = WriteReferenceWav("narrator-pl-dom.wav");
        WriteCatalogRaw("{ \"voices\": { \"v\": { \"referenceWav\": \"../../voices/narrator-pl-dom.wav\" } } }");

        Assert.Equal(expected, _store.ResolveReferenceWav("v"));
    }

    [Fact]
    public void PreviewPath_IsLibraryVoicePreviewsMp3()
    {
        Assert.Equal(Path.Combine(_library, "voice-previews", "narrator-en-dry.mp3"),
            _store.PreviewPath("narrator-en-dry"));
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

    // ---- structured edits (the panel's voice cards) ---------------------------------------

    [Fact]
    public void WriteVoice_CreatesThenReplaces_KeepingHandWrittenFields()
    {
        WriteCatalogRaw("""
            {
              "voices": {
                "narrator-en-dry": {
                  "provider": "chatterbox-onnx",
                  "languages": ["en"],
                  "referenceWav": "narrator-en-dry.wav",
                  "exaggeration": 0.65,
                  "someFutureKnob": true
                }
              },
              "default": "narrator-en-dry",
              "_notes": "hand-written"
            }
            """);

        Assert.True(_store.WriteVoice("narrator-pl-new", new VoiceEntryEdit("piper-onnx", ["pl"])));
        // Replacing an existing entry: knobs written, referenceWav and unknown JSON left alone.
        Assert.False(_store.WriteVoice("narrator-en-dry",
            new VoiceEntryEdit("chatterbox-onnx", ["en", "pl"], Exaggeration: 0.4)));

        var catalog = _store.ReadCatalog()!;
        Assert.Equal(["pl"], catalog.Voices["narrator-pl-new"].Languages);
        Assert.Equal("piper-onnx", catalog.Voices["narrator-pl-new"].Provider);
        var edited = catalog.Voices["narrator-en-dry"];
        Assert.Equal(["en", "pl"], edited.Languages);
        Assert.Equal(0.4, edited.Exaggeration);
        Assert.Equal("narrator-en-dry.wav", edited.ReferenceWav);
        Assert.Equal("narrator-en-dry", catalog.Default);

        var raw = _store.ReadCatalogFile()!.Text;
        Assert.Contains("_notes", raw);
        Assert.Contains("hand-written", raw);
        Assert.Contains("someFutureKnob", raw);
    }

    [Fact]
    public void PatchVoice_AppliesOnlyTheFieldsGiven_And404sOnUnknownId()
    {
        WriteCatalogRaw("""
            { "voices": { "v": { "provider": "piper-onnx", "languages": ["pl"], "cfg": 0.3 } } }
            """);

        Assert.True(_store.PatchVoice("v", new VoiceEntryEdit(Languages: ["pl", "en"])));
        Assert.False(_store.PatchVoice("nope", new VoiceEntryEdit(Languages: ["en"])));

        var entry = _store.ReadCatalog()!.Voices["v"];
        Assert.Equal(["pl", "en"], entry.Languages);
        Assert.Equal("piper-onnx", entry.Provider);
        Assert.Equal(0.3, entry.Cfg);
    }

    [Fact]
    public void DeleteVoice_ClearsDefaultPreviewAndCache_ButKeepsTheSharedWav()
    {
        var wav = WriteReferenceWav("shared.wav");
        WriteCatalogRaw("""
            {
              "voices": {
                "gone": { "provider": "chatterbox-onnx", "languages": ["en"], "referenceWav": "shared.wav" },
                "stays": { "provider": "chatterbox-onnx", "languages": ["en"], "referenceWav": "shared.wav" }
              },
              "default": "gone"
            }
            """);
        var preview = WritePreview("gone");
        var cache = WriteCache("gone");

        Assert.True(_store.DeleteVoice("gone"));
        Assert.False(_store.DeleteVoice("gone"));

        var catalog = _store.ReadCatalog()!;
        Assert.DoesNotContain("gone", catalog.Voices.Keys);
        Assert.Null(catalog.Default);
        Assert.False(File.Exists(preview));
        Assert.False(Directory.Exists(cache));
        // The wav may back other entries, so it survives unless the caller insists.
        Assert.True(File.Exists(wav));

        Assert.True(_store.DeleteVoice("stays", deleteReferenceWav: true));
        Assert.False(File.Exists(wav));
    }

    [Fact]
    public void SaveReferenceWav_PointsTheEntryAtItAndDropsDerivedArtifacts()
    {
        WriteCatalogRaw("""
            { "voices": { "clone": { "provider": "chatterbox-onnx", "languages": ["en"] } } }
            """);
        var preview = WritePreview("clone");
        var cache = WriteCache("clone");

        using (var upload = new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x01]))
            Assert.True(_store.SaveReferenceWav("clone", upload));

        Assert.Equal("clone.wav", _store.ReadCatalog()!.Voices["clone"].ReferenceWav);
        Assert.Equal(Path.Combine(_library, "voices", "clone.wav"), _store.ResolveReferenceWav("clone"));
        Assert.Equal(5, new FileInfo(Path.Combine(_library, "voices", "clone.wav")).Length);
        // Stale conditionals are the classic bug: the cache never re-reads its source wav.
        Assert.False(Directory.Exists(cache));
        Assert.False(File.Exists(preview));

        using var orphan = new MemoryStream([0x52]);
        Assert.False(_store.SaveReferenceWav("not-in-catalog", orphan));
    }

    [Fact]
    public void SetDefaultVoice_RejectsAnIdThatIsNotInTheCatalog()
    {
        WriteCatalogRaw("""
            { "voices": { "known": { "provider": "piper-onnx", "languages": ["pl"] } }, "default": "known" }
            """);

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

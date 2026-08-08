using SessionStories.Core.Stories;
using SessionStories.Providers.Store;

namespace SessionStories.Tests;

public sealed class FileUniverseStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileUniverseStore _store;

    public FileUniverseStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ss-universe-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new FileUniverseStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string NewUniverse(string id = "generic-fantasy")
    {
        Directory.CreateDirectory(Path.Combine(_root, id));
        return id;
    }

    private void WriteRaw(string universeId, string relativePath, string content)
    {
        var path = Path.Combine(_root, universeId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadRaw(string universeId, string relativePath) =>
        File.ReadAllText(Path.Combine(_root, universeId, relativePath));

    // ---- listing ------------------------------------------------------------------------

    [Fact]
    public void ListUniverses_ReturnsDirectoryNamesSorted()
    {
        NewUniverse("grimdark");
        NewUniverse("generic-fantasy");

        Assert.Equal(["generic-fantasy", "grimdark"], _store.ListUniverses());
    }

    // ---- whitelist ----------------------------------------------------------------------

    [Fact]
    public void ReadWrite_WhitelistedFiles_RoundTrip()
    {
        var id = NewUniverse();

        _store.WriteFile(id, "constraints.md", "# Constraints\nNo okay, no guys.\n");
        _store.WriteFile(id, "tones/kids.md", "# Kids tone\nThe fall still happens.\n");

        Assert.Equal("# Constraints\nNo okay, no guys.\n", _store.ReadFile(id, "constraints.md")!.Text);
        Assert.Equal("# Kids tone\nThe fall still happens.\n", _store.ReadFile(id, "tones/kids.md")!.Text);
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("voices.yaml")]
    [InlineData("constraints.md.bak")]
    [InlineData("../escape.md")]
    [InlineData("..\\escape.md")]
    [InlineData("tones/../../evil.md")]
    [InlineData("tones/../constraints.md")]
    [InlineData("tones/sub/deep.md")]
    [InlineData("tones\\kids.md")]
    [InlineData("tones/.md")]
    [InlineData("tones/kids.txt")]
    [InlineData("")]
    public void ReadAndWrite_RejectNamesOutsideWhitelist(string name)
    {
        var id = NewUniverse();

        Assert.Throws<ArgumentException>(() => _store.WriteFile(id, name, "x"));
        Assert.Throws<ArgumentException>(() => _store.ReadFile(id, name));
    }

    [Fact]
    public void ReadFile_MissingWhitelistedFile_ReturnsNull()
    {
        Assert.Null(_store.ReadFile(NewUniverse(), "characters.md"));
    }

    // ---- ETag ---------------------------------------------------------------------------

    [Fact]
    public void WriteFile_StaleETag_ThrowsWithCurrentContent()
    {
        var id = NewUniverse();
        _store.WriteFile(id, "bible.md", "canon v1");
        var etag = _store.ReadFile(id, "bible.md")!.ETag;

        WriteRaw(id, "bible.md", "canon v2, hand-edited and longer");

        var ex = Assert.Throws<ETagMismatchException>(() => _store.WriteFile(id, "bible.md", "stale", etag));
        Assert.Equal("canon v2, hand-edited and longer", ex.CurrentText);
        Assert.NotEqual(etag, ex.CurrentETag);
        Assert.Equal("canon v2, hand-edited and longer", ReadRaw(id, "bible.md"));
    }

    // ---- AppendBibleFacts ---------------------------------------------------------------

    [Fact]
    public void AppendBibleFacts_AppendsDatedHeadingAndBullets_PreservingExistingCanon()
    {
        var id = NewUniverse();
        WriteRaw(id, "bible.md", "# Bible\n\nExisting canon line.\n");

        _store.AppendBibleFacts(id, "2026-08-08 — The Gem of Marrowfield (michal)",
        [
            "Corin no longer trusts walls. (s2)",
            "- The elder of Marrowfield pays in gratitude only. (s1)", // already bulleted
        ]);

        var text = ReadRaw(id, "bible.md");
        Assert.StartsWith("# Bible\n\nExisting canon line.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## 2026-08-08 — The Gem of Marrowfield (michal)\n\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- Corin no longer trusts walls. (s2)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- The elder of Marrowfield pays in gratitude only. (s1)\n", text, StringComparison.Ordinal);

        // a second approval appends under its own heading, keeping the first
        _store.AppendBibleFacts(id, "2026-08-15 — Another Story (kid1)", ["A new fact."]);
        var updated = ReadRaw(id, "bible.md");
        Assert.Contains("## 2026-08-08 — The Gem of Marrowfield (michal)", updated, StringComparison.Ordinal);
        Assert.Contains("## 2026-08-15 — Another Story (kid1)", updated, StringComparison.Ordinal);
        Assert.Contains("- A new fact.\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBibleFacts_CreatesBibleWhenMissing()
    {
        var id = NewUniverse();

        _store.AppendBibleFacts(id, "2026-08-08 — First Story (michal)", ["The first ever fact."]);

        Assert.Equal(
            "## 2026-08-08 — First Story (michal)\n\n- The first ever fact.\n",
            ReadRaw(id, "bible.md"));
    }

    // ---- ReadVoices ---------------------------------------------------------------------

    [Fact]
    public void ReadVoices_ParsesRealCatalogShape_IgnoringUnknownFields()
    {
        var id = NewUniverse();
        // mirrors library/universes/generic-fantasy/voices.json, including the "_notes" field
        WriteRaw(id, "voices.json", """
            {
              "voices": {
                "narrator-en-dry": {
                  "provider": "chatterbox-onnx",
                  "languages": ["en"],
                  "referenceWav": "voices/narrator-en-dry.wav",
                  "exaggeration": 0.65,
                  "cfg": 0.3,
                  "someFutureKnob": true
                },
                "narrator-pl-dom": {
                  "provider": "chatterbox-onnx",
                  "languages": ["pl"],
                  "referenceWav": "voices/narrator-pl-dom.wav",
                  "exaggeration": 0.6,
                  "cfg": 0.3
                }
              },
              "default": "narrator-en-dry",
              "_notes": "referenceWav paths are relative to this universe folder; wavs are gitignored."
            }
            """);

        var catalog = _store.ReadVoices(id)!;

        Assert.Equal("narrator-en-dry", catalog.Default);
        Assert.Equal(2, catalog.Voices.Count);
        var en = catalog.Voices["narrator-en-dry"];
        Assert.Equal("chatterbox-onnx", en.Provider);
        Assert.Equal(["en"], en.Languages);
        Assert.Equal("voices/narrator-en-dry.wav", en.ReferenceWav);
        Assert.Equal(0.65, en.Exaggeration);
        Assert.Equal(0.3, en.Cfg);
        var pl = catalog.Voices["narrator-pl-dom"];
        Assert.Equal(["pl"], pl.Languages);
        Assert.Equal(0.6, pl.Exaggeration);
    }

    [Fact]
    public void ReadVoices_ToleratesMissingEntryFields()
    {
        var id = NewUniverse();
        WriteRaw(id, "voices.json", "{ \"voices\": { \"bare\": {} } }");

        var catalog = _store.ReadVoices(id)!;

        Assert.Null(catalog.Default);
        var bare = catalog.Voices["bare"];
        Assert.Equal("", bare.Provider);
        Assert.Empty(bare.Languages);
        Assert.Equal("", bare.ReferenceWav);
        Assert.Null(bare.Exaggeration);
        Assert.Null(bare.Cfg);
    }

    [Fact]
    public void ReadVoices_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.ReadVoices(NewUniverse()));
    }
}

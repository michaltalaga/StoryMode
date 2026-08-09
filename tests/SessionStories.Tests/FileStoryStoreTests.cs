using System.Text.Json.Nodes;
using SessionStories.Core.Stories;
using SessionStories.Providers.Store;

namespace SessionStories.Tests;

public sealed class FileStoryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileStoryStore _store;

    public FileStoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ss-story-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new FileStoryStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string NewStory(string id = "2026-08-08-test")
    {
        Directory.CreateDirectory(Path.Combine(_root, id, "recollections"));
        return id;
    }

    private void WriteRaw(string storyId, string relativePath, string content)
    {
        var path = Path.Combine(_root, storyId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadRaw(string storyId, string relativePath) =>
        File.ReadAllText(Path.Combine(_root, storyId, relativePath));

    private bool Exists(string storyId, string relativePath) =>
        File.Exists(Path.Combine(_root, storyId, relativePath));

    // ---- ETag ---------------------------------------------------------------------------

    [Fact]
    public void WriteFile_StaleETag_ThrowsWithCurrentContentAndETag()
    {
        var id = NewStory();
        _store.WriteFile(id, "outline.michal.md", "original");
        var etag = _store.ReadFile(id, "outline.michal.md")!.ETag;

        // hand-edit behind the store's back; different length guarantees a different ETag
        WriteRaw(id, "outline.michal.md", "hand-edited content, longer than before");

        var ex = Assert.Throws<ETagMismatchException>(
            () => _store.WriteFile(id, "outline.michal.md", "stale write", etag));
        Assert.Equal("hand-edited content, longer than before", ex.CurrentText);
        Assert.NotEqual(etag, ex.CurrentETag);
        // the hand-edit was not clobbered
        Assert.Equal("hand-edited content, longer than before", ReadRaw(id, "outline.michal.md"));
    }

    [Fact]
    public void WriteFile_MatchingETag_Writes()
    {
        var id = NewStory();
        _store.WriteFile(id, "verify.michal.md", "v1");
        var etag = _store.ReadFile(id, "verify.michal.md")!.ETag;

        _store.WriteFile(id, "verify.michal.md", "v2", etag);

        Assert.Equal("v2", _store.ReadFile(id, "verify.michal.md")!.Text);
    }

    [Fact]
    public void ReadFile_PathEscapingStoryFolder_Throws()
    {
        var id = NewStory();
        Assert.Throws<ArgumentException>(() => _store.ReadFile(id, "../elsewhere.md"));
        Assert.Throws<ArgumentException>(() => _store.ReadFile(id, "..\\elsewhere.md"));
    }

    // ---- splice -------------------------------------------------------------------------

    private const string TwoSceneDraft =
        "<!-- scene:s1 -->\nThe elder did not offer them chairs.\r\nHand-edited line with trailing spaces   \n\n<!-- scene:s2 -->\nOld scene two prose.\n";

    [Fact]
    public void Splice_ReplacesScene_PreservesSiblingBytes_BacksUpPrev_DeletesScratch()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", TwoSceneDraft);
        WriteRaw(id, "scene.s2.out.md", "<!-- scene:s2 -->\nNew scene two prose.\n");

        _store.SpliceSceneFromScratch(id, "michal", "s2");

        // scene one — mixed newlines, trailing spaces and all — survives byte-for-byte
        var sceneOneRegion = TwoSceneDraft[..TwoSceneDraft.IndexOf("<!-- scene:s2 -->", StringComparison.Ordinal)];
        Assert.Equal(sceneOneRegion + "<!-- scene:s2 -->\nNew scene two prose.\n", ReadRaw(id, "draft.michal.md"));
        Assert.Equal("<!-- scene:s2 -->\nOld scene two prose.\n", ReadRaw(id, "draft.michal.s2.prev.md"));
        Assert.False(Exists(id, "scene.s2.out.md"));
    }

    [Fact]
    public void Splice_MiddleScene_PreservesBothSiblings()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md",
            "<!-- scene:s1 -->\nOne.\n<!-- scene:s2 -->\nTwo.\n<!-- scene:s3 -->\nThree — hand-edited, żółć.\n");
        WriteRaw(id, "scene.s2.out.md", "<!-- scene:s2 -->\nTwo, rewritten.\n");

        _store.SpliceSceneFromScratch(id, "michal", "s2");

        Assert.Equal(
            "<!-- scene:s1 -->\nOne.\n<!-- scene:s2 -->\nTwo, rewritten.\n<!-- scene:s3 -->\nThree — hand-edited, żółć.\n",
            ReadRaw(id, "draft.michal.md"));
    }

    [Fact]
    public void Splice_NewHighestSceneId_AppendsAtEnd()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nOne.\n<!-- scene:s2 -->\nTwo.\n");
        WriteRaw(id, "scene.s3.out.md", "<!-- scene:s3 -->\nThree, brand new.\n");

        _store.SpliceSceneFromScratch(id, "michal", "s3");

        Assert.Equal(
            "<!-- scene:s1 -->\nOne.\n<!-- scene:s2 -->\nTwo.\n<!-- scene:s3 -->\nThree, brand new.\n",
            ReadRaw(id, "draft.michal.md"));
        Assert.False(Exists(id, "draft.michal.s3.prev.md")); // nothing was replaced
        Assert.False(Exists(id, "scene.s3.out.md"));
    }

    [Fact]
    public void Splice_MarkerLineDeleted_RefusesAndLeavesEverythingAlone()
    {
        var id = NewStory();
        // a hand-edit merged scene two into scene one by deleting the s2 marker line
        var draft = "<!-- scene:s1 -->\nOne, now also containing what used to be scene two.\n<!-- scene:s3 -->\nThree.\n";
        WriteRaw(id, "draft.michal.md", draft);
        WriteRaw(id, "scene.s2.out.md", "<!-- scene:s2 -->\nRegenerated two.\n");

        var ex = Assert.Throws<InvalidOperationException>(() => _store.SpliceSceneFromScratch(id, "michal", "s2"));

        Assert.Contains("s2", ex.Message);
        Assert.Equal(draft, ReadRaw(id, "draft.michal.md")); // untouched
        Assert.True(Exists(id, "scene.s2.out.md"));          // scratch kept for retry
    }

    [Fact]
    public void Splice_MarkerlessNonEmptyDraft_Refuses()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "All markers were deleted by hand.\n");
        WriteRaw(id, "scene.s1.out.md", "<!-- scene:s1 -->\nProse.\n");

        Assert.Throws<InvalidOperationException>(() => _store.SpliceSceneFromScratch(id, "michal", "s1"));
    }

    [Fact]
    public void Splice_NoDraftYet_CreatesDraftFromScratchFile()
    {
        var id = NewStory();
        WriteRaw(id, "scene.s1.out.md", "<!-- scene:s1 -->\nThe first scene.\n");

        _store.SpliceSceneFromScratch(id, "michal", "s1");

        Assert.Equal("<!-- scene:s1 -->\nThe first scene.\n", ReadRaw(id, "draft.michal.md"));
        Assert.False(Exists(id, "scene.s1.out.md"));
    }

    [Fact]
    public void Splice_ScratchWithoutMarker_Refuses()
    {
        var id = NewStory();
        WriteRaw(id, "scene.s1.out.md", "prose with no marker line\n");

        Assert.Throws<InvalidOperationException>(() => _store.SpliceSceneFromScratch(id, "michal", "s1"));
    }

    // ---- WriteScene ---------------------------------------------------------------------

    [Fact]
    public void WriteScene_ReplacesOnlyThatScene()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nOne.\n<!-- scene:s2 -->\nTwo.\n");

        _store.WriteScene(id, "michal", "s1", "One, rewritten by hand.\n");

        Assert.Equal(
            "<!-- scene:s1 -->\nOne, rewritten by hand.\n<!-- scene:s2 -->\nTwo.\n",
            ReadRaw(id, "draft.michal.md"));
    }

    [Fact]
    public void WriteScene_UnknownScene_Throws()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nOne.\n");

        Assert.Throws<InvalidOperationException>(() => _store.WriteScene(id, "michal", "s9", "text"));
    }

    [Fact]
    public void WriteScene_StaleETag_Throws()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nOne.\n");
        var etag = _store.ReadFile(id, "draft.michal.md")!.ETag;
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nOne, hand-edited meanwhile.\n");

        Assert.Throws<ETagMismatchException>(() => _store.WriteScene(id, "michal", "s1", "clobber", etag));
    }

    // ---- ReadDraftScenes ----------------------------------------------------------------

    [Fact]
    public void ReadDraftScenes_TitleFromHeadingElseOutline_FlagsMergedFromVerify()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md",
            "<!-- scene:s1 -->\n## The Bargain\nThe elder did not offer them chairs.\n\n<!-- scene:s2 -->\nThe third trap was the one that got them.\n");
        WriteRaw(id, "outline.michal.md",
            "## Scene s1: Outline Title One\n- pov: corin\n- beats: b1\n- targetWords: 400\n- note: gratitude as currency\n\n" +
            "## Scene s2: The Caves\n- pov: corin\n- beats: b2\n- targetWords: 500\n- note: being pleased with yourself is the trap\n");
        WriteRaw(id, "verify.michal.md",
            "## s2\n- [anachronism] \"okay\" — constraints.md > Forbidden anachronisms\n- [given-drift] beat b2: draft omits that the wall gave way\n" +
            "## global\n- [naming] \"Steve\" violates naming conventions\n");

        var scenes = _store.ReadDraftScenes(id, "michal");

        Assert.Equal(2, scenes.Count);
        Assert.Equal("s1", scenes[0].SceneId);
        Assert.Equal("The Bargain", scenes[0].Title); // in-draft heading wins over the outline
        Assert.StartsWith("## The Bargain", scenes[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- scene:", scenes[0].Text, StringComparison.Ordinal);
        Assert.Empty(scenes[0].Flags);

        Assert.Equal("s2", scenes[1].SceneId);
        Assert.Equal("The Caves", scenes[1].Title); // no heading → outline title
        Assert.Equal("The third trap was the one that got them.", scenes[1].Text);
        Assert.Equal(2, scenes[1].Flags.Count);
        Assert.Equal("anachronism", scenes[1].Flags[0].Rule);
        Assert.Contains("okay", scenes[1].Flags[0].Detail, StringComparison.Ordinal);
        Assert.Equal("given-drift", scenes[1].Flags[1].Rule);
    }

    [Fact]
    public void ReadDraftScenes_NoOutlineNoVerify_StillParses()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nJust prose, no heading.\n");

        var scenes = _store.ReadDraftScenes(id, "michal");

        var scene = Assert.Single(scenes);
        Assert.Equal("s1", scene.SceneId);
        Assert.Equal("", scene.Title);
        Assert.Equal("Just prose, no heading.", scene.Text);
        Assert.Empty(scene.Flags);
    }

    [Fact]
    public void ReadDraftScenes_VerifySaysNoViolations_ProducesNoFlags()
    {
        var id = NewStory();
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nProse.\n");
        WriteRaw(id, "verify.michal.md", "No violations found.\n");

        Assert.Empty(Assert.Single(_store.ReadDraftScenes(id, "michal")).Flags);
    }

    [Fact]
    public void ReadDraftScenes_MissingDraft_ReturnsEmpty()
    {
        Assert.Empty(_store.ReadDraftScenes(NewStory(), "michal"));
    }

    // ---- ClearVerifyFindings ------------------------------------------------------------

    [Fact]
    public void ClearVerifyFindings_MiddleSection_PreservesOtherSectionsByteForByte()
    {
        var id = NewStory();
        // mixed newlines and trailing spaces on purpose — untouched sections must keep exact bytes
        WriteRaw(id, "verify.michal.md",
            "## s1\r\n- [register] narrator slips into modern slang  \r\n\n" +
            "## s2\n- [anachronism] \"okay\" — constraints.md > Forbidden anachronisms\n- [given-drift] beat b2: draft omits that the wall gave way\n\n" +
            "## global\n- [naming] \"Steve\" violates naming conventions\n");

        _store.ClearVerifyFindings(id, "michal", "s2");

        Assert.Equal(
            "## s1\r\n- [register] narrator slips into modern slang  \r\n\n" +
            "## global\n- [naming] \"Steve\" violates naming conventions\n",
            ReadRaw(id, "verify.michal.md"));
    }

    [Fact]
    public void ClearVerifyFindings_LastSection_DeletesFile()
    {
        var id = NewStory();
        WriteRaw(id, "verify.michal.md", "## s1\n- [tone] too grim for the requested tone\n");

        _store.ClearVerifyFindings(id, "michal", "s1");

        Assert.False(Exists(id, "verify.michal.md"));
    }

    [Fact]
    public void ClearVerifyFindings_AbsentFile_IsNoOp()
    {
        var id = NewStory();

        _store.ClearVerifyFindings(id, "michal", "s1");

        Assert.False(Exists(id, "verify.michal.md"));
    }

    [Fact]
    public void ClearVerifyFindings_NoSectionForScene_LeavesFileAsIs()
    {
        var id = NewStory();
        WriteRaw(id, "verify.michal.md", "No violations found.\n");

        _store.ClearVerifyFindings(id, "michal", "s1");

        Assert.Equal("No violations found.\n", ReadRaw(id, "verify.michal.md"));
    }

    // ---- outline ------------------------------------------------------------------------

    [Fact]
    public void ReadOutlineScenes_ParsesHeadingsInDocumentOrder()
    {
        var id = NewStory();
        WriteRaw(id, "outline.michal.md",
            "## Scene s1: The Bargain\n- pov: corin\n- beats: b1\n- targetWords: 450\n- note: gratitude as currency\n\n" +
            "## Scene s2: The Caves\n- pov: corin\n- beats: b2, b3\n- note: no target words on this one\n");

        var scenes = _store.ReadOutlineScenes(id, "michal");

        Assert.Equal(2, scenes.Count);
        Assert.Equal(new OutlineScene("s1", "The Bargain", 450), scenes[0]);
        Assert.Equal(new OutlineScene("s2", "The Caves", null), scenes[1]);
    }

    [Fact]
    public void ReadOutlineScenes_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_store.ReadOutlineScenes(NewStory(), "michal"));
    }

    // ---- session ------------------------------------------------------------------------

    [Fact]
    public void Session_RoundTrip_PreservesUnknownFieldsAndComments()
    {
        var id = NewStory();
        var raw = """
            {
              // hand-written comment that must survive the round-trip
              "schema": 1,
              "universe": "generic-fantasy",
              "language": "en",
              "pov": "corin",
              "voice": "narrator-en-dry",
              "voiceOverrides": { "exaggeration": 0.7, },
              "targetMinutes": 12,
              "customField": { "keep": ["me"] },
            }
            """;

        _store.WriteFile(id, "session.michal.json", raw);

        // WriteFile passes raw text through verbatim — comments, trailing commas, unknown fields intact
        Assert.Equal(raw, _store.ReadFile(id, "session.michal.json")!.Text);

        var info = _store.ReadSessionInfo(id, "michal")!;
        Assert.Equal("generic-fantasy", info.Universe);
        Assert.Equal("en", info.Language);
        Assert.Equal("corin", info.Pov);
        Assert.Equal("narrator-en-dry", info.Voice);
        Assert.Equal(0.7, info.VoiceOverrides["exaggeration"]);
        Assert.Equal(12, info.TargetMinutes);
    }

    [Fact]
    public void ReadSessionInfo_MissingFields_MapToNullOrEmpty()
    {
        var id = NewStory();
        WriteRaw(id, "session.michal.json", "{ \"schema\": 1 }");

        var info = _store.ReadSessionInfo(id, "michal")!;

        Assert.Equal("", info.Universe);
        Assert.Equal("", info.Language);
        Assert.Equal("", info.Pov);
        Assert.Null(info.Voice);
        Assert.Empty(info.VoiceOverrides);
        Assert.Null(info.TargetMinutes);
    }

    [Fact]
    public void ReadSessionInfo_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.ReadSessionInfo(NewStory(), "michal"));
    }

    // ---- pending facts ------------------------------------------------------------------

    [Fact]
    public void ReadPendingFacts_ParsesIdTextAndOptionalScene()
    {
        var id = NewStory();
        WriteRaw(id, "bible.pending.michal.md",
            "- [f1] Corin nie ufa ścianom po upadku w jaskini. (s2)\n" +
            "- [f2] A fact without scene attribution.\n" +
            "not a fact bullet\n");

        var facts = _store.ReadPendingFacts(id, "michal");

        Assert.Equal(2, facts.Count);
        Assert.Equal(new PendingFact("f1", "Corin nie ufa ścianom po upadku w jaskini.", "s2"), facts[0]);
        Assert.Equal(new PendingFact("f2", "A fact without scene attribution.", null), facts[1]);
    }

    [Fact]
    public void RemovePendingFacts_RewritesFile_ThenDeletesWhenEmpty()
    {
        var id = NewStory();
        WriteRaw(id, "bible.pending.michal.md",
            "- [f1] First fact. (s1)\n- [f2] Second fact. (s2)\n- [f3] Third fact.\n");

        _store.RemovePendingFacts(id, "michal", ["f2"]);

        var remaining = ReadRaw(id, "bible.pending.michal.md");
        Assert.Equal("- [f1] First fact. (s1)\n- [f3] Third fact.\n", remaining);

        _store.RemovePendingFacts(id, "michal", ["f1", "f3"]);

        Assert.False(Exists(id, "bible.pending.michal.md")); // deleted when no facts remain
    }

    // ---- generation log -----------------------------------------------------------------

    [Fact]
    public void GenerationLog_AppendsEntries_AndTracksDraftSessionIdFromOutline()
    {
        var id = NewStory();
        var at1 = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var at2 = new DateTimeOffset(2026, 8, 8, 12, 5, 0, TimeSpan.Zero);

        _store.AppendGenerationLog(id, "michal", new GenerationLogEntry("outline", "sess-outline", 0.04m, at1, true));
        _store.AppendGenerationLog(id, "michal", new GenerationLogEntry("scene s1", "sess-outline", 0.11m, at2, true));

        var log = _store.ReadGenerationLog(id, "michal")!;
        Assert.Equal("sess-outline", log.DraftSessionId);
        Assert.Equal(2, log.Invocations.Count);
        Assert.Equal(new GenerationLogEntry("outline", "sess-outline", 0.04m, at1, true), log.Invocations[0]);
        Assert.Equal(new GenerationLogEntry("scene s1", "sess-outline", 0.11m, at2, true), log.Invocations[1]);

        // on-disk shape per docs: { "draftSessionId": ..., "invocations": [ { stage, sessionId, costUsd, at, success } ] }
        var json = JsonNode.Parse(ReadRaw(id, "gen.michal.json"))!.AsObject();
        Assert.Equal("sess-outline", (string?)json["draftSessionId"]);
        var first = json["invocations"]!.AsArray()[0]!.AsObject();
        Assert.Equal("outline", (string?)first["stage"]);
        Assert.Equal(0.04m, (decimal?)first["costUsd"]);
        Assert.True((bool?)first["success"]);
        Assert.NotNull((string?)first["at"]);
    }

    [Fact]
    public void GenerationLog_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.ReadGenerationLog(NewStory(), "michal"));
    }

    // ---- CreateStory + summaries --------------------------------------------------------

    [Fact]
    public void CreateStory_WritesSkeletonSessionAndRecollectionsFolder()
    {
        var id = _store.CreateStory("2026-08-09-new", "generic-fantasy", "kid1", "The New One", "pl");

        Assert.Equal("2026-08-09-new", id);
        Assert.True(Directory.Exists(Path.Combine(_root, id, "recollections")));

        var json = JsonNode.Parse(ReadRaw(id, "session.kid1.json"))!.AsObject();
        Assert.Equal(1, (int?)json["schema"]);
        Assert.Equal("generic-fantasy", (string?)json["universe"]);
        Assert.Equal("pl", (string?)json["language"]);
        Assert.Equal("The New One", (string?)json["title"]);
        Assert.Equal("", (string?)json["pov"]);
        Assert.Equal("", (string?)json["tone"]);
        Assert.Empty(json["beats"]!.AsArray());
        Assert.Empty(json["cast"]!.AsArray());
        Assert.Empty(json["skip"]!.AsArray());
        Assert.Empty(json["voiceOverrides"]!.AsObject());
        Assert.Empty(json["sources"]!["primary"]!.AsArray());
        Assert.Empty(json["sources"]!["background"]!.AsArray());

        // a second variant in the same folder is the multi-kid case and must work
        _store.CreateStory("2026-08-09-new", "generic-fantasy", "kid2", "The New One", "pl");
        // recreating an existing variant must not
        Assert.Throws<InvalidOperationException>(
            () => _store.CreateStory("2026-08-09-new", "generic-fantasy", "kid1", "x", "pl"));
    }

    [Fact]
    public void VariantStage_TracksFurthestArtifact()
    {
        var id = _store.CreateStory("2026-08-09-stages", "generic-fantasy", "michal", "Stages", "en");

        Assert.Equal("spec", Stage());
        WriteRaw(id, "outline.michal.md", "## Scene s1: T\n- targetWords: 100\n");
        Assert.Equal("outline", Stage());
        WriteRaw(id, "draft.michal.md", "<!-- scene:s1 -->\nProse.\n");
        Assert.Equal("draft", Stage());
        WriteRaw(id, Path.Combine("audio", "michal.mp3"), "not really mp3 bytes");
        Assert.Equal("audio", Stage());

        string Stage() => Assert.Single(_store.GetStory(id)!.Variants).Stage;
    }

    [Fact]
    public void BareSessionJson_IsNotAVariant()
    {
        var id = NewStory();
        WriteRaw(id, "session.json", "{ \"schema\": 1 }");

        Assert.Empty(_store.GetStory(id)!.Variants);
    }

    [Fact]
    public void ListStories_ReadsTitleUniversePovLanguageFromSession()
    {
        var id = NewStory("2026-08-08-marrowfield");
        WriteRaw(id, "session.michal.json",
            "{ \"universe\": \"generic-fantasy\", \"language\": \"en\", \"title\": \"The Gem of Marrowfield\", \"pov\": \"corin\" }");

        var story = Assert.Single(_store.ListStories());
        Assert.Equal("2026-08-08-marrowfield", story.Id);
        Assert.Equal("The Gem of Marrowfield", story.Title);
        Assert.Equal("generic-fantasy", story.Universe);
        var variant = Assert.Single(story.Variants);
        Assert.Equal("michal", variant.Variant);
        Assert.Equal("corin", variant.Pov);
        Assert.Equal("en", variant.Language);
        Assert.Equal("spec", variant.Stage);
    }

    // ---- recollections ------------------------------------------------------------------

    [Fact]
    public void SaveRecollection_WritesOnce_ThenIsImmutable()
    {
        var id = NewStory();
        using var upload = new MemoryStream("nagranie testowe"u8.ToArray());

        var relative = _store.SaveRecollection(id, "kid1.txt", upload);

        Assert.Equal("recollections/kid1.txt", relative);
        Assert.Equal("nagranie testowe", ReadRaw(id, Path.Combine("recollections", "kid1.txt")));
        Assert.Contains("kid1.txt", _store.ListRecollections(id));
        Assert.Throws<InvalidOperationException>(
            () => _store.SaveRecollection(id, "kid1.txt", new MemoryStream()));
    }
}

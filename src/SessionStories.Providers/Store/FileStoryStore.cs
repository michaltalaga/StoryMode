using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SessionStories.Core.Stories;

namespace SessionStories.Providers.Store;

/// <summary>
/// File-backed <see cref="IStoryStore"/>. No caching, no locks: every call re-reads disk so
/// hand-edits are always visible; writes are atomic (same-directory .tmp + move) and splices
/// re-read the draft immediately before writing so sibling scenes are never clobbered.
/// </summary>
public sealed class FileStoryStore(string storiesRoot) : IStoryStore
{
    public string StoriesRoot { get; } = Path.GetFullPath(storiesRoot);

    // Marker must occupy a whole line; scene ids are permanent (s1, s2, …) and never renumbered.
    private static readonly Regex MarkerLine =
        new(@"^[ \t]*<!--\s*scene:(s\d+)\s*-->[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex OutlineHeading =
        new(@"^##\s+Scene\s+(s\d+)\s*:\s*(.*?)\s*\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TargetWordsBullet =
        new(@"^-\s*targetWords\s*:\s*(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex VerifyBullet =
        new(@"^\s*-\s*\[([^\]\r\n]+)\]\s*(.*?)\s*$", RegexOptions.Compiled);

    private static readonly Regex PendingBullet =
        new(@"^-\s*\[(f\d+)\]\s*(.*?)(?:\s*\((s\d+)\))?\s*$", RegexOptions.Compiled);

    private static readonly Regex SceneIdShape = new(@"^s\d+$", RegexOptions.Compiled);

    // Comments and trailing commas are legal in hand-edited session files.
    private static readonly JsonDocumentOptions TolerantJson =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public IReadOnlyList<StorySummary> ListStories()
    {
        if (!Directory.Exists(StoriesRoot))
            return [];
        return Directory.EnumerateDirectories(StoriesRoot)
            .Select(d => BuildSummary(Path.GetFileName(d)!))
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    public StorySummary? GetStory(string storyId) => BuildSummary(storyId);

    public string CreateStory(string slug, string universe, string variant, string title, string language)
    {
        ValidateName(slug, nameof(slug));
        ValidateName(variant, nameof(variant));
        var dir = Path.Combine(StoriesRoot, slug);
        var sessionPath = Path.Combine(dir, $"session.{variant}.json");
        if (File.Exists(sessionPath))
            throw new InvalidOperationException($"Variant '{variant}' already exists for story '{slug}'.");
        Directory.CreateDirectory(Path.Combine(dir, "recollections"));

        // Mirrors the shape of library/stories/2026-08-08-marrowfield/session.michal.json.
        var skeleton = new JsonObject
        {
            ["schema"] = 1,
            ["inputType"] = "",
            ["universe"] = universe,
            ["language"] = language,
            ["title"] = title,
            ["pov"] = "",
            ["tone"] = "",
            ["targetMinutes"] = null,
            ["voice"] = "",
            ["voiceOverrides"] = new JsonObject(),
            ["sources"] = new JsonObject
            {
                ["primary"] = new JsonArray(),
                ["background"] = new JsonArray(),
            },
            ["cast"] = new JsonArray(),
            ["stakes"] = "",
            ["beats"] = new JsonArray(),
            ["outcome"] = "",
            ["skip"] = new JsonArray(),
        };
        AtomicWrite(sessionPath, skeleton.ToJsonString(IndentedJson) + "\n");
        return slug;
    }

    public FileContent? ReadFile(string storyId, string relativePath)
    {
        var path = ResolveStoryPath(storyId, relativePath, requireStory: false);
        if (!File.Exists(path))
            return null;
        return new FileContent(File.ReadAllText(path), ComputeETag(path));
    }

    public void WriteFile(string storyId, string relativePath, string text, string? expectedETag = null)
    {
        var path = ResolveStoryPath(storyId, relativePath, requireStory: true);
        CheckETag(path, expectedETag);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Raw text is written verbatim — session JSON passes through untouched so hand-written
        // comments and unknown fields survive the round-trip.
        AtomicWrite(path, text);
    }

    public string SaveRecollection(string storyId, string fileName, Stream content)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException($"Invalid recollection file name '{fileName}'.", nameof(fileName));
        var dir = Path.Combine(RequireStoryDir(storyId), "recollections");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
            throw new InvalidOperationException($"Recollection '{fileName}' already exists; recollections are immutable once written.");
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            content.CopyTo(fs);
        // overwrite: false — immutability holds even if two uploads race.
        File.Move(tmp, path, overwrite: false);
        return "recollections/" + fileName;
    }

    public IReadOnlyList<string> ListRecollections(string storyId)
    {
        var dir = Path.Combine(RequireStoryDir(storyId), "recollections");
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir)
            .Select(f => Path.GetFileName(f)!)
            .Where(n => !n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<SceneDto> ReadDraftScenes(string storyId, string variant)
    {
        ValidateName(variant, nameof(variant));
        var dir = RequireStoryDir(storyId);
        var draftPath = Path.Combine(dir, $"draft.{variant}.md");
        if (!File.Exists(draftPath))
            return [];
        var draft = File.ReadAllText(draftPath);
        var blocks = ParseMarkerBlocks(draft);

        // The outline is optional — titles fall back to it only when present.
        var outlineTitles = ReadOutlineScenes(storyId, variant)
            .GroupBy(o => o.SceneId)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        var flags = ParseVerifyFlags(Path.Combine(dir, $"verify.{variant}.md"));

        var scenes = new List<SceneDto>(blocks.Count);
        foreach (var block in blocks)
        {
            var text = draft[block.ContentStart..block.End].Trim('\r', '\n');
            var title = HeadingTitle(text)
                ?? outlineTitles.GetValueOrDefault(block.SceneId)
                ?? "";
            IReadOnlyList<VerifyFlag> sceneFlags = flags.TryGetValue(block.SceneId, out var f) ? f : [];
            scenes.Add(new SceneDto(block.SceneId, title, text, sceneFlags));
        }
        return scenes;
    }

    public void WriteScene(string storyId, string variant, string sceneId, string text, string? expectedETag = null)
    {
        ValidateName(variant, nameof(variant));
        ValidateSceneId(sceneId);
        var path = Path.Combine(RequireStoryDir(storyId), $"draft.{variant}.md");
        if (!File.Exists(path))
            throw new FileNotFoundException($"draft.{variant}.md does not exist for story '{storyId}'.", path);
        CheckETag(path, expectedETag);

        var draft = File.ReadAllText(path);
        var blocks = ParseMarkerBlocks(draft);
        RequireUniqueMarkers(blocks, variant);
        var target = blocks.FirstOrDefault(b => b.SceneId == sceneId)
            ?? throw new InvalidOperationException($"Scene '{sceneId}' has no marker in draft.{variant}.md.");

        var content = StripLeadingMarker(text, sceneId);
        if (content.Length > 0 && !content.EndsWith('\n'))
            content += "\n";
        // Substring splice: bytes outside the target block are never touched.
        AtomicWrite(path, draft[..target.ContentStart] + content + draft[target.End..]);
    }

    public void SpliceSceneFromScratch(string storyId, string variant, string sceneId)
    {
        ValidateName(variant, nameof(variant));
        ValidateSceneId(sceneId);
        var dir = RequireStoryDir(storyId);
        var scratchPath = Path.Combine(dir, $"scene.{sceneId}.out.md");
        if (!File.Exists(scratchPath))
            throw new FileNotFoundException($"Scratch file scene.{sceneId}.out.md not found for story '{storyId}'.", scratchPath);

        var scratch = File.ReadAllText(scratchPath);
        var scratchBlocks = ParseMarkerBlocks(scratch);
        if (scratchBlocks.Count != 1 || scratchBlocks[0].SceneId != sceneId
            || scratch[..scratchBlocks[0].MarkerStart].Trim().Length > 0)
            throw new InvalidOperationException(
                $"scene.{sceneId}.out.md must contain exactly the '<!-- scene:{sceneId} -->' marker line followed by prose.");
        var newBlock = EnsureTrailingNewline(scratch[scratchBlocks[0].MarkerStart..]);

        var draftPath = Path.Combine(dir, $"draft.{variant}.md");
        // Splice-time re-read: the draft is read here, immediately before the atomic write, so
        // hand-edits made while the scene was generating are preserved.
        if (!File.Exists(draftPath))
        {
            AtomicWrite(draftPath, newBlock);
            File.Delete(scratchPath);
            return;
        }

        var draft = File.ReadAllText(draftPath);
        var blocks = ParseMarkerBlocks(draft);
        RequireUniqueMarkers(blocks, variant);

        string newDraft;
        var target = blocks.FirstOrDefault(b => b.SceneId == sceneId);
        if (target is not null)
        {
            AtomicWrite(Path.Combine(dir, $"draft.{variant}.{sceneId}.prev.md"), draft[target.MarkerStart..target.End]);
            newDraft = draft[..target.MarkerStart] + newBlock + draft[target.End..];
        }
        else
        {
            // Marker integrity: a missing marker with later scenes present (or a marker-less
            // non-empty draft) means a marker line was hand-deleted — appending would misplace
            // the scene and hide the hand-edit. Refuse; scratch is kept for retry.
            if (blocks.Count == 0 && draft.Trim().Length > 0)
                throw new InvalidOperationException(
                    $"draft.{variant}.md has content but no scene markers; a marker line was probably deleted by hand. Restore it before splicing '{sceneId}'.");
            var targetNumber = SceneNumber(sceneId);
            if (blocks.Any(b => SceneNumber(b.SceneId) > targetNumber))
                throw new InvalidOperationException(
                    $"Marker '<!-- scene:{sceneId} -->' is missing from draft.{variant}.md although later scenes exist; a marker line was probably deleted by hand. Restore it before splicing.");
            var prefix = draft.Length == 0 || draft.EndsWith('\n') ? draft : draft + "\n";
            newDraft = prefix + newBlock;
        }
        AtomicWrite(draftPath, newDraft);
        File.Delete(scratchPath);
    }

    public void ClearVerifyFindings(string storyId, string variant, string sceneId)
    {
        ValidateName(variant, nameof(variant));
        ValidateSceneId(sceneId);
        var path = Path.Combine(RequireStoryDir(storyId), $"verify.{variant}.md");
        if (!File.Exists(path))
            return;

        var sb = new StringBuilder();
        var removing = false;
        var removedSection = false;
        var remainingSections = 0;
        foreach (var (segment, line) in RawLines(File.ReadAllText(path)))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                removing = line[3..].Trim() == sceneId;
                if (removing)
                {
                    removedSection = true;
                    continue;
                }
                remainingSections++;
            }
            else if (removing)
            {
                continue; // the section's bullets (and blank lines) go with its heading
            }
            sb.Append(segment); // untouched lines keep their exact bytes, terminators included
        }

        // No section for this scene (e.g. a bare "No violations found." file) — leave it alone.
        if (!removedSection)
            return;
        if (remainingSections == 0)
            File.Delete(path);
        else
            AtomicWrite(path, sb.ToString());
    }

    public IReadOnlyList<OutlineScene> ReadOutlineScenes(string storyId, string variant)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"outline.{variant}.md");
        if (!File.Exists(path))
            return [];
        var text = File.ReadAllText(path);
        var matches = OutlineHeading.Matches(text);
        var scenes = new List<OutlineScene>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var blockEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            int? targetWords = null;
            var tw = TargetWordsBullet.Match(text[m.Index..blockEnd]);
            if (tw.Success && int.TryParse(tw.Groups[1].Value, out var words))
                targetWords = words;
            scenes.Add(new OutlineScene(m.Groups[1].Value, m.Groups[2].Value, targetWords));
        }
        return scenes;
    }

    public SessionInfo? ReadSessionInfo(string storyId, string variant)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"session.{variant}.json");
        if (!File.Exists(path))
            return null;
        if (JsonNode.Parse(File.ReadAllText(path), null, TolerantJson) is not JsonObject root)
            return null;

        var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
        if (root["voiceOverrides"] is JsonObject vo)
            foreach (var (key, value) in vo)
                if (value is JsonValue v && v.TryGetValue<double>(out var d))
                    overrides[key] = d;

        int? targetMinutes = null;
        if (root["targetMinutes"] is JsonValue tm)
        {
            if (tm.TryGetValue<int>(out var i))
                targetMinutes = i;
            else if (tm.TryGetValue<double>(out var dbl))
                targetMinutes = (int)Math.Round(dbl);
            else if (tm.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                targetMinutes = parsed;
        }

        var voice = GetString(root, "voice");
        return new SessionInfo(
            GetString(root, "universe") ?? "",
            GetString(root, "language") ?? "",
            GetString(root, "pov") ?? "",
            string.IsNullOrWhiteSpace(voice) ? null : voice,
            overrides,
            targetMinutes);
    }

    public IReadOnlyList<PendingFact> ReadPendingFacts(string storyId, string variant)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"bible.pending.{variant}.md");
        if (!File.Exists(path))
            return [];
        var facts = new List<PendingFact>();
        foreach (var (_, line) in RawLines(File.ReadAllText(path)))
        {
            var m = PendingBullet.Match(line);
            if (m.Success)
                facts.Add(new PendingFact(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null));
        }
        return facts;
    }

    public void RemovePendingFacts(string storyId, string variant, IReadOnlyCollection<string> factIds)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"bible.pending.{variant}.md");
        if (!File.Exists(path))
            return;
        var sb = new StringBuilder();
        var remainingFacts = 0;
        foreach (var (segment, line) in RawLines(File.ReadAllText(path)))
        {
            var m = PendingBullet.Match(line);
            if (m.Success && factIds.Contains(m.Groups[1].Value))
                continue;
            if (m.Success)
                remainingFacts++;
            sb.Append(segment); // untouched lines keep their exact bytes, terminators included
        }
        if (remainingFacts == 0)
            File.Delete(path);
        else
            AtomicWrite(path, sb.ToString());
    }

    public void AppendGenerationLog(string storyId, string variant, GenerationLogEntry entry)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"gen.{variant}.json");
        JsonObject root;
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), null, TolerantJson) is JsonObject existing)
            root = existing;
        else
            root = new JsonObject { ["draftSessionId"] = null, ["invocations"] = new JsonArray() };

        if (root["invocations"] is not JsonArray invocations)
        {
            invocations = new JsonArray();
            root["invocations"] = invocations;
        }
        invocations.Add(new JsonObject
        {
            ["stage"] = entry.Stage,
            ["sessionId"] = entry.SessionId,
            ["costUsd"] = entry.CostUsd is { } cost ? JsonValue.Create(cost) : null,
            ["at"] = entry.At.ToString("O", CultureInfo.InvariantCulture),
            ["success"] = entry.Success,
        });
        // The outline invocation's session id is the draft session scene stages resume with.
        if (entry is { Stage: "outline", Success: true, SessionId: not null })
            root["draftSessionId"] = entry.SessionId;

        AtomicWrite(path, root.ToJsonString(IndentedJson) + "\n");
    }

    public GenerationLog? ReadGenerationLog(string storyId, string variant)
    {
        ValidateName(variant, nameof(variant));
        var path = Path.Combine(RequireStoryDir(storyId), $"gen.{variant}.json");
        if (!File.Exists(path))
            return null;
        if (JsonNode.Parse(File.ReadAllText(path), null, TolerantJson) is not JsonObject root)
            return null;

        var entries = new List<GenerationLogEntry>();
        if (root["invocations"] is JsonArray invocations)
            foreach (var node in invocations)
            {
                if (node is not JsonObject o)
                    continue;
                decimal? cost = o["costUsd"] is JsonValue cv && cv.TryGetValue<decimal>(out var c) ? c : null;
                var at = o["at"] is JsonValue av && av.TryGetValue<string>(out var ats)
                    && DateTimeOffset.TryParse(ats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedAt)
                        ? parsedAt : default;
                var success = o["success"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
                entries.Add(new GenerationLogEntry(GetString(o, "stage") ?? "", GetString(o, "sessionId"), cost, at, success));
            }
        return new GenerationLog(GetString(root, "draftSessionId"), entries);
    }

    // ---- private helpers ----------------------------------------------------------------

    private sealed record MarkerBlock(string SceneId, int MarkerStart, int ContentStart, int End);

    private StorySummary? BuildSummary(string storyId)
    {
        var dir = StoryDir(storyId);
        if (!Directory.Exists(dir))
            return null;
        var variants = new List<VariantSummary>();
        var title = "";
        var universe = "";
        // "session.json" never matches "session.*.json" — a bare session file is not a variant.
        foreach (var file in Directory.EnumerateFiles(dir, "session.*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var variant = name["session.".Length..^".json".Length];
            if (variant.Length == 0)
                continue;
            JsonObject? session = null;
            try
            {
                session = JsonNode.Parse(File.ReadAllText(file), null, TolerantJson) as JsonObject;
            }
            catch (JsonException)
            {
                // an unparseable session still counts as a variant; typed fields stay empty
            }
            if (title.Length == 0)
                title = GetString(session, "title") ?? "";
            if (universe.Length == 0)
                universe = GetString(session, "universe") ?? "";
            variants.Add(new VariantSummary(
                variant,
                GetString(session, "pov") ?? "",
                GetString(session, "language") ?? "",
                DetectStage(dir, variant)));
        }
        return new StorySummary(storyId, title.Length > 0 ? title : storyId, universe, variants);
    }

    /// <summary>Stage is the furthest artifact present: spec | outline | draft | audio.</summary>
    private static string DetectStage(string dir, string variant) =>
        File.Exists(Path.Combine(dir, "audio", variant + ".mp3")) ? "audio"
        : File.Exists(Path.Combine(dir, $"draft.{variant}.md")) ? "draft"
        : File.Exists(Path.Combine(dir, $"outline.{variant}.md")) ? "outline"
        : "spec";

    private string StoryDir(string storyId)
    {
        ValidateName(storyId, nameof(storyId));
        return Path.Combine(StoriesRoot, storyId);
    }

    private string RequireStoryDir(string storyId)
    {
        var dir = StoryDir(storyId);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Story '{storyId}' not found under {StoriesRoot}.");
        return dir;
    }

    private string ResolveStoryPath(string storyId, string relativePath, bool requireStory)
    {
        var dir = requireStory ? RequireStoryDir(storyId) : StoryDir(storyId);
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path must not be empty.", nameof(relativePath));
        var full = Path.GetFullPath(Path.Combine(dir, relativePath));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path '{relativePath}' escapes the story folder.", nameof(relativePath));
        return full;
    }

    private static void ValidateName(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || Path.GetFileName(value) != value
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid name '{value}'.", paramName);
    }

    private static void ValidateSceneId(string sceneId)
    {
        if (sceneId is null || !SceneIdShape.IsMatch(sceneId))
            throw new ArgumentException($"Invalid scene id '{sceneId}'; expected s1, s2, …", nameof(sceneId));
    }

    private static int SceneNumber(string sceneId) => int.Parse(sceneId[1..], CultureInfo.InvariantCulture);

    private static List<MarkerBlock> ParseMarkerBlocks(string text)
    {
        var matches = MarkerLine.Matches(text);
        var blocks = new List<MarkerBlock>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var newline = text.IndexOf('\n', m.Index + m.Length);
            var contentStart = newline < 0 ? text.Length : newline + 1;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            blocks.Add(new MarkerBlock(m.Groups[1].Value, m.Index, contentStart, end));
        }
        return blocks;
    }

    private static void RequireUniqueMarkers(IEnumerable<MarkerBlock> blocks, string variant)
    {
        var duplicate = blocks.GroupBy(b => b.SceneId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"draft.{variant}.md contains duplicate markers for scene '{duplicate.Key}'.");
    }

    private static string StripLeadingMarker(string text, string sceneId)
    {
        var blocks = ParseMarkerBlocks(text);
        if (blocks.Count == 1 && blocks[0].SceneId == sceneId && text[..blocks[0].MarkerStart].Trim().Length == 0)
            return text[blocks[0].ContentStart..];
        if (blocks.Count > 0)
            throw new InvalidOperationException("Scene text must not contain scene marker lines.");
        return text;
    }

    private static string? HeadingTitle(string sceneText)
    {
        foreach (var (_, line) in RawLines(sceneText))
        {
            if (line.Trim().Length == 0)
                continue;
            return line.StartsWith("## ", StringComparison.Ordinal) ? line[3..].Trim() : null;
        }
        return null;
    }

    private static Dictionary<string, IReadOnlyList<VerifyFlag>> ParseVerifyFlags(string verifyPath)
    {
        var result = new Dictionary<string, IReadOnlyList<VerifyFlag>>(StringComparer.Ordinal);
        if (!File.Exists(verifyPath))
            return result;
        List<VerifyFlag>? current = null;
        foreach (var (_, line) in RawLines(File.ReadAllText(verifyPath)))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var id = line[3..].Trim();
                if (id.Length > 0)
                {
                    var list = new List<VerifyFlag>();
                    result[id] = list;
                    current = list;
                }
                continue;
            }
            var m = VerifyBullet.Match(line);
            if (m.Success && current is not null)
                current.Add(new VerifyFlag(m.Groups[1].Value, m.Groups[2].Value));
        }
        return result;
    }

    /// <summary>Yields each line's raw segment (terminator included) plus its trimmed content.</summary>
    private static IEnumerable<(string Segment, string Line)> RawLines(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var newline = text.IndexOf('\n', pos);
            var end = newline < 0 ? text.Length : newline + 1;
            var segment = text[pos..end];
            yield return (segment, segment.TrimEnd('\r', '\n'));
            pos = end;
        }
    }

    private static string EnsureTrailingNewline(string text) =>
        text.EndsWith('\n') ? text : text + "\n";

    private static string? GetString(JsonObject? obj, string name) =>
        obj?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string ComputeETag(string path)
    {
        var info = new FileInfo(path);
        return $"{info.LastWriteTimeUtc.Ticks}-{info.Length}";
    }

    private static void CheckETag(string path, string? expected)
    {
        if (expected is null)
            return;
        if (!File.Exists(path))
            throw new ETagMismatchException("", "");
        var current = ComputeETag(path);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
            throw new ETagMismatchException(File.ReadAllText(path), current);
    }

    private static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SessionStories.Core.Stories;
using SessionStories.Core.Universes;

namespace SessionStories.Providers.Store;

/// <summary>
/// File-backed <see cref="IUniverseStore"/>. Editable files are whitelisted per docs/api.md
/// (constraints.md | bible.md | characters.md | voices.json | tones/&lt;tone&gt;.md); everything
/// else — including any traversal attempt — is rejected. Same ETag + atomic-write rules as the
/// story store.
/// </summary>
public sealed class FileUniverseStore(string universesRoot) : IUniverseStore
{
    private static readonly string[] AllowedRootFiles = ["constraints.md", "bible.md", "characters.md", "voices.json"];

    // Single path segment under tones/, .md only; the explicit ".." check below is belt-and-braces.
    private static readonly Regex ToneFileName =
        new(@"^tones/[A-Za-z0-9][A-Za-z0-9._ -]*\.md$", RegexOptions.Compiled);

    private static readonly JsonDocumentOptions TolerantJson =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public string UniversesRoot { get; } = Path.GetFullPath(universesRoot);

    public IReadOnlyList<string> ListUniverses()
    {
        if (!Directory.Exists(UniversesRoot))
            return [];
        return Directory.EnumerateDirectories(UniversesRoot)
            .Select(d => Path.GetFileName(d)!)
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public FileContent? ReadFile(string universeId, string relativePath)
    {
        var path = ResolvePath(universeId, relativePath, requireUniverse: false);
        if (!File.Exists(path))
            return null;
        return new FileContent(File.ReadAllText(path), ComputeETag(path));
    }

    public void WriteFile(string universeId, string relativePath, string text, string? expectedETag = null)
    {
        var path = ResolvePath(universeId, relativePath, requireUniverse: true);
        CheckETag(path, expectedETag);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, text);
    }

    public void AppendBibleFacts(string universeId, string heading, IReadOnlyList<string> factLines)
    {
        var path = Path.Combine(RequireUniverseDir(universeId), "bible.md");
        var sb = new StringBuilder(File.Exists(path) ? File.ReadAllText(path) : "");
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n')
            sb.Append('\n');
        if (sb.Length > 0)
            sb.Append('\n'); // blank line between the existing canon and the new dated heading
        sb.Append("## ").Append(heading).Append("\n\n");
        foreach (var fact in factLines)
        {
            var line = fact.TrimEnd();
            if (!line.StartsWith("- ", StringComparison.Ordinal))
                line = "- " + line;
            sb.Append(line).Append('\n');
        }
        AtomicWrite(path, sb.ToString());
    }

    public VoiceCatalog? ReadVoices(string universeId)
    {
        var path = Path.Combine(UniverseDir(universeId), "voices.json");
        if (!File.Exists(path))
            return null;
        // Tolerant parse: unknown fields such as "_notes" are simply ignored.
        if (JsonNode.Parse(File.ReadAllText(path), null, TolerantJson) is not JsonObject root)
            return null;

        var voices = new Dictionary<string, VoiceEntry>(StringComparer.Ordinal);
        if (root["voices"] is JsonObject entries)
            foreach (var (name, node) in entries)
            {
                if (node is not JsonObject entry)
                    continue;
                var languages = new List<string>();
                if (entry["languages"] is JsonArray langs)
                    foreach (var lang in langs)
                        if (lang is JsonValue lv && lv.TryGetValue<string>(out var s))
                            languages.Add(s);
                voices[name] = new VoiceEntry(
                    GetString(entry, "provider") ?? "",
                    languages,
                    GetString(entry, "referenceWav") ?? "",
                    GetDouble(entry, "exaggeration"),
                    GetDouble(entry, "cfg"));
            }
        return new VoiceCatalog(voices, GetString(root, "default"));
    }

    // ---- private helpers ----------------------------------------------------------------

    private string UniverseDir(string universeId)
    {
        if (string.IsNullOrWhiteSpace(universeId) || universeId is "." or ".."
            || Path.GetFileName(universeId) != universeId
            || universeId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid universe id '{universeId}'.", nameof(universeId));
        return Path.Combine(UniversesRoot, universeId);
    }

    private string RequireUniverseDir(string universeId)
    {
        var dir = UniverseDir(universeId);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Universe '{universeId}' not found under {UniversesRoot}.");
        return dir;
    }

    private string ResolvePath(string universeId, string relativePath, bool requireUniverse)
    {
        var dir = requireUniverse ? RequireUniverseDir(universeId) : UniverseDir(universeId);
        if (!IsAllowedName(relativePath))
            throw new ArgumentException(
                $"'{relativePath}' is not an editable universe file (allowed: constraints.md, bible.md, characters.md, voices.json, tones/<tone>.md).",
                nameof(relativePath));
        return Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsAllowedName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (AllowedRootFiles.Contains(name, StringComparer.Ordinal))
            return true;
        return name.StartsWith("tones/", StringComparison.Ordinal)
            && !name.Contains("..", StringComparison.Ordinal)
            && ToneFileName.IsMatch(name);
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? GetDouble(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

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

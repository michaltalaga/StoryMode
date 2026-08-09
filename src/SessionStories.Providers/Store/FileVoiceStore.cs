using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionStories.Core.Stories;
using SessionStories.Core.Voices;

namespace SessionStories.Providers.Store;

/// <summary>
/// File-backed <see cref="IVoiceStore"/> rooted at the library: <c>voices.json</c> (catalog),
/// <c>voices/</c> (reference wavs), <c>voice-previews/</c> (rendered samples) and
/// <c>voice-cache/</c> (conditionals derived from a reference wav). Same ETag + atomic-write
/// rules as the story store; structured edits round-trip through <see cref="JsonNode"/> so
/// hand-written fields (<c>_notes</c>, future knobs) survive.
/// </summary>
public sealed class FileVoiceStore(string libraryRoot, string? voiceCacheDir = null) : IVoiceStore
{
    private static readonly JsonDocumentOptions TolerantJson =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // Relaxed encoder: the catalog is hand-editable, so Polish text must stay readable in the file.
    private static readonly JsonSerializerOptions PrettyJson =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _libraryRoot = Path.GetFullPath(libraryRoot);

    public string VoicesRoot { get; } = Path.Combine(Path.GetFullPath(libraryRoot), "voices");

    private string VoiceCacheRoot { get; } = string.IsNullOrWhiteSpace(voiceCacheDir)
        ? Path.Combine(Path.GetFullPath(libraryRoot), "voice-cache")
        : Path.GetFullPath(voiceCacheDir);

    private string CatalogPath => Path.Combine(_libraryRoot, "voices.json");

    private string PreviewsRoot => Path.Combine(_libraryRoot, "voice-previews");

    public VoiceCatalog? ReadCatalog()
    {
        if (ReadRoot() is not { } root)
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

    public FileContent? ReadCatalogFile()
        => File.Exists(CatalogPath) ? new FileContent(File.ReadAllText(CatalogPath), ComputeETag(CatalogPath)) : null;

    public void WriteCatalogFile(string text, string? expectedETag = null)
    {
        CheckETag(CatalogPath, expectedETag);
        Directory.CreateDirectory(_libraryRoot);
        AtomicWrite(CatalogPath, text);
    }

    public string? ResolveReferenceWav(string voiceId)
    {
        var entry = ReadCatalog()?.Voices.GetValueOrDefault(voiceId);
        if (entry is null || entry.ReferenceWav.Length == 0)
            return null;
        // Bare file name relative to library/voices — a catalog entry may not name anything outside it.
        var name = Path.GetFileName(entry.ReferenceWav);
        if (name.Length == 0)
            return null;
        var path = Path.Combine(VoicesRoot, name);
        return File.Exists(path) ? path : null;
    }

    public string PreviewPath(string voiceId) => Path.Combine(PreviewsRoot, SafeVoiceId(voiceId) + ".mp3");

    public string VoiceCachePath(string voiceId) => Path.Combine(VoiceCacheRoot, SafeVoiceId(voiceId));

    // ---- structured catalog edits --------------------------------------------------------

    public bool WriteVoice(string voiceId, VoiceEntryEdit edit)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot() ?? new JsonObject();
        var voices = RequireObject(root, "voices");
        var created = voices[voiceId] is not JsonObject;
        var entry = RequireObject(voices, voiceId);

        // Replace semantics: every known field is written, a null knob drops the property.
        // referenceWav and anything hand-written on the entry are deliberately left alone.
        entry["provider"] = edit.Provider ?? GetString(entry, "provider") ?? "";
        entry["languages"] = ToArray(edit.Languages ?? []);
        SetOrRemove(entry, "exaggeration", edit.Exaggeration);
        SetOrRemove(entry, "cfg", edit.Cfg);

        WriteRoot(root);
        return created;
    }

    public bool PatchVoice(string voiceId, VoiceEntryEdit edit)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject entry)
            return false;

        if (edit.Provider is { } provider)
            entry["provider"] = provider;
        if (edit.Languages is { } languages)
            entry["languages"] = ToArray(languages);
        if (edit.Exaggeration is { } exaggeration)
            entry["exaggeration"] = exaggeration;
        if (edit.Cfg is { } cfg)
            entry["cfg"] = cfg;

        WriteRoot(root!);
        return true;
    }

    public bool DeleteVoice(string voiceId, bool deleteReferenceWav = false)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject entry)
            return false;

        var wavName = Path.GetFileName(GetString(entry, "referenceWav") ?? "");
        voices.Remove(voiceId);
        // A dangling default would break every render that falls back to it.
        if (string.Equals(GetString(root!, "default"), voiceId, StringComparison.Ordinal))
            root!["default"] = null;
        WriteRoot(root!);

        DeleteDerived(voiceId);
        if (deleteReferenceWav && wavName.Length > 0)
            TryDeleteFile(Path.Combine(VoicesRoot, wavName));
        return true;
    }

    public bool SetDefaultVoice(string voiceId)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject)
            return false;
        root!["default"] = voiceId;
        WriteRoot(root);
        return true;
    }

    public bool SaveReferenceWav(string voiceId, Stream wav)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject entry)
            return false;

        Directory.CreateDirectory(VoicesRoot);
        var fileName = voiceId + ".wav";
        var path = Path.Combine(VoicesRoot, fileName);
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
            wav.CopyTo(file);
        File.Move(tmp, path, overwrite: true);

        entry["referenceWav"] = fileName;
        WriteRoot(root!);

        // The conditionals cache never re-reads its source wav, and the preview was rendered from
        // the old one — both must go or the "new" voice would keep sounding like the old one.
        DeleteDerived(voiceId);
        return true;
    }

    // ---- private helpers ----------------------------------------------------------------

    private void DeleteDerived(string voiceId)
    {
        TryDeleteFile(PreviewPath(voiceId));
        var cache = VoiceCachePath(voiceId);
        try
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
        catch (IOException)
        {
            // A render may hold the files open; the catalog edit still stands.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(CatalogPath))
            return null;
        // Tolerant parse: unknown fields such as "_notes" are simply carried through.
        return JsonNode.Parse(File.ReadAllText(CatalogPath), null, TolerantJson) as JsonObject;
    }

    private void WriteRoot(JsonObject root)
    {
        Directory.CreateDirectory(_libraryRoot);
        AtomicWrite(CatalogPath, root.ToJsonString(PrettyJson) + "\n");
    }

    private static JsonObject RequireObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static void SetOrRemove(JsonObject entry, string name, double? value)
    {
        if (value is { } number)
            entry[name] = number;
        else
            entry.Remove(name);
    }

    private static string SafeVoiceId(string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId) || voiceId is "." or ".."
            || Path.GetFileName(voiceId) != voiceId
            || voiceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid voice id '{voiceId}'.", nameof(voiceId));
        return voiceId;
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

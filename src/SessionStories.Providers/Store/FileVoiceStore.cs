using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionStories.Core.Stories;
using SessionStories.Core.Tts;
using SessionStories.Core.Voices;

namespace SessionStories.Providers.Store;

/// <summary>
/// File-backed <see cref="IVoiceStore"/> rooted at the library: <c>voices.json</c> (catalog),
/// <c>voices/</c> (reference wavs), <c>voice-previews/</c> (rendered samples) and
/// <c>voice-cache/</c> (conditionals derived from a reference wav). Same ETag + atomic-write
/// rules as the story store; structured edits round-trip through <see cref="JsonNode"/> so
/// hand-written fields (<c>_notes</c>, future settings) survive.
/// <para>
/// Reads accept both schemas. Schema 1 entries — <c>provider</c>/<c>languages</c>/<c>referenceWav</c>
/// plus raw <c>exaggeration</c>/<c>cfg</c> knobs — are mapped to the installed-voice model in memory,
/// and any structured write converges that entry to schema 2. A read never rewrites the file.
/// </para>
/// </summary>
public sealed class FileVoiceStore(string libraryRoot, string? voiceCacheDir = null) : IVoiceStore
{
    /// <summary>Schema 1 had no engine field, so its presence is the discriminator.</summary>
    public const int CurrentSchema = 2;

    private const string DefaultEngineId = "chatterbox-onnx";

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

        var voices = new Dictionary<string, InstalledVoice>(StringComparer.Ordinal);
        if (root["voices"] is JsonObject entries)
            foreach (var (id, node) in entries)
                if (node is JsonObject entry)
                    voices[id] = ParseEntry(id, entry);

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
        var voice = ReadCatalog()?.Voices.GetValueOrDefault(voiceId);
        if (voice is null || !voice.EngineData.TryGetValue("referenceWav", out var configured))
            return null;
        // Bare file name relative to library/voices — a catalog entry may not name anything outside it.
        var name = Path.GetFileName(configured);
        if (name.Length == 0)
            return null;
        var path = Path.Combine(VoicesRoot, name);
        return File.Exists(path) ? path : null;
    }

    public string PreviewPath(string voiceId) => Path.Combine(PreviewsRoot, SafeVoiceId(voiceId) + ".mp3");

    public string VoiceCachePath(string voiceId) => Path.Combine(VoiceCacheRoot, SafeVoiceId(voiceId));

    // ---- structured catalog edits --------------------------------------------------------

    public bool UpsertVoice(InstalledVoice voice)
    {
        SafeVoiceId(voice.Id);
        var root = ReadRoot() ?? new JsonObject();
        var voices = RequireObject(root, "voices");
        var created = voices[voice.Id] is not JsonObject;
        // Mutate the existing object so hand-written fields we know nothing about survive.
        var entry = RequireObject(voices, voice.Id);

        entry["name"] = voice.Name;
        entry["description"] = voice.Description;
        entry["locale"] = voice.Locale;
        entry["style"] = voice.Style;
        entry["engine"] = voice.EngineId;
        entry["engineData"] = ToObject(voice.EngineData);
        if (voice.Source is { } source)
            entry["source"] = new JsonObject
            {
                ["shelf"] = source.Shelf,
                ["license"] = source.License,
                ["attribution"] = source.Attribution,
            };
        else
            entry.Remove("source");

        // Schema 1 leftovers on this entry: their meaning now lives in engine/locale/style/engineData,
        // so leaving them would give one voice two contradictory sources of truth.
        foreach (var legacy in LegacyEntryFields)
            entry.Remove(legacy);

        root["schema"] = CurrentSchema;
        WriteRoot(root);
        return created;
    }

    public bool PatchVoice(string voiceId, VoiceEdit edit)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject entry)
            return false;

        // Read through the same migration as everything else, then write the whole entry back:
        // a schema 1 row converges to schema 2 rather than growing a "name" on top of "provider".
        var current = ParseEntry(voiceId, entry);
        UpsertVoice(current with
        {
            Name = string.IsNullOrWhiteSpace(edit.Name) ? current.Name : edit.Name.Trim(),
            Description = edit.Description?.Trim() ?? current.Description,
            Style = edit.Style is null ? current.Style : VoiceStyle.Normalize(edit.Style),
        });
        return true;
    }

    public bool DeleteVoice(string voiceId, bool deleteReferenceWav = false)
    {
        SafeVoiceId(voiceId);
        var root = ReadRoot();
        if (root?["voices"] is not JsonObject voices || voices[voiceId] is not JsonObject entry)
            return false;

        var wavName = Path.GetFileName(ParseEntry(voiceId, entry).EngineData.GetValueOrDefault("referenceWav", ""));
        voices.Remove(voiceId);
        // A dangling default would break every render that falls back to it.
        if (string.Equals(GetString(root!, "default"), voiceId, StringComparison.Ordinal))
            root!["default"] = null;
        WriteRoot(root!);

        InvalidateDerived(voiceId);
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

    public string SaveReferenceWav(string voiceId, Stream wav)
    {
        SafeVoiceId(voiceId);
        Directory.CreateDirectory(VoicesRoot);
        var fileName = voiceId + ".wav";
        var path = Path.Combine(VoicesRoot, fileName);
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
            wav.CopyTo(file);
        File.Move(tmp, path, overwrite: true);

        InvalidateDerived(voiceId);
        return fileName;
    }

    public void InvalidateDerived(string voiceId)
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

    // ---- parsing / migration -------------------------------------------------------------

    private static readonly string[] LegacyEntryFields =
        ["provider", "languages", "referenceWav", "exaggeration", "cfg"];

    /// <summary>Reads either schema into the installed-voice model. Never writes.</summary>
    private static InstalledVoice ParseEntry(string id, JsonObject entry)
    {
        var engineData = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry["engineData"] is JsonObject data)
            foreach (var (key, node) in data)
                if (node is JsonValue value && value.TryGetValue<string>(out var text))
                    engineData[key] = text;

        var isSchema2 = GetString(entry, "engine") is not null;
        if (!isSchema2)
        {
            // Schema 1: the reference wav was a first-class field, and knobs were raw numbers.
            if (GetString(entry, "referenceWav") is { Length: > 0 } wav)
                engineData["referenceWav"] = wav;
        }

        var locale = isSchema2
            ? VoiceLocale.Normalize(GetString(entry, "locale"))
            : VoiceLocale.Normalize(FirstLanguage(entry));

        return new InstalledVoice(
            Id: id,
            Name: GetString(entry, "name") is { Length: > 0 } name ? name : HumanizeId(id),
            Description: GetString(entry, "description") ?? "",
            Locale: locale,
            Style: isSchema2
                ? VoiceStyle.Normalize(GetString(entry, "style"))
                : StyleFromLegacyKnobs(GetDouble(entry, "exaggeration")),
            EngineId: GetString(entry, "engine") is { Length: > 0 } engine ? engine
                : GetString(entry, "provider") is { Length: > 0 } provider ? provider
                : DefaultEngineId,
            EngineData: engineData,
            Source: ParseProvenance(entry));
    }

    private static VoiceProvenance? ParseProvenance(JsonObject entry)
        => entry["source"] is JsonObject source
            ? new VoiceProvenance(
                GetString(source, "shelf") ?? "",
                GetString(source, "license") ?? "",
                GetString(source, "attribution") ?? "")
            : null;

    private static string? FirstLanguage(JsonObject entry)
    {
        if (entry["languages"] is not JsonArray langs)
            return null;
        foreach (var lang in langs)
            if (lang is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0)
                return s;
        return null;
    }

    /// <summary>Schema 1 stored raw exaggeration; map it onto the nearest named style.</summary>
    private static string StyleFromLegacyKnobs(double? exaggeration) => exaggeration switch
    {
        null => VoiceStyle.Default,
        < 0.5 => VoiceStyle.Calm,
        < 0.8 => VoiceStyle.Natural,
        _ => VoiceStyle.Lively,
    };

    /// <summary>"narrator-pl-gosia" → "Narrator Pl Gosia". Only ever a starting point — it is renamable.</summary>
    private static string HumanizeId(string id)
        => string.Join(' ', id.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));

    // ---- private helpers ----------------------------------------------------------------

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

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
            obj[key] = value;
        return obj;
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

using System.Text.Json;
using System.Text.Json.Nodes;
using SessionStories.Core.Voices;

namespace SessionStories.Providers.Voices;

/// <summary>
/// The curated shelf, read from <c>&lt;repo&gt;/voice-gallery/manifest.json</c>. App content rather than
/// library content: it ships with the code, so it is versioned alongside the engines that can install
/// it and is unaffected by where a reader points their library.
/// <para>
/// Re-read per call, like every other store here, so editing the manifest shows up without a restart.
/// </para>
/// </summary>
public sealed class ShelfVoiceGallery(string galleryRoot) : IVoiceGallery
{
    private static readonly JsonDocumentOptions TolerantJson =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _root = Path.GetFullPath(galleryRoot);

    public string ManifestPath => Path.Combine(_root, "manifest.json");

    /// <summary>Pre-rendered samples, one per offer — the reason auditioning is instant.</summary>
    public string SamplesRoot => Path.Combine(_root, "samples");

    /// <summary>Reference recordings that ship with the repo (cloning engines).</summary>
    public string WavsRoot => Path.Combine(_root, "wavs");

    public IReadOnlyList<VoiceLanguage> ListLanguages()
        => [.. ReadOffers()
            .GroupBy(offer => offer.Locale, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VoiceLanguage(group.Key, group.Count()))
            .OrderBy(language => language.Locale, StringComparer.Ordinal)];

    public IReadOnlyList<VoiceOffer> ListOffers(string locale)
        => [.. ReadOffers().Where(offer => string.Equals(offer.Locale, locale, StringComparison.OrdinalIgnoreCase))];

    public VoiceOffer? FindOffer(string key)
        => ReadOffers().FirstOrDefault(offer => string.Equals(offer.Key, key, StringComparison.Ordinal));

    public string? SamplePath(string key)
    {
        if (FindOffer(key)?.Plan.SampleFile is not { Length: > 0 } file)
            return null;
        // A manifest may not reach outside the samples folder.
        var path = Path.Combine(SamplesRoot, Path.GetFileName(file));
        return File.Exists(path) ? path : null;
    }

    private IReadOnlyList<VoiceOffer> ReadOffers()
    {
        if (!File.Exists(ManifestPath))
            return [];
        if (JsonNode.Parse(File.ReadAllText(ManifestPath), null, TolerantJson) is not JsonObject root)
            return [];
        if (root["voices"] is not JsonArray entries)
            return [];

        var offers = new List<VoiceOffer>();
        foreach (var node in entries)
        {
            if (node is not JsonObject entry)
                continue;
            if (GetString(entry, "key") is not { Length: > 0 } key)
                continue;
            if (entry["install"] is not JsonObject install)
                continue;
            if (GetString(install, "engine") is not { Length: > 0 } engineId)
                continue;

            offers.Add(new VoiceOffer(
                Key: key,
                Name: GetString(entry, "name") ?? key,
                Description: GetString(entry, "description") ?? "",
                Locale: VoiceLocale.Normalize(GetString(entry, "locale")),
                DownloadBytes: GetLong(entry, "downloadBytes") ?? 0,
                License: GetString(entry, "license") ?? "",
                Attribution: GetString(entry, "attribution") ?? "",
                Plan: new VoiceInstallPlan(
                    EngineId: engineId,
                    DownloadUrl: GetString(install, "downloadUrl"),
                    Bundle: GetString(install, "bundle"),
                    ReferenceWavFile: GetString(install, "referenceWavFile"),
                    SampleFile: GetString(entry, "sample"))));
        }
        return offers;
    }

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? GetLong(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
}

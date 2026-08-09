namespace SessionStories.Core.Voices;

/// <summary>
/// The shelf: voices you could install but have not. Curated rather than scraped — every entry was
/// listened to before it was added, and the manifest ships in the repo, so browsing works offline
/// and cannot break when someone else's index moves.
/// </summary>
public interface IVoiceGallery
{
    /// <summary>Locales with at least one offer, for the first step of "add a voice".</summary>
    IReadOnlyList<VoiceLanguage> ListLanguages();

    /// <summary>Offers for one locale, in shelf order. Empty for an unknown locale.</summary>
    IReadOnlyList<VoiceOffer> ListOffers(string locale);

    VoiceOffer? FindOffer(string key);

    /// <summary>
    /// Absolute path of the shipped, pre-rendered sample for an offer; null when it is missing.
    /// These exist so auditioning is instant: the reader hears the voice before committing to a
    /// download, and installing one is a file copy rather than a render.
    /// </summary>
    string? SamplePath(string key);
}

public sealed record VoiceLanguage(string Locale, int OfferCount);

/// <param name="DownloadBytes">Shown on the Add button. 0 when everything is already on disk.</param>
/// <param name="Plan">Opaque to the UI — how this voice gets installed, and by which engine.</param>
public sealed record VoiceOffer(
    string Key,
    string Name,
    string Description,
    string Locale,
    long DownloadBytes,
    string License,
    string Attribution,
    VoiceInstallPlan Plan);

/// <summary>
/// What an installer needs to materialise a voice. Every field past <paramref name="EngineId"/> is
/// engine-specific and only that engine's <see cref="IVoiceInstaller"/> reads it.
/// </summary>
/// <param name="DownloadUrl">Remote asset to fetch; null when the asset ships with the repo.</param>
/// <param name="Bundle">Piper: the bundle folder name under the piper models root.</param>
/// <param name="ReferenceWavFile">Cloning engines: file name of the reference recording.</param>
/// <param name="SampleFile">
/// Pre-rendered sample shipped alongside the manifest. Its presence is what makes a shelf install
/// fast — the installed voice's sample is a copy of this, not a fresh render.
/// </param>
public sealed record VoiceInstallPlan(
    string EngineId,
    string? DownloadUrl = null,
    string? Bundle = null,
    string? ReferenceWavFile = null,
    string? SampleFile = null);

namespace SessionStories.Core.Voices;

/// <summary>
/// Turns an offer (or a recording the reader supplied) into an installed voice: downloads, extracts,
/// converts and caches whatever its engine needs. This is the layer that was missing — without it the
/// reader had to be the integration step, choosing an engine and hand-writing a catalog row.
/// <para>
/// An installer owns engine assets only. Writing the catalog entry and guaranteeing the voice has a
/// playable sample happen once, engine-agnostically, in the install job.
/// </para>
/// </summary>
public interface IVoiceInstaller
{
    /// <summary>Matches <see cref="VoiceInstallPlan.EngineId"/>.</summary>
    string EngineId { get; }

    /// <summary>
    /// Materialises the voice and returns the entry to record. Progress messages are user-visible
    /// job log lines, so they describe the work ("downloading 42 MB"), never the plumbing.
    /// </summary>
    Task<InstalledVoice> InstallAsync(
        VoiceInstallRequest request, IProgress<string> progress, CancellationToken ct = default);
}

/// <param name="SuppliedAudioPath">
/// Audio the reader uploaded or recorded, already on local disk. Null for a shelf install. An engine
/// that cannot clone must reject this rather than silently ignore it — a fixed-voice engine accepting
/// a recording and then not sounding like it is exactly the kind of lie this refactor removes.
/// </param>
public sealed record VoiceInstallRequest(
    string VoiceId,
    string Name,
    string Description,
    string Locale,
    string Style,
    VoiceInstallPlan Plan,
    VoiceProvenance? Source = null,
    string? SuppliedAudioPath = null);

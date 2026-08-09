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
    /// Materialises the voice and returns the entry to record. Progress is reported as a
    /// <see cref="VoiceInstallStep"/> so the panel can say what is happening in plain language
    /// instead of pattern-matching log text.
    /// </summary>
    Task<InstalledVoice> InstallAsync(
        VoiceInstallRequest request, IProgress<VoiceInstallStep> progress, CancellationToken ct = default);
}

/// <summary>
/// One beat of an install. <paramref name="Step"/> is a stable key the UI turns into its own
/// wording; <paramref name="Detail"/> is the human line that lands in the job log for anyone who
/// wants to see the actual work. Installing a voice takes minutes, so saying which minute it is
/// matters more than it would for a fast operation.
/// </summary>
public sealed record VoiceInstallStep(string Step, string Detail, int? Percent = null);

/// <summary>The step keys. Every installer reports from this set; the UI knows only these.</summary>
public static class VoiceInstallSteps
{
    /// <summary>Fetching engine assets — the only step with a meaningful percentage.</summary>
    public const string Download = "download";
    public const string Unpack = "unpack";
    /// <summary>Turning the reader's recording into what the engine conditions on.</summary>
    public const string Convert = "convert";
    /// <summary>The expensive one: computing and caching voice conditioning.</summary>
    public const string Learn = "learn";
    /// <summary>Producing the voice's playable sample — the last thing an install owes.</summary>
    public const string Sample = "sample";
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
    VoiceInstallPlan Plan,
    VoiceProvenance? Source = null,
    string? SuppliedAudioPath = null);

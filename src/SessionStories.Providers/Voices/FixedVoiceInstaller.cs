using SessionStories.Core.Voices;

namespace SessionStories.Providers.Voices;

/// <summary>
/// Installs a voice that already exists inside its engine — nothing to download, nothing to learn,
/// just a record of which built-in speaker this voice is.
/// <para>
/// Not every engine clones. Qwen's Polish checkpoint ships one trained speaker and discards its
/// speaker encoder at load, so running it through <see cref="CloningVoiceInstaller"/> would spend
/// minutes preparing a reference recording the model then ignores — an install that looks like it
/// did something it did not.
/// </para>
/// </summary>
public sealed class FixedVoiceInstaller(string engineId) : IVoiceInstaller
{
    public string EngineId => engineId;

    public Task<InstalledVoice> InstallAsync(
        VoiceInstallRequest request, IProgress<VoiceInstallStep> progress, CancellationToken ct = default)
    {
        if (request.SuppliedAudioPath is { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"'{request.Name}' is a built-in voice — it cannot be made to sound like a recording. " +
                "Pick a voice from the list, or record into a voice that copies yours.");
        }

        var engineData = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Plan.Speaker is { Length: > 0 } speaker)
            engineData["speaker"] = speaker;

        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Download, "already in the engine", 100));

        return Task.FromResult(new InstalledVoice(
            Id: request.VoiceId,
            Name: request.Name,
            Description: request.Description,
            Locale: request.Locale,
            EngineId: engineId,
            EngineData: engineData,
            Source: request.Source));
    }
}

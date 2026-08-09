using SessionStories.Core.Tts;
using SessionStories.Core.Voices;

namespace SessionStories.Providers.Voices;

/// <summary>
/// Installs a voice for any engine that clones from a reference recording. Works the same whether the
/// recording came off the shelf or out of the reader's own microphone — that symmetry is the point:
/// "pick a voice" and "use my voice" are the same operation with a different source of wav.
/// <para>
/// Engine-agnostic by construction: it holds an engine <em>id</em> and a factory, never a concrete
/// provider type, so a second cloning engine needs no new installer.
/// </para>
/// </summary>
public sealed class CloningVoiceInstaller(
    string engineId,
    IVoiceStore voices,
    Func<ITtsProvider> engineFactory,
    HttpClient http,
    string shippedWavsRoot) : IVoiceInstaller
{
    public string EngineId => engineId;

    public async Task<InstalledVoice> InstallAsync(
        VoiceInstallRequest request, IProgress<VoiceInstallStep> progress, CancellationToken ct = default)
    {
        var sourceWav = await ResolveSourceAudioAsync(request, progress, ct);

        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Convert, "preparing the recording"));
        var voiceWavPath = Path.Combine(voices.VoicesRoot, request.VoiceId + ".wav");
        Directory.CreateDirectory(voices.VoicesRoot);
        // The length guidance applies to what a reader recorded, not to a vetted shelf asset.
        ReferenceAudio.ConvertToWav(sourceWav, voiceWavPath,
            enforceMinimum: request.SuppliedAudioPath is { Length: > 0 });
        // Any cached conditionals or sample under this id describe a different voice now.
        voices.InvalidateDerived(request.VoiceId);

        // Conditioning is the slow part (a full pass over the reference audio) and it is cached
        // per voice, so paying it here means the first real render does not.
        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Learn, "learning the voice"));
        var engine = engineFactory();
        try
        {
            if (!engine.Capabilities.SupportsVoiceCloning)
            {
                throw new InvalidOperationException(
                    $"Engine '{engineId}' cannot copy a voice from a recording.");
            }
            await engine.PrepareVoiceAsync(voiceWavPath, request.VoiceId, ct);
        }
        finally
        {
            (engine as IDisposable)?.Dispose();
        }

        return new InstalledVoice(
            Id: request.VoiceId,
            Name: request.Name,
            Description: request.Description,
            Locale: request.Locale,
            Style: VoiceStyle.Normalize(request.Style),
            EngineId: engineId,
            EngineData: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["referenceWav"] = Path.GetFileName(voiceWavPath),
            },
            Source: request.Source);
    }

    /// <summary>Reader's own audio wins; otherwise the shelf's wav, shipped or downloaded.</summary>
    private async Task<string> ResolveSourceAudioAsync(
        VoiceInstallRequest request, IProgress<VoiceInstallStep> progress, CancellationToken ct)
    {
        if (request.SuppliedAudioPath is { Length: > 0 } supplied)
        {
            if (!File.Exists(supplied))
                throw new FileNotFoundException("The uploaded recording is no longer on disk.", supplied);
            return supplied;
        }

        if (request.Plan.ReferenceWavFile is not { Length: > 0 } wavFile)
            throw new InvalidOperationException($"Voice '{request.Name}' has no recording to copy.");

        // A manifest may not name anything outside the shipped wavs folder.
        var shipped = Path.Combine(shippedWavsRoot, Path.GetFileName(wavFile));
        if (File.Exists(shipped))
            return shipped;

        if (request.Plan.DownloadUrl is not { Length: > 0 } url)
        {
            throw new FileNotFoundException(
                $"Voice '{request.Name}' expects '{wavFile}' in the voice gallery, and it is not there.", shipped);
        }

        await VoiceAssetDownloader.DownloadAsync(http, url, shipped, progress, ct);
        return shipped;
    }
}

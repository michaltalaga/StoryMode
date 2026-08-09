using System.Diagnostics;
using SessionStories.Core.Voices;

namespace SessionStories.Providers.Voices;

/// <summary>
/// Installs a piper voice: a trained VITS bundle (model + tokens + phonemiser data) unpacked under the
/// piper models root. Nothing is cloned and no reference recording is involved — the whole point of the
/// installer seam is that the reader never learns this engine works completely differently from the other.
/// <para>
/// The resulting voice records its bundle folder in <c>engineData.bundle</c>, which is what replaced the
/// old hand-maintained appsettings mapping.
/// </para>
/// </summary>
public sealed class PiperVoiceInstaller(
    string engineId,
    string modelsRoot,
    HttpClient http) : IVoiceInstaller
{
    public string EngineId => engineId;

    public async Task<InstalledVoice> InstallAsync(
        VoiceInstallRequest request, IProgress<string> progress, CancellationToken ct = default)
    {
        if (request.SuppliedAudioPath is { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"'{request.Name}' is a fixed trained voice — it cannot be made to sound like a recording. " +
                "Pick a voice from the list instead, or record into a voice that copies yours.");
        }

        if (request.Plan.Bundle is not { Length: > 0 } bundle || Path.GetFileName(bundle) != bundle)
            throw new InvalidOperationException($"Voice '{request.Name}' names no valid voice bundle.");

        var bundleDir = Path.Combine(modelsRoot, bundle);
        if (Directory.Exists(bundleDir))
        {
            // Already on disk from an earlier install or the model download script — nothing to fetch.
            progress.Report("already downloaded");
        }
        else
        {
            if (request.Plan.DownloadUrl is not { Length: > 0 } url)
                throw new InvalidOperationException($"Voice '{request.Name}' is not on disk and has nowhere to download from.");

            Directory.CreateDirectory(modelsRoot);
            var archive = Path.Combine(modelsRoot, bundle + ".tar.bz2");
            await VoiceAssetDownloader.DownloadAsync(http, url, archive, progress, ct);
            try
            {
                progress.Report("unpacking");
                ExtractTarBz2(archive, modelsRoot, ct);
            }
            finally
            {
                TryDelete(archive);
            }

            if (!Directory.Exists(bundleDir))
            {
                throw new InvalidOperationException(
                    $"'{request.Name}' unpacked, but no '{bundle}' folder appeared in the voice models directory.");
            }
        }

        return new InstalledVoice(
            Id: request.VoiceId,
            Name: request.Name,
            Description: request.Description,
            Locale: request.Locale,
            Style: Core.Tts.VoiceStyle.Normalize(request.Style),
            EngineId: engineId,
            EngineData: new Dictionary<string, string>(StringComparer.Ordinal) { ["bundle"] = bundle },
            Source: request.Source);
    }

    /// <summary>
    /// bsdtar ships with Windows 10+ and handles bzip2; .NET's compression stack does not. Extracting
    /// in-process would mean taking a dependency purely to unpack one archive format.
    /// </summary>
    private static void ExtractTarBz2(string archivePath, string destinationRoot, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("tar")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-xjf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destinationRoot);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start 'tar' to unpack the voice.");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unpacking the voice failed (tar exit {process.ExitCode}). {stderr.Trim()}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // best effort — a leftover archive costs disk, not correctness
        }
    }
}

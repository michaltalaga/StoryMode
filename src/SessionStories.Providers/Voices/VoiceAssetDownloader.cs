using SessionStories.Core.Voices;

namespace SessionStories.Providers.Voices;

/// <summary>
/// Fetches an install asset to disk, reporting human progress. Downloads land on a scratch file and
/// are moved into place, so a cancelled or failed install can never leave a half-file that later
/// looks installed.
/// </summary>
public static class VoiceAssetDownloader
{
    public static async Task DownloadAsync(
        HttpClient http, string url, string destinationPath,
        IProgress<VoiceInstallStep> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tmp = destinationPath + ".part";

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Download failed ({(int)response.StatusCode}) for {url}");
        }

        var total = response.Content.Headers.ContentLength;
        var size = total is { } bytes ? Describe(bytes) : "";
        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Download,
            size.Length > 0 ? $"downloading {size}" : "downloading", total is > 0 ? 0 : null));

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(tmp))
        {
            var buffer = new byte[81_920];
            long received = 0;
            var lastReported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                // Every 5%: enough to keep a progress bar honest on a 64 MB bundle without
                // filling the log with noise.
                if (total is { } length and > 0)
                {
                    var percent = (int)(received * 100 / length);
                    if (percent >= lastReported + 5)
                    {
                        lastReported = percent - percent % 5;
                        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Download,
                            $"downloading {lastReported}% of {size}", lastReported));
                    }
                }
            }
        }

        File.Move(tmp, destinationPath, overwrite: true);
        progress.Report(new VoiceInstallStep(VoiceInstallSteps.Download,
            $"downloaded {Path.GetFileName(destinationPath)}", 100));
    }

    /// <summary>
    /// Invariant on purpose: this lands in the job log next to English text, and the machine's own
    /// locale would render it "64,1 MB" there. What the reader sees is formatted client-side anyway.
    /// </summary>
    public static string Describe(long bytes)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => string.Format(culture, "{0:0.#} GB", bytes / (1024.0 * 1024 * 1024)),
            >= 1024 * 1024 => string.Format(culture, "{0:0.#} MB", bytes / (1024.0 * 1024)),
            >= 1024 => string.Format(culture, "{0:0.#} KB", bytes / 1024.0),
            _ => $"{bytes} B",
        };
    }
}

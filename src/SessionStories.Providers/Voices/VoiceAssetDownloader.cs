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
        IProgress<string> progress, CancellationToken ct = default)
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
        progress.Report(total is { } bytes ? $"downloading {Describe(bytes)}" : "downloading");

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
                // Every 10%: enough to show movement on a 100 MB bundle, not enough to flood the log.
                if (total is { } size and > 0)
                {
                    var percent = (int)(received * 100 / size);
                    if (percent >= lastReported + 10)
                    {
                        lastReported = percent - percent % 10;
                        progress.Report($"downloading {lastReported}%");
                    }
                }
            }
        }

        File.Move(tmp, destinationPath, overwrite: true);
        progress.Report($"downloaded {Path.GetFileName(destinationPath)}");
    }

    public static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

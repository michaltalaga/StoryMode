using NAudio.Wave;

namespace SessionStories.Providers.Voices;

/// <summary>
/// Normalises whatever a reader hands us into the one shape a cloning engine can condition on:
/// mono PCM wav. Phones record m4a and browsers record webm/opus, so accepting only wav would put
/// a format conversion between the reader and their own voice.
/// </summary>
public static class ReferenceAudio
{
    /// <summary>24 kHz is what the chatterbox speech encoder wants; resampling later would be lossy twice.</summary>
    public const int SampleRate = 24_000;

    /// <summary>Hard floor. Below this a clone conditions on too little speech to be stable at all;
    /// the UI asks for 20–40 s, which is where it actually sounds like the person.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> (wav/mp3/aiff natively, m4a/aac/wma via Media Foundation),
    /// downmixes to mono, resamples to <see cref="SampleRate"/> and writes a PCM wav atomically.
    /// </summary>
    /// <param name="enforceMinimum">
    /// Only for audio a reader supplied. A curated shelf voice ships a reference that was listened to
    /// before it was added — chatterbox's own default voice is 7.4 s and sounds fine — so applying the
    /// recording guidance to it would reject voices we know work.
    /// </param>
    /// <exception cref="InvalidOperationException">The file cannot be decoded, or holds too little audio.</exception>
    public static void ConvertToWav(string sourcePath, string destinationPath, bool enforceMinimum = true)
    {
        var samples = ReadMono(sourcePath);
        var duration = TimeSpan.FromSeconds((double)samples.Length / SampleRate);
        if (enforceMinimum && duration < MinimumDuration)
        {
            throw new InvalidOperationException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "That recording is only {0:0.#} seconds long. " +
                "A voice needs at least 10 seconds of clear speech to copy; 20–40 seconds works best.",
                duration.TotalSeconds));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tmp = destinationPath + ".tmp";
        using (var writer = new WaveFileWriter(tmp, new WaveFormat(SampleRate, 16, 1)))
        {
            foreach (var sample in samples)
            {
                var clipped = Math.Clamp(sample, -1f, 1f);
                writer.WriteSample(clipped);
            }
        }
        File.Move(tmp, destinationPath, overwrite: true);
    }

    /// <summary>Duration of an already-decodable file, for validation before any work is queued.</summary>
    public static TimeSpan? TryReadDuration(string path)
    {
        try
        {
            using var reader = OpenReader(path);
            return reader.TotalTime;
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static float[] ReadMono(string sourcePath)
    {
        using var reader = OpenReader(sourcePath);
        var source = reader.ToSampleProvider();

        // Downmix before resampling: half the samples through the (more expensive) resampler.
        var mono = source.WaveFormat.Channels > 1
            ? new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : source;

        var resampled = mono.WaveFormat.SampleRate == SampleRate
            ? mono
            : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(mono, SampleRate);

        var buffer = new float[SampleRate];
        var all = new List<float>(SampleRate * 30);
        int read;
        while ((read = resampled.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    private static WaveStream OpenReader(string path)
    {
        try
        {
            // Covers wav/mp3/aiff.
            return new AudioFileReader(path);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            try
            {
                // Everything a phone actually produces — m4a/aac, wma, and mp4 audio tracks.
                return new MediaFoundationReader(path);
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(path)}' is not an audio file this machine can read. " +
                    "Try a wav, mp3 or m4a.", inner);
            }
        }
    }
}

using NAudio.Wave;
using SessionStories.Tts.Chatterbox;

namespace SessionStories.Tests;

public sealed class AudioPostProcessorTests : IDisposable
{
    private const int Rate = 24000;

    private readonly string _tempDir;

    public AudioPostProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SessionStories.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void WriteWav_TrimsSilenceFromBothEnds_KeepingHang()
    {
        var silence = new float[Rate / 2];
        var sine = Sine(440, 0.5, 1.0, Rate);
        var chunk = new float[silence.Length + sine.Length + silence.Length];
        sine.CopyTo(chunk, silence.Length);

        var path = Path.Combine(_tempDir, "trim.wav");
        AudioPostProcessor.WriteWav(
            [new ChunkAudio(chunk, StartsParagraph: false)],
            path,
            new AudioPostOptions { OutputSampleRate = Rate });

        var output = ReadWavMono(path);

        int hangSamples = (int)(0.050 * Rate);
        int expectedLength = sine.Length + 2 * hangSamples;
        Assert.InRange(output.Length, expectedLength - 64, expectedLength + 64);

        // The retained hang region is original silence; the tone follows right after it.
        Assert.All(output.Take(hangSamples - 100), s => Assert.True(Math.Abs(s) < 1e-3));
        Assert.True(output.Skip(hangSamples + 100).Take(2000).Max(Math.Abs) > 0.05f);
    }

    [Fact]
    public void WriteWav_FullySilentChunk_ProducesNoSamples()
    {
        var path = Path.Combine(_tempDir, "silent.wav");
        AudioPostProcessor.WriteWav(
            [new ChunkAudio(new float[Rate], StartsParagraph: false)],
            path,
            new AudioPostOptions { OutputSampleRate = Rate });

        Assert.Empty(ReadWavMono(path));
    }

    [Fact]
    public void WriteWav_InsertsGapsBetweenChunks_LongerBeforeParagraphs()
    {
        var tone = Constant(0.5f, 0.5, Rate);
        var chunks = new[]
        {
            new ChunkAudio(tone, StartsParagraph: true), // no gap before the first chunk
            new ChunkAudio(tone, StartsParagraph: false),
            new ChunkAudio(tone, StartsParagraph: true),
        };

        var path = Path.Combine(_tempDir, "gaps.wav");
        AudioPostProcessor.WriteWav(chunks, path, new AudioPostOptions { OutputSampleRate = Rate });

        var output = ReadWavMono(path);

        int gap = (int)(0.3 * Rate);
        int paragraphGap = (int)(0.6 * Rate);
        Assert.Equal(3 * tone.Length + gap + paragraphGap, output.Length);

        AssertSilent(output, tone.Length, gap);
        AssertSilent(output, tone.Length + gap + tone.Length, paragraphGap);
        Assert.True(Math.Abs(output[100]) > 0.05f);
        Assert.True(Math.Abs(output[tone.Length + gap + 100]) > 0.05f);
        Assert.True(Math.Abs(output[2 * tone.Length + gap + paragraphGap + 100]) > 0.05f);
    }

    [Fact]
    public void WriteWav_NormalizesToTargetRms()
    {
        var sine = Sine(440, 0.25, 1.0, Rate);

        var path = Path.Combine(_tempDir, "rms.wav");
        AudioPostProcessor.WriteWav(
            [new ChunkAudio(sine, StartsParagraph: false)],
            path,
            new AudioPostOptions { OutputSampleRate = Rate });

        var output = ReadWavMono(path);
        double rmsDb = 20 * Math.Log10(Rms(output));
        Assert.InRange(rmsDb, -20.5, -19.5);
    }

    [Fact]
    public void WriteWav_PeakGuard_PreventsClipping()
    {
        // Quiet sine (~ -23 dBFS RMS) wants gain > 1 to hit -20 dBFS, but the spike
        // would then exceed -1 dBFS, so the guard must cap the gain instead.
        var sine = Sine(440, 0.1, 1.0, Rate);
        sine[Rate / 2] = 0.95f;

        var path = Path.Combine(_tempDir, "peak.wav");
        AudioPostProcessor.WriteWav(
            [new ChunkAudio(sine, StartsParagraph: false)],
            path,
            new AudioPostOptions { OutputSampleRate = Rate });

        var output = ReadWavMono(path);
        float peak = output.Max(Math.Abs);
        double peakCeiling = Math.Pow(10, -1.0 / 20);
        Assert.True(peak <= peakCeiling + 0.005, $"Peak {peak} exceeds -1 dBFS ceiling {peakCeiling}");

        double rmsDb = 20 * Math.Log10(Rms(output));
        Assert.True(rmsDb < -21, $"RMS {rmsDb:F1} dB should stay below target because the guard reduced gain");
    }

    [Fact]
    public void LoadMono24k_StereoWav44k1_DownmixesByAveraging_AndResamplesTo24k()
    {
        const int sourceRate = 44100;
        int frames = sourceRate / 2;
        var path = Path.Combine(_tempDir, "stereo.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(sourceRate, 16, 2)))
        {
            for (int i = 0; i < frames; i++)
            {
                writer.WriteSample(0.6f);
                writer.WriteSample(0.2f);
            }
        }

        var mono = AudioPostProcessor.LoadMono24k(path);

        int expectedLength = frames * 24000 / sourceRate;
        Assert.InRange(mono.Length, expectedLength - 240, expectedLength + 240);

        // (0.6 + 0.2) / 2 = 0.4; check away from resampler edge effects.
        double meanMiddle = mono.Skip(4000).Take(4000).Average();
        Assert.InRange(meanMiddle, 0.38, 0.42);
    }

    [Fact]
    public void LoadMono24k_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.wav");
        Assert.Throws<FileNotFoundException>(() => AudioPostProcessor.LoadMono24k(path));
    }

    private static void AssertSilent(float[] samples, int start, int length)
    {
        for (int i = start; i < start + length; i++)
            Assert.True(Math.Abs(samples[i]) < 1e-3, $"Expected silence at sample {i}, got {samples[i]}");
    }

    private static float[] Sine(double frequency, double amplitude, double seconds, int sampleRate)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        return samples;
    }

    private static float[] Constant(float value, double seconds, int sampleRate)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        Array.Fill(samples, value);
        return samples;
    }

    private static double Rms(float[] samples)
    {
        double sumSquares = 0;
        foreach (float sample in samples)
            sumSquares += (double)sample * sample;
        return Math.Sqrt(sumSquares / samples.Length);
    }

    private static float[] ReadWavMono(string path)
    {
        using var reader = new AudioFileReader(path);
        Assert.Equal(1, reader.WaveFormat.Channels);
        var output = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add(buffer[i]);
        }

        return [.. output];
    }
}

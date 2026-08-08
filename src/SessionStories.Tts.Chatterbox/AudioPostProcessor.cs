using System.Buffers.Binary;
using NAudio.Lame;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SessionStories.Tts.Chatterbox;

/// <summary>One synthesized chunk of mono 24 kHz audio.</summary>
public sealed record ChunkAudio(float[] Samples, bool StartsParagraph);

public sealed record AudioPostOptions
{
    public double TrimThresholdDb { get; init; } = -45;
    public double TrimHangMs { get; init; } = 50;
    public double GapSeconds { get; init; } = 0.3;
    public double ParagraphGapSeconds { get; init; } = 0.6;
    public double TargetRmsDb { get; init; } = -20;
    public int InputSampleRate { get; init; } = 24000;
    public int OutputSampleRate { get; init; } = 44100;
    public int Mp3BitrateKbps { get; init; } = 128;
}

/// <summary>
/// Shared post-processing for synthesized chunks: silence trim, gap stitching,
/// RMS normalization with a -1 dBFS peak guard, resampling, and encoding.
/// </summary>
public static class AudioPostProcessor
{
    private const double PeakCeilingDb = -1.0;
    private const int ChatterboxSampleRate = 24000;

    public static void EncodeMp3(IReadOnlyList<ChunkAudio> chunks, string outputMp3Path, AudioPostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        options ??= new AudioPostOptions();

        var samples = Process(chunks, options);
        var bytes = ToPcm16Bytes(samples);
        using var writer = new LameMP3FileWriter(
            outputMp3Path,
            new WaveFormat(options.OutputSampleRate, 16, 1),
            options.Mp3BitrateKbps);
        writer.Write(bytes, 0, bytes.Length);
    }

    public static void WriteWav(IReadOnlyList<ChunkAudio> chunks, string outputWavPath, AudioPostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        options ??= new AudioPostOptions();

        var samples = Process(chunks, options);
        var bytes = ToPcm16Bytes(samples);
        using var writer = new WaveFileWriter(outputWavPath, new WaveFormat(options.OutputSampleRate, 16, 1));
        writer.Write(bytes, 0, bytes.Length);
    }

    public static float[] LoadMono24k(string audioPath)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"Audio file not found: {audioPath}", audioPath);

        float[] interleaved;
        int sourceRate;
        int channels;
        try
        {
            using var reader = new AudioFileReader(audioPath);
            sourceRate = reader.WaveFormat.SampleRate;
            channels = reader.WaveFormat.Channels;
            interleaved = ReadAll(reader);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            // AudioFileReader covers wav/mp3/aiff; anything else (m4a, wma, ...) goes through Media Foundation.
            MediaFoundationApi.Startup();
            using var reader = new MediaFoundationReader(audioPath);
            var sampleProvider = reader.ToSampleProvider();
            sourceRate = sampleProvider.WaveFormat.SampleRate;
            channels = sampleProvider.WaveFormat.Channels;
            interleaved = ReadAll(sampleProvider);
        }

        var mono = DownmixToMono(interleaved, channels);
        return Resample(mono, sourceRate, ChatterboxSampleRate);
    }

    private static float[] Process(IReadOnlyList<ChunkAudio> chunks, AudioPostOptions options)
    {
        var stitched = Stitch(chunks, options);
        NormalizeRms(stitched, options.TargetRmsDb);
        return Resample(stitched, options.InputSampleRate, options.OutputSampleRate);
    }

    private static float[] Stitch(IReadOnlyList<ChunkAudio> chunks, AudioPostOptions options)
    {
        float threshold = (float)Math.Pow(10, options.TrimThresholdDb / 20);
        int hangSamples = (int)Math.Round(options.TrimHangMs * options.InputSampleRate / 1000.0);
        int gapSamples = (int)Math.Round(options.GapSeconds * options.InputSampleRate);
        int paragraphGapSamples = (int)Math.Round(options.ParagraphGapSeconds * options.InputSampleRate);

        var trimmed = new List<(float[] Samples, bool StartsParagraph)>(chunks.Count);
        foreach (var chunk in chunks)
            trimmed.Add((Trim(chunk.Samples, threshold, hangSamples), chunk.StartsParagraph));

        int totalLength = 0;
        for (int i = 0; i < trimmed.Count; i++)
        {
            if (i > 0)
                totalLength += trimmed[i].StartsParagraph ? paragraphGapSamples : gapSamples;
            totalLength += trimmed[i].Samples.Length;
        }

        var result = new float[totalLength];
        int position = 0;
        for (int i = 0; i < trimmed.Count; i++)
        {
            if (i > 0)
                position += trimmed[i].StartsParagraph ? paragraphGapSamples : gapSamples;
            trimmed[i].Samples.CopyTo(result, position);
            position += trimmed[i].Samples.Length;
        }

        return result;
    }

    private static float[] Trim(float[] samples, float threshold, int hangSamples)
    {
        int firstLoud = -1;
        for (int i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) >= threshold)
            {
                firstLoud = i;
                break;
            }
        }

        if (firstLoud < 0)
            return [];

        int lastLoud = samples.Length - 1;
        while (Math.Abs(samples[lastLoud]) < threshold)
            lastLoud--;

        int start = Math.Max(0, firstLoud - hangSamples);
        int end = Math.Min(samples.Length, lastLoud + 1 + hangSamples);
        return samples[start..end];
    }

    private static void NormalizeRms(float[] samples, double targetRmsDb)
    {
        if (samples.Length == 0)
            return;

        double sumSquares = 0;
        float peak = 0;
        foreach (float sample in samples)
        {
            sumSquares += (double)sample * sample;
            float abs = Math.Abs(sample);
            if (abs > peak)
                peak = abs;
        }

        double rms = Math.Sqrt(sumSquares / samples.Length);
        if (rms <= 0)
            return;

        double gain = Math.Pow(10, targetRmsDb / 20) / rms;
        double peakCeiling = Math.Pow(10, PeakCeilingDb / 20);
        if (peak * gain > peakCeiling)
            gain = peakCeiling / peak;

        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)(samples[i] * gain);
    }

    private static float[] Resample(float[] samples, int fromRate, int toRate)
    {
        if (fromRate == toRate)
            return samples;

        var resampler = new WdlResamplingSampleProvider(new FloatArraySampleProvider(samples, fromRate), toRate);
        var output = new List<float>((int)((long)samples.Length * toRate / fromRate) + 16);
        var buffer = new float[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add(buffer[i]);
        }

        return [.. output];
    }

    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        if (channels == 1)
            return interleaved;

        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (int channel = 0; channel < channels; channel++)
                sum += interleaved[frame * channels + channel];
            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var output = new List<float>();
        var buffer = new float[16384];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add(buffer[i]);
        }

        return [.. output];
    }

    // Own conversion instead of WaveFileWriter.WriteSamples so mp3 and wav share
    // byte-identical PCM, with clamping (WriteSamples would wrap on overflow).
    private static byte[] ToPcm16Bytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short value = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
        }

        return bytes;
    }

    private sealed class FloatArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}

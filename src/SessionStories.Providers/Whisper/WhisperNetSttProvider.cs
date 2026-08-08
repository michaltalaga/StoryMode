using System.Text;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SessionStories.Core.Stt;
using Whisper.net;

namespace SessionStories.Providers.Whisper;

/// <summary>
/// Whisper.net adapter. The model is loaded lazily inside each call and disposed before the
/// transcript is returned — Whisper (~3 GB) must never sit in VRAM alongside Chatterbox.
/// Output is the raw transcript: whitespace-trimmed at the edges, never cleaned or normalized.
/// </summary>
public sealed class WhisperNetSttProvider(WhisperOptions options) : ISttProvider
{
    private const int WhisperSampleRate = 16_000;

    public string Id => "whisper-net";

    // large-v3 is multilingual; these are the two languages this pipeline cares about.
    public SttCapabilities Capabilities { get; } = new(["pl", "en"]);

    public async Task<string> TranscribeAsync(string audioPath, string? languageHint = null, CancellationToken ct = default)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"Audio file not found: {audioPath}", audioPath);
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"Whisper model not found: {options.ModelPath}", options.ModelPath);

        var samples = ReadMono16k(audioPath, ct);

        using var factory = WhisperFactory.FromPath(options.ModelPath);
        var builder = factory.CreateBuilder();
        builder = string.IsNullOrWhiteSpace(languageHint)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(languageHint);
        await using var processor = builder.Build();

        var transcript = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            // Segment edges are trimmed only to guarantee single-space joins; the words
            // themselves are never touched — messiness is the input.
            var text = segment.Text.Trim();
            if (text.Length == 0)
                continue;
            if (transcript.Length > 0)
                transcript.Append(' ');
            transcript.Append(text);
        }

        return transcript.ToString();
    }

    /// <summary>Decodes any supported container (m4a/mp3/wav/ogg/webm) to mono 16 kHz floats.</summary>
    private static float[] ReadMono16k(string path, CancellationToken ct)
    {
        WaveStream reader;
        try
        {
            reader = new AudioFileReader(path);
        }
        catch (Exception)
        {
            // Formats AudioFileReader cannot open go through Media Foundation directly.
            MediaFoundationApi.Startup();
            reader = new MediaFoundationReader(path);
        }

        using (reader)
        {
            ISampleProvider source = reader as ISampleProvider ?? reader.ToSampleProvider();
            if (source.WaveFormat.SampleRate != WhisperSampleRate)
                source = new WdlResamplingSampleProvider(source, WhisperSampleRate);

            var channels = source.WaveFormat.Channels;
            var all = new List<float>();
            var buffer = new float[WhisperSampleRate * channels];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                all.AddRange(buffer.AsSpan(0, read));
            }

            if (channels == 1)
                return all.ToArray();

            var mono = new float[all.Count / channels];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                    sum += all[i * channels + c];
                mono[i] = sum / channels;
            }

            return mono;
        }
    }
}
